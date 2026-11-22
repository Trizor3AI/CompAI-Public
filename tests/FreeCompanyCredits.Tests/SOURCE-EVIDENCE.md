# Free Company Topics and item-shop credit balance

Topics adapter checked on 2026-11-05; the original shop evidence below was checked on 2026-10-26. `FreeCompanyCreditsReader` prefers the displayed balance in the normally opened main Free Company Topics tab, and retains the existing ready agent-owned `FreeCompanyCreditShop` fallback. It does not open/refresh menus, make purchases, activate actions, request server data, or call the broad `ShopReader.Capture` path. Permission and fifteen-minute cache policy belong to the integration under the existing `FreeCompany` grant.

## Topics layout evidence and acceptance

The user-supplied, untruncated manual menu diagnostic dated 2026-10-27 identifies addon `FreeCompany`, owning agent `FreeCompany`, selected tab 0 (`Topics`), and visible numeric text node 17 with parent 16 in the root manager (empty component path). The reduced private evidence is `.test-tmp/manual-tests/results-0521/fc-topics-evidence.json`. This observation establishes the selected field association for the supplied layout; it does not establish live acceptance of this new production reader. Public fixtures use invented amounts, not the user's reported balance.

Reviewed primary definitions at FFXIVClientStructs commit `dd57986b8287e7c6ed9c27198f8c76cb04a81dfe`:

- [AgentFreeCompany](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentFreeCompany.cs) supplies the two typed proxy pointers.
- [InfoProxyFreeCompany](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/Info/InfoProxyFreeCompany.cs) identifies the company ID and `ActiveListItemNum`, explicitly documenting 0 as Topics. [InfoProxyFreeCompanyMember](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/Info/InfoProxyFreeCompanyMember.cs) identifies its company ID; no member entries are accessed.
- [AtkUldManager](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Component/GUI/AtkUldManager.cs) supplies the root manager's counted node list. [AtkResNode](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Component/GUI/AtkResNode.cs) supplies node ID/type/parent and visibility/draw metadata.
- [AtkTextNode](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Component/GUI/AtkTextNode.cs) identifies `NodeText`. [Utf8String](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/System/String/Utf8String.cs) uses `BufUsed - 1` for byte length; `StringLength` is not used as the byte bound.

Installed `FFXIVClientStructs.dll` SHA256 was rechecked as `F5A2C68CCECD3E199BDA572050E98902B2E7594066C95DC7826C2D4297CD0564`. The native fixtures compile against and allocate these actual installed typed bindings, not copied offsets or substitute structures.

Topics requires a loaded, nonzero player ContentId matching InfoModule's local identity; exact active/ready/shown `AgentId.FreeCompany` and typed agent identity; no active Inspect or FreeCompanyProfile agent; both registered FC proxies agreeing on a nonzero company ID; tab 0 only; exact `FreeCompany` addon ownership and a matching ready/visible Dalamud handle. These gates reuse the existing main reader/manual diagnostic boundary. Company IDs are internal stability guards and never enter the returned observation.

The root node list must contain 1–512 entries. Positive aligned structure addresses, unique nonnull node pointers, one exact text node 17, owned parent 16, and a visible/nontransparent/draw-enabled owned ancestor chain reaching the addon root within 32 nodes are mandatory. Only node metadata is inspected to locate that known field; no component descent or arbitrary numeric text search occurs. Other text buffers, Members/Activity tab contents, roster entries, company names, Board/Slogan, actions and permission fields are not read.

The single text buffer must contain 1–13 bytes plus a terminating NUL, with `BufSize >= BufUsed`. Only ASCII digits, optionally grouped by commas in exact three-digit groups, are accepted. Signs, leading zeroes except literal zero, whitespace, embedded NUL, overflow, decimal points, mixed/localized separators and SeString payloads fail closed. Text buffers may be byte-aligned because Utf8String inline storage need not be word-aligned. Two complete copies must agree on the value, raw bytes, buffer header, node membership, ancestors and owner identity, with additional header/content checks inside each copy. Other displayed formats remain unsupported; the independently validated shop fallback remains available.

## Item-shop primary layout evidence

