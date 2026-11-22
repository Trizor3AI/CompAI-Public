using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Bounded public definitions only. No native reads, progress, assignments, or reward claims.</summary>
internal sealed class MogpendiumCatalog(IDataManager data)
{
    internal const int MaxSeasons = 128;
    internal const int MaxObjectivesPerCategory = 200;
    private const int MaxNameLength = 128;
    private const int MaxTextLength = 4096;
    private readonly List<(object Sheet, Func<object> Current)> identities = [];
    private readonly HashSet<Type> sheetTypes = [];
    private MogpendiumSeason[]? seasons;
    private object? gameData;
    private string? language;

    internal MogpendiumSeason[] GetSeasons()
    {
        if (seasons is not null && Matches()) return seasons;
        seasons = null;
        identities.Clear();
        sheetTypes.Clear();
        gameData = data.GameData;
        language = data.Language.ToString();
        var seasonRows = Sheet<CSBonusSeason>().Take(MaxSeasons + 1).ToArray();
        if (seasonRows.Length > MaxSeasons) throw Invalid("season count");
        var missions = data.GetSubrowExcelSheet<CSBonusMission>();
        identities.Add((missions.RawSheet, () => data.GetSubrowExcelSheet<CSBonusMission>().RawSheet));
        // The localized labels/descriptions are plain Addon rows, independently corroborated
        // against the four manually selected pages. No category is inferred from a native offset.
        var labels = new uint[] { 15877, 15878, 15879, 15880 }.Select(AddonText).ToArray();
        var descriptions = new uint[] { 15881, 15882, 15884, 15885 }.Select(AddonText).ToArray();
        var result = new List<MogpendiumSeason>();
        foreach (var row in seasonRows)
        {
            // Pending/finished placeholder rows have no event currency or named catalog.
            if (row.Item.RowId == 0 || row.Text1.RowId == 0) continue;
            if (row.Text0.RowId == 0) throw Invalid("event heading");
            var groups = new uint[] { row.Category0, row.Category1, row.Category2, row.Category3 };
            MogpendiumObjective[][] categories;
            string? unavailableReason = null;
            try { categories = BuildCategories(groups, missions); }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException)
            {
                // An unsupported old/future objective must not erase independently
                // resolved event identity or disable other seasons. Empty categories
                // are unavailable, never a complete catalog containing zero objectives.
                categories = [[], [], [], []];
                unavailableReason = $"Public objectives could not be fully resolved ({ex.GetType().Name}).";
            }
            // Installed Lumina calls this Unknown13. The newer primary schema names the
            // same second Item column Currency2; MogMogCheck also reads Unknown13 as currency.
            // https://github.com/xivdev/EXDSchema/blob/a1e24a0c72058353f30f23beb996792cdb926868/CSBonusSeason.yml
            // https://github.com/Haselnussbomber/MogMogCheck/blob/2261b6b08ec7509a78e5741eb9f9123f7e9d1c4c/MogMogCheck/Services/SpecialShopService.cs
            var heading = Text(Sheet<CSBonusTextData>().GetRow(row.Text0.RowId).Text.ExtractText()) + " " +
                Text(Sheet<CSBonusTextData>().GetRow(row.Text1.RowId).Text.ExtractText());
            result.Add(new(row.RowId, heading,
                ItemName(row.Item.RowId), row.Item.RowId, row.Unknown13 == 0 ? null : ItemName(row.Unknown13),
                row.Unknown13 == 0 ? null : row.Unknown13, categories, labels, descriptions, unavailableReason));
        }
        if (result.Count == 0 || !IdentitiesMatch()) throw Invalid("changed public definitions");
        return seasons = result.ToArray();
    }

    internal bool Matches() => seasons is not null && IdentitiesMatch();

    internal static MogpendiumObjective? MatchDisplayedLabel(MogpendiumObjective[] objectives, string label)
    {
        if (!ValidMatchText(label, MaxNameLength)) return null;
        // Public heading macros capitalize some initial articles. Ignore casing only;
        // never remove variant suffixes or accept a substring of a generic label.
        var matches = objectives.Where(objective => string.Equals(NormalizeMatchText(objective.Name),
            NormalizeMatchText(label), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Matches the entire localized instruction, including any exclusion note. This is
    /// deliberately not a general SeString evaluator. The only target expression accepted
    /// is the verified single Content0 identifier used by InstanceContent/PublicContent
    /// instructions. Numeric placeholders are bounded displayed text, not inferred progress
    /// or a guessed CSBonusContent score-field mapping. Unknown expressions fail closed.
    /// </summary>
    internal static bool TryMatchOverview(MogpendiumObjective objective, string observed)
    {
        if (objective is null || objective.TargetNames is not { Length: 1 } ||
            !ValidMatchText(objective.TargetNames[0], MaxNameLength) ||
            !ValidMatchText(objective.Overview, MaxTextLength) || !ValidMatchText(observed, MaxTextLength)) return false;
        var template = NormalizeMatchText(objective.Overview.Replace("<br>", " ", StringComparison.Ordinal));
        var target = NormalizeMatchText(objective.TargetNames[0]);
        var pattern = new StringBuilder("\\A");
        var sawTarget = false;
        var sawNumber = false;
        var sawConditional = false;
        for (var offset = 0; offset < template.Length;)
        {
            if (template[offset] != '<')
            {
                var end = template.IndexOf('<', offset);
                if (end < 0) end = template.Length;
                var literal = template[offset..end];
                if (literal.Contains('>') || literal.Contains('\\')) return false;
                pattern.Append(Regex.Escape(literal));
                offset = end;
                continue;
            }
            var remaining = template.AsSpan(offset);
            string? token = null;
            foreach (var candidate in InstructionTargetTokens)
                if (remaining.StartsWith(candidate, StringComparison.Ordinal)) { token = candidate; break; }
            if (token is not null)
            {
                if (sawTarget) return false;
                pattern.Append(Regex.Escape(target));
                sawTarget = true;
            }
            else if (remaining.StartsWith("<num(lnum8)>", StringComparison.Ordinal))
            {
                token = "<num(lnum8)>";
                pattern.Append(sawNumber ? "\\k<count>" : "(?<count>[0-9]{1,5})");
                sawNumber = true;
            }
            else if (remaining.StartsWith("<if([lnum8==1],time,times)>", StringComparison.Ordinal))
            {
                if (sawConditional) return false;
                token = "<if([lnum8==1],time,times)>";
                pattern.Append("(?<plural>time|times)");
                sawConditional = true;
            }
            else return false;
            offset += token.Length;
        }
        if (!sawTarget || sawConditional && !sawNumber) return false;
        pattern.Append("\\z");
        try
        {
            var match = Regex.Match(NormalizeMatchText(observed), pattern.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25));
            if (!match.Success) return false;
            if (!sawNumber) return true;
            if (!uint.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                count is 0 or > 10000) return false;
            return !sawConditional || string.Equals(match.Groups["plural"].Value, count == 1 ? "time" : "times", StringComparison.OrdinalIgnoreCase);
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static readonly string[] InstructionTargetTokens =
    [
        "<head(<sheet(InstanceContent,lnum5,3)>)>", "<head(<sheet(PublicContent,lnum5,3)>)>",
        "<sheet(InstanceContent,lnum5,3)>", "<sheet(PublicContent,lnum5,3)>"
    ];
    private static bool ValidMatchText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        !value.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c));
    private static string NormalizeMatchText(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private bool IdentitiesMatch() => ReferenceEquals(gameData, data.GameData) &&
        language == data.Language.ToString() && identities.All(pair => ReferenceEquals(pair.Sheet, pair.Current()));

    private ExcelSheet<T> Sheet<T>() where T : struct, IExcelRow<T>
    {
        var sheet = data.GetExcelSheet<T>();
        if (sheetTypes.Add(typeof(T))) identities.Add((sheet.RawSheet, () => data.GetExcelSheet<T>().RawSheet));
        return sheet;
    }

    private MogpendiumObjective[][] BuildCategories(uint[] groups, SubrowExcelSheet<CSBonusMission> missions)
    {
        if (groups.Any(id => id == 0)) throw Invalid("category reference");
        var categories = new MogpendiumObjective[4][];
        for (var index = 0; index < groups.Length; index++)
        {
            var rows = missions.GetRow(groups[index]).Take(MaxObjectivesPerCategory + 1).ToArray();
            if (rows.Length is 0 or > MaxObjectivesPerCategory) throw Invalid("mission count");
            var objectives = new Dictionary<uint, MogpendiumObjective>();
            foreach (var mission in rows)
            {
                foreach (var id in new[] { mission.Content0.RowId, mission.Content1.RowId })
                {
                    if (id == 0 || objectives.ContainsKey(id)) continue;
                    if (objectives.Count >= MaxObjectivesPerCategory) throw Invalid("objective count");
                    objectives.Add(id, Objective(Sheet<CSBonusContent>().GetRow(id), index));
                }
            }
            if (objectives.Count == 0) throw Invalid("empty category");
            categories[index] = objectives.Values.ToArray();
        }
        return categories;
    }

    private MogpendiumObjective Objective(CSBonusContent row, int category)
    {
        if (row.ContentType.RowId == 0) throw Invalid("content type");
        var type = Sheet<CSBonusContentType>().GetRow(row.ContentType.RowId);
        var names = new List<string>();
        foreach (var id in new[] { row.Content0.RowId, row.Content1.RowId })
            if (id != 0) names.Add(TargetName(Sheet<CSBonusContentIdentifier>().GetRow(id)));
        var targets = names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Some objectives target a feature, rather than a row in ContentIdentifier.
        // Ocean Fishing's public ContentType name resolves its Standard entry. Other
        // such rows remain in the catalog but are not candidates for detail matching.
        var name = targets.Length > 0 ? string.Join(" / ", targets) :
            type.ContentType.RowId != 0 ? Text(Sheet<ContentType>().GetRow(type.ContentType.RowId).Name.ExtractText()) :
            throw Invalid("objective name");
        if (name.Length > MaxNameLength) throw Invalid("objective name length");
        if (category == 0 && targets.Length == 0) targets = [name];
        // A raw localized macro template is retained for inspection only. ExtractText
        // would silently remove target/count substitutions; no template is evaluated here.
        var overview = type.Dialogue.Count > 2 && type.Dialogue[2].RowId != 0
            ? Text(Sheet<Addon>().GetRow(type.Dialogue[2].RowId).Text.ToMacroString(), MaxTextLength) : "";
        // Variable reward tiers (PvP, GATEs, Ocean Fishing) are not a fixed reward.
        uint? reward = row.RewardCount0 > 0 && row.RewardCount1 == 0 && row.RewardCount2 == 0 &&
            row.RewardCount3 == 0 && row.RewardCount4 == 0 ? row.RewardCount0 : null;
        return new(row.RowId, name, overview, reward, targets);
    }

    private string TargetName(CSBonusContentIdentifier row)
    {
        var id = row.Content.RowId;
        if (id == 0) throw Invalid("content target");
        // Union mapping comes from the primary EXDSchema, not coincidental matching IDs.
        // In particular InstanceContent.RowId must never be used as ContentFinderCondition.RowId.
        // https://github.com/xivdev/EXDSchema/blob/a1e24a0c72058353f30f23beb996792cdb926868/CSBonusContentIdentifier.yml
        return row.ContentLinkType switch
        {
            1 => DutyName(Sheet<InstanceContent>().GetRow(id).ContentFinderCondition.RowId),
            2 => Text(Sheet<GoldSaucerTextData>().GetRow(Sheet<GFateType>().GetRow(id).Name.RowId).Text.ExtractText()),
            3 => PlaceName(Sheet<TerritoryType>().GetRow(id).PlaceName.RowId),
            4 => Text(Sheet<EventItem>().GetRow(Sheet<MobHuntOrderType>().GetRow(id).EventItem.RowId).Name.ExtractText()),
            5 => ItemName(Sheet<TreasureHuntRank>().GetRow(id).ItemName.RowId),
            6 => ItemName(Sheet<FishParameter>().GetRow(id).Item.RowId),
            7 => Text(Sheet<ENpcResident>().GetRow(id).Singular.ExtractText()),
            8 => DutyName(Sheet<PublicContent>().GetRow(id).ContentFinderCondition.RowId),
            9 => Text(Sheet<DeepDungeon>().GetRow(id).Name.ExtractText()),
            10 => Text(Sheet<DynamicEvent>().GetRow(id).Name.ExtractText()),
            _ => throw Invalid("unsupported content target type")
        };
    }

    private string DutyName(uint id) => id != 0
        ? Text(Sheet<ContentFinderCondition>().GetRow(id).Name.ExtractText()) : throw Invalid("duty reference");
    private string ItemName(uint id) => id != 0
        ? Text(Sheet<Item>().GetRow(id).Name.ExtractText()) : throw Invalid("item reference");
    private string PlaceName(uint id) => id != 0
        ? Text(Sheet<PlaceName>().GetRow(id).Name.ExtractText()) : throw Invalid("place reference");
    private string AddonText(uint id) => id != 0
        ? Text(Sheet<Addon>().GetRow(id).Text.ExtractText(), MaxTextLength) : throw Invalid("text reference");
    private static string Text(string value, int maximum = MaxNameLength)
    {
        value = value.Trim();
        if (value.Length is 0 || value.Length > maximum || value.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw Invalid("public text");
        return value;
    }
    private static InvalidOperationException Invalid(string reason) => new($"Mogpendium catalog is unsupported ({reason}).");
}

internal sealed record MogpendiumSeason(uint Id, string Heading, string CurrencyName, uint CurrencyItemId,
    string? SecondaryCurrencyName, uint? SecondaryCurrencyItemId, MogpendiumObjective[][] Categories,
    string[] CategoryLabels, string[] CategoryDescriptions, string? CatalogUnavailableReason = null);
internal sealed record MogpendiumObjective(uint ContentId, string Name, string Overview, uint? RewardCount, string[] TargetNames);
