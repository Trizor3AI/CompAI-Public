using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CompAI;

internal sealed unsafe class SightseeingLogReader(IDataManager data)
{
    // Current installed native source: dd57986b8287e7c6ed9c27198f8c76cb04a81dfe, PlayerState.cs.
    internal const uint AdventureOffset = 0x210000;
    internal const int AdventureCount = 340;
    internal const int AdventureBytes = 43;
    private SightseeingCatalog? catalog;

    internal ProgressionSnapshot Capture()
    {
        try
        {
            var player = PlayerState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0)
                return Unavailable("Loaded character sightseeing state is unavailable.");
            var owner = player->ContentId;
            var current = GetCatalog();
            var before = ReadBitmap(player);
            var snapshot = Describe(DateTimeOffset.UtcNow, current.Definitions, index => player->IsAdventureComplete(index));
            if (!before.AsSpan().SequenceEqual(ReadBitmap(player)) || PlayerState.Instance() != player ||
                !player->IsLoaded || player->ContentId != owner || !CatalogMatchesCurrent(current))
                return Unavailable("Character, sightseeing or public definition state changed during reading.");
            return snapshot;
        }
        catch (Exception ex)
        {
            catalog = null;
            return Unavailable($"Sightseeing read unavailable ({ex.GetType().Name}).");
        }
    }

    private static byte[] ReadBitmap(PlayerState* player)
    {
        if (player->CompletedAdventuresBitArray.BitCount != AdventureCount || player->CompletedAdventures.Length != AdventureBytes)
            throw new InvalidOperationException("Sightseeing structure bounds are unsupported.");
        return player->CompletedAdventures.ToArray();
    }

    private SightseeingCatalog GetCatalog()
    {
        var gameData = data.GameData; var language = data.Language.ToString();
        var sheet = data.GetExcelSheet<Adventure>();
        if (catalog is { } cached && cached.Matches(sheet.RawSheet, gameData, language)) return cached;
        catalog = null;
        var rows = sheet.Take(AdventureCount + 1).ToArray();
        if (rows.Length != AdventureCount)
            throw new InvalidOperationException("Sightseeing catalog bounds are unsupported.");
        var definitions = rows.Select(row => new SightseeingDefinition(row.RowId, row.Name.ExtractText().Trim())).ToArray();
        if (!ValidDefinitions(definitions)) throw new InvalidOperationException("Sightseeing definitions are invalid.");
        var current = new SightseeingCatalog(sheet.RawSheet, gameData, language, definitions);
        if (!CatalogMatchesCurrent(current)) throw new InvalidOperationException("Sightseeing definitions changed during capture.");
        return catalog = current;
    }

    private bool CatalogMatchesCurrent(SightseeingCatalog current) =>
        current.Matches(data.GetExcelSheet<Adventure>().RawSheet, data.GameData, data.Language.ToString());

    internal static bool SupportedId(uint id) => id >= AdventureOffset && id - AdventureOffset < AdventureCount;
    private static bool ValidDefinitions(SightseeingDefinition[] rows) => rows.Length is > 0 and <= AdventureCount &&
        rows.All(row => row is not null && SupportedId(row.Id) && ProgressionSnapshot.ValidText(row.Name, ProgressionSnapshot.MaxNameLength)) &&
        rows.Select(row => row.Id).Distinct().Count() == rows.Length;

    internal static ProgressionSnapshot Describe(DateTimeOffset capturedAt, IEnumerable<SightseeingDefinition> definitions,
        Func<uint, bool> observeIndex, bool loaded = true)
    {
        if (!loaded) return Unavailable("Loaded character sightseeing state is unavailable.");
        var rows = definitions.Take(AdventureCount + 1).ToArray();
        if (!ValidDefinitions(rows)) return Unavailable("Sightseeing definitions are invalid or outside the supported mapping.");
        var entries = rows.Select(row =>
        {
            // Dalamud UnlockState.IsAdventureComplete(Adventure) explicitly subtracts 0x210000.
            // https://github.com/goatcorp/Dalamud/blob/0bbbf4f5dc1106da40bd51e0f5184a12ffd1024c/Dalamud/Game/UnlockState/UnlockState.cs
            var complete = observeIndex(row.Id - AdventureOffset);
            return new ProgressionEntry(row.Id, row.Name, complete ? "completed" : "not_observed_complete",
                Completed: complete ? true : null, UnknownReason: complete ? null :
                    "No positive sightseeing completion was observed; negative-state readiness is unverified, so this does not prove an undiscovered vista.");
        });
        return new("sightseeing_log", capturedAt, true, "positive_observations_only", entries);
    }

    private static ProgressionSnapshot Unavailable(string reason) => ProgressionSnapshot.Unavailable("sightseeing_log", reason);
}

internal sealed record SightseeingDefinition(uint Id, string Name);
internal sealed record SightseeingCatalog(object Sheet, object GameData, string Language, SightseeingDefinition[] Definitions)
{
    internal bool Matches(object sheet, object gameData, string language) =>
        ReferenceEquals(Sheet, sheet) && ReferenceEquals(GameData, gameData) && Language == language;
}
