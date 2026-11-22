using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread only. Reads already loaded state; never opens menus or requests game data.</summary>
internal sealed unsafe class ProgressionReader(IDataManager data, IGameGui gui)
{
    internal const int AetherZoneBitCount = 31;
    internal const int AetherCurrentBitCount = 448;
    internal const int AetherCurrentByteCount = 56;
    internal const int AetherZoneByteCount = 4;
    internal const int AetherCurrentsPerZone = 15;
    internal const int FateTabCount = 3;
    internal const int FateZoneCount = 6;
    internal const int TrustGroupCount = 80;
    internal const int TrustMembersPerGroup = 12;
    private readonly AchievementReader achievements = new(data);
    private readonly GatheringLogReader gatheringLog = new(data);
    private readonly AchievementProgressReader achievementProgress = new(data);
    private readonly FishingLogReader fishingLog = new(data);
    private readonly VariantNotebookReader variantNotebook = new(data);
    private readonly SightseeingLogReader sightseeingLog = new(data);
    private readonly HuntingLogReader huntingLog = new(data);
    private readonly ChallengeLogReader challengeLog = new(data, gui);
    private readonly BattleChocoboReader battleChocobo = new(data);
    private readonly PvpReader pvp = new(data, gui);
    private AetherCurrentCatalog? aetherCatalog;
    private Dictionary<uint, FateZoneDefinition>? fateCatalog;

