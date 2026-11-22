using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace CompAI;

/// <summary>Framework-thread, permission-gated caller only. Copies four corroborated display fields; never lore or unlock arrays.</summary>
internal sealed unsafe class FieldRecordReader(IDataManager data, IGameGui gui)
{
    internal const int MaxPublicRows = 56; // Installed sheet: row zero, 50 records and five unnamed Number=0 placeholders.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private FieldRecordCatalog? catalog;

    internal ActivitySnapshot? Capture()
    {
        try
        {
            // Only the English marker/total format has supplied live evidence. Other
            // languages remain unavailable rather than treating an unknown label as owned.
            if (data.Language.ToString() != "English") return null;
            var current = GetCatalog();
            var rows = CaptureRows(Observe, address => CopyView((AddonMYCWarResultNotebook*)address),
                current.Definitions, () => CatalogMatches(current));
            return rows is null ? null : new(FieldRecordContract.Category, DateTimeOffset.UtcNow, true, rows);
        }
        catch (Exception) { return null; }
    }

    private FieldRecordOwner? Observe()
    {
        var player = PlayerState.Instance();
        var module = AgentModule.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0 || module == null || manager == null) return null;
        var agent = module->GetAgentByInternalId(AgentId.MycWarResultNotebook);
        if (agent == null || agent->AddonId is 0 or > ushort.MaxValue || !agent->IsAgentActive() ||
            !agent->IsAddonReady() || !agent->IsAddonShown()) return null;
        var addon = manager->GetAddonById((ushort)agent->AddonId);
        if (addon == null || addon->NameString != "MYCWarResultNotebook") return null;
        var handle = gui.GetAddonByName("MYCWarResultNotebook");
        if (handle.IsNull || handle.Address != (nint)addon || handle.Id != agent->AddonId || !handle.IsReady || !handle.IsVisible) return null;
        var notebook = (AddonMYCWarResultNotebook*)addon;
        var selection = Selection(notebook);
        return ValidSelection(selection) ? new((nint)player, player->ContentId, (nint)module, (nint)manager,
            (nint)agent, (nint)addon, agent->AddonId, selection) : null;
    }

    internal static ActivityEntry[]? CaptureRows(Func<FieldRecordOwner?> observe, Func<nint, FieldRecordView?> copy,
        FieldRecordDefinition[] definitions, Func<bool> catalogMatches)
    {
        var before = observe();
        if (before is null || !ValidSelection(before.Value.Selection)) return null;
        var first = copy(before.Value.Addon);
        if (first is null || first.Selection != before.Value.Selection || before != observe()) return null;
        var rows = DescribeRows(first, definitions);
        return rows is not null && first == copy(before.Value.Addon) && before == observe() && catalogMatches() ? rows : null;
    }

    // Supplied registered/uncollected captures corroborate root IDs 43/44/45 under
    // 34 and 28 under 6. This copier never descends into component 46 (biography).
    internal static FieldRecordView? CopyView(AddonMYCWarResultNotebook* addon)
    {
        if (addon == null) return null;
        var selection = Selection(addon);
        var manager = &addon->UldManager;
        var count = manager->NodeListCount;
        var list = manager->NodeList;
        if (!ValidSelection(selection) || count is 0 or > 512 || list == null) return null;
        var nodes = new Dictionary<uint, nint>();
        var owned = new HashSet<nint>();
        for (var index = 0; index < count; ++index)
        {
            var node = list[index];
            if (node == null) continue;
            owned.Add((nint)node);
            if (node->NodeId is 6 or 28 or 34 or 43 or 44 or 45 && !nodes.TryAdd(node->NodeId, (nint)node)) return null;
        }
        if (!nodes.TryGetValue(34, out var detailParent) || !nodes.TryGetValue(6, out var totalParent) ||
            !ReadText(nodes.GetValueOrDefault(43u), detailParent, owned, 16, out var number) || number is null ||
            !ReadText(nodes.GetValueOrDefault(44u), detailParent, owned, 256, out var name) || name is null ||
            !ReadText(nodes.GetValueOrDefault(45u), detailParent, owned, 64, out var absent) ||
            !ReadText(nodes.GetValueOrDefault(28u), totalParent, owned, 64, out var total) || total is null ||
            manager->NodeList != list || manager->NodeListCount != count || selection != Selection(addon)) return null;
        return new(selection, number, name, absent, total);
    }

    private static bool ReadText(nint address, nint expectedParent, HashSet<nint> owned, int maximumBytes, out string? value)
    {
        value = null;
        var node = (AtkResNode*)address;
        if (node == null) return true;
        if (node->Type != NodeType.Text || (nint)node->ParentNode != expectedParent || !owned.Contains(expectedParent)) return false;
        var hidden = false;
        var cursor = node;
        for (var depth = 0; cursor != null && depth < 32; ++depth, cursor = cursor->ParentNode)
        {
            if (!owned.Contains((nint)cursor)) return false;
            hidden |= (cursor->NodeFlags & NodeFlags.Visible) == 0 || cursor->Color.A == 0;
        }
        if (cursor != null) return false;
        if (hidden) return true;
        var text = (AtkTextNode*)node;
        var used = text->NodeText.BufUsed;
        var length = text->NodeText.StringLength;
        if (used < 1 || used > maximumBytes + 1 || length < 0 || length >= used || text->NodeText.BufSize < used ||
            (byte*)text->NodeText.StringPtr == null) return false;
        var bytes = new ReadOnlySpan<byte>((byte*)text->NodeText.StringPtr, (int)used);
        if (bytes[^1] != 0 || bytes[..^1].Contains((byte)0)) return false;
        try
        {
            var parsed = new ReadOnlySeStringSpan(bytes[..^1]);
            foreach (var payload in parsed)
            {
                if (payload.Type == ReadOnlySePayloadType.Invalid) return false;
                if (payload.Type == ReadOnlySePayloadType.Text) _ = StrictUtf8.GetCharCount(payload.Body);
                else if (!payload.Validate()) return false;
            }
            var plain = SeString.Parse(bytes[..^1].ToArray()).TextValue;
            if (!ActivitySnapshot.ValidText(plain, maximumBytes)) return false;
            value = plain.Trim();
            return true;
        }
        catch (Exception) { return false; }
    }

    private static FieldRecordSelection Selection(AddonMYCWarResultNotebook* addon) =>
        new(addon->MaxNoteIndex, addon->CurrentNoteIndex, addon->CurrentPageIndex);
    internal static bool ValidSelection(FieldRecordSelection selection) => selection.MaxNoteIndex == FieldRecordContract.RecordCount &&
        selection.CurrentNoteIndex is >= 0 and < FieldRecordContract.RecordCount && selection.CurrentPageIndex is >= 0 and < 5;

    private FieldRecordCatalog GetCatalog()
    {
        if (catalog is { } cached && CatalogMatches(cached)) return cached;
        catalog = null;
        var sheet = data.GetExcelSheet<MYCWarResultNotebook>();
        var rows = sheet.Take(MaxPublicRows + 1).ToArray();
        if (rows.Length > MaxPublicRows || rows.Count(row => row.RowId == 0) != 1)
            throw new InvalidOperationException("Unsupported Field Record catalog bounds.");
        // Number, not RowId or CurrentNoteIndex, supplies the displayed correspondence.
        // Description/NameJP/Quest are deliberately never copied.
        var definitions = BuildCatalog(rows.Where(row => row.RowId != 0).Select(row =>
            new FieldRecordDefinition(row.RowId, row.Number, row.Name.ExtractText().Trim())));
        return catalog = new(sheet.RawSheet, data.GameData, data.Language.ToString(), definitions);
    }

    private bool CatalogMatches(FieldRecordCatalog value) => ReferenceEquals(value.Sheet, data.GetExcelSheet<MYCWarResultNotebook>().RawSheet) &&
        ReferenceEquals(value.GameData, data.GameData) && value.Language == data.Language.ToString() && value.Language == "English";

    internal static FieldRecordDefinition[] BuildCatalog(IEnumerable<FieldRecordDefinition> definitions)
    {
        var raw = definitions.Take(MaxPublicRows).ToArray();
        if (raw.Length >= MaxPublicRows || raw.Any(row => row is null || row.Id is 0 or > ushort.MaxValue ||
                row.Number is < 0 or > FieldRecordContract.RecordCount ||
                (row.Number == 0 ? row.Name != "" : !ActivitySnapshot.ValidText(row.Name, 128) || row.Name == "???")) ||
            raw.Select(row => row.Id).Distinct().Count() != raw.Length)
            throw new InvalidOperationException("Unsupported Field Record definitions.");
        var rows = raw.Where(row => row.Number != 0).ToArray();
        if (rows.Length != FieldRecordContract.RecordCount || rows.Select(row => row.Number).Distinct().Count() != rows.Length)
            throw new InvalidOperationException("Unsupported Field Record numbered definitions.");
        return rows;
    }

    internal static ActivityEntry[]? DescribeRows(FieldRecordView view, IEnumerable<FieldRecordDefinition> definitions)
    {
        if (!ValidSelection(view.Selection) || view.Number.Length != 3 || view.Number[2] != '.' ||
            !int.TryParse(view.Number.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number is < 1 or > FieldRecordContract.RecordCount || !view.Total.StartsWith("Total: ", StringComparison.Ordinal)) return null;
        var ratio = view.Total.AsSpan(7);
        var slash = ratio.IndexOf('/');
        if (slash < 1 || !int.TryParse(ratio[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var total) ||
            !int.TryParse(ratio[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var maximum) ||
            maximum != FieldRecordContract.RecordCount || total is < 0 or > FieldRecordContract.RecordCount) return null;
        FieldRecordDefinition[] catalog;
        try { catalog = BuildCatalog(definitions); }
        catch (InvalidOperationException) { return null; }
        var selected = catalog.Single(row => row.Number == number);
        var registered = view.Absent is null && view.Name == selected.Name;
        var missing = view.Absent == "No record available" && view.Name == "???";
        if (!registered && !missing) return null;
        ActivityEntry[] rows =
        [
            new(1, "selection", "Selected record number", Value: number, Unit: "record"),
            new(2, "selection", "Displayed record name", Text: view.Name),
            new(3, "selection", "Displayed registration", Text: registered ? FieldRecordContract.Registered : FieldRecordContract.Unavailable),
            new(4, "collection", "Displayed registered total", Value: total, Required: maximum, Unit: "records"),
        ];
        return FieldRecordContract.ValidShape(rows) ? rows : null;
    }
}

internal sealed record FieldRecordSelection(int MaxNoteIndex, int CurrentNoteIndex, int CurrentPageIndex);
internal sealed record FieldRecordView(FieldRecordSelection Selection, string Number, string Name, string? Absent, string Total);
internal sealed record FieldRecordDefinition(uint Id, int Number, string Name);
internal sealed record FieldRecordCatalog(object Sheet, object GameData, string Language, FieldRecordDefinition[] Definitions);
internal readonly record struct FieldRecordOwner(nint Player, ulong ContentId, nint Module, nint Manager, nint Agent,
    nint Addon, uint AddonId, FieldRecordSelection Selection);
