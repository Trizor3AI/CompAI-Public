# Credits

CompAI is maintained by [Trizor3](https://github.com/Trizor3AI).

Triple Triad registrations, battle-companion progression and personal PvP readers use FFXIVClientStructs' documented structures/helpers and Lumina's public game sheets. The matching pinned source references and coverage limits are recorded beside these readers and in [security and storage](SECURITY.md).

Occult Record unlock mapping was cross-checked against Dalamud's pinned UnlockState implementation and Recollection's [Occult Record adapter](https://github.com/zodiia/BetterAchievements/blob/c49bb2809871da410780bcdafb5325deb1e9d526/Recollection/Data/Unlockable/UnlockableOccultRecord.cs). Collection-tool and Yo-kai links use the installed public sheets and documented helpers. Gold Saucer, Series rewards and FC observations use FFXIVClientStructs and Lumina; no additional third-party runtime is bundled.

The playable deck count is corroborated by Square Enix's [Triple Triad guide](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/tripletriad/). Racing stat, rating and star-order mappings use the pinned FFXIVClientStructs helpers with local read-only implementation checks; [mapping evidence](tests/Activities.Tests/SOURCE-EVIDENCE.md) records the limits.

Shop layout and validation references include ECommons' [currency exchange](https://github.com/NightmareXIV/ECommons/blob/3ebb7c7911c667b89da450d527423f938f15466c/ECommons/UIHelpers/AddonMasterImplementations/ShopExchangeCurrency.cs) and [FC credit shop](https://github.com/NightmareXIV/ECommons/blob/3ebb7c7911c667b89da450d527423f938f15466c/ECommons/UIHelpers/AddonMasterImplementations/FreeCompanyCreditShop.cs) mappings, plus [ADS shop runtime](https://github.com/McVaxius/ADS/blob/1d5ad22a9c09fcccd628e1044560c371df6e1f67/ADS/Services/ShopPurchaseRuntime.cs). These informed bounded read-only adapters; none of their transaction code or runtime dependencies is bundled. Gil and Scrip shops use FFXIVClientStructs' typed ShopEventHandler and InclusionShop structures.

The second Mogpendium currency mapping was cross-checked against xivdev contributors' [EXDSchema CSBonusSeason schema](https://github.com/xivdev/EXDSchema/blob/a1e24a0c72058353f30f23beb996792cdb926868/CSBonusSeason.yml) and Haselnussbomber and contributors' [MogMogCheck SpecialShopService](https://github.com/Haselnussbomber/MogMogCheck/blob/2261b6b08ec7509a78e5741eb9f9123f7e9d1c4c/MogMogCheck/Services/SpecialShopService.cs). These are factual mapping references; MogMogCheck publishes AGPL-3.0 source. No license grant was found for the referenced EXDSchema revision, so this credit does not imply permission to copy its files.

Reader mappings were also cross-checked against [Hunty](https://github.com/Infiziert90/Hunty) for hunting-log books, [EurekaTrackerAutoPopper](https://github.com/Infiziert90/EurekaTrackerAutoPopper) and [VERMAXION](https://github.com/McVaxius/VERMAXION) for specific Challenge Log counters, and [Recollection](https://github.com/zodiia/BetterAchievements) and [Charon](https://github.com/ofnature/Charon) for Variant notebook flags. These are implementation references, not bundled dependencies; pinned source references and remaining limits are recorded beside the readers and in SECURITY.md.

CompAI relies on the work of these projects and their contributors:

- [Dalamud and XIVLauncher](https://github.com/goatcorp/Dalamud) — plugin hosting, game services, configuration and the in-game UI framework.
- [Dalamud.NET.Sdk](https://github.com/goatcorp/Dalamud.NET.Sdk) and [DalamudPackager](https://github.com/goatcorp/DalamudPackager) — plugin builds and package generation.
- [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) — native game structures used by the readers.
- [Lumina](https://github.com/NotAdam/Lumina) and [Lumina.Excel](https://github.com/NotAdam/Lumina.Excel) — local game-data and sheet access.
- [Dear ImGui](https://github.com/ocornut/imgui), through Dalamud's bindings — chat and settings controls.
- [OpenAI Codex](https://github.com/openai/codex) — the local App Server runtime used to connect to the user's account. The runtime's upstream notices also credit [Ratatui](https://github.com/ratatui/ratatui).
- [Python](https://www.python.org/) and [.NET](https://dotnet.microsoft.com/) — companion and plugin runtimes.
- [GitHub Copilot CLI](https://github.com/github/copilot-cli) — the evaluated Copilot account runtime; the official [Copilot SDK](https://github.com/github/copilot-sdk) was used as a protocol reference, not bundled as a Python dependency.
- [Grok Build](https://github.com/xai-org/grok-build) — the evaluated Grok account runtime and its ACP/custom-tool protocol.

For optional public game-data lookups, thanks to [XIVAPI](https://v2.xivapi.com/docs/guides/search/) and [Universalis](https://universalis.app/), including the contributors who maintain their data and services. Universalis prices are crowdsourced, not guaranteed current listings.

The [xivgear project](https://github.com/xiv-gear-planner/gear-planner) provides the optional public gear-plan service. Its [API documentation](https://github.com/xiv-gear-planner/gear-planner/blob/main/API_DOC.md), [export contracts](https://github.com/xiv-gear-planner/gear-planner/blob/main/packages/xivmath/src/geartypes.ts), [stat-cap formula](https://github.com/xiv-gear-planner/gear-planner/blob/main/packages/xivmath/src/xivmath.ts) and [item-stat loading](https://github.com/xiv-gear-planner/gear-planner/blob/main/packages/core/src/datamanager_new.ts) were factual references for the bounded share adapter and local stat interpretation. No xivgear source is copied or bundled. Reference-plan calculations are supplied by its service, not a local CompAI DPS or meld solver.

The quest journal reader references the sheet layout and sequence-key convention demonstrated by Haselnussbomber's [HaselDebug](https://github.com/Haselnussbomber/HaselDebug/tree/91b4e56b626d0347edf22f3825b187ac07bc5996). Its source references are retained beside the reader. The implementation uses a bounded plain-text reader and does not include HaselDebug as a dependency. Dalamud's [SamplePlugin](https://github.com/goatcorp/SamplePlugin) and documentation were also implementation references.

The beta inventory overlay's layout mapping was cross-checked against [Allagan Tools](https://github.com/Critical-Impact/InventoryTools/blob/70a9f410d9a8ab97bef6bcf795415ea2d7c75097/InventoryTools/Overlays/InventoryLargeOverlay.cs) and [CriticalCommonLib](https://github.com/Critical-Impact/CriticalCommonLib/blob/34d364ea938e585b4f7eeab5b36e4261fdd817d0/Services/Ui/AtkInventoryLarge.cs). These were factual layout references, not added dependencies. The overlay uses FFXIVClientStructs' typed inventory sort order to preserve physical bag/slot identity after sorting.

The dated item guidance in `item_sources.json` identifies its references individually: Square Enix's Eorzea Database and the contributors to the [FFXIV Console Games Wiki](https://ffxiv.consolegameswiki.com/wiki/Phantom_Dispeller_%CE%B2). Game content and trademarks belong to their respective owners, including Square Enix. CompAI is an independent project and is not endorsed by these projects or companies.

The retainer, saddlebag, housing and FC chest readers use FFXIVClientStructs' typed game structures. Container sizes and housing storage mappings were also cross-checked against Critical-Impact's [CriticalCommonLib InventoryScanner](https://github.com/Critical-Impact/CriticalCommonLib/blob/main/Services/InventoryScanner.cs). This is an implementation reference, not an added dependency.

See [Third-party notices](THIRD-PARTY-NOTICES.md) for dependency and distribution details. Credits do not replace license terms.
