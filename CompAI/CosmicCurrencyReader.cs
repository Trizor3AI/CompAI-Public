using System;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace CompAI;

/// <summary>Passive framework-thread read of the loaded Cosmic HUD. No menu or packet requests.</summary>
internal sealed unsafe class CosmicCurrencyReader(IDataManager data)
{
    internal const uint CosmocreditId = 45690;
    internal static bool SupportedPlanetCredit(uint id) => id is 45691 or 48146 or 48147 or 48148;

    internal CurrencyEntry[] Capture(uint territory)
    {
        if (territory is 0 or > ushort.MaxValue) return [];
        var game = data.GameData;
        var language = data.Language;
        var sheet = data.GetExcelSheet<Item>();
        CosmicCurrencyDefinition? Definition(uint id)
        {
            if (!sheet.TryGetRow(id, out var row)) return null;
            return new(row.Name.ExtractText(), row.Icon);
        }
        var entries = CaptureStable(() => Copy((ushort)territory), Definition, (ushort)territory);
        // Typed ExcelSheet wrappers are recreated; the underlying raw sheet is the identity.
        return ReferenceEquals(sheet.RawSheet, data.GetExcelSheet<Item>().RawSheet) &&
            ReferenceEquals(game, data.GameData) && language == data.Language ? entries : [];
    }

    private static CosmicCurrencySample? Copy(ushort territory)
    {
        var manager = WKSManager.Instance();
        if (manager == null || !manager->IsLoaded || manager->TerritoryId != territory) return null;
        var agent = AgentWKSHud.Instance();
        if (agent == null || agent->Info == null) return null;
        var info = agent->Info;
        // Documented states 1/2 are still fetching data; only 3 is ready.
        if (info->State != 3) return null;
        var sample = new CosmicCurrencySample((nint)manager, (nint)agent, (nint)info,
            manager->TerritoryId, manager->IsLoaded, info->State,
            info->CosmoCredits, info->CosmoCreditsMax, info->CosmoCreditsIcon,
            info->ZoneCredits, info->ZoneCreditsMax, info->ZoneCreditsItemId, info->ZoneCreditsIcon);
        if (WKSManager.Instance() != manager || AgentWKSHud.Instance() != agent ||
            agent->Info != info || !manager->IsLoaded || manager->TerritoryId != territory || info->State != 3)
            return null;
        return sample;
    }

    // Managed seam checks the same pair of complete samples used in production.
    internal static CurrencyEntry[] CaptureStable(Func<CosmicCurrencySample?> read,
        Func<uint, CosmicCurrencyDefinition?> definition, ushort territory)
    {
        var first = read();
        if (first is not { } sample || !Valid(sample, territory)) return [];
        var cosmos = definition(CosmocreditId);
        var planet = definition((uint)sample.PlanetItemId);
        if (!ValidDefinition(cosmos, sample.CosmoIcon) || !ValidDefinition(planet, sample.PlanetIcon)) return [];
        if (read() != first || definition(CosmocreditId) != cosmos ||
            definition((uint)sample.PlanetItemId) != planet) return [];
        return [new(CosmocreditId, cosmos!.Value.Name.Trim(), sample.CosmoCount),
            new((uint)sample.PlanetItemId, planet!.Value.Name.Trim(), sample.PlanetCount)];
    }

    private static bool Valid(CosmicCurrencySample sample, ushort territory) =>
        territory != 0 && sample.Territory == territory && sample.Loaded && sample.State == 3 &&
        sample.Manager != 0 && sample.Agent != 0 && sample.Info != 0 &&
        sample.PlanetItemId > 0 && SupportedPlanetCredit((uint)sample.PlanetItemId) &&
        sample.CosmoMax > 0 && sample.CosmoCount >= 0 && sample.CosmoCount <= sample.CosmoMax &&
        sample.PlanetMax > 0 && sample.PlanetCount >= 0 && sample.PlanetCount <= sample.PlanetMax;

    private static bool ValidDefinition(CosmicCurrencyDefinition? definition, int icon) =>
        definition is { } item && icon > 0 && item.Icon == icon &&
        !string.IsNullOrWhiteSpace(item.Name) && item.Name.Length <= CurrencySnapshot.MaxNameLength &&
        !item.Name.Any(char.IsControl);
}

internal readonly record struct CosmicCurrencyDefinition(string Name, uint Icon);
internal readonly record struct CosmicCurrencySample(nint Manager, nint Agent, nint Info,
    ushort Territory, bool Loaded, byte State, int CosmoCount, int CosmoMax, int CosmoIcon,
    int PlanetCount, int PlanetMax, int PlanetItemId, int PlanetIcon);
