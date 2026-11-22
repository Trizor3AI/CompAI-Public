using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace CompAI;

/// <summary>Copies bounded company data from the ready main company menu. Never reads the roster or chat.</summary>
internal sealed unsafe class FreeCompanyReader
{
    internal const string Category = "free_company";
    internal const string DetailsCategory = "free_company_details";
    internal const int MaxDetailTextChars = 1024;
    internal const int MaxDetailTextBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IDataManager data;
    private readonly IGameGui gui;
    private FreeCompanyReadDiagnostic detailsDiagnostic = FreeCompanyReadDiagnostic.Unavailable("not_read");

    internal FreeCompanyReader(IDataManager data, IGameGui gui)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.data = data;
        this.gui = gui;
    }

    internal ActivitySnapshot Capture()
    {
        try { return Describe(DateTimeOffset.UtcNow, Observe(), Observe()); }
        catch (Exception ex) { return ActivitySnapshot.Unavailable(Category, $"Free Company menu read unavailable ({ex.GetType().Name})."); }
    }

    // A separate bounded category under the user's Free Company permission.
    internal ActivitySnapshot CaptureDetails()
    {
        this.detailsDiagnostic = FreeCompanyReadDiagnostic.Unavailable("reading");
        try
        {
            var before = Observe(out var readiness);
            if (!before.IsReady)
            {
                this.detailsDiagnostic = FreeCompanyReadDiagnostic.Unavailable(readiness);
                return DetailsUnavailable("The main Free Company menu or its character-bound profile is not ready. Close inspection/profile windows and reopen the main menu.");
            }
            var gameData = this.data.GameData;
            var language = this.data.Language;
            // Public catalogs are optional too: failure of one cannot discard unrelated observations.
            var entitlements = ReadEntitlements();
            var first = CopyDetails(before);
            var second = CopyDetails(before);
            var third = CopyDetails(before);
            if (first is null || second is null || third is null || before != Observe())
            {
                this.detailsDiagnostic = FreeCompanyReadDiagnostic.Unavailable("company_context_changed");
                return DetailsUnavailable("The company menu or profile changed during capture. Let the main menu settle and retry.");
            }
            var stable = StableDetails(StableDetails(first, second), third);
            var world = ReadWorldName(stable.HomeWorld);
            var company = ReadGrandCompanyName(stable.GrandCompany);
            var result = DescribeDetails(DateTimeOffset.UtcNow, before, Observe(), stable, stable,
                _ => world, _ => company, entitlements);
            if (before != Observe() || !ReferenceEquals(gameData, this.data.GameData) || language != this.data.Language)
            {
                this.detailsDiagnostic = FreeCompanyReadDiagnostic.Unavailable("company_context_changed");
                return DetailsUnavailable("The character, company menu or public data changed during capture. Let the main menu settle and retry.");
            }
            this.detailsDiagnostic = DiagnoseDetails(result, stable, before.TotalMembers, entitlements is not null);
            return result;
        }
        catch (Exception)
        {
            this.detailsDiagnostic = FreeCompanyReadDiagnostic.Unavailable("reader_error");
            return DetailsUnavailable("Free Company details could not be read safely. Use the manual Free Company menu diagnostic to report the reader stage.");
        }
    }

    // Human-triggered only. Returns fixed labels/booleans and a bounded exported-row count, never field values.
    internal FreeCompanyReadDiagnostic CaptureReadDiagnostic()
    {
        try
        {
            var before = Observe(out var readiness);
            if (!before.IsReady) return FreeCompanyReadDiagnostic.Unavailable(readiness);
            _ = CaptureDetails();
            return before == Observe() ? this.detailsDiagnostic : FreeCompanyReadDiagnostic.Unavailable("company_context_changed");
        }
        catch (Exception) { return FreeCompanyReadDiagnostic.Unavailable("reader_error"); }
    }

    private string? ReadWorldName(ushort id)
    {
        if (id == 0) return null;
        try
        {
            var sheet = this.data.GetExcelSheet<Lumina.Excel.Sheets.World>();
            var name = sheet.TryGetRow(id, out var row) ? row.Name.ExtractText() : null;
            return ReferenceEquals(sheet.RawSheet, this.data.GetExcelSheet<Lumina.Excel.Sheets.World>().RawSheet) ? name : null;
        }
        catch (Exception) { return null; }
    }

    private string? ReadGrandCompanyName(byte id)
    {
        if (id is < 1 or > 3) return null;
        try
        {
            var sheet = this.data.GetExcelSheet<Lumina.Excel.Sheets.GrandCompany>();
            var name = sheet.TryGetRow(id, out var row) ? row.Name.ExtractText() : null;
            return ReferenceEquals(sheet.RawSheet, this.data.GetExcelSheet<Lumina.Excel.Sheets.GrandCompany>().RawSheet) ? name : null;
        }
        catch (Exception) { return null; }
    }

    private FreeCompanyEntitlement[]? ReadEntitlements()
    {
        try
        {
            var ranks = this.data.GetExcelSheet<Lumina.Excel.Sheets.FCRank>();
            var rights = this.data.GetExcelSheet<Lumina.Excel.Sheets.FCRights>();
            var result = BuildEntitlements(ranks.Select(row => new FreeCompanyRankRight(row.RowId, row.Rights)),
                rights.Where(row => row.RowId != 0).Select(row => new FreeCompanyRight(row.RowId, row.Name.ExtractText(), row.Description.ExtractText())));
            return ReferenceEquals(ranks.RawSheet, this.data.GetExcelSheet<Lumina.Excel.Sheets.FCRank>().RawSheet) &&
                ReferenceEquals(rights.RawSheet, this.data.GetExcelSheet<Lumina.Excel.Sheets.FCRights>().RawSheet) ? result : null;
        }
        catch (Exception) { return null; }
    }

    private static FreeCompanyDetails? CopyDetails(FreeCompanyObservation owner)
    {
        if (!owner.IsReady) return null;
        var profile = (InfoProxyFreeCompany*)owner.ProfileAddress;
        var agent = (AgentFreeCompany*)owner.AgentAddress;
        // Source: pinned dd57986b AgentFreeCompany and InfoProxyFreeCompany.
        // ShortMessage, master names, roster data and Activity event records are not read.
        if (profile->Id != owner.CompanyId || agent->InfoProxyFreeCompany != profile || profile->Ranks.Length != 14)
            return null;
        var board = ReadDetailText(&agent->Board);
        var slogan = ReadDetailText(&agent->Slogan);
        var ranks = new FreeCompanyRank[14];
        for (var index = 0; index < ranks.Length; index++)
        {
            ref var rank = ref profile->Ranks[index];
            var name = ReadFixedText(rank.Name, 16, true);
            ranks[index] = new(index + 1, rank.RankNumber, name, rank.MemberCount);
        }
        return new(profile->HomeWorldId, (byte)profile->GrandCompany, board.Text, slogan.Text, ranks,
            board.State, slogan.State, board.LengthMetadataDiffers, slogan.LengthMetadataDiffers);
    }

    internal static bool SameDetails(FreeCompanyDetails? first, FreeCompanyDetails? second) =>
        first is not null && second is not null && first.HomeWorld == second.HomeWorld &&
        first.GrandCompany == second.GrandCompany && first.Board == second.Board && first.Slogan == second.Slogan &&
        (first.Ranks is null ? second.Ranks is null : second.Ranks is not null && first.Ranks.SequenceEqual(second.Ranks));

    internal static FreeCompanyDetails StableDetails(FreeCompanyDetails first, FreeCompanyDetails second) => new(
        first.HomeWorld == second.HomeWorld ? first.HomeWorld : (ushort)0,
        first.GrandCompany == second.GrandCompany ? first.GrandCompany : (byte)0,
        first.Board == second.Board ? first.Board : null, first.Slogan == second.Slogan ? first.Slogan : null,
        first.Ranks is not null && second.Ranks is not null && first.Ranks.SequenceEqual(second.Ranks) ? first.Ranks : null,
        first.Board == second.Board ? second.BoardState : "changed",
        first.Slogan == second.Slogan ? second.SloganState : "changed",
        first.BoardLengthMetadataDiffers || second.BoardLengthMetadataDiffers,
        first.SloganLengthMetadataDiffers || second.SloganLengthMetadataDiffers);

    internal static ActivitySnapshot DescribeDetails(DateTimeOffset capturedAt, FreeCompanyObservation before,
        FreeCompanyObservation after, FreeCompanyDetails? first, FreeCompanyDetails? second,
        Func<uint, string?> worldName, Func<uint, string?> companyName,
        IReadOnlyList<FreeCompanyEntitlement>? entitlements = null)
    {
        if (!before.IsReady || before != after || first is null || second is null) return DetailsUnavailable();
        var stable = StableDetails(first, second);
        var world = stable.HomeWorld > 0 ? worldName(stable.HomeWorld) : null;
        var company = stable.GrandCompany is >= 1 and <= 3 ? companyName(stable.GrandCompany) : null;
        var entries = new List<ActivityEntry>
        {
            new(1, "profile", "Observed company name", Text: before.Name),
        };
        if (ActivitySnapshot.ValidText(world, 128)) entries.Add(new(2, "profile", "Home world", Text: world,
            ReferenceId: stable.HomeWorld, ReferenceKind: "world"));
        if (ActivitySnapshot.ValidText(company, 128)) entries.Add(new(3, "profile", "Grand Company", Text: company,
            ReferenceId: stable.GrandCompany, ReferenceKind: "grand_company"));
        if (ValidDetailText(stable.Slogan)) entries.Add(new(10, "profile", "Slogan", Text: string.IsNullOrWhiteSpace(stable.Slogan) ? "(empty)" : stable.Slogan));
        if (ValidDetailText(stable.Board)) entries.Add(new(20, "board", "Company Board", Text: string.IsNullOrWhiteSpace(stable.Board) ? "(empty)" : stable.Board));
        // Only complete, meaningful rank lists are exported. Unknown/unloaded ranks are omitted,
        // never represented as zero. No permission helper or opaque permission byte is accessed.
        if (ValidRanks(stable.Ranks, before.TotalMembers))
            entries.AddRange(stable.Ranks!.Where(rank => rank.Name!.Length > 0).Select(rank =>
                new ActivityEntry((uint)(99 + rank.Slot), "ranks", rank.Name!, Value: rank.MemberCount,
                    Unit: "members", Slot: rank.Slot)));
        if (entitlements is not null)
            entries.AddRange(entitlements.Where(right => right.Rank <= before.Rank).Select(right =>
                new ActivityEntry(200 + right.Id, "entitlements", right.Name,
                    Text: $"Public rank-based entitlement at company rank {right.Rank}. {right.Description}",
                    Value: right.Rank, Unit: "rank", ReferenceId: right.Id, ReferenceKind: "fc_rights")));
        return new(DetailsCategory, capturedAt, true, entries);
    }

    internal static FreeCompanyReadDiagnostic DiagnoseDetails(ActivitySnapshot result, FreeCompanyDetails details,
        ushort totalMembers, bool entitlementCatalogAvailable) => new("captured", true, result.Available,
        result.Entries.Any(row => row.Id == 2) ? "observed" : "unavailable",
        result.Entries.Any(row => row.Id == 3) ? "observed" : "unavailable",
        result.Entries.Any(row => row.Id == 20) ? (details.Board!.Length == 0 ? "empty" : "observed") : DetailFailure(details.BoardState),
        result.Entries.Any(row => row.Id == 10) ? (details.Slogan!.Length == 0 ? "empty" : "observed") : DetailFailure(details.SloganState),
        ValidRanks(details.Ranks, totalMembers) ? "observed" : "incomplete_or_unreadable",
        entitlementCatalogAvailable ? "observed" : "catalog_unavailable", details.BoardLengthMetadataDiffers,
        details.SloganLengthMetadataDiffers, result.Entries.Count);

    private static string DetailFailure(string state) => state is "not_loaded" or "header_bounds" or "missing_terminator" or "unsupported_text" or "changed"
        ? state : "unavailable";

    internal static FreeCompanyEntitlement[] BuildEntitlements(IEnumerable<FreeCompanyRankRight> ranks,
        IEnumerable<FreeCompanyRight> rights)
    {
        // FCRights.FCRank does not match later actual rank thresholds in installed data.
        // Use the unique forward FCRank.Rights reference instead; validate the complete public mapping.
        var rankRows = ranks.Take(32).OrderBy(row => row.Rank).ToArray();
        var rightRows = rights.Take(14).OrderBy(row => row.Id).ToArray();
        if (rankRows.Length != 31 || !rankRows.Select(row => row.Rank).SequenceEqual(Enumerable.Range(0, 31).Select(i => (uint)i)) ||
            rankRows[0].RightId != 0 || rightRows.Length != 13 ||
            !rightRows.Select(row => row.Id).SequenceEqual(Enumerable.Range(1, 13).Select(i => (uint)i)) ||
            rankRows.Any(row => row.RightId > 13)) throw new ArgumentException("Invalid Free Company entitlement catalog.");
        return rightRows.Select(right =>
        {
            var matches = rankRows.Where(rank => rank.RightId == right.Id && rank.Rank > 0).ToArray();
            if (matches.Length != 1 || !ActivitySnapshot.ValidText(right.Name, 128) ||
                !ActivitySnapshot.ValidText(right.Description, 190)) throw new ArgumentException("Invalid Free Company entitlement mapping.");
            return new FreeCompanyEntitlement(right.Id, (byte)matches[0].Rank, right.Name, right.Description);
        }).ToArray();
    }

    internal static bool ValidRanks(FreeCompanyRank[]? ranks, ushort totalMembers) =>
        ranks is { Length: 14 } && ranks.All(rank => rank is not null) &&
        ranks.Select(rank => rank.Slot).SequenceEqual(Enumerable.Range(1, 14)) &&
        ranks.All(rank => rank is not null && rank.Name is not null && (rank.Name.Length == 0 ? rank.MemberCount == 0 :
            ActivitySnapshot.ValidText(rank.Name, 15))) &&
        ranks.Where(rank => rank.Name!.Length > 0).Select(rank => rank.Number).Distinct().Count() == ranks.Count(rank => rank.Name!.Length > 0) &&
        ranks.Sum(rank => (int)rank.MemberCount) == totalMembers && totalMembers > 0;

    internal static bool ValidDetailText(string? value)
    {
        if (value is null || value.Length > MaxDetailTextChars || value.Any(c => char.IsControl(c) && c != '\n')) return false;
        try { return StrictUtf8.GetByteCount(value) <= MaxDetailTextBytes; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static string? DecodeDetailText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxDetailTextBytes || bytes.Contains((byte)0)) return null;
        try
        {
            var value = StrictUtf8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            return ValidDetailText(value) ? value : null;
        }
        catch (DecoderFallbackException) { return null; }
    }

    internal static FreeCompanyTextRead ReadDetailText(Utf8String* value)
    {
        if (value == null) return new(null, "not_loaded", false);
        var differs = value->StringLength != value->BufUsed - 1;
        // Pinned Utf8String.Length/AsSpan use BufUsed-1, not StringLength, to bound UTF8 bytes.
        // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/System/String/Utf8String.cs
        // StringLength is metadata; it is not required to equal the byte count. Bounds and NUL remain mandatory.
        if (value->BufUsed is < 1 or > MaxDetailTextBytes + 1 || value->BufSize < value->BufUsed)
            return new(null, "header_bounds", differs);
        if ((byte*)value->StringPtr == null) return new(null, "not_loaded", differs);
        var bytes = new ReadOnlySpan<byte>((byte*)value->StringPtr, (int)value->BufUsed);
        if (bytes[^1] != 0) return new(null, "missing_terminator", differs);
        var text = DecodeDetailText(bytes[..^1]);
        return new(text, text is null ? "unsupported_text" : text.Length == 0 ? "empty" : "observed", differs);
    }

    internal static string? ReadFixedText(ReadOnlySpan<byte> bytes, int exactLength, bool allowEmpty)
    {
        if (bytes.Length != exactLength) return null;
        var zero = bytes.IndexOf((byte)0);
        if (zero < 0 || !allowEmpty && zero == 0) return null;
        if (zero == 0) return "";
        try { var value = StrictUtf8.GetString(bytes[..zero]); return ActivitySnapshot.ValidText(value, exactLength - 1) ? value : null; }
        catch (DecoderFallbackException) { return null; }
    }

    private static ActivitySnapshot DetailsUnavailable(string? reason = null) => ActivitySnapshot.Unavailable(DetailsCategory,
        reason ?? "Open the main Free Company menu yourself and let it settle. No stable character-bound company observation is available; missing data is unknown.");

    private FreeCompanyObservation Observe() => Observe(out _);

    private FreeCompanyObservation Observe(out string stage)
    {
        stage = "player_context_unavailable";
        var player = PlayerState.Instance();
        var info = InfoModule.Instance();
        var agent = AgentFreeCompany.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (player == null || !player->IsLoaded || player->ContentId == 0) return default;
        stage = "local_ui_owner_unavailable";
        if (info == null || info->LocalContentId != player->ContentId) return default;
        stage = "main_agent_unavailable";
        if (agent == null) return default;
        stage = "addon_manager_unavailable";
        if (manager == null) return default;
        stage = "main_addon_unassigned";
        if (agent->AddonId is 0 or > ushort.MaxValue) return default;
        stage = "main_menu_inactive";
        if (!agent->IsAgentActive()) return default;
        stage = "main_menu_loading";
        if (!agent->IsAddonReady()) return default;
        stage = "main_menu_hidden";
        if (!agent->IsAddonShown()) return default;
        var inspected = AgentInspect.Instance();
        var companyProfile = AgentFreeCompanyProfile.Instance();
        stage = "inspection_active";
        if (inspected != null && inspected->IsAgentActive()) return default;
        stage = "other_company_profile_active";
        if (companyProfile != null && companyProfile->IsAgentActive()) return default;

        var profile = agent->InfoProxyFreeCompany;
        var members = agent->InfoProxyFreeCompanyMember;
        // The company proxy is also used for inspections. Require the normal menu's
        // two registered proxies to agree, and still label this as the shown company,
        // not independently verified membership. Do not access CharData or names.
        stage = "company_proxies_unavailable";
        if (profile == null || members == null) return default;
        stage = "company_profile_unpopulated";
        if (profile->Id == 0) return default;
        stage = "company_proxy_mismatch";
        if (profile->Id != members->FreeCompanyId) return default;
        stage = "company_proxy_association";
        if ((nint)profile != (nint)info->GetInfoProxyById(InfoProxyId.FreeCompany) ||
            (nint)members != (nint)info->GetInfoProxyById(InfoProxyId.FreeCompanyMember)) return default;
        stage = "main_addon_unavailable";
        var addon = manager->GetAddonById((ushort)agent->AddonId);
        if (addon == null) return default;
        stage = "main_addon_name_unavailable";
        var addonName = addon->NameString;
        if (!ActivitySnapshot.ValidText(addonName, 64)) return default;
        stage = "main_addon_handle_unavailable";
        var handle = gui.GetAddonByName(addonName);
        if (handle.IsNull) return default;
        stage = "main_addon_association";
        if (handle.Address != (nint)addon || handle.Id != agent->AddonId) return default;
        stage = "main_addon_hidden";
        if (!handle.IsVisible) return default;
        stage = "main_addon_loading";
        if (!handle.IsReady) return default;
        var owner = player->ContentId;
        var name = ReadName(profile->Name);
        var value = new FreeCompanyObservation((nint)player, (nint)info, (nint)agent, (nint)profile,
            (nint)members, handle.Address, owner, info->LocalContentId, profile->Id, members->FreeCompanyId,
            agent->AddonId, true, true, name, profile->Rank, profile->OnlineMembers, profile->TotalMembers);
        stage = "owner_changed";
        if (!player->IsLoaded || player->ContentId != owner || info->LocalContentId != owner) return default;
        stage = name is null ? "company_name_unavailable" : "company_scalars_unavailable";
        if (!value.IsReady) return default;
        stage = "ready";
        return value;
    }

    internal static string? ReadName(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 22) return null;
        var zero = bytes.IndexOf((byte)0);
        if (zero <= 0) return null;
        try { var name = StrictUtf8.GetString(bytes[..zero]); return ActivitySnapshot.ValidText(name, 21) ? name : null; }
        catch (DecoderFallbackException) { return null; }
    }

    internal static ActivitySnapshot Describe(DateTimeOffset capturedAt, FreeCompanyObservation before, FreeCompanyObservation after)
    {
        if (!before.IsReady || !after.IsReady || before != after)
            return ActivitySnapshot.Unavailable(Category, "Open the main Free Company menu yourself and wait for it to populate. No stable company observation is available; missing data is unknown.");
        return new(Category, capturedAt, true,
        [
            new(1, "company", "Observed company name", Text: before.Name),
            new(2, "company", "Company rank", Value: before.Rank, Unit: "rank"),
            new(3, "company", "Members", Value: before.TotalMembers, Unit: "members"),
            new(4, "company", "Online members", Value: before.OnlineMembers, Unit: "members"),
        ]);
    }
}

