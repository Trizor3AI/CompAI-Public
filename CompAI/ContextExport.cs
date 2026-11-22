using System;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompAI;

// Deliberately independent of Dalamud, providers, and the raw tooltip representation.
// The caller copies saved inventory and current scan state separately under its state lock.
internal sealed record ContextExportData(DateTimeOffset ExportedAtUtc,
    ExportInventorySnapshot? SavedInventory, bool CapturePending, bool InventoryChanged,
    string? InventoryUnavailableReason, ExportPedigreeScan CurrentPedigreeScan)
{
    public ExportExtraInventory[] ExtraInventory { get; init; } = [];
}

internal sealed record ExportExtraInventory(string Category, string Container, string ItemNamespace,
    DateTimeOffset CapturedAtUtc, int Capacity, ExportInventoryItem[] Items);

internal sealed record ExportInventorySnapshot(DateTimeOffset CapturedAtUtc, ExportInventoryBag[] Bags);

internal sealed record ExportInventoryBag(string Bag, int? SlotCount,
    string? UnavailableReason, ExportInventoryItem[] Items);

internal sealed record ExportInventoryItem(uint SlotZeroBased, uint RawItemId, uint BaseItemId,
    string? Name, int Quantity, bool IsHq, ExportPedigree? Pedigree)
{
    public ulong SlotDisplay => (ulong)this.SlotZeroBased + 1;
}

internal sealed record ExportPedigree(DateTimeOffset ObservedAtUtc, string? Kind, int? Grade,
    string? Sex, ExportStarRow FatherStars, ExportStarRow MotherStars, string Status);

// Named fields make the fixed stat order explicit and preserve partial unknown readings.
internal sealed record ExportStarRow(int? Speed, int? Acceleration, int? Endurance,
    int? Stamina, int? Cunning)
{
    internal static ExportStarRow From(int[]? values)
    {
        int? At(int index) => values is not null && index < values.Length && values[index] is >= 1 and <= 4
            ? values[index] : null;
        return new(At(0), At(1), At(2), At(3), At(4));
    }
}

internal enum ExportPedigreeCheckStatus { Decoded, Unavailable }

internal sealed record ExportPedigreeScan(bool Scanning, int SecondsLeft, string? Status,
    ExportPedigreeScanCard[] Cards);

internal sealed record ExportPedigreeScanCard(string Bag, uint SlotZeroBased, uint RawItemId,
    uint BaseItemId, string? Name, DateTimeOffset ObservedAtUtc,
    ExportPedigreeCheckStatus Status, string? Reason, ExportPedigree? Reading)
{
    public ulong SlotDisplay => (ulong)this.SlotZeroBased + 1;
}

internal static class Export
{
    private const string UnknownAttributes =
        "Ability, colour, breeding capacity and own racing stats are unavailable in this export. " +
        "The two star rows describe pedigree parents, not the chocobo's own racing stats.";
    private const string SeparateStates =
        "Saved inventory and current pedigree scan are separate observations. " +
        "Matching bag, slot or item IDs do not establish that a current scan card is the same historical item.";
    private const string Coverage =
        "Carried bags Inventory1-4 and separately recorded personal crystals/key items are covered; other storage is excluded. " +
        "Key items use EventItem IDs and cannot be priced as normal Item IDs. " +
        "Items without an attached pedigree have unknown pedigree data. " +
        "decoded_tooltip records were read automatically; user_verified_tooltip records retain a legacy user comparison.";
    private static readonly string[] StatOrder = ["Speed", "Acceleration", "Endurance", "Stamina", "Cunning"];
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal static string CreateJson(ContextExportData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return JsonSerializer.Serialize(new
        {
            schema_version = 3,
            scope = "carried_inventory_and_pedigree",
            exported_at_utc = data.ExportedAtUtc,
            pedigree_stat_order = StatOrder,
            notes = new[] { Coverage, SeparateStates, UnknownAttributes },
            capture_pending = data.CapturePending,
            inventory_changed = data.InventoryChanged,
            inventory_unavailable_reason = data.InventoryUnavailableReason,
            saved_inventory = data.SavedInventory,
            extra_inventory = data.ExtraInventory,
            current_pedigree_scan = data.CurrentPedigreeScan,
        }, JsonOptions);
    }

