using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Framework-thread copies of visible Gold Saucer menu state. No game calls that request or mutate data.</summary>
internal sealed unsafe class GoldSaucerReader(IDataManager data, IGameGui gui)
{
    // The native allocation has ten entries; the playable UI exposes only five decks.
    // https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/tripletriad/
    internal const int NativeDeckCapacity = 10;
    internal const int DeckCount = 5;
    internal const int DeckSize = 5;
    internal const int DeckNameBytes = 48;
    internal const int HotbarSize = 23;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal ActivitySnapshot Capture(string category)
    {
        if (category == "mahjong") return ActivitySnapshot.Unavailable(category,
            "The installed Mahjong structure has no verified personal-statistic fields. Mahjong observations are unavailable; no values are inferred.");
        if (category is not ("race_chocobo" or "triple_triad_decks" or "verminion"))
            throw new ArgumentException("Unsupported Gold Saucer category.");
        try
        {
            var before = ObserveContext(category);
            if (!before.IsReady) return Unavailable(category);
            var gameData = data.GameData;
            var language = data.Language;
            ActivitySnapshot result;
            bool sheetsStable;
            if (category == "race_chocobo")
            {
                var names = data.GetExcelSheet<RacingChocoboName>();
                var abilities = data.GetExcelSheet<ChocoboRaceAbility>();
                var colors = data.GetExcelSheet<Stain>();
                var classes = data.GetExcelSheet<ChocoboRaceRank>();
                var classNames = data.GetExcelSheet<GoldSaucerTextData>();
                var classRows = new List<GoldSaucerRaceClass>();
                // The installed Gold Saucer updater searches only the thirteen rated classes (2..14).
                for (uint id = 2; id <= 14; ++id)
                    if (classes.TryGetRow(id, out var row) && row.Name.RowId != 0 &&
                        classNames.TryGetRow(row.Name.RowId, out var label))
                        classRows.Add(new(id, row.RatingMin, row.RatingMax, label.Text.ExtractText().Trim()));
                var native = RaceChocoboManager.Instance();
                var first = CopyRace(native);
                var second = CopyRace(native);
                result = native != null && native == RaceChocoboManager.Instance()
                    ? DescribeRace(DateTimeOffset.UtcNow, first, second,
                        id => names.TryGetRow(id, out var row) ? row.Name.ExtractText().Trim() : null,
                        id => abilities.TryGetRow(id, out var row) ? row.Name.ExtractText().Trim() : null,
                        id => colors.TryGetRow(id, out var row) ? row.Name.ExtractText().Trim() : null,
                        rating => ResolveRatingClass(rating, classRows))
                    : Unavailable(category);
                if (native == null || native != RaceChocoboManager.Instance() || first != CopyRace(native)) return Unavailable(category);
                sheetsStable = ReferenceEquals(names.RawSheet, data.GetExcelSheet<RacingChocoboName>().RawSheet) &&
                    ReferenceEquals(abilities.RawSheet, data.GetExcelSheet<ChocoboRaceAbility>().RawSheet) &&
                    ReferenceEquals(colors.RawSheet, data.GetExcelSheet<Stain>().RawSheet) &&
                    ReferenceEquals(classes.RawSheet, data.GetExcelSheet<ChocoboRaceRank>().RawSheet) &&
                    ReferenceEquals(classNames.RawSheet, data.GetExcelSheet<GoldSaucerTextData>().RawSheet);
            }
            else
            {
                var native = GoldSaucerModule.Instance();
                var first = CopyConfiguration(native, before.Owner, category);
                var second = CopyConfiguration(native, before.Owner, category);
                if (native == null || native != GoldSaucerModule.Instance()) return Unavailable(category);
                if (category == "triple_triad_decks")
                {
                    var names = data.GetExcelSheet<TripleTriadCard>();
                    result = DescribeConfiguration(DateTimeOffset.UtcNow, category, first, second, before.Owner,
                        id => names.TryGetRow(id, out var row) ? row.Name.ExtractText().Trim() : null);
                    sheetsStable = ReferenceEquals(names.RawSheet, data.GetExcelSheet<TripleTriadCard>().RawSheet);
                }
                else
                {
                    var names = data.GetExcelSheet<Lumina.Excel.Sheets.Companion>();
                    result = DescribeConfiguration(DateTimeOffset.UtcNow, category, first, second, before.Owner,
                        id => names.TryGetRow(id, out var row) ? row.Singular.ExtractText().Trim() : null);
                    sheetsStable = ReferenceEquals(names.RawSheet, data.GetExcelSheet<Lumina.Excel.Sheets.Companion>().RawSheet);
                }
                if (native != GoldSaucerModule.Instance() || !StableConfiguration(first,
                    CopyConfiguration(native, before.Owner, category), before.Owner, category)) return Unavailable(category);
            }
            return before == ObserveContext(category) && sheetsStable && ReferenceEquals(gameData, data.GameData) && language == data.Language
                ? result : Unavailable(category);
        }
        catch (Exception ex)
        {
            return ActivitySnapshot.Unavailable(category, $"Gold Saucer observation unavailable ({ex.GetType().Name}).");
        }
    }

