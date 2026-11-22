using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record CharacterDetailsLookupSummary(string scope);
internal sealed record CharacterDetailsLookupResult(string scope, string captured_at_utc,
    string view, int? gearset_id, bool available, string? unavailable_reason, int total,
    int offset, int? next_offset, object[] entries);
internal sealed record CharacterJobLookupRow(uint class_job_id, string name, string abbreviation,
    int? level, int? experience);
internal sealed record CharacterGearsetLookupRow(int id, string name, uint class_job_id,
    string class_job_name, bool is_current);
internal sealed record CharacterEquipmentLookupRow(int slot, string slot_name, uint item_id,
    uint base_item_id, string name, bool is_hq, int? item_level, bool is_empty,
    CharacterMateriaLookupRow[] materia, string? materia_unavailable);
internal sealed record CharacterMateriaLookupRow(int slot, uint materia_id, int grade,
    uint? item_id, string? name, bool is_empty);

/// <summary>
/// Pure managed lookup over one frozen on-demand capture. Query results own their
/// arrays; neither input arrays nor returned pages can rewrite later results.
/// Permission and framework-thread capture are the caller's responsibility.
/// </summary>
internal sealed class CharacterDetailsLookup
{
    internal const string Scope = "local_character_details";
    internal const int MaxBytes = 20000;
    private static readonly HashSet<string> ArgumentFields = new(StringComparer.Ordinal)
        { "view", "gearset_id", "offset", "limit" };
    private readonly CharacterDetailsReading snapshot;
    private readonly string capturedAtUtc;

    internal CharacterDetailsLookupSummary Summary => new(Scope);

    internal CharacterDetailsLookup(CharacterDetailsReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.Jobs?.Length > 100 || reading.Equipped?.Length > 14 || reading.Gearsets?.Length > 100)
            throw new ArgumentException("Character detail collections exceed their supported bounds.", nameof(reading));
        if (reading.Gearsets is { } gearsets &&
            (gearsets.Any(g => g.Id is < 0 or > 99) || gearsets.Select(g => g.Id).Distinct().Count() != gearsets.Length))
            throw new ArgumentException("Saved gearset IDs are invalid or ambiguous.", nameof(reading));
        this.snapshot = reading with
        {
            Jobs = reading.Jobs?.ToArray(),
            Equipped = reading.Equipped?.Select(CloneItem).ToArray(),
            Gearsets = reading.Gearsets?.Select(g => g with { Items = g.Items is null ? null : CloneItems(g.Items) }).ToArray(),
        };
        this.capturedAtUtc = reading.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>Call before scheduling any game-data capture, including the first query.</summary>
    internal static void ValidateArguments(JsonElement arguments) => _ = Parse(arguments);

    internal static CharacterDetailsSelection SelectCapture(JsonElement arguments)
    {
        var request = Parse(arguments);
        return new(request.View switch
        {
            "jobs" => CharacterDetailsSection.Jobs,
            "equipped" => CharacterDetailsSection.Equipped,
            "gearsets" => CharacterDetailsSection.Gearsets,
            _ => CharacterDetailsSection.Gearset,
        }, request.GearsetId);
    }

