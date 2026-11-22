using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace CompAI;

internal sealed unsafe class HuntingLogReader(IDataManager data)
{
    // Current installed typed bounds/ID helper: ClientStructs dd57986b8287e7c6ed9c27198f8c76cb04a81dfe.
    internal const int BookCount = 12;
    internal const int TasksPerRank = 10;
    internal const int TargetsPerTask = 4;
    internal const int CatalogRows = 600;
    private HuntingCatalog? catalog;

    internal ProgressionSnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0) return Unavailable();
            var owner = player->ContentId;
            var before = Observe();
            if (before is null) return Unavailable();
            var current = GetCatalog();
            var snapshot = Describe(DateTimeOffset.UtcNow, current.Definitions, before);
            var after = Observe();
            if (after is null || !before.SameAs(after) || PlayerState.Instance() != player ||
                !player->IsLoaded || player->ContentId != owner || !CatalogMatchesCurrent(current)) return Unavailable();
            return snapshot;
        }
        catch (Exception ex)
        {
            catalog = null;
            return ProgressionSnapshot.Unavailable("hunting_log", $"Hunting Log read unavailable ({ex.GetType().Name}).");
        }
    }

    internal static int BookIndex(uint baseId, byte classId) => baseId switch
    {
        10000 => classId switch { >= 1 and <= 7 => classId - 1, 26 => 7, 29 => 11, _ => -1 },
        1000000 => classId is >= 1 and <= 3 ? classId + 7 : -1,
        _ => -1,
    };

    private static HuntingObservation? Observe()
    {
        var agent = AgentMonsterNote.Instance(); var manager = MonsterNoteManager.Instance();
        if (agent == null || !agent->IsAgentActive() || !agent->IsAddonReady() || !agent->IsAddonShown() || agent->IsLocked ||
            agent->Rank >= 5 || manager == null || manager->RankData.Length != BookCount) return null;
        // Explicit Hunty mapping; do not treat a Grand Company ClassId as a class/job.
        // https://github.com/Infiziert90/Hunty/blob/b3c9125ddc70056df72fda144ce5be2f9da82843/Hunty/Data/Data.cs
        var slot = BookIndex(agent->BaseId, agent->ClassId);
        if (slot < 0) return null;
        ref var book = ref manager->RankData[slot];
        if (book.Index != slot || book.Rank is < 0 or > 5 || book.RankData.Length != TasksPerRank) return null;
        var ids = new uint[TasksPerRank]; var counts = new byte[TasksPerRank * TargetsPerTask];
        for (var index = 0; index < TasksPerRank; index++)
        {
            if (book.RankData[index].Counts.Length != TargetsPerTask) return null;
            ids[index] = agent->GetMonsterNoteIdForIndex(index);
            book.RankData[index].Counts.CopyTo(counts.AsSpan(index * TargetsPerTask, TargetsPerTask));
        }
        return new((nint)agent, (nint)manager, agent->BaseId, agent->ClassId, agent->ClassIndex,
            agent->Rank, agent->Filter, slot, book.Rank, ids, counts);
    }

    private HuntingCatalog GetCatalog()
    {
        var gameData = data.GameData; var language = data.Language.ToString();
        var sheet = data.GetExcelSheet<MonsterNote>(); var targets = data.GetExcelSheet<MonsterNoteTarget>();
        var names = data.GetExcelSheet<BNpcName>();
        if (catalog is { } cached && cached.Matches(sheet.RawSheet, targets.RawSheet, names.RawSheet, gameData, language)) return cached;
        catalog = null;
        var rows = sheet.Take(CatalogRows + 1).ToArray();
        if (rows.Length != CatalogRows) throw new InvalidOperationException("Hunting catalog bounds are unsupported.");
        var definitions = new List<HuntingDefinition>();
        foreach (var row in rows)
        {
            var title = row.Name.ExtractText().Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (row.MonsterNoteTarget.Count != TargetsPerTask || row.Count.Count != TargetsPerTask)
                throw new InvalidOperationException("Hunting target bounds are unsupported.");
            var targetNames = new List<string>();
            for (var index = 0; index < TargetsPerTask; index++)
            {
                var id = row.MonsterNoteTarget[index].RowId;
                if (id == 0 && row.Count[index] == 0) continue;
                if (id == 0 || row.Count[index] == 0 || !targets.TryGetRow(id, out var target) ||
                    !names.TryGetRow(target.BNpcName.RowId, out var name))
                    throw new InvalidOperationException("Hunting target identity is unavailable.");
                var text = name.Singular.ExtractText().Trim();
                if (!ProgressionSnapshot.ValidText(text, ProgressionSnapshot.MaxNameLength))
                    throw new InvalidOperationException("Hunting target name is unsupported.");
                targetNames.Add(text);
            }
            if (targetNames.Count == 0) continue;
            var combined = title + " — " + string.Join(", ", targetNames);
            var display = ProgressionSnapshot.ValidText(combined, ProgressionSnapshot.MaxNameLength) ? combined : title;
            if (!ProgressionSnapshot.ValidText(display, ProgressionSnapshot.MaxNameLength))
                throw new InvalidOperationException("Hunting task name is unsupported.");
            definitions.Add(new(row.RowId, display, row.Count.ToArray()));
        }
        var current = new HuntingCatalog(sheet.RawSheet, targets.RawSheet, names.RawSheet, gameData, language, definitions.ToArray());
        if (!CatalogMatchesCurrent(current)) throw new InvalidOperationException("Hunting definitions changed during capture.");
        return catalog = current;
    }

    private bool CatalogMatchesCurrent(HuntingCatalog current) => current.Matches(data.GetExcelSheet<MonsterNote>().RawSheet,
        data.GetExcelSheet<MonsterNoteTarget>().RawSheet, data.GetExcelSheet<BNpcName>().RawSheet, data.GameData, data.Language.ToString());

    internal static ProgressionSnapshot Describe(DateTimeOffset capturedAt, IEnumerable<HuntingDefinition> definitions,
        HuntingObservation observation, bool visibleAndReady = true)
    {
        if (!visibleAndReady || !observation.Valid()) return Unavailable();
        var known = definitions.Take(CatalogRows + 1).ToArray();
        if (known.Length is < 1 or > CatalogRows || known.Any(row => row is null || row.Id == 0 ||
                !ProgressionSnapshot.ValidText(row.Name, ProgressionSnapshot.MaxNameLength) || row.Required is null ||
                row.Required.Length != TargetsPerTask || row.Required.All(count => count == 0)) ||
            known.Select(row => row.Id).Distinct().Count() != known.Length) return Unavailable();
        var byId = known.ToDictionary(row => row.Id);
        if (observation.Ids.Any(id => !byId.ContainsKey(id))) return Unavailable();
        // The public catalog has five ranks for classes and three for Grand Companies.
        // Bound native rank advancement by this book's actual named tasks, not all five possible ranks.
        var bookBase = observation.ClassId * observation.BaseId;
        var bookRows = known.Where(row => row.Id > bookBase && row.Id - bookBase <= 50).ToArray();
        var rankCount = bookRows.Max(row => (row.Id - bookBase - 1) / TasksPerRank) + 1;
        if (observation.ProgressRank > rankCount || observation.SelectedRank >= rankCount) return Unavailable();
        var entries = new List<ProgressionEntry>();
        for (var index = 0; index < TasksPerRank; index++)
        {
            var definition = byId[observation.Ids[index]];
            if (observation.ProgressRank < observation.SelectedRank)
            {
                entries.Add(new(definition.Id, definition.Name, "unknown", UnknownReason:
                    "This selected rank has no corresponding loaded counters; future progress is unknown, not zero."));
                continue;
            }
            if (observation.ProgressRank > observation.SelectedRank)
            {
                // Advancement positively establishes completion. Native counters now
                // belong to a later rank (or are reset at the end of the book), so
                // report the completed public requirement, not those unrelated bytes.
                var completedTotal = definition.Required.Sum(value => (int)value);
                entries.Add(new(definition.Id, definition.Name, "completed", Completed: true,
                    Rank: observation.SelectedRank + 1, Progress: completedTotal, Required: completedTotal));
                continue;
            }
            var counts = observation.Counts.AsSpan(index * TargetsPerTask, TargetsPerTask);
            var progress = 0; var required = 0; var valid = true;
            for (var target = 0; target < TargetsPerTask; target++)
            {
                valid &= counts[target] <= definition.Required[target];
                progress += counts[target]; required += definition.Required[target];
            }
            if (!valid)
            {
                entries.Add(new(definition.Id, definition.Name, "unknown", UnknownReason:
                    "The loaded target counters do not match this public hunting task; wait for the menu to settle."));
                continue;
            }
            var complete = progress == required;
            entries.Add(new(definition.Id, definition.Name, complete ? "completed" : "not_observed_complete",
                Completed: complete ? true : null, Rank: observation.SelectedRank + 1, Progress: progress, Required: required,
                UnknownReason: complete ? null : "No completion was established; observed counters do not establish current server freshness or negative-state readiness."));
        }
        return new("hunting_log", capturedAt, true, "visible_menu_observation", entries,
            menuPage: observation.BookIndex * 5 + observation.SelectedRank);
    }

    private static ProgressionSnapshot Unavailable() => ProgressionSnapshot.Unavailable("hunting_log",
        "Open Hunting Log yourself, select a book and rank and wait for its targets to populate. Stable matching observations are unavailable.");
}