    private GoldSaucerContext ObserveContext(string category)
    {
        var expected = category switch { "race_chocobo" => 1, "triple_triad_decks" => 3, "verminion" => 4, _ => -1 };
        var player = PlayerState.Instance();
        var agent = AgentGoldSaucer.Instance();
        var handle = gui.GetAddonByName("GoldSaucerInfo");
        if (player == null || !player->IsLoaded || player->ContentId == 0 || agent == null ||
            handle.IsNull || !handle.IsReady || !handle.IsVisible || agent->AddonId != handle.Id ||
            !agent->IsAgentActive() || !agent->IsAddonReady() || !agent->IsAddonShown()) return default;
        var addon = (AddonGoldSaucerInfo*)handle.Address;
        // Pinned ClientStructs dd57986b: category button order is General/Chocobo/CardList/CardDecks/Verminion/Mahjong.
        // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/AddonGoldSaucerInfo.cs
        if (addon->CategoryRadioButtons.Length != 6 || addon->SelectedCategory != expected ||
            agent->GoldSaucerSelectedTab != expected || addon->Id != handle.Id) return default;
        return new((nint)player, (nint)agent, handle.Address, handle.Id, player->ContentId,
            expected, addon->SelectedSubCategory, agent->ChocoboSeletedTab);
    }

    internal static GoldSaucerConfiguration? CopyConfiguration(GoldSaucerModule* module, ulong owner, string category)
    {
        // Fixed arrays and owner derive from GoldSaucerModule/UserFileEvent at the installed source commit.
        // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/Misc/GoldSaucerModule.cs
        // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/Misc/UserFileManager.cs
        if (module == null || owner == 0 || module->CharacterContentId != owner ||
            module->Decks.Length != NativeDeckCapacity || module->HotbarMinions.Length != HotbarSize) return null;
        if (category == "verminion")
        {
            var references = module->HotbarMinions.ToArray();
            return module->CharacterContentId == owner ? new(owner, references, []) : null;
        }
        if (category != "triple_triad_decks") return null;
        var cards = new ushort[DeckCount * DeckSize];
        var names = new byte[DeckCount][];
        for (var deck = 0; deck < DeckCount; ++deck)
        {
            var current = module->Decks[deck];
            if (current.Name.Length != DeckNameBytes || current.Cards.Length != DeckSize) return null;
            names[deck] = current.Name.ToArray();
            current.Cards.CopyTo(cards.AsSpan(deck * DeckSize, DeckSize));
        }
        return module->CharacterContentId == owner ? new(owner, cards, names) : null;
    }

