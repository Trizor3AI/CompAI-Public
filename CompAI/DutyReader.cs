using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using InstanceSheet = Lumina.Excel.Sheets.InstanceContent;

namespace CompAI;

/// <summary>Framework-thread only; no menu, packet, interaction or disk access.</summary>
internal sealed unsafe class DutyReader(IDataManager data)
{
    private DutyDefinition[]? catalog;

    internal DutySnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            var ui = UIState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0 || ui == null)
                return DutySnapshot.Unavailable("Loaded character duty state is unavailable.");
            var owner = player->ContentId;
            var definitions = GetCatalog();
            var snapshot = Describe(DateTimeOffset.UtcNow, definitions, id =>
            {
                var observed = new DutyObservedState(UIState.IsInstanceContentUnlocked(id), UIState.IsInstanceContentCompleted(id));
                if (observed != new DutyObservedState(UIState.IsInstanceContentUnlocked(id), UIState.IsInstanceContentCompleted(id)))
                    throw new InvalidOperationException("Duty state changed during reading.");
                return observed;
            });
            if (PlayerState.Instance() != player || UIState.Instance() != ui || !player->IsLoaded || player->ContentId != owner)
                return DutySnapshot.Unavailable("Character duty state changed during reading; try again after loading.");
            return snapshot;
        }
        catch (Exception ex)
        {
            return DutySnapshot.Unavailable($"Duty read unavailable ({ex.GetType().Name}).");
        }
    }

    private DutyDefinition[] GetCatalog()
    {
        if (catalog is not null) return catalog;
        var instances = data.GetExcelSheet<InstanceSheet>();
        var definitions = new List<DutyDefinition>();
        foreach (var row in data.GetExcelSheet<ContentFinderCondition>())
        {
            if (row.RowId == 0) continue;
            if (definitions.Count >= DutySnapshot.MaxEntries)
                throw new InvalidOperationException("Duty catalog exceeds the supported bound.");
            var name = row.Name.ExtractText().Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name.Length > DutySnapshot.MaxNameLength || name.Any(char.IsControl)) name = "[duty name unavailable]";
            uint? instanceId = null;
            if (row.Content.Is<InstanceSheet>() && row.Content.RowId > 0 &&
                instances.TryGetRow(row.Content.RowId, out var instance) && instance.ContentFinderCondition.RowId == row.RowId)
                instanceId = instance.RowId;
            definitions.Add(new(row.RowId, instanceId, name));
        }
        if (definitions.Count == 0) throw new InvalidOperationException("Duty catalog is unavailable.");
        return catalog = definitions.OrderBy(row => row.DutyId).ToArray();
    }

    internal static DutySnapshot Describe(DateTimeOffset capturedAt, IEnumerable<DutyDefinition> definitions,
        Func<uint, DutyObservedState> observe)
    {
        var bounded = definitions.Take(DutySnapshot.MaxEntries + 1).ToArray();
        if (bounded.Length == 0 || bounded.Length > DutySnapshot.MaxEntries ||
            bounded.Any(row => row.DutyId == 0 || row.InstanceContentId == 0) ||
            bounded.Select(row => row.DutyId).Distinct().Count() != bounded.Length)
            return DutySnapshot.Unavailable("Duty catalog is invalid or unavailable.");
        var entries = new List<DutyEntry>();
        foreach (var row in bounded)
        {
            if (row.InstanceContentId is not { } instanceId)
            {
                entries.Add(new(row.DutyId, null, row.Name, null, null, "This duty has no validated InstanceContent mapping; its state is unknown."));
                continue;
            }
            var observation = observe(instanceId);
            // PlayerState.IsLoaded does not document independent readiness for every
            // duty flag. Keep native negative results unknown until that is proven.
            entries.Add(new(row.DutyId, instanceId, row.Name, observation.Unlocked ? true : null,
                observation.Completed ? true : null, observation.Unlocked && observation.Completed ? null :
                    "A negative native flag was observed, but negative-state readiness is unverified; null remains unknown."));
        }
        return new(capturedAt, true, entries);
    }
}

internal sealed record DutyDefinition(uint DutyId, uint? InstanceContentId, string Name);
internal readonly record struct DutyObservedState(bool Unlocked, bool Completed);