- [ECommons FreeCompanyCreditShop at 3ebb7c7](https://github.com/NightmareXIV/ECommons/blob/3ebb7c7911c667b89da450d527423f938f15466c/ECommons/UIHelpers/AddonMasterImplementations/FreeCompanyCreditShop.cs) names UInt rank at AtkValue 0, CompanyCredits at 3, item count at 9, item IDs at 30+i and per-item prices at 130+i. Its transaction helpers are not copied or invoked.
- [AutoRetainer ReaderFreeCompanyCreditShop at cc020df](https://github.com/PunishXIV/AutoRetainer/blob/cc020df0efee3ea65a45117e2b89999108c702e8/AutoRetainer/UiHelpers/ReaderFreeCompanyCreditShop.cs) independently names UInt index 3 as Credits and the same item/price locations. Its missing-to-zero fallback is deliberately not used.
- The existing `CompAI/ShopReader.cs` already corroborates this item-shop layout against installed `FccShop.ItemData` pairs, with count 1–20 and actual AtkValues bounds. The new reader uses the same documented field facts but has its own narrow FC-only capture path; enabling Free Company access cannot invoke general shop capture.
- Installed `FFXIVClientStructs.dll` SHA256 is `F5A2C68CCECD3E199BDA572050E98902B2E7594066C95DC7826C2D4297CD0564`, unchanged from the prior typed FC metadata evidence. The test project compiles actual installed AgentModule, AgentInterface, RaptureAtkUnitManager, AtkUnitBase, AtkValue, PlayerState and public Lumina sheet APIs.

## Item-shop acceptance

The framework-thread reader requires a loaded PlayerState with nonzero ContentId; exact active/ready/shown `AgentId.FreeCompanyCreditShop`; a registered Rapture-owned addon with the same bounded nonzero ID and exact name; and the matching ready/visible Dalamud handle. Native pointers must be positive and aligned to `IntPtr.Size` before dereference. Player, agent/module, addon manager, addon, buffer, count and identity are rechecked around copies.

The AtkValues buffer must be nonnull, contain at most 4,096 entries, and have actual capacity for every accessed slot. Offer count is 1–20, requiring at least `130 + count` entries. This 20-row cap preserves the documented array regions; 4,096 is a defensive acceptance cap. Header and offer fields require their documented UInt types. No reinterpretation of Int or unknown unions is accepted.

Rank must be a nonzero public `FCRank` row. Every item/unit-price pair must match installed `FccShop.ItemData`, and its Item name must be valid. These are internal layout/readiness checks, not evidence of inventory, stock, membership, buying eligibility or affordability. The reader does not read item inventory quantities, purchase maxima, user text, profiles, chat, roster, action records or permission fields.

Two complete balance/rank/offer samples must agree while ownership and public GameData/RawSheet/language identity remain stable. Missing, empty, wrong-type, changed or uncorroborated data is unavailable. **Zero is accepted only after those gates.** The native balance is UInt and is preserved through 4,294,967,295; that is a wire/type bound, not a verified gameplay maximum.

## Detached output

The detached result is exactly `FreeCompanyCreditsObservation(DateTimeOffset CapturedAtUtc, uint Credits, FreeCompanyCreditsSource Source = FreeCompanyCreditsSource.ItemShop)`. Topics returns `Topics`; the unchanged shop path returns `ItemShop`. No native pointer, character identifier, rank, offers or company identity enters it. The shop-to-main-profile company-ID join is unverified, so a previous named FC profile must not be attached to a shop balance as identity proof. After close, integration may retain the observed value for fifteen minutes, without refreshing its capture time or calling it live. The source marker lets integration distinguish the actual observed menu.

## Existing shop integration text

Source:

`Visible ready agent-owned FreeCompanyCreditShop; displayed company credit balance; FccShop, FCRank and Item sheets`

Coverage before the existing common suffix:

`Company credits observed in the open Company Credit item shop. Retained for 15 minutes after observation; a closed menu does not refresh the balance. The observation does not independently establish a named company identity, membership, spending permission, stock or affordability. Missing or unreadable data is unknown, not zero. No chat, roster, company actions or purchases are included.`

## Validation

Run `dotnet run --project tests/FreeCompanyCredits.Tests/FreeCompanyCredits.Tests.csproj`. Fourteen shop groups and eleven Topics groups passed without warnings. The original shop coverage remains; Topics adds actual installed typed buffers, exact node/parent association, rejection of other tabs and changed company/session ownership, unknown/zero/max parsing, malformed/unsupported text, hidden/unowned/cyclic ancestors, buffer/list bounds, repeated sample stability and exclusion of unrelated text buffers.

Optional `-- --produce-fixtures <path>` writes shop `all`, `zero`, `maximum`, null `unavailable`, plus `topics`, `topicsZero` and `topicsMaximum` DTO examples. The Topics producer is private at `.test-tmp/fc-topics-native-producer.json`.

These are synthetic checks. The existing item-shop balance has a separate user-reported pass in the local handoff. The new Topics adapter still requires a live displayed-balance comparison, plus caller permission/retention checks; no live Topics acceptance or server freshness is claimed. No purchase is needed to verify either balance.