    internal ProgressionSnapshot Capture(string category)
    {
        ProgressionSnapshot.ValidateCategory(category);
        if (category == "battle_chocobo") return this.battleChocobo.Capture();
        if (category == "pvp") return this.pvp.Capture();
        if (category == "achievements") return this.achievements.Capture();
        if (category == "gathering_log") return this.gatheringLog.Capture();
        if (category == "achievement_progress") return this.achievementProgress.Capture();
        if (category is "fishing_log" or "spearfishing_log") return this.fishingLog.Capture(category);
        if (category == "variant_notebook") return this.variantNotebook.Capture();
        if (category == "sightseeing_log") return this.sightseeingLog.Capture();
        if (category == "hunting_log") return this.huntingLog.Capture();
        if (category == "challenge_log") return this.challengeLog.Capture();
        try
        {
            var player = PlayerState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0)
                return ProgressionSnapshot.Unavailable(category, "Loaded character progression state is unavailable.");
            var owner = player->ContentId;
            var snapshot = category switch
            {
                "aether_currents" => CaptureAetherCurrents(player),
                "shared_fates" => CaptureSharedFates(),
                "trust" => CaptureTrust(),
                _ => throw new ArgumentException("Unsupported progression category.", nameof(category)),
            };
            if (PlayerState.Instance() != player || !player->IsLoaded || player->ContentId != owner)
                return ProgressionSnapshot.Unavailable(category, "Character progression state changed during reading; try again after loading.");
            return snapshot;
        }
        catch (Exception ex)
        {
            if (category == "aether_currents") this.aetherCatalog = null;
            return ProgressionSnapshot.Unavailable(category, $"Progression read unavailable ({ex.GetType().Name}).");
        }
    }

    private ProgressionSnapshot CaptureAetherCurrents(PlayerState* player)
    {
        var owner = player->ContentId;
        AetherCurrentCatalog? catalog = null;
        var snapshot = CaptureAetherCurrents(DateTimeOffset.UtcNow, () =>
        {
            var observed = ObserveAetherCurrents();
            return observed is { } value && value.Player == (nint)player && value.ContentId == owner ? observed : null;
        }, () =>
        {
            catalog = GetAetherCatalog();
            return catalog.Definitions;
        }, id => player->IsAetherCurrentZoneComplete(id), id => player->IsAetherCurrentUnlocked(id),
            () => catalog is not null && AetherCatalogMatches(catalog));
        if (!snapshot.Available) this.aetherCatalog = null;
        return snapshot;
    }

    private static AetherCurrentObservation? ObserveAetherCurrents()
    {
        var player = PlayerState.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0 ||
            player->UnlockedAetherCurrentsBitArray.BitCount != AetherCurrentBitCount ||
            player->UnlockedAetherCurrentCompFlgSetsBitArray.BitCount != AetherZoneBitCount ||
            player->UnlockedAetherCurrents.Length != AetherCurrentByteCount ||
            player->UnlockedAetherCurrentCompFlgSets.Length != AetherZoneByteCount) return null;
        // Equality fingerprints only: current flags are interpreted by the documented
        // RowId API, never by indexing these bytes or inventing an EventId conversion.
        return new((nint)player, player->ContentId,
            Convert.ToHexString(player->UnlockedAetherCurrents),
            Convert.ToHexString(player->UnlockedAetherCurrentCompFlgSets));
    }

    internal static ProgressionSnapshot CaptureAetherCurrents(DateTimeOffset capturedAt,
        Func<AetherCurrentObservation?> observe, Func<AetherZoneDefinition[]> getCatalog,
        Func<uint, bool> observeZone, Func<uint, bool> observeCurrent, Func<bool> catalogMatches)
    {
        var before = observe();
        if (before is not { IsReady: true }) return AetherUnavailable();
        var definitions = getCatalog();
        // Catalog access can be slow. Recheck the native owner before using the
        // retained player pointer in either pass, not just after dereferencing it.
        if (observe() != before || !catalogMatches()) return AetherUnavailable();
        var first = DescribeAetherCurrents(capturedAt, definitions, observeZone, observeCurrent);
        if (!first.Available || observe() != before || !catalogMatches()) return AetherUnavailable();
        var second = DescribeAetherCurrents(capturedAt, definitions, observeZone, observeCurrent);
        return second.Available && observe() == before && catalogMatches() && first.Entries.SequenceEqual(second.Entries)
            ? first : AetherUnavailable();
    }

    private static ProgressionSnapshot AetherUnavailable() => ProgressionSnapshot.Unavailable("aether_currents",
        "Loaded aether-current state or public definitions are unavailable, inconsistent or changed during reading.");

    private AetherCurrentCatalog GetAetherCatalog()
    {
        if (this.aetherCatalog is { } cached && AetherCatalogMatches(cached)) return cached;
        this.aetherCatalog = null;
        var zones = data.GetExcelSheet<AetherCurrentCompFlgSet>();
        var currents = data.GetExcelSheet<AetherCurrent>();
        var territories = data.GetExcelSheet<TerritoryType>();
        var places = data.GetExcelSheet<PlaceName>();
        var result = new AetherCurrentCatalog(zones.RawSheet, currents.RawSheet, territories.RawSheet,
            places.RawSheet, data.GameData, data.Language.ToString(),
            BuildAetherCatalog(zones, currents, territories, places));
        if (!AetherCatalogMatches(result)) throw new InvalidOperationException("Aether-current definitions changed while loading.");
        return this.aetherCatalog = result;
    }

    private bool AetherCatalogMatches(AetherCurrentCatalog catalog) => catalog.Matches(
        data.GetExcelSheet<AetherCurrentCompFlgSet>().RawSheet, data.GetExcelSheet<AetherCurrent>().RawSheet,
        data.GetExcelSheet<TerritoryType>().RawSheet, data.GetExcelSheet<PlaceName>().RawSheet,
        data.GameData, data.Language.ToString());

    internal static AetherZoneDefinition[] BuildAetherCatalog(ExcelSheet<AetherCurrentCompFlgSet> zones,
        ExcelSheet<AetherCurrent> currents, ExcelSheet<TerritoryType> territories, ExcelSheet<PlaceName> places)
    {
        var rows = zones.Take(ProgressionSnapshot.MaxEntries + 1).ToArray();
        if (rows.Length > ProgressionSnapshot.MaxEntries) throw new InvalidOperationException("Aether-current catalog exceeds its bound.");
        var definitions = new List<AetherZoneDefinition>();
        foreach (var row in rows)
        {
            var references = row.AetherCurrents.Take(AetherCurrentsPerZone + 1)
                .Select(reference => new AetherCurrentReference(reference.RowId,
                    reference.RowId == 0 || currents.TryGetRow(reference.RowId, out _))).ToArray();
            if (row.RowId == 0 || row.Territory.RowId == 0)
            {
                if (references.Any(reference => reference.Id != 0))
                    throw new InvalidOperationException("Aether-current placeholder has nonempty references.");
                continue;
            }
            if (!territories.TryGetRow(row.Territory.RowId, out var territory) || territory.PlaceName.RowId == 0 ||
                !places.TryGetRow(territory.PlaceName.RowId, out var place))
                throw new InvalidOperationException("Aether-current territory or name reference is invalid.");
            definitions.Add(DefineAetherZone(row.RowId, place.Name.ExtractText().Trim(), references));
        }
        return definitions.OrderBy(row => row.Id).ToArray();
    }

    internal static AetherZoneDefinition DefineAetherZone(uint id, string name, IEnumerable<AetherCurrentReference> references)
    {
        var slots = references.Take(AetherCurrentsPerZone + 1).ToArray();
        var ids = slots.Where(reference => reference.Id != 0).Select(reference => reference.Id).ToArray();
        if (id == 0 || !ProgressionSnapshot.ValidText(name, ProgressionSnapshot.MaxNameLength) ||
            slots.Length > AetherCurrentsPerZone || ids.Length == 0 ||
            slots.Any(reference => reference.Id != 0 && !reference.Exists) || ids.Distinct().Count() != ids.Length)
            throw new InvalidOperationException("Aether-current zone membership is invalid or unsupported.");
        return new(id, name, ids);
    }

    internal static ProgressionSnapshot DescribeAetherCurrents(DateTimeOffset capturedAt,
        IEnumerable<AetherZoneDefinition> definitions, Func<uint, bool> observeZone,
        Func<uint, bool> observeCurrent, bool loaded = true)
    {
        if (!loaded) return ProgressionSnapshot.Unavailable("aether_currents", "Loaded aether-current state is unavailable.");
        var rows = definitions.Take(ProgressionSnapshot.MaxEntries + 1).ToArray();
        if (rows.Length == 0 || rows.Length > ProgressionSnapshot.MaxEntries ||
            rows.Any(row => row is null || row.Id == 0 || !ProgressionSnapshot.ValidText(row.Name, ProgressionSnapshot.MaxNameLength) ||
                (row.Id <= AetherZoneBitCount && (row.CurrentIds is null || row.CurrentIds.Length is < 1 or > AetherCurrentsPerZone ||
                    row.CurrentIds.Any(id => id == 0) || row.CurrentIds.Distinct().Count() != row.CurrentIds.Length))) ||
            rows.Select(row => row.Id).Distinct().Count() != rows.Length)
            return ProgressionSnapshot.Unavailable("aether_currents", "Aether-current catalog is invalid or unavailable.");
        var entries = new List<ProgressionEntry>();
        foreach (var row in rows)
        {
            // The documented API accepts the sheet RowId (1..31), not a raw bitmap index.
            if (row.Id > AetherZoneBitCount)
            {
                entries.Add(new(row.Id, row.Name, "unknown",
                    UnknownReason: "This zone is outside the supported native mapping; completion and current counts are unknown."));
                continue;
            }
            var complete = observeZone(row.Id);
            var currentIds = row.CurrentIds!;
            var positive = currentIds.Count(observeCurrent);
            if (complete && positive != currentIds.Length) return AetherUnavailable();
            // All-positive member flags do not promote an unconfirmed zone flag.
            // False members are unconfirmed, so Progress is an observed lower bound.
            entries.Add(new(row.Id, row.Name, complete ? "completed" : "not_observed_complete",
                Completed: complete ? true : null, Progress: positive, Required: currentIds.Length,
                UnknownReason: complete ? null : "Zone completion is unconfirmed. Progress counts positive current discoveries only; other currents are unknown, not missing."));
        }
        return new("aether_currents", capturedAt, true, "positive_observations_only", entries);
    }

    private ProgressionSnapshot CaptureSharedFates()
    {
        var agent = AgentFateProgress.Instance();
        if (!FateMenuReady(agent))
            return ProgressionSnapshot.Unavailable("shared_fates",
                "Open Shared FATE yourself, select the region tab and wait for its ranks to appear, then ask again.");
        var tab = agent->TabIndex;
        var observed = ReadFateTab(agent, tab);
        if (!FateMenuReady(agent) || agent != AgentFateProgress.Instance() || agent->TabIndex != tab ||
            !observed.SequenceEqual(ReadFateTab(agent, tab)))
            return ProgressionSnapshot.Unavailable("shared_fates", "Shared FATE menu state changed during reading.");
        return DescribeSharedFates(DateTimeOffset.UtcNow, observed, menuPage: tab);
    }

    private static bool FateMenuReady(AgentFateProgress* agent) =>
        agent != null && agent->IsAgentActive() && agent->IsAddonReady() && agent->IsAddonShown() &&
        agent->Tabs.Length == FateTabCount && agent->TabIndex < FateTabCount;

    private FateProgressObservation[] ReadFateTab(AgentFateProgress* agent, byte tabIndex)
    {
        if (agent->TabIndex != tabIndex || tabIndex >= FateTabCount)
            throw new InvalidOperationException("Shared FATE tab changed.");
        ref var tab = ref agent->Tabs[tabIndex];
        if (tab.TabIndex != tabIndex || tab.Zones.Length != FateZoneCount)
            throw new InvalidOperationException("Shared FATE tab bounds are unavailable.");
        var definitions = GetFateCatalog();
        var result = new List<FateProgressObservation>();
        for (var index = 0; index < FateZoneCount; index++)
        {
            ref var zone = ref tab.Zones[index];
            if (!definitions.TryGetValue(zone.TerritoryTypeId, out var definition))
                throw new InvalidOperationException("Shared FATE zone is outside the supported catalog.");
            result.Add(ReadFateZone((FateProgressZone*)Unsafe.AsPointer(ref zone), definition));
        }
        return result.ToArray();
    }

    private Dictionary<uint, FateZoneDefinition> GetFateCatalog()
    {
        if (this.fateCatalog is not null) return this.fateCatalog;
        var rows = data.GetExcelSheet<FateProgressUI>().Take(256).ToArray();
        if (rows.Length >= 256) throw new InvalidOperationException("Shared FATE catalog exceeds the supported bound.");
        return this.fateCatalog = rows.Where(row => row.Location.RowId != 0 && row.Location.IsValid &&
                row.Location.Value.PlaceName.IsValid)
            .Select(row => DefineFateZone(row.Location.RowId, row.Location.Value.PlaceName.Value.Name.ExtractText().Trim(),
                row.ReqFatesToRank2, row.ReqFatesToRank3, row.ReqFatesToRank4))
            .ToDictionary(row => row.Id);
    }

    internal static FateZoneDefinition DefineFateZone(uint id, string name, byte rank2, byte rank3, byte rank4)
    {
        if (id == 0 || !ProgressionSnapshot.ValidText(name, ProgressionSnapshot.MaxNameLength) ||
            rank2 == 0 || rank3 == 0)
            throw new InvalidOperationException("Shared FATE rank definitions are unavailable or unsupported.");
        // FateProgressUI defines three ranks when the fourth-rank threshold is zero.
        return new(id, name, rank4 == 0 ? 3 : 4);
    }

    internal static FateProgressObservation ReadFateZone(FateProgressZone* zone, FateZoneDefinition definition)
    {
        // Native MaxRank has been observed as zero with populated ranks. It is not a readiness flag.
        // Validate against the public FateProgressUI thresholds instead, without inventing personal progress.
        if (zone == null || definition.Id == 0 || zone->TerritoryTypeId != definition.Id || definition.MaxRank is < 3 or > 4 ||
            !ProgressionSnapshot.ValidText(definition.Name, ProgressionSnapshot.MaxNameLength) ||
            zone->CurrentRank < 1 || zone->CurrentRank > definition.MaxRank ||
            zone->FateProgress > zone->NeededFates ||
            !ProgressionSnapshot.ValidText(ReadText(&zone->RankText), 128))
            throw new InvalidOperationException("Shared FATE zone has not populated valid ranks and progress.");
        var progressText = ReadText(&zone->ProgressText);
        // A capped rank need not display a next-rank progress string. Empty text below the cap is still unavailable.
        if (!(progressText.Length == 0 && zone->CurrentRank == definition.MaxRank) &&
            !ProgressionSnapshot.ValidText(progressText, 128))
            throw new InvalidOperationException("Shared FATE progress text has not populated.");
        return new(zone->TerritoryTypeId, definition.Name, zone->CurrentRank, zone->FateProgress, zone->NeededFates);
    }

    internal static ProgressionSnapshot DescribeSharedFates(DateTimeOffset capturedAt,
        IEnumerable<FateProgressObservation> observations, bool visibleReady = true, int? menuPage = null)
    {
        if (!visibleReady) return ProgressionSnapshot.Unavailable("shared_fates", "A loaded visible Shared FATE tab is required.");
        var rows = observations.Take(FateZoneCount + 1).ToArray();
        if (rows.Length == 0 || rows.Length > FateZoneCount || rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            rows.Any(row => row.Id == 0 || !ProgressionSnapshot.ValidText(row.Name, ProgressionSnapshot.MaxNameLength) ||
                row.Rank is < 1 or > 10 || row.Progress < 0 || row.Progress > ushort.MaxValue ||
                row.Required < 0 || row.Required > ushort.MaxValue || row.Progress > row.Required))
            return ProgressionSnapshot.Unavailable("shared_fates", "Shared FATE ranks or progress are unpopulated or invalid.");
        return new("shared_fates", capturedAt, true, "visible_menu_observation",
            rows.Select(row => new ProgressionEntry(row.Id, row.Name, "observed",
                Rank: row.Rank, Progress: row.Progress, Required: row.Required)), menuPage: menuPage);
    }

    private static ProgressionSnapshot CaptureTrust()
    {
        var agent = AgentDawn.Instance();
        if (!TrustMenuReady(agent))
            return ProgressionSnapshot.Unavailable("trust",
                "Open Trust yourself, select the members you want to inspect and wait for their levels to appear, then ask again.");
        var nativeData = agent->Data;
        var group = nativeData->MemberData.CurrentMembersIndex;
        var observed = ReadTrustMembers(agent, group);
        if (!TrustMenuReady(agent) || agent != AgentDawn.Instance() || agent->Data != nativeData ||
            nativeData->MemberData.CurrentMembersIndex != group || !observed.SequenceEqual(ReadTrustMembers(agent, group)))
            return ProgressionSnapshot.Unavailable("trust", "Trust menu state changed during reading.");
        return DescribeTrust(DateTimeOffset.UtcNow, observed, menuPage: group);
    }

    private static bool TrustMenuReady(AgentDawn* agent) =>
        agent != null && agent->IsAgentActive() && agent->IsAddonReady() && agent->IsAddonShown() &&
        agent->Data != null;

    private static TrustProgressObservation[] ReadTrustMembers(AgentDawn* agent, byte group)
    {
        var members = &agent->Data->MemberData;
        if (members->Members.Length != TrustGroupCount * TrustMembersPerGroup ||
            members->MemberCounts.Length != TrustGroupCount || group >= TrustGroupCount ||
            members->CurrentMembersIndex != group)
            throw new InvalidOperationException("Trust member group bounds are unavailable.");
        var count = members->GetMemberCount(group);
        var start = members->GetMembers(group);
        var first = (AgentDawnInterface.DawnMemberEntry*)Unsafe.AsPointer(ref members->Members[0]);
        // Verify the read-only helper points inside the declared fixed array before dereferencing it.
        if (count is < 1 or > TrustMembersPerGroup || start == null || start < first ||
            start > first + members->Members.Length - count ||
            ((nuint)start - (nuint)first) % (nuint)sizeof(AgentDawnInterface.DawnMemberEntry) != 0)
            throw new InvalidOperationException("Trust members have not populated valid data.");
        var result = new List<TrustProgressObservation>();
        for (var index = 0; index < count; index++)
        {
            var member = start + index;
            result.Add(new(member->MemberId, ReadText(&member->Name), member->Level, member->Exp, member->MaxExp));
        }
        return result.ToArray();
    }

    internal static ProgressionSnapshot DescribeTrust(DateTimeOffset capturedAt,
        IEnumerable<TrustProgressObservation> observations, bool visibleReady = true, int? menuPage = null)
    {
        if (!visibleReady) return ProgressionSnapshot.Unavailable("trust", "A loaded visible Trust member group is required.");
        var rows = observations.Take(TrustMembersPerGroup + 1).ToArray();
        if (rows.Length == 0 || rows.Length > TrustMembersPerGroup || rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            rows.Any(row => row.Id == 0 || !ProgressionSnapshot.ValidText(row.Name, ProgressionSnapshot.MaxNameLength) ||
                row.Level is < 1 or > 200 || row.Experience > row.ExperienceRequired))
            return ProgressionSnapshot.Unavailable("trust", "Trust member levels or experience are unpopulated or invalid.");
        return new("trust", capturedAt, true, "visible_menu_observation",
            rows.Select(row => new ProgressionEntry(row.Id, row.Name, "observed",
                Level: row.Level, Experience: row.Experience, ExperienceRequired: row.ExperienceRequired)), menuPage: menuPage);
    }

    private static string ReadText(Utf8String* source)
    {
        const int maxBytes = 512;
        if (source == null || source->BufUsed < 0 || source->BufUsed > maxBytes + 1 ||
            source->BufSize < source->BufUsed || source->StringLength < 0 ||
            source->StringLength > maxBytes || source->StringLength > source->BufUsed)
            throw new InvalidOperationException("Progression menu text exceeds the reader bounds.");
        if (source->BufUsed <= 1) return "";
        if ((byte*)source->StringPtr == null) throw new InvalidOperationException("Progression menu text is unavailable.");
        return SeString.Parse(source->AsSpan()).TextValue.Trim();
    }
}

