# Cosmic research reader evidence — 2026-10-26

The reader copies the loaded research module only while the player's owned Cosmic
research menu is visible and ready. It returns all 11 crafting/gathering jobs'
current/unlocked research-stage values and seven named banked analysis values.
It does not infer tool possession, equipped gear, stats, mission medals, submitted
research totals, bonuses or required/max amounts.

## Primary contracts and menu identity

- [ClientStructs WKSResearchModule](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/Game/WKS/WKSResearchModule.cs)
  identifies the Cosmic Research Information module, the 77-ushort Analysis array
  (11 tool classes x 7 types), 11 current/unlocked stage bytes, and IsLoaded.
  Installed typed properties were independently verified by offline reflection.
- [WKSManager](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/Game/WKS/WKSManager.cs)
  provides the typed ResearchModule pointer, TerritoryId and IsLoaded. No Load,
  GetNeededAnalysis, GetMaxAnalysis, IsTypeAvailable or other research function is
  called by the production reader.
- [HaselTweaks CosmicResearchTodo](https://github.com/Haselnussbomber/HaselTweaks/blob/011bc40b4b0d1e5a53de94b46e819a014a7f1646/HaselTweaks/Tweaks/CosmicResearchTodo.cs)
  corroborates ToolClass = ClassJob.RowId - 7, stage-array index ToolClass - 1,
  and public Types[type - 1].Name. Native getter usage in that consumer is not
  treated as proof of getter purity or copied into CompAI.
- [ECommons WKSToolCustomize](https://github.com/NightmareXIV/ECommons/blob/00693e92b52d067250f1724e362b5e88e3fdf365/ECommons/UIHelpers/AddonMasterImplementations/WKSToolCustomize.cs)
  names the addon as Cosmic Relic Tool UI and identifies its 11-class selector.
  Its introductory XML comment mentions the recipe notebook and is inconsistent
  with its implementation; that comment is not relied upon.
- [Ferret WKSHud](https://github.com/OhKannaDuh/Ferret/blob/15061f9bcfdcd0d3db37847ee6db42e92486baf5/lib/Ferret/Modules/CosmicExploration/Addons/WKSHud.lua)
  independently associates open_cosmic_research/close_cosmic_research with
  WKSToolCustomize visibility. This corroborates the research-window identity,
  not any callback permission. No action code is copied or invoked.
- [AgentModule](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentModule.cs)
  provides AgentId.WKSToolCustomize. The reader checks that exact active,
  ready, shown agent, its Rapture-owned addon, and the matching visible/ready
  Dalamud addon handle. It does not accept WKSHud or a similarly titled window.

## Public metadata and raw-array mapping

The offline public-sheet probe in `.test-tmp/research-0490/Program.cs` verified
ToolClass rows 1..11 and ClassJob rows 8..18, localized job and data names, exactly
20 public stages and 7 analysis types per tool. Stage 1 is the initial public
stage without a research label. Later public stage labels include N-1 through
N-8, I-1 through I-5, A-1 through A-3, and E-1 through E-3. CompAI returns the
localized public label at the observed 1-based stage minus one; observed stage
0 and the unnamed initial stage have no label. These are research labels, not
claims that the corresponding tool Item is owned. Item references are not read.

The source's 11x7 declaration was corroborated against the documented native
GetCurrentAnalysis signature in the on-disk executable. No function was called:

- Executable SHA256: `46A52AB1DBE80B9A8D6C4D4531BF66693904200C5135FACACD053EF28A5DE14A`.
- The documented signature has exactly one match, resolving to 0x141A70400.
- Its range checks decrement ToolClass/type then require 0..10 and 0..6.
- 0x141A70441 multiplies the zero-based tool class by 7; 0x141A70448 adds the
  zero-based type; 0x141A7044B reads the corresponding ushort from Analysis.
- The current-stage helper reached from the documented GetNeededAnalysis body
  reads CurrentStages[ToolClass - 1]. Its caller validates the returned stage as
  1..20 before using public stage definitions. This corroborates the 1-based stage
  convention; CompAI does not call that helper or copy its offsets.

These addresses are research evidence for that file hash only. Production uses
the installed typed arrays and named fields. Raw output and the reproducible
offline decoder are in `.test-tmp/research-0490/native.txt`, `native-helpers.txt`
and `Native.cs`. No process attachment, native invocation or game-state write
occurred.

The final public requirement row contains nonzero amounts even though the user's
completed research screen shows required zero with positive banked analysis.
Additional native logic selects the live requirement target. That logic was not
reimplemented. Required/max amounts, completion targets and percentage bonuses
remain unavailable; current amounts must not be compared against an invented
required value or treated as zero when data is missing.

## Capture boundary and validation

Capture requires the same loaded PlayerState/content ID, loaded WKSManager with
the caller's current territory, loaded ResearchModule, agent module, addon
manager, exact agent and exact visible menu throughout. Native addresses are
re-resolved before copying after sheet work. The complete detached arrays are
copied twice and compared; stage values must be 0..20 and dimensions exactly
11/11/77. All 11 jobs' metadata must be named, correctly mapped and stable.
RawSheet, GameData and language identities must remain unchanged. Counts retain
the native ushort range, including observed zero. Zero stages or zero banked data
do not establish missing tools or locked research types.

The immutable result contains timestamp, public job/type identities and names,
stage values/optional labels and banked quantities. It contains no native
pointers, content ID, rates, player text or native flags. Root integration owns
default-off access, activity projection and 15-minute historical retention; a
closed menu cannot refresh this reader.

Validation command run:

```powershell
dotnet run --project tests/CosmicResearch.Tests/CosmicResearch.Tests.csproj -- --produce-fixture .test-tmp/research-0490/observation.json
```

Observed result: 11 fixture groups passed, exit 0, no compiler warnings. Raw
console output is `.test-tmp/research-0490/fixture-output.txt`. Fixtures exercise
all-job/type ordering, the provided banked-value example, zero/initial stages,
owner and territory changes, unloaded native modules, malformed shapes/stages,
missing/changed public definitions, exact menu binding and immutable output.
Native-copy fixtures allocate isolated synthetic WKS structs; they never resolve
native singleton functions or read a running game. No plugin build was run.

Live comparison remains necessary: naturally open Cosmic Research Information,
compare selected Carpenter data I..VII against all seven banked quantities, and
compare public current-stage labels for several jobs. The supplied screenshot is
a mapping/test target, not a recorded reader pass. Required zero, +150%, tier
submission totals and Auxesia completion are deliberately outside this v1 result.