    internal static GoldSaucerRace? CopyRace(RaceChocoboManager* native)
    {
        // Typed getters were reviewed in the installed executable: they only read this struct, write
        // the caller's output and check the stack cookie. Call them on a detached copy, never live memory.
        // This uses the game's displayed-stat calculation, including its integer rounding, rather than
        // treating the stored percentages as absolute stats. See tests/Activities.Tests/SOURCE-EVIDENCE.md.
        // https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/Game/RaceChocoboManager.cs
        if (native == null || native->State != RaceChocoboManager.RaceChocoboState.Loaded) return null;
        var copy = *native;
        if (copy.State != RaceChocoboManager.RaceChocoboState.Loaded || copy.Rank == 0 ||
            copy.NameFirst <= 0 || copy.NameLast <= 0 || copy.MaximumSpeed > 100 || copy.Acceleration > 100 ||
            copy.Endurance > 100 || copy.Stamina > 100 || copy.Cunning > 100) return null;
        RaceChocoboAttributeValues current = default, maximum = default, stars = default, fatherStars = default, motherStars = default;
        copy.GetAttributesCurrent(&current);
        copy.GetAttributesMaximum(&maximum);
        copy.GetAttributesStars(&stars);
        // The pinned parent bitfield labels are reversed relative to the installed getter's output
        // order. Use that same verified getter with all five local selection bits set to one parent.
        // These are detached stack copies; neither live state nor its selection flags are changed.
        var father = copy;
        var mother = copy;
        father.Parameters &= 0xE0;
        mother.Parameters |= 0x1F;
        father.GetAttributesStars(&fatherStars);
        mother.GetAttributesStars(&motherStars);
        return new(copy.Rank, copy.ExperienceCurrent, copy.ExperienceMax, copy.NameFirst, copy.NameLast,
            Attributes(current), Attributes(maximum), Attributes(stars, 1),
            Attributes(fatherStars, 1), Attributes(motherStars, 1),
            copy.AbilityHereditary, copy.AbilityLearned, copy.IsFemale, copy.IsWeatherFoul,
            copy.GetPedigreeLevel(), copy.GetRating(), copy.Color, copy.SessionsAvailable);
    }

    private static GoldSaucerAttributes Attributes(RaceChocoboAttributeValues values, uint add = 0) =>
        new(values.MaximumSpeed + add, values.Acceleration + add, values.Endurance + add, values.Stamina + add, values.Cunning + add);