    internal CharacterDetailsLookupResult Query(JsonElement arguments)
    {
        var request = Parse(arguments);
        object[]? source;
        string? unavailable;
        switch (request.View)
        {
            case "jobs":
                source = this.snapshot.Jobs?.Select(j => (object)new CharacterJobLookupRow(j.ClassJobId,
                    j.Name, j.Abbreviation, j.Level, j.Experience)).ToArray();
                unavailable = this.snapshot.JobsUnavailable;
                break;
            case "equipped":
                source = this.snapshot.Equipped?.Select(ToItemRow).Cast<object>().ToArray();
                unavailable = this.snapshot.EquippedUnavailable;
                break;
            case "gearsets":
                source = this.snapshot.Gearsets?.Select(g => (object)new CharacterGearsetLookupRow(g.Id,
                    g.Name, g.ClassJobId, g.ClassJobName, g.IsCurrent)).ToArray();
                unavailable = this.snapshot.GearsetsUnavailable;
                break;
            default:
                var gearset = this.snapshot.Gearsets?.FirstOrDefault(g => g.Id == request.GearsetId);
                source = gearset?.Items?.Select(ToItemRow).Cast<object>().ToArray();
                unavailable = this.snapshot.Gearsets is null ? this.snapshot.GearsetsUnavailable
                    : gearset is not null ? "Items for this saved gearset were not requested in this capture."
                    : "The requested saved gearset is not present in this capture.";
                break;
        }
        if (source is null)
            return new(Scope, this.capturedAtUtc, request.View, request.GearsetId, false,
                ReasonOrDefault(unavailable), 0, request.Offset, null, []);

        var selected = source.Skip(request.Offset).Take(request.Limit).ToList();
        CharacterDetailsLookupResult Build() => new(Scope, this.capturedAtUtc, request.View,
            request.GearsetId, true, null, source.Length, request.Offset,
            request.Offset < source.Length && selected.Count < source.Length - request.Offset
                ? request.Offset + selected.Count : null,
            selected.ToArray());
        var result = Build();
        while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes && selected.Count > 0)
        {
            selected.RemoveAt(selected.Count - 1);
            result = Build();
        }
        if (request.Offset < source.Length && selected.Count == 0)
            throw new InvalidOperationException("A character detail row exceeded its response budget.");
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaxBytes)
            throw new InvalidOperationException("The character detail response exceeded its response budget.");
        return result;
    }

    private static CharacterEquipmentItem[] CloneItems(CharacterEquipmentItem[] items)
    {
        if (items.Length > 14) throw new ArgumentException("Equipment slot count exceeds its supported bounds.");
        return items.Select(CloneItem).ToArray();
    }

    private static CharacterEquipmentItem CloneItem(CharacterEquipmentItem item)
    {
        if (item.Materia.Length > 5) throw new ArgumentException("Materia count exceeds its supported bounds.");
        return item with { Materia = item.Materia.ToArray() };
    }

    private static CharacterEquipmentLookupRow ToItemRow(CharacterEquipmentItem item) => new(item.Slot,
        item.SlotName, item.ItemId, item.BaseItemId, item.Name, item.IsHq, item.ItemLevel, item.IsEmpty,
        item.Materia.Select(m => new CharacterMateriaLookupRow(m.Slot, m.MateriaId, m.Grade,
            m.ItemId, m.Name, m.IsEmpty)).ToArray(), item.MateriaUnavailable);

    private static string ReasonOrDefault(string? reason) =>
        string.IsNullOrWhiteSpace(reason) || reason.Length > 256
            ? "Character details are unavailable in this capture." : reason;

    private sealed record LookupRequest(string View, int? GearsetId, int Offset, int Limit);

    private static LookupRequest Parse(JsonElement arguments)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new ArgumentException("Invalid character detail lookup arguments.", nameof(arguments));
        }
        Require(arguments.ValueKind == JsonValueKind.Object);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            Require(ArgumentFields.Contains(property.Name) && fields.Add(property.Name));
        Require(fields.SetEquals(ArgumentFields));

        var viewValue = arguments.GetProperty("view");
        Require(viewValue.ValueKind == JsonValueKind.String);
        var view = viewValue.GetString()!;
        Require(view is "jobs" or "equipped" or "gearsets" or "gearset");
        var gearsetValue = arguments.GetProperty("gearset_id");
        int? gearsetId = null;
        if (view == "gearset")
        {
            Require(gearsetValue.ValueKind == JsonValueKind.Number && gearsetValue.TryGetInt32(out _));
            gearsetId = gearsetValue.GetInt32();
            Require(gearsetId is >= 0 and <= 99);
        }
        else Require(gearsetValue.ValueKind == JsonValueKind.Null);
        var offsetValue = arguments.GetProperty("offset");
        Require(offsetValue.ValueKind == JsonValueKind.Number && offsetValue.TryGetInt32(out _));
        var offset = offsetValue.GetInt32();
        Require(offset is >= 0 and <= 100);
        var limitValue = arguments.GetProperty("limit");
        Require(limitValue.ValueKind == JsonValueKind.Number && limitValue.TryGetInt32(out _));
        var limit = limitValue.GetInt32();
        Require(limit is >= 1 and <= 20);
        return new(view, gearsetId, offset, limit);
    }
}
