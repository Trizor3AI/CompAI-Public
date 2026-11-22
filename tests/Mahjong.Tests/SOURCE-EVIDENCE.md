# Mahjong statistics evidence

Installed `FFXIVClientStructs.dll` informational version was rechecked as
`1.0.0+dd57986b8287e7c6ed9c27198f8c76cb04a81dfe`. Reflection confirms all six typed
`AtkTextNode*` fields in [AddonGSInfoEmj](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/AddonGSInfoEmj.cs):
`Rank`, `Points`, `NextRankPoints`, `MatchesPlayed`, `CurrentRating`, `HighestRating`.
No raw native offsets or opaque `UIState.Emj` counters are read.

The existing user-supplied manual captures corroborate the exact visible
`GSInfoEmj` child of `GoldSaucerInfo` category 5. The 0.45.1 typed-field diagnostic
reported `field_node_id_mismatch` for `points`; the accompanying screenshot
places `15 points` on the Rank line (node 9) and `5 pts` on the Next Rank line
(node 11). The earlier flat-text interpretation reversed those fields. The
correct direct node/parent pairs in contract order are:
`7/4`, `9/4`, `11/4`, `16/13`, `23/18`, `24/18`.
The capture is private test evidence and is not copied into this test project.
Parent linkage uses the existing bounded typed `AtkAddonControl` traversal.
The raw child-tab index is not treated as a Mahjong category index.

This reader preserves formatted text. `NextRankPoints` does not establish whether
the display is a total threshold or points remaining; no numeric denominator,
percentage or arithmetic is emitted. The identical observed NPC-win counters do
not corroborate label-to-counter pairing, so NPC wins remain excluded. Settings,
voice choice, reset controls and history are outside the projection.

Run `dotnet run --project tests/Mahjong.Tests/Mahjong.Tests.csproj` for synthetic
native ownership/bounds, stable capture, strict six-row contract and lookup checks.
The suite also reuses the eight direct-child ownership checks. Optional
`--produce-fixtures <path>` emits synthetic lookup results for bridge validation.
Offline checks do not verify every localized/live game layout.

`CaptureReadDiagnostic()` is an explicit human-only entry point through the same
capture orchestration and native guards as `Capture()`. Its bounded projection
contains only schema/category, a fixed status, an optional field label and node
count at or below 256. It never copies displayed values, native addresses or
character IDs. A non-Mahjong tab stops before child traversal. Diagnostic output
does not grant AI access, weaken a gate or establish the cause of a live failure.