internal sealed record FreeCompanyDetails(ushort HomeWorld, byte GrandCompany, string? Board, string? Slogan, FreeCompanyRank[]? Ranks,
    string BoardState = "unavailable", string SloganState = "unavailable", bool BoardLengthMetadataDiffers = false, bool SloganLengthMetadataDiffers = false);
internal sealed record FreeCompanyTextRead(string? Text, string State, bool LengthMetadataDiffers);
internal sealed record FreeCompanyReadDiagnostic(string Stage, bool OwnerReady, bool Available, string WorldState,
    string GrandCompanyState, string BoardState, string SloganState, string RanksState, string EntitlementsState,
    bool BoardLengthMetadataDiffers, bool SloganLengthMetadataDiffers, int ExportedRowCount)
{
    internal static FreeCompanyReadDiagnostic Unavailable(string stage) => new(stage, false, false,
        "not_read", "not_read", "not_read", "not_read", "not_read", "not_read", false, false, 0);
}
internal sealed record FreeCompanyRank(int Slot, byte Number, string? Name, ushort MemberCount);
internal sealed record FreeCompanyRankRight(uint Rank, ushort RightId);
internal sealed record FreeCompanyRight(uint Id, string Name, string Description);
internal sealed record FreeCompanyEntitlement(uint Id, byte Rank, string Name, string Description);

// Pointers, content IDs and company IDs are stability guards only, never exported.
internal readonly record struct FreeCompanyObservation(nint PlayerAddress, nint InfoAddress, nint AgentAddress,
    nint ProfileAddress, nint MembersAddress, nint AddonAddress, ulong ContentId, ulong InfoContentId,
    ulong CompanyId, ulong MemberCompanyId, uint AddonId, bool PlayerLoaded, bool MenuReady,
    string? Name, byte Rank, ushort OnlineMembers, ushort TotalMembers)
{
    internal bool IsReady => PlayerAddress != 0 && InfoAddress != 0 && AgentAddress != 0 && ProfileAddress != 0 &&
        MembersAddress != 0 && AddonAddress != 0 && ContentId != 0 && ContentId == InfoContentId && CompanyId != 0 &&
        CompanyId == MemberCompanyId && AddonId is > 0 and <= ushort.MaxValue && PlayerLoaded && MenuReady &&
        ActivitySnapshot.ValidText(Name, 21) && Rank > 0 && TotalMembers > 0 && OnlineMembers <= TotalMembers;
}
