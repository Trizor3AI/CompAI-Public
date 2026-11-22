# Observed Isleventory resource and Felicitous Token evidence

Originally checked on 2026-10-26; installed public catalog and typed native fields rechecked on 2026-11-05 for the narrow Felicitous Token addition. The reader copies supported, discovered resources and the exact identified Felicitous Token row from the normally open and ready Isleventory (`MJIPouch`). It does not claim a complete island inventory or ownership of a visited island. Missing, undiscovered, unsupported or unidentifiable rows stay unknown; they are never synthesized as zero. The caller supplies the separate Island Materials permission, framework-thread scheduling, character/session invalidation and retained-observation policy.

## Primary source and installed API evidence

- [ClientStructs AgentMJIPouch](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentMJIPouch.cs) defines the agent-owned `InventoryData`, its contiguous `Inventory` vector, and separate category pointer vectors. `PouchInventoryItem` names ItemId, IconId, RowId, StackSize, MaxStackSize, InventoryIndex, ItemCategory and IsUndiscovered. RowId may refer to either MJIItemPouch or MJIKeyItem, so row ID alone cannot identify a resource. The category vector order places Tools at index 3. No explicit data-initialized flag is documented.
- [ClientStructs StdVector](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/STD/StdVector.cs) defines First, Last and End as the first element, one past the last element and allocation end. The reader validates these typed bounds before indexing the contiguous vector; it does not traverse the category pointer vectors.
- [xgather's existing read](https://github.com/xanunderscore/xgather/blob/9f2dcba485d7e3f9fd85d67f53d30f92f6842da8/xgather/Tasks/Gather/Island.cs) explicitly maps material RowId to StackSize in `GetIsleventory`. This corroborates quantity semantics only. Its custom offsets, loading assumptions and automation are not used.
- [Explorers-Icebox's pouch task](https://github.com/LeontopodiumNivale14/Explorers-Icebox/blob/a71380ec2660125bff9ab6c0df3465dcef56bef7/ExplorersIcebox/Scheduler/Tasks/Task_UpdateShop.cs) associates the Isleventory command with the `MJIPouch` addon and checks addon readiness. This confirms the addon association; its open/close/callback actions are not used.

An isolated installed-assembly probe confirmed the typed AgentMJIPouch fields, contiguous vector and item fields. Production uses those installed types, not hardcoded offsets. A separate explicit public-sheet read confirmed 109 real MJIItemPouch rows with unique valid Item references. Row 0 is Island Palm Leaf, Item 37551; it is not a placeholder.

The verified public MJIItemCategory mapping is:

| Category | Public name | Rows | Included |
|---|---|---:|---|
| 1 | Materials | 50 | Yes |
| 3 | Gardening Starters | 20 | Yes |
| 4 | Produce | 20 | Yes |
| 5 | Leavings | 9 | Yes |
| 6 | Feed | 3 | Yes |
| 7 | Restraints | 6 | Yes |
| 10 | Unnamed category containing Felicitous Token | 1 | Exact token only |

All 108 allowed public resource/consumable rows currently have Item.StackSize 999. The installed public catalog independently identifies **MJIItemPouch row 108, Item 41668, category 10, icon 65104, maximum stack 150**, named Felicitous Token in English. The [public MJIItemPouch extract](https://github.com/xivapi/ffxiv-datamining/blob/master/csv/en/MJIItemPouch.csv) agrees on its row/item/category references. Runtime acceptance is restricted to that exact row/item/category identity and cap; the native icon must equal its currently loaded public Item icon. Other category-10 definitions are not accepted.

The public category name is empty. Only this exact token may use the fixed **Tokens** fallback; this is CompAI's display label, not text extracted from MJIItemCategory. Public item names remain localized and validated normally. A malformed nonempty category name is still rejected.

The native token InventoryIndex mapping remains unverified. It is not inferred from MJIItemPouch.Unknown0, vector position, or the selected tab. Instead, only the exact corroborated token is exempt from the resource-tab restriction: its row/item/category/icon/cap/quantity and repeated primitive samples must still agree. InventoryIndex remains part of the complete repeated sample, but no byte value is assigned token semantics. This relies on the already documented PouchInventoryItem fields in the same bounded contiguous vector, with no new offset, getter, allocation or pointer traversal. Synthetic tests cover all possible grouping bytes; they do not establish the real live value or prove that the token will be present in a particular menu state.

Permanent key tools belong to a separate namespace and are not included. Native Tools inventory rows are excluded before definition lookup unless their row/item/category exactly identifies this token; overlapping MJIKeyItem row IDs must also fail the ItemId match. Restraints are the public consumable category, not permanent key tools. An unidentified token does not widen the general category allowlist.

## Acceptance and scope

The source requires a loaded player with a nonzero content ID, current AgentModule and RaptureAtkUnitManager, the active/ready/shown MJIPouch agent, and matching addon name/ID/address between the agent-owned native addon and the visible/ready Dalamud handle. InventoryData must exist. The contiguous vector must contain 1–512 elements with aligned, ordered, integral bounds and capacity at most 512. No undocumented initialization, dirty, ownership or allocation flag is guessed.

Each emitted row must be discovered, belong to a supported MJIItemPouch resource category or be the exact token above, and agree on native/public ItemId, icon, item category and stack limit. Resource rows retain the resource/consumable inventory-index restriction. Quantity must be nonnegative and within the corroborated limit, at most 999 for resources and 150 for the token. Public names are bounded. Duplicate emitted Item IDs fail closed. Definitions come from local MJIItemPouch, Item and MJIItemCategory sheets and retain RawSheet/GameData/language identity.

Owners and vector bounds are re-resolved before copying, after managed sheet work, and around both complete detached samples. Every copied primitive row field must remain stable, including fields on omitted rows. Native name buffers, category-list pointers and allocation records are never read or followed; no game-state update or key-item getter is called. Unsupported records are excluded from the observation after the bounded primitive copy. The fixture places unusable pointers in unused native text/category fields to check that they are not traversed.

An empty or malformed native vector is unavailable. A populated stable vector with no supported discovered rows may return an empty observed subset; that does not establish an empty island inventory. A discovered, identified row may report zero, but no zero is inferred from absence. The source does not prove server freshness or completeness of the entire public catalog. Its ownership checks identify the current session's menu, not the owner of a visited island.

The detached result contains a capture time and a read-only list of ItemId, public localized Name, Quantity, public PouchRowId, CategoryId and CategoryName (with the token's documented display-label fallback). PouchRowId is a public definition reference, not a physical inventory slot. Native addresses, caps, visibility flags and hidden item names are not exposed.

## Verification

```text
dotnet run --project tests/IslandMaterials.Tests/IslandMaterials.Tests.csproj
```

The 22 synthetic groups cover real row zero, explicit zero quantities, hidden/unsupported/tool exclusions, malformed quantities/definitions, vector bounds, ownership changes, two-copy stability, public catalog changes, native source binding, unused pointer non-traversal, and detached read-only output. Token cases cover exact identity, zero/partial/capped quantities, all grouping-index bytes, category-label provenance, unrelated token/key exclusions, duplicate rows, malformed metadata, definition/sample changes, and the existing typed native copy. Default fixture execution uses only allocated synthetic memory and managed seams; it does not access a game process or local game files.

The explicit public-catalog probe can be reproduced with:

```text
dotnet run --project tests/IslandMaterials.Tests/IslandMaterials.Tests.csproj -- --public-catalog "<local-game-sqpack-directory>"
```

The original machine-specific probe and validation output stays under ignored `.test-tmp/research-0500/island`; the 2026-11-05 recheck used the same explicit catalog probe against the installed public game files. No live game call, model call or UI action was performed for this addition. In-game validation must compare the displayed Felicitous Token count (including zero where available) and ordinary resources, verify refresh after gathering/spending, confirm undiscovered/permanent-tool exclusion, and exercise normal menu closure, guest-island attribution, permission revocation and character/session changes. Public definitions and synthetic fixtures do not prove live menu correspondence.