internal sealed record AetherZoneDefinition(uint Id, string Name, uint[]? CurrentIds = null);
internal readonly record struct AetherCurrentReference(uint Id, bool Exists);
internal readonly record struct AetherCurrentObservation(nint Player, ulong ContentId, string Currents, string Zones)
{
    internal bool IsReady => Player != 0 && ContentId != 0 && Currents?.Length == ProgressionReader.AetherCurrentByteCount * 2 &&
        Zones?.Length == ProgressionReader.AetherZoneByteCount * 2;
}
internal sealed record AetherCurrentCatalog(object Zones, object Currents, object Territories, object Places,
    object GameData, string Language, AetherZoneDefinition[] Definitions)
{
    internal bool Matches(object zones, object currents, object territories, object places, object gameData, string language) =>
        ReferenceEquals(Zones, zones) && ReferenceEquals(Currents, currents) && ReferenceEquals(Territories, territories) &&
        ReferenceEquals(Places, places) && ReferenceEquals(GameData, gameData) && Language == language;
}
internal sealed record FateZoneDefinition(uint Id, string Name, int MaxRank);
internal sealed record FateProgressObservation(uint Id, string Name, int Rank, int Progress, int Required);
internal sealed record TrustProgressObservation(uint Id, string Name, int Level, uint Experience, uint ExperienceRequired);