internal sealed record HuntingDefinition(uint Id, string Name, byte[] Required);
internal sealed record HuntingObservation(nint Agent, nint Manager, uint BaseId, byte ClassId, byte ClassIndex,
    byte SelectedRank, byte Filter, int BookIndex, int ProgressRank, uint[] Ids, byte[] Counts)
{
    internal bool Valid() => HuntingLogReader.BookIndex(BaseId, ClassId) == BookIndex && BookIndex is >= 0 and < 12 &&
        SelectedRank < 5 && ProgressRank is >= 0 and <= 5 && Ids.Length == 10 && Counts.Length == 40 &&
        Ids.Select((id, index) => id == ClassId * BaseId + SelectedRank * 10 + index + 1).All(value => value);
    internal bool SameAs(HuntingObservation other) => Agent == other.Agent && Manager == other.Manager && BaseId == other.BaseId &&
        ClassId == other.ClassId && ClassIndex == other.ClassIndex && SelectedRank == other.SelectedRank && Filter == other.Filter &&
        BookIndex == other.BookIndex && ProgressRank == other.ProgressRank && Ids.SequenceEqual(other.Ids) && Counts.SequenceEqual(other.Counts);
}
internal sealed record HuntingCatalog(object Sheet, object Targets, object Names, object GameData, string Language, HuntingDefinition[] Definitions)
{
    internal bool Matches(object sheet, object targets, object names, object gameData, string language) =>
        ReferenceEquals(Sheet, sheet) && ReferenceEquals(Targets, targets) && ReferenceEquals(Names, names) &&
        ReferenceEquals(GameData, gameData) && Language == language;
}
