# Island Index rank and experience evidence

Checked on 2026-10-26. This reader observes the rank and experience displayed by the normally open, ready Islekeep's Index (`MJIHud`). It performs no menu action, native update request, packet request, persistence, or background polling. The caller must provide the separate Island Progress permission, framework-thread scheduling, current-character/session checks, and retained-observation policy.

## Source and cap interpretation

- [ECommons MJIHud](https://github.com/NightmareXIV/ECommons/blob/00693e92b52d067250f1724e362b5e88e3fdf365/ECommons/UIHelpers/AddonMasterImplementations/MJIHud.cs) explicitly exposes `AtkValues[11].UInt` as `SanctuaryRank`, `[12].UInt` as `CurrentIslandXP`, and `[13].UInt` as `NextIslandLevelXP`. These three documented indices are the only native progress values copied. Its menu-action methods are not used.
- [ClientStructs IslandState](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/Game/MJI/IslandState.cs) identifies the byte-valued current rank with `MJIRank` and describes current XP as experience toward the next rank, resetting on rank-up. This supports the rank bound and within-rank interpretation; this reader does not read IslandState.
- [ClientStructs MJIManager](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/Game/MJI/MJIManager.cs) leaves manager loading flags undocumented. IslandState also marks `CanEditIsland` semantics uncertain. Neither is used as a readiness or island-ownership guess.
- An explicit read of the installed public `MJIRank` sheet confirmed the installed typed `ExpToNext` property is UInt32. Row 0 is a zero-valued placeholder; rows 1–19 have positive requirements; row 19 requires 100000; row 20 has requirement 0. No character or game-process data was read by this catalog probe.

The reader validates the complete bounded public catalog: contiguous positive ranks, no duplicates or gaps, positive requirements before the final rank, and exactly one terminal zero requirement. The maximum is derived from that table, rather than hardcoded to 20. The reported capped state therefore requires both the observed final rank and its zero requirement in a valid catalog. At cap, the reader copies rank only and returns `IsMaximumRank = true` with both experience fields null. It never interprets the Index's masked `-/-` as `0/0` or assumes a masked value has an integral type.

Below cap, experience and next-rank requirement must be nonnegative Int or UInt values within Int32 range. The observed requirement must match the public rank row, and current experience must be strictly below that positive requirement. A transient rank-up mismatch fails closed until the next coherent observation. Zero current experience is valid when the full rank/requirement pair is valid.

## Readiness and lifetime checks

The source requires a loaded player with nonzero content ID, the current AgentModule and RaptureAtkUnitManager, the active/ready/shown `AgentId.MJIHud`, and an exact native addon ID/name/address match to the visible/ready Dalamud handle. The AtkValues pointer must be nonnull with 14 through 256 entries. Native fields use the installed ClientStructs types, not hardcoded memory offsets. The fixture compiles those APIs against the installed development assemblies.

Owners include player/content identity, module, manager, agent, addon, addon ID, AtkValues pointer and count. Public catalog work may outlive the menu, so ownership is re-resolved before the first native copy, before the second, after each copy, and after final public definition checks. Both detached selected samples and their integral types must match. Public catalog checks preserve RawSheet, GameData and language identity. Output contains only its capture timestamp, rank, nullable experience/requirement and capped status.

Exact agent ownership establishes the source UI's ownership by the current session. It does not independently identify the owner of a visited island. Live validation must establish whether the Index hides, retains personal values, or displays host values when visiting another player's island; no unverified `CanEditIsland` predicate is added to conceal that uncertainty.

## Reproduction and limits

Run the isolated synthetic fixture:

```text
dotnet run --project tests/IslandProgress.Tests/IslandProgress.Tests.csproj
```

The 18 groups cover valid/non-cap/cap outputs, zero EXP, public catalog completeness, future cap expansion, malformed or changed sample types/values, definition and catalog changes, missing or changed owners, exact addon binding, stale pointer/count/ID rejection, ignored unrelated fields, masked EXP at cap, and detached immutable output. Allocated native structures are synthetic; default execution never opens local game files, resolves a native singleton, or accesses a game process.

The separate explicit public-data probe is reproducible with a user-supplied local installation path:

```text
dotnet run --project tests/IslandProgress.Tests/IslandProgress.Tests.csproj -- --public-catalog "<local-game-sqpack-directory>"
```

Validation passed all 18 synthetic groups without warnings. Logs are retained only under the ignored `.test-tmp/checks-0490` directory, not in this test project. This is not an in-game verification: compare a capped Index against its `-/-` display, compare a non-cap rank and current/required EXP, verify changes after earning EXP or ranking up, check guest-island attribution, and exercise the caller's permission/session/retention behavior separately.
