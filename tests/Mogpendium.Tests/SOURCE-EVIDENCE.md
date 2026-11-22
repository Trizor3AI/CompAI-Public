# Mogpendium reader evidence

Verified 2026-10-24 against the installed Dalamud / Lumina assemblies and local public game sheets. This is source and synthetic validation, not an in-game acceptance result for the new reader.

## Primary references

- FFXIVClientStructs commit `dd57986b8287e7c6ed9c27198f8c76cb04a81dfe`: [AgentId.MoogleCollection](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentId.cs) identifies agent452. The installed assembly has no typed MoogleCollection agent or addon layout. The reader therefore uses the common agent-owned addon and corroborated text-node paths, with no private offsets.
- [CSBonusManager](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/Game/CSBonusManager.cs) identifies the active season, which does not establish which historical event the menu is displaying. Its opaque mission progress/claim arrays are not read.
- EXDSchema commit `a1e24a0c72058353f30f23beb996792cdb926868`: [CSBonusSeason](https://github.com/xivdev/EXDSchema/blob/a1e24a0c72058353f30f23beb996792cdb926868/CSBonusSeason.yml), [CSBonusMission](https://github.com/xivdev/EXDSchema/blob/a1e24a0c72058353f30f23beb996792cdb926868/CSBonusMission.yml), [CSBonusContent](https://github.com/xivdev/EXDSchema/blob/a1e24a0c72058353f30f23beb996792cdb926868/CSBonusContent.yml), [CSBonusContentIdentifier](https://github.com/xivdev/EXDSchema/blob/a1e24a0c72058353f30f23beb996792cdb926868/CSBonusContentIdentifier.yml). In particular, identifier link type1 targets InstanceContent, whose ContentFinderCondition provides the display name; it is not a direct ContentFinderCondition ID.
- MogMogCheck commit `2261b6b08ec7509a78e5741eb9f9123f7e9d1c4c`, [SpecialShopService](https://github.com/Haselnussbomber/MogMogCheck/blob/2261b6b08ec7509a78e5741eb9f9123f7e9d1c4c/MogMogCheck/Services/SpecialShopService.cs), corroborates the installed season `Unknown13` field as the secondary currency, named Currency2 in the newer schema. No MogMogCheck implementation was copied.

## Public-sheet verification

English, German, French and Japanese projections resolve these Astronomy event catalogs:

| Season | Primary currency item | Standard definitions | Weekly candidate definitions | Minimog candidate definitions | Ultimog definitions |
| --- | --- | ---: | ---: | ---: | ---: |
| 28, First Hunt | 51750 | 47 | 35 | 10 | 1 |
| 30, Second Hunt | 51751 | 56 | 44 | 10 | 1 |

Secondary currency is item51752, Uolon Horn Token in English. Candidate counts are public definitions, not the player's assignments. Repeated mission references are deduplicated by CSBonusContent ID. Twelve Standard rewards in season28 use variable tiers and are exported with no fixed amount.

Heading combines the localized CSBonusSeason Text0 and Text1 references. Addon rows15877–15880 supply category labels; rows15881,15882,15884,15885 supply the four descriptions. The English selected details resolve uniquely to content323 (Halatali (Hard),4),364 (the Gilded Araya,20),312 (Delubrum Reginae,30),303 (Aloalo Island,70).

The private `.test-tmp/mog-catalog-probe/public-catalog-evidence.json` and `.test-tmp/mogpendium-public-catalog.json` hold repeatable local public-sheet probe output. They contain public definitions only. Unsupported old/future objective catalogs retain independently known event identity with unavailable categories; they never become empty complete catalogs.

The bounded instruction matcher accepts the four evidenced objective forms in all four localized public projections (16 checks). Its English Standard template eligibility is41/47 for season28 and50/56 for season30, including six PvP objectives. Exactly six Standard selections use unsupported instruction forms: Cliffhanger, Air Force One, Leap of Faith, Any Way the Wind Blows, The Slice Is Right and Ocean Fishing. These six remain catalog entries when a supported objective is selected. Because this first contract requires a verified selected detail, selecting one of these unsupported entries does not produce a fresh Standard snapshot. Select Halatali (Hard) for the initial in-game test. Template eligibility is not additional live layout verification.

## Observed layout and limits

Four user-supplied English captures selected First Hunt (Finished), one per category. They corroborated root heading53/parent52, currency54/parent52, secondary currency58/parent52; one visible description72/73/74/75 under71; selected title component8 → text3/parent8; overview component34 → text6/parent34; reward component38 → text7 and9/parent5. These captures are not packaged. Other locales have public-sheet validation, but no live layout capture validation.

The global diagnostic contained stale component79 list rows after switching categories. The selected-detail reader remains separate from list traversal. It requires a stable owned visible addon, a unique exact localized event/currency/description match, an unambiguous whole public objective instruction, and matching fixed primary reward when defined. The matcher anchors escaped localized literals and exact target text, including variant suffixes and exclusion notes. A bounded numeric token is accepted only at an explicit supported public template placeholder; no Score field is inferred to supply that parameter. Unsupported macro forms and ambiguous candidate IDs fail closed. Substring matching cannot confuse Halatali with Halatali (Hard), or select Delubrum Reginae (Savage) from an exclusion note.

The root list is capped at512 entries, each selected component at64, parent chains at32, native string storage at1025 bytes including NUL, emitted title/overview at256 characters and catalog at200 definitions. It copies only fixed allowlisted paths, validates UTF-8 text payloads and SeString macro envelopes, and checks native ownership/text and public-sheet identity again before returning. StringLength equality is not required; bounded BufUsed, BufSize and NUL define the copy.

Only Standard returns the full selected public catalog. Ultimog returns one supported selected objective and displayed reward previews. Weekly and Minimog additionally support the complete observed backing list described below. Selected week, event timing, personal completion, reward claims and ownership are not mapped. The selected event is not labeled current or finished from native active-season data. Event-only identity is returned separately so switching to a corroborated pending/unsupported view can invalidate prior event caches.

## Active Weekly and Minimog lists

Two user-provided 0.42.2 English captures corroborate component79 as a `TreeList`, with all backing items uniquely matched to reached renderer nodes. Weekly has base length6 and13 tree items: an ignored status header followed by six type4 week headers, each with one type0 duty. Minimog has base length12 and19 tree items: the ignored status header followed by six type4 week headers, each with two type0 choice labels. All observed items have depth0 and are unhidden. These counts establish the captured complete view, not a universal event schedule or an index-domain formula. Raw captures remain private under `.test-tmp/manual-tests/results-0422/` and are not packaged.

The pinned [AtkComponentTreeList.Items](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentTreeList.cs#L14-L20) vector and [AtkComponentTreeListItem.Renderer/Type/Depth/IsHidden](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentTreeList.cs#L55-L66) provide typed association and structural fields. Component type12/renderer type14 and exact OwnerNode identity guard derived access. Selection and renderer indices are only stability metadata; neither is used to index the vector or infer a selected week. Backing Renderer pointers are equality-matched to reached components and never blindly dereferenced.

`MogpendiumActiveList` accepts at most16 week groups and64 backing items, requires the complete observed status/header/choice pattern and matching base/tree/vector counts, and rejects pending refreshes, hidden items, duplicate membership and incomplete matches. It reads only text7 from each matched week header and text4 from each matched choice. It does not parse the ignored status header, broad diagnostic text, nested opaque values, progress arrays or rewards for unselected choices. Root/component lists, backing identities, typed fields, renderer identities and bounded text are checked again; two complete copies must agree.

All six captured Weekly duty names uniquely match the selected season's public Weekly candidates using full normalized labels with casing ignored for public heading capitalization. Generic Minimog labels such as Save the Queen and Fishing do not identify a specific target; they are exported as displayed with null content references unless the full label uniquely matches a public objective. There is no inferred mission-pair or week/subrow algorithm. The displayed week label and choice position are copied as text and slot1/2, not converted into current-week or completion semantics.

List and selected-detail support are independent. A supported complete list can be returned without selected detail, including the captured Fishing selection whose item-target instruction macro is unsupported. An unsupported list layout can retain independently corroborated selected detail. A snapshot requires at least one supported list or selected detail; it cannot present an empty complete view. Other locales retain localized public-catalog support, but the new native list layout has English live evidence only.

## Focused checks

```powershell
dotnet run --project tests/Mogpendium.Tests/Mogpendium.Tests.csproj -- --produce-fixtures .test-tmp/mogpendium-results.json
dotnet run --no-build --project tests/Mogpendium.Tests/Mogpendium.Tests.csproj -- --public-catalog .test-tmp/mogpendium-public-catalog.json
dotnet run --no-build --project tests/Mogpendium.Tests/Mogpendium.Tests.csproj -- --captured-lists .test-tmp/mogpendium-public-catalog.json .test-tmp/manual-tests/results-0422/mogpendium-weekly-capture.json .test-tmp/manual-tests/results-0422/mogpendium-minimog-capture.json
```

The first command runs synthetic contract/native-projection checks and produces actual ActivityLookup wire results for Python validation, including combined list/detail, list-only and selected-detail-only cases. The second additionally binds the four supplied English detail projections against the installed public catalog. The third reads the two private supplied captures locally to verify public label binding and the generic Minimog/list-only result; this offline test parser is never used by the automatic reader. None reads a running game, sends a model request, automates menus or contacts an account service.
