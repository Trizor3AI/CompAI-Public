using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NativeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsNote;
using NoteSheet = Lumina.Excel.Sheets.ContentsNote;
using Lumina.Excel.Sheets;

namespace CompAI;

internal sealed unsafe class ChallengeLogReader(IDataManager data, IGameGui gui)
{
    // Current installed ContentsNote source: ClientStructs dd57986b8287e7c6ed9c27198f8c76cb04a81dfe.
    internal const int CompletionBits = 104;
    internal const int CompletionBytes = 13;
    internal const int DisplaySlots = 19;
    private ChallengeCatalog? catalog;

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
            var firstCounters = ReadVisibleCounters(before, current.Definitions, out var firstAddon);
            var secondCounters = ReadVisibleCounters(before, current.Definitions, out var secondAddon);
            var after = Observe();
            if (after is null || !before.SameAs(after) || PlayerState.Instance() != player ||
                !player->IsLoaded || player->ContentId != owner || !CatalogMatchesCurrent(current)) return Unavailable();
            return Describe(DateTimeOffset.UtcNow, current.Definitions, before.Rows, menuPage: before.Tab,
                visibleCounters: StableVisibleCounters(firstAddon, firstCounters, secondAddon, secondCounters));
        }
        catch (Exception ex)
        {
            catalog = null;
            return ProgressionSnapshot.Unavailable("challenge_log", $"Challenge Log read unavailable ({ex.GetType().Name}).");
        }
    }

    internal static ChallengeObservation? Observe()
    {
        var module = AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId(AgentId.ContentsNote);
        var note = NativeNote.Instance();
        if (agent == null || !agent->IsAgentActive() || !agent->IsAddonReady() || !agent->IsAddonShown() ||
            note == null || note->State != NativeNote.ContentsNoteState.Loaded ||
            note->CompletionFlagsBitArray.BitCount != CompletionBits || note->CompletionFlags.Length != CompletionBytes ||
            note->DisplayIds.Length != DisplaySlots || note->DisplayStatuses.Length != DisplaySlots ||
            note->DisplayCount is < 1 or > DisplaySlots || note->SelectedTab > 14) return null;
        var rows = new ChallengeRowObservation[note->DisplayCount];
        for (var index = 0; index < rows.Length; index++)
        {
            var id = note->DisplayIds[index];
            if (id is < 1 or > CompletionBits) return null;
            rows[index] = new((uint)id, note->DisplayStatuses[index], note->IsContentNoteComplete(id));
        }
        return new((nint)agent, (nint)note, note->SelectedTab, Convert.ToHexString(note->CompletionFlags), rows);
    }

    private ChallengeCatalog GetCatalog()
    {
        var gameData = data.GameData; var language = data.Language.ToString();
        var sheet = data.GetExcelSheet<NoteSheet>(); var categories = data.GetExcelSheet<ContentsNoteCategory>();
        if (catalog is { } cached && cached.Matches(sheet.RawSheet, categories.RawSheet, gameData, language)) return cached;
        catalog = null;
        var rows = sheet.Take(CompletionBits + 1).ToArray();
        if (rows.Length != CompletionBits) throw new InvalidOperationException("Challenge catalog bounds are unsupported.");
        var definitions = new List<ChallengeDefinition>();
        foreach (var row in rows)
        {
            var title = row.Name.ExtractText().Trim();
            if (row.RowId == 0 || string.IsNullOrWhiteSpace(title)) continue;
            if (row.RowId > CompletionBits || row.RequiredAmount <= 0 || row.ContentType.RowId == 0 ||
                !categories.TryGetRow(row.ContentType.RowId, out var category))
                throw new InvalidOperationException("Challenge definition links are unavailable.");
            var name = category.Name.ExtractText().Trim() + " — " + title;
            if (!ProgressionSnapshot.ValidText(name, ProgressionSnapshot.MaxNameLength))
                throw new InvalidOperationException("Challenge name is unsupported.");
            definitions.Add(new(row.RowId, name, row.RequiredAmount, title));
        }
        var current = new ChallengeCatalog(sheet.RawSheet, categories.RawSheet, gameData, language, definitions.ToArray());
        if (!CatalogMatchesCurrent(current)) throw new InvalidOperationException("Challenge definitions changed during capture.");
        return catalog = current;
    }

    private bool CatalogMatchesCurrent(ChallengeCatalog current) =>
        current.Matches(data.GetExcelSheet<NoteSheet>().RawSheet, data.GetExcelSheet<ContentsNoteCategory>().RawSheet,
            data.GameData, data.Language.ToString());

    internal static ProgressionSnapshot Describe(DateTimeOffset capturedAt, IEnumerable<ChallengeDefinition> definitions,
        IEnumerable<ChallengeRowObservation> observations, bool visibleAndLoaded = true, int? menuPage = null,
        IReadOnlyList<ChallengeVisibleCounter>? visibleCounters = null)
    {
        if (!visibleAndLoaded || menuPage is not null && !ProgressionSnapshot.ValidMenuPage("challenge_log", menuPage.Value)) return Unavailable();
        var known = definitions.Take(CompletionBits + 1).ToArray();
        var rows = observations.Take(DisplaySlots + 1).ToArray();
        if (known.Length is < 1 or > CompletionBits || rows.Length is < 1 or > DisplaySlots ||
            known.Any(row => row is null || row.Id is < 1 or > CompletionBits || row.Required <= 0 ||
                !ProgressionSnapshot.ValidText(row.Name, ProgressionSnapshot.MaxNameLength)) ||
            known.Select(row => row.Id).Distinct().Count() != known.Length ||
            rows.Any(row => row is null) || rows.Select(row => row.Id).Distinct().Count() != rows.Length) return Unavailable();
        var byId = known.ToDictionary(row => row.Id);
        if (rows.Any(row => !byId.ContainsKey(row.Id))) return Unavailable();
        var entries = rows.Select(row =>
        {
            var definition = byId[row.Id];
            // Explicit consumers corroborate counters for Eureka rows56–65 and Verminion47/66/67.
            // Rows11/12/33/34 additionally require a stable matching visible title+ratio. Other
            // DisplayStatuses remain uninterpreted rather than generalized to every objective.
            // https://github.com/Infiziert90/EurekaTrackerAutoPopper/blob/cf2609f2c6f5a8933d5b10073d5dd26e1d1ddab9/EurekaTrackerAutoPopper/Windows/LogWindow.cs
            // https://github.com/McVaxius/VERMAXION/blob/7d94ab1b7221a382d30fa467a0512345b853b404/VERMAXION/Services/VerminionGameInteraction.cs
            var counter = ProgressionSnapshot.ChallengeCounterSupported(row.Id) ||
                menuPage == VisibleCounterTab(row.Id) && ProgressionSnapshot.ChallengeVisibleCounterSupported(row.Id) &&
                visibleCounters is not null && visibleCounters.Count <= 2 &&
                visibleCounters.Count(value => value.Id == row.Id && value.Title == definition.Title &&
                    value.Required == definition.Required && value.Progress == row.RawStatus) == 1;
            if (counter && (row.RawStatus < 0 || row.RawStatus > definition.Required || row.Completed != (row.RawStatus == definition.Required)))
                return new ProgressionEntry(row.Id, definition.Name, "unknown", UnknownReason:
                    "The displayed counter and completion flag disagree or are unsupported; wait for the menu to finish loading.");
            return new ProgressionEntry(row.Id, definition.Name, row.Completed ? "completed" : "not_observed_complete",
                // A public objective target is known even when this row's partial counter is not.
                // Positive completion establishes the target was reached, not a live raw counter.
                Completed: row.Completed ? true : null,
                Progress: counter ? row.RawStatus : row.Completed ? definition.Required : null,
                Required: definition.Required, UnknownReason: row.Completed ? null : counter ?
                    "No positive completion flag was observed in the loaded menu; reset receipt and negative-state readiness are unverified." :
                    ProgressionSnapshot.ChallengeVisibleCounterSupported(row.Id) ?
                    "The visible objective title and counter could not be verified together. Open its Challenge Log tab and let its rows settle to refresh progress." :
                    "Partial progress is not supported for this objective; required is its public target. No positive completion flag was observed.");
        });
        return new("challenge_log", capturedAt, true, "visible_menu_observation", entries, menuPage: menuPage);
    }

    private static ProgressionSnapshot Unavailable() => ProgressionSnapshot.Unavailable("challenge_log",
        "Open Challenge Log yourself, select the relevant tab and wait for it to load. Stable visible observations are unavailable.");

    private ChallengeVisibleCounter[] ReadVisibleCounters(ChallengeObservation observation, ChallengeDefinition[] definitions, out nint addonAddress)
    {
        addonAddress = 0;
        if (!observation.Rows.Any(row => ProgressionSnapshot.ChallengeVisibleCounterSupported(row.Id) && VisibleCounterTab(row.Id) == observation.Tab)) return [];
        var handle = gui.GetAddonByName("ContentsNote");
        if (handle.IsNull || !handle.IsReady || !handle.IsVisible) return [];
        var addon = (AtkUnitBase*)handle.Address;
        if (addon->NameString != "ContentsNote" || !ChallengeLogDiagnostic.TryCopyVisibleText(&addon->UldManager, out var texts)) return [];
        addonAddress = handle.Address;
        return VerifyVisibleCounters(observation.Tab, definitions, observation.Rows, texts);
    }

    internal static ChallengeVisibleCounter[] StableVisibleCounters(nint firstAddon, ChallengeVisibleCounter[] first,
        nint secondAddon, ChallengeVisibleCounter[] second) =>
        firstAddon != 0 && firstAddon == secondAddon && first.SequenceEqual(second) ? first : [];

    internal static int? VisibleCounterTab(uint id) => id switch { 11 or 12 => 4, 33 or 34 => 9, _ => null };

    // User-supplied ContentsNote diagnostics on 2026-10-23 corroborated two leve and two retainer rows.
    // Bind values by exact localized sheet title and same card component; never by display order.
    internal static ChallengeVisibleCounter[] VerifyVisibleCounters(byte tab, IReadOnlyList<ChallengeDefinition> definitions,
        IReadOnlyList<ChallengeRowObservation> rows, IReadOnlyList<ChallengeDiagnosticText> texts)
    {
        if (tab is not (4 or 9) || definitions.Count > CompletionBits || rows.Count > DisplaySlots ||
            texts.Count > ChallengeLogDiagnostic.MaxTextNodes) return [];
        var verified = new List<ChallengeVisibleCounter>();
        foreach (var row in rows.Where(row => ProgressionSnapshot.ChallengeVisibleCounterSupported(row.Id) && VisibleCounterTab(row.Id) == tab))
        {
            var matches = definitions.Where(definition => definition.Id == row.Id).ToArray();
            if (matches.Length != 1 || rows.Count(value => value.Id == row.Id) != 1) continue;
            var definition = matches[0];
            if (!ProgressionSnapshot.ValidText(definition.Title, ProgressionSnapshot.MaxNameLength) || definition.Required <= 0 ||
                definitions.Count(value => value.Title == definition.Title) != 1) continue;
            var titles = texts.Where(text => text.NodeId == 3 && text.ParentNodeId == 2 && text.Text == definition.Title &&
                text.ComponentPath.Length == 2 && text.ComponentPath[0] == 8 &&
                text.ComponentPath[1] is 5 or 51001).ToArray();
            if (titles.Length != 1) continue;
            var title = titles[0];
            var counters = texts.Where(text => text.NodeId == 11 && text.ParentNodeId == 7 &&
                text.ComponentPath.SequenceEqual(title.ComponentPath)).ToArray();
            if (counters.Length != 1) continue;
            var parts = counters[0].Text.Split('/');
            if (parts.Length != 2 || parts.Any(part => part.Length is < 1 or > 10 || !part.All(char.IsAsciiDigit)) ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var progress) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var required) ||
                required != definition.Required || progress > required || progress != row.RawStatus ||
                row.Completed != (progress == required)) continue;
            verified.Add(new(row.Id, definition.Title!, progress, required, title.ComponentPath[1]));
        }
        return verified.ToArray();
    }
}

internal sealed record ChallengeDefinition(uint Id, string Name, int Required, string? Title = null);
internal sealed record ChallengeVisibleCounter(uint Id, string Title, int Progress, int Required, uint ComponentId);
internal sealed record ChallengeRowObservation(uint Id, int RawStatus, bool Completed);
internal sealed record ChallengeObservation(nint Agent, nint Note, byte Tab, string Bitmap, ChallengeRowObservation[] Rows)
{
    internal bool SameAs(ChallengeObservation other) => Agent == other.Agent && Note == other.Note &&
        Tab == other.Tab && Bitmap == other.Bitmap && Rows.SequenceEqual(other.Rows);
}
internal sealed record ChallengeCatalog(object Sheet, object Categories, object GameData, string Language, ChallengeDefinition[] Definitions)
{
    internal bool Matches(object sheet, object categories, object gameData, string language) =>
        ReferenceEquals(Sheet, sheet) && ReferenceEquals(Categories, categories) && ReferenceEquals(GameData, gameData) && Language == language;
}
