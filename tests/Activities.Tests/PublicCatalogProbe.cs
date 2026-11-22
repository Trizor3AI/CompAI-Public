using System.Text.Json;
using CompAI;
using Lumina.Excel.Sheets;

internal static class PublicCatalogProbe
{
    internal static void Run(string sqpack)
    {
        using var game = new Lumina.GameData(sqpack);
        foreach (var language in new[] { Lumina.Data.Language.English, Lumina.Data.Language.German,
            Lumina.Data.Language.French, Lumina.Data.Language.Japanese })
        {
            var names = game.Excel.GetSheet<RacingChocoboName>(language);
            var abilities = game.Excel.GetSheet<ChocoboRaceAbility>(language);
            var colors = game.Excel.GetSheet<Stain>(language);
            var classes = game.Excel.GetSheet<ChocoboRaceRank>(language);
            var classNames = game.Excel.GetSheet<GoldSaucerTextData>(language);
            var cards = game.Excel.GetSheet<TripleTriadCard>(language);
            var selectedNames = names.Where(row => row.RowId is > 0 and <= 32767u &&
                !string.IsNullOrWhiteSpace(row.Name.ExtractText())).Take(2).ToArray();
            var selectedAbility = abilities.First(row => row.RowId is > 0 and <= byte.MaxValue && !string.IsNullOrWhiteSpace(row.Name.ExtractText()));
            var selectedColor = colors.First(row => row.RowId is > 0 and <= byte.MaxValue && !string.IsNullOrWhiteSpace(row.Name.ExtractText()));
            var classRows = Enumerable.Range(2, 13).Select(id => classes.GetRow((uint)id)).Select(row =>
                new GoldSaucerRaceClass(row.RowId, row.RatingMin, row.RatingMax, classNames.GetRow(row.Name.RowId).Text.ExtractText().Trim())).ToArray();
            foreach (var row in classRows)
                if (GoldSaucerReader.ResolveRatingClass(row.Minimum, classRows)?.Id != row.Id ||
                    GoldSaucerReader.ResolveRatingClass(row.Maximum, classRows)?.Id != row.Id)
                    throw new Exception("Installed named class range is not unique at its boundaries.");
            var race = new GoldSaucerRace(17, 150, 600, (short)selectedNames[0].RowId, (short)selectedNames[1].RowId,
                new(141, 90, 85, 132, 77), new(300, 220, 220, 300, 220), new(4, 2, 2, 4, 2),
                new(4, 1, 2, 3, 2), new(3, 2, 1, 4, 1), (byte)selectedAbility.RowId, 0, true, false,
                4, 67, (byte)selectedColor.RowId, 8);
            var observation = GoldSaucerReader.DescribeRace(DateTimeOffset.UtcNow, race, race,
                id => names.GetRow(id).Name.ExtractText().Trim(), id => abilities.GetRow(id).Name.ExtractText().Trim(),
                id => colors.GetRow(id).Name.ExtractText().Trim(), rating => GoldSaucerReader.ResolveRatingClass(rating, classRows));
            using var query = JsonDocument.Parse("{\"category\":\"race_chocobo\"}");
            var result = new ActivityLookup(observation).Query(query.RootElement);
            if (!observation.Available || observation.Entries.Count != 32 || observation.Entries.Single(row => row.Id == 5).ReferenceId != 5 ||
                observation.Entries.Single(row => row.Id == 32).ReferenceId != selectedColor.RowId || JsonSerializer.SerializeToUtf8Bytes(result).Length > 20_000)
                throw new Exception("Installed public race sheet projection failed.");
            var config = new GoldSaucerConfiguration(7, Enumerable.Repeat((ushort)475, 25).ToArray(),
                Enumerable.Range(0, 5).Select(_ => new byte[48]).ToArray());
            var decks = GoldSaucerReader.DescribeConfiguration(DateTimeOffset.UtcNow, "triple_triad_decks", config, config, 7,
                id => cards.GetRow(id).Name.ExtractText().Trim());
            if (!decks.Available || decks.Entries.Count != 25) throw new Exception("Five-deck public card projection failed.");
            Console.WriteLine($"PASS {language}: installed public name/ability/color/class/card sheets; 13 unique class ranges; synthetic 32-row race bytes={JsonSerializer.SerializeToUtf8Bytes(result).Length}; 25 deck slots");
        }
    }
}