    internal static GoldSaucerRaceClass? ResolveRatingClass(int rating, IEnumerable<GoldSaucerRaceClass> rows)
    {
        var matches = rows.Take(14).Where(row => row.Id is >= 2 and <= 14 && row.Minimum is >= 1 and <= 300 &&
            row.Maximum >= row.Minimum && row.Maximum <= 300 && rating >= row.Minimum && rating <= row.Maximum &&
            ActivitySnapshot.ValidText(row.Name, 256)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static ActivitySnapshot DescribeRace(DateTimeOffset time, GoldSaucerRace? first, GoldSaucerRace? second,
        Func<uint, string?> name, Func<uint, string?> abilityName, Func<uint, string?> colorName,
        Func<int, GoldSaucerRaceClass?> ratingClass)
    {
        if (first is null || first != second || first.Rank < 1 || first.Experience < 0 || first.ExperienceMax < 0 ||
            first.ExperienceMax != 0 && first.Experience > first.ExperienceMax ||
            first.Pedigree is < 1 or > 9 || first.Rating is < 1 or > 500 ||
            !first.Maximum.InRange(100, 500) || !first.Current.AtMost(first.Maximum) ||
            !first.Stars.InRange(1, 4) || !first.FatherStars.InRange(1, 4) || !first.MotherStars.InRange(1, 4) ||
            first.NameFirst <= 0 || first.NameLast <= 0) return Unavailable("race_chocobo");
        var firstName = name((uint)first.NameFirst);
        var lastName = name((uint)first.NameLast);
        if (!ActivitySnapshot.ValidText(firstName, 128) || !ActivitySnapshot.ValidText(lastName, 128) ||
            !ActivitySnapshot.ValidText(firstName + " " + lastName, 256)) return Unavailable("race_chocobo");
        var entries = new List<ActivityEntry>
        {
            new(1, "progression", "Race chocobo rank", Value: first.Rank, Unit: "rank"),
            new(2, "progression", "Race chocobo EXP", Value: first.Experience,
                Required: first.ExperienceMax == 0 ? null : first.ExperienceMax, Unit: "experience"),
            new(3, "identity", "Race chocobo name", Text: firstName + " " + lastName),
            new(4, "progression", "Race chocobo rating", Value: first.Rating, Unit: "rating"),
            new(30, "identity", "Sex", Text: first.Female ? "Female" : "Male"),
            new(31, "identity", "Weather preference", Text: first.WeatherFoul ? "Foul" : "Fair"),
            new(40, "pedigree", "Pedigree level", Value: first.Pedigree, Unit: "grade"),
            new(41, "training", "Training capacity", Value: first.SessionsAvailable, Unit: "sessions"),
        };
        var labels = new[] { "Maximum speed", "Acceleration", "Endurance", "Stamina", "Cunning" };
        for (var index = 0; index < labels.Length; ++index)
        {
            entries.Add(new((uint)index + 10, "attributes", labels[index], Value: first.Current[index],
                Required: first.Maximum[index], Unit: "stat"));
            entries.Add(new((uint)index + 50, "pedigree_own", labels[index] + " stars", Value: first.Stars[index], Unit: "stars"));
            entries.Add(new((uint)index + 60, "pedigree_father", labels[index] + " stars", Value: first.FatherStars[index], Unit: "stars"));
            entries.Add(new((uint)index + 70, "pedigree_mother", labels[index] + " stars", Value: first.MotherStars[index], Unit: "stars"));
        }
        var color = first.Color == 0 ? "No color reference in the loaded record." : colorName(first.Color);
        if (!ActivitySnapshot.ValidText(color, 256)) color = $"Unresolved chocobo color #{first.Color}";
        entries.Add(new(32, "identity", "Color", Text: color, ReferenceId: first.Color == 0 ? null : first.Color,
            ReferenceKind: first.Color == 0 ? null : "stain"));
        var raceClass = ratingClass(first.Rating);
        if (raceClass is not null && ResolveRatingClass(first.Rating, [raceClass]) is null) raceClass = null;
        entries.Add(raceClass is null
            ? new(5, "progression", "Rating class", Text: "No named rating class could be resolved.")
            : new(5, "progression", "Rating class", Text: raceClass.Name, Value: raceClass.Minimum,
                Required: raceClass.Maximum, Unit: "rating", ReferenceId: raceClass.Id, ReferenceKind: "chocobo_race_rank"));
        foreach (var (rowId, label, reference) in new[] { (20u, "Hereditary ability", first.HereditaryAbility), (21u, "Learned ability", first.LearnedAbility) })
        {
            var text = reference == 0 ? "No ability reference in the loaded record." : abilityName(reference);
            if (!ActivitySnapshot.ValidText(text, 256)) text = $"Unresolved race ability #{reference}";
            entries.Add(new(rowId, "abilities", label, Text: text, ReferenceId: reference == 0 ? null : reference,
                ReferenceKind: reference == 0 ? null : "chocobo_race_ability"));
        }
        return new("race_chocobo", time, true, entries);
    }

    internal static ActivitySnapshot DescribeConfiguration(DateTimeOffset time, string category,
        GoldSaucerConfiguration? first, GoldSaucerConfiguration? second, ulong expectedOwner, Func<uint, string?> publicName)
    {
        if (category is not ("triple_triad_decks" or "verminion")) throw new ArgumentException("Unsupported configuration category.");
        if (!StableConfiguration(first, second, expectedOwner, category)) return Unavailable(category);
        var rows = new List<ActivityEntry>();
        var deckNames = category == "triple_triad_decks" ? first!.Names.Select(DecodeDeckName).ToArray() : [];
        if (deckNames.Any(value => value is null)) return Unavailable(category);
        for (var index = 0; index < first!.References.Length; ++index)
        {
            var reference = first.References[index];
            var deck = index / DeckSize;
            var slot = category == "verminion" ? index + 1 : index % DeckSize + 1;
            var group = category == "verminion" ? "hotbar" : $"deck_{deck + 1}";
            var label = reference == 0 ? $"Slot {slot}" : publicName(reference);
            if (!ActivitySnapshot.ValidText(label, 128)) label = category == "verminion"
                ? $"Unresolved minion #{reference}" : $"Unresolved Triple Triad card #{reference}";
            var deckLabel = category == "triple_triad_decks" ? deckNames[deck] : null;
            if (reference == 0 && deckLabel is not null) label = $"{deckLabel} — slot {slot}";
            rows.Add(new((uint)index + 1, group, label!, Text: reference == 0
                    ? category == "verminion" ? "No minion reference in this observed slot." : "No card reference in this observed slot."
                    : deckLabel, ReferenceId: reference == 0 ? null : reference,
                ReferenceKind: reference == 0 ? null : category == "verminion" ? "companion" : "triple_triad_card", Slot: slot));
        }
        return new(category, time, true, rows);
    }

    internal static bool StableConfiguration(GoldSaucerConfiguration? first, GoldSaucerConfiguration? second,
        ulong owner, string category)
    {
        var decks = category == "triple_triad_decks";
        var length = decks ? DeckCount * DeckSize : HotbarSize;
        if (category is not ("triple_triad_decks" or "verminion") || first is null || second is null || owner == 0 ||
            first.Owner != owner || second.Owner != owner || first.References is null || second.References is null ||
            first.Names is null || second.Names is null || first.References.Length != length || second.References.Length != length ||
            !first.References.SequenceEqual(second.References) || first.Names.Length != (decks ? DeckCount : 0) ||
            second.Names.Length != first.Names.Length || decks && first.References.Any(id => id >= 476)) return false;
        for (var index = 0; index < first.Names.Length; ++index)
            if (first.Names[index] is null || second.Names[index] is null || first.Names[index].Length != DeckNameBytes ||
                second.Names[index].Length != DeckNameBytes || !first.Names[index].SequenceEqual(second.Names[index])) return false;
        return true;
    }

    internal static string? DecodeDeckName(byte[] bytes)
    {
        if (bytes is null || bytes.Length != DeckNameBytes) return null;
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) return null;
        try
        {
            var raw = StrictUtf8.GetString(bytes, 0, end);
            if (raw.Any(char.IsControl)) return null;
            var name = raw.Trim();
            return name.Length == 0 ? "Unnamed deck" : ActivitySnapshot.ValidText(name, 128) ? name : null;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static ActivitySnapshot Unavailable(string category) => ActivitySnapshot.Unavailable(category,
        "No stable loaded observation is available. Open the corresponding Gold Saucer tab yourself, wait for it to populate, then ask again. Missing values are unknown.");
}

// Local-only native-copy evidence. These records are never returned on the wire or retained by the plugin.
internal sealed record GoldSaucerConfiguration(ulong Owner, ushort[] References, byte[][] Names);
internal sealed record GoldSaucerRace(byte Rank, short Experience, short ExperienceMax, short NameFirst, short NameLast,
    GoldSaucerAttributes Current, GoldSaucerAttributes Maximum, GoldSaucerAttributes Stars,
    GoldSaucerAttributes FatherStars, GoldSaucerAttributes MotherStars,
    byte HereditaryAbility, byte LearnedAbility, bool Female, bool WeatherFoul,
    int Pedigree, int Rating, byte Color, byte SessionsAvailable);
internal readonly record struct GoldSaucerAttributes(uint MaximumSpeed, uint Acceleration, uint Endurance, uint Stamina, uint Cunning)
{
    internal uint this[int index] => index switch
    { 0 => MaximumSpeed, 1 => Acceleration, 2 => Endurance, 3 => Stamina, 4 => Cunning, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
    internal bool InRange(uint minimum, uint maximum)
    {
        for (var index = 0; index < 5; ++index) if (this[index] < minimum || this[index] > maximum) return false;
        return true;
    }
    internal bool AtMost(GoldSaucerAttributes maximum)
    {
        for (var index = 0; index < 5; ++index) if (this[index] > maximum[index]) return false;
        return true;
    }
}
internal sealed record GoldSaucerRaceClass(uint Id, ushort Minimum, ushort Maximum, string Name);
internal readonly record struct GoldSaucerContext(nint Player, nint Agent, nint Addon, uint AddonId, ulong Owner,
    int Category, int SubCategory, int ChocoboTab)
{
    internal bool IsReady => Player != 0 && Agent != 0 && Addon != 0 && AddonId != 0 && Owner != 0;
}