    internal static string CreateText(ContextExportData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var text = new StringBuilder();
        text.AppendLine("FFXIV carried inventory and pedigree export");
        text.AppendLine($"Exported UTC: {Utc(data.ExportedAtUtc)}");
        text.AppendLine(Coverage);
        text.AppendLine(SeparateStates);
        text.AppendLine(UnknownAttributes);
        text.AppendLine("Stat order: " + string.Join(" | ", StatOrder));
        text.AppendLine("Displayed slots are one-based; raw slots are zero-based.");
        text.AppendLine();
        text.AppendLine("SAVED CARRIED INVENTORY");
        text.AppendLine($"Capture pending: {YesNo(data.CapturePending)}");
        text.AppendLine($"Inventory changed since saved capture: {YesNo(data.InventoryChanged)}");
        text.AppendLine($"Inventory unavailable reason: {Optional(data.InventoryUnavailableReason, "none reported")}");
        if (data.SavedInventory is not { } inventory)
        {
            text.AppendLine("Saved capture: unavailable (no saved inventory snapshot)");
        }
        else
        {
            text.AppendLine($"Captured UTC: {Utc(inventory.CapturedAtUtc)}");
            if (inventory.Bags.Length == 0) text.AppendLine("No bags recorded; inventory coverage is unknown.");
            foreach (var bag in inventory.Bags)
            {
                text.AppendLine();
                text.AppendLine($"Bag: {Value(bag.Bag)}");
                text.AppendLine($"  Availability: {(bag.UnavailableReason is null ? "available" : "unavailable")}");
                text.AppendLine($"  Unavailable reason: {Optional(bag.UnavailableReason, "none reported")}");
                text.AppendLine($"  Slot count: {Number(bag.SlotCount)}; occupied item records: {bag.Items.Length.ToString(CultureInfo.InvariantCulture)}");
                if (bag.Items.Length == 0)
                    text.AppendLine(bag.UnavailableReason is null
                        ? "  No occupied item records."
                        : "  No item records; unavailable does not mean empty.");
                foreach (var item in bag.Items)
                {
                    text.AppendLine($"  Slot {Number(item.SlotDisplay)} (raw zero-based slot {Number(item.SlotZeroBased)}): {Optional(item.Name, "[name unavailable]")} | quantity {Number(item.Quantity)} | HQ {YesNo(item.IsHq)} | raw ID {Number(item.RawItemId)} | base ID {Number(item.BaseItemId)}");
                    if (item.Pedigree is { } pedigree)
                    {
                        text.AppendLine("    Saved pedigree: attached reading; provenance " + Value(pedigree.Status));
                        AppendPedigree(text, pedigree, "      ");
                    }
                }
            }
        }

        text.AppendLine();
        text.AppendLine("PERSONAL CRYSTALS AND KEY ITEMS — separate observations");
        if (data.ExtraInventory.Length == 0) text.AppendLine("No extra containers recorded; this does not establish zero holdings.");
        foreach (var container in data.ExtraInventory)
        {
            text.AppendLine($"{Value(container.Container)} | definition namespace {Value(container.ItemNamespace)} | captured UTC {Utc(container.CapturedAtUtc)} | {container.Items.Length} occupied slots of {container.Capacity}");
            foreach (var item in container.Items)
                text.AppendLine($"  Slot {item.SlotDisplay}: {Optional(item.Name, "[name unavailable]")} | quantity {item.Quantity} | ID {item.RawItemId}");
        }
        var scan = data.CurrentPedigreeScan;
        text.AppendLine();
        text.AppendLine("CURRENT PEDIGREE SCAN — separate from saved inventory");
        text.AppendLine($"Scanning: {YesNo(scan.Scanning)}; seconds left: {Number(scan.SecondsLeft)}");
        if (scan.Scanning) text.AppendLine("Scan is in progress; this export contains only cards collected so far.");
        text.AppendLine($"Scan status: {Value(scan.Status)}");
        text.AppendLine($"Collected card records: {Number(scan.Cards.Length)}");
        if (scan.Cards.Length == 0) text.AppendLine("No scan cards recorded; this does not establish that no chocobo cards exist.");
        foreach (var card in scan.Cards)
        {
            text.AppendLine();
            text.AppendLine($"  {Value(card.Bag)}, slot {Number(card.SlotDisplay)} (raw zero-based slot {Number(card.SlotZeroBased)}): {Optional(card.Name, "[name unavailable]")}");
            text.AppendLine($"    Raw item ID: {Number(card.RawItemId)}; base item ID: {Number(card.BaseItemId)}");
            text.AppendLine($"    Observed UTC: {Utc(card.ObservedAtUtc)}");
            text.AppendLine($"    Status: {card.Status.ToString().ToLowerInvariant()}");
            text.AppendLine($"    Reason: {Optional(card.Reason, "none reported")}");
            if (card.Reading is { } reading) AppendPedigree(text, reading, "    ");
            else text.AppendLine("    Pedigree reading: unavailable; kind, grade, sex and both star rows are unknown.");
        }
        return text.ToString();
    }

    private static void AppendPedigree(StringBuilder text, ExportPedigree reading, string indent)
    {
        text.AppendLine($"{indent}Reading provenance: {Value(reading.Status)}");
        text.AppendLine($"{indent}Reading observed UTC: {Utc(reading.ObservedAtUtc)}");
        text.AppendLine($"{indent}Kind: {Value(reading.Kind)}; grade: {Number(reading.Grade)}; sex: {Value(reading.Sex)}");
        text.AppendLine($"{indent}Father stars (Speed | Acceleration | Endurance | Stamina | Cunning): {Stars(reading.FatherStars)}");
        text.AppendLine($"{indent}Mother stars (Speed | Acceleration | Endurance | Stamina | Cunning): {Stars(reading.MotherStars)}");
    }

    private static string Stars(ExportStarRow row) => string.Join(" | ",
        Number(row.Speed), Number(row.Acceleration), Number(row.Endurance), Number(row.Stamina), Number(row.Cunning));
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static string YesNo(bool value) => value ? "yes" : "no";
    private static string Number<T>(T? value) where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture) ?? "unknown";
    private static string Number<T>(T value) where T : struct, IFormattable =>
        value.ToString(null, CultureInfo.InvariantCulture);
    private static string Value(string? value) => Optional(value, "unknown");
    private static string Optional(string? value, string missing) => string.IsNullOrWhiteSpace(value) ? missing :
        value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter<ExportPedigreeCheckStatus>(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset().ToUniversalTime();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Utc(value));
    }
}
