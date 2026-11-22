# Manual Mogpendium list structure

This diagnostic supports controlled human comparisons. It is not an AI reader, a complete assignment list, or evidence of objective completion, reward claims, selected week or current event timing.

The manual Mogpendium clipboard output adds `mogpendium_lists` with schema `mogpendium_list_structure_v2`. Other menu outputs keep their previous shape. Existing visible text remains useful context, but may contain recycled/stale rows.

## Source and bounds

Pinned FFXIVClientStructs commit: `dd57986b8287e7c6ed9c27198f8c76cb04a81dfe`.

- [AtkComponentBase.GetComponentType](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentBase.cs) guards every derived cast. The installed enum identifies List9, TreeList12 and ListItemRenderer14; a component node type alone is insufficient. The accessor is a native read, never a mutation.
- [AtkComponentList](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentList.cs) supplies logical length, selection, first-visible index, visible count, renderer allocation count and pending flags. Its stale "type1" comment is not used.
- [AtkComponentTreeList](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentTreeList.cs#L14-L66) supplies tree counts, layout-pending state and the typed Items vector. A bounded prefix of item pointers and their scalar Renderer/Type/Depth/State/IsHidden fields is copied. Renderer is used only for equality with already-reached, type-checked, owned components; an unmatched renderer pointer is never dereferenced. UIntValues, StringValues and the base renderer array remain unread.
- [AtkComponentListItemRenderer](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Component/GUI/AtkComponentListItemRenderer.cs) supplies `ListItemIndex` for renderer components already reached by the bounded visible walk. Tree backing vector slots and renderer indices are recorded separately. Matching duplicate indices alone is insufficient: stale widgets are excluded by backing pointer identity. Unmatched/null/ambiguous backing entries are counted explicitly.

Caps:8 list components,512 bounded renderer candidates,64 tree backing slots across the capture,32 exported renderer associations,512 count metadata, existing depth8 and2048 node-examination limits, existing16KB total JSON limit. Tree selection is bounded independently from the base ListLength; selected_in_base_list and selected_in_tree_vector are descriptive comparisons, not proof of the index domain. Ordinary-list selection checks remain unchanged. Malformed vector headers are rejected before traversal. Only matched tree associations consume the32-row output cap. Actual truncations are explicit; if metadata alone exceeds16KB, renderer records are omitted with output_renderers_omitted instead of discarding the whole diagnostic. List summaries are retained.

The existing ready/visible addon and character ownership checks remain. List owner nodes, component types, pointer/header identity, counts, selection and renderer indices are checked again before returning. Backing item slot identity and scalar fields are also rechecked; the two copies must agree. Internal native addresses never enter the serialized records.

## Evidence to request

Keep First Hunt selected; do not attempt to select the disabled Second Hunt.

1. Weekly: select a late week/objective that previously caused a bounds error, then copy the diagnostic.
2. Minimog: select one choice that previously failed or was partial, then copy the diagnostic.

For each copy, record the visible week/highlight and selected detail. The original five-capture procedure can wait until the corrected diagnostic works. Further paired selections may still be necessary to establish week/choice semantics. Structural matches alone do not establish every personal assignment, completion or claims; raw indices are not content IDs.

## Offline validation

`dotnet run --project tests/ActivityLogs.Tests/ActivityLogs.Tests.csproj -- --diagnostics-only`

The focused run includes seven structural groups plus the existing thirteen diagnostic groups. The six-base-entry/thirteen-tree-row regression fails before the correction and passes afterwards, including a late selection and sixty recycled widgets. Fixtures also cover same-index stale widgets, changed/duplicate backing references, unmatched renderer pointers that must never be followed, ordinary-list bounds, metadata-only output limits, and an invalid65th item pointer beyond the traversal cap. Fixtures inject component-type results and use synthetic allocations; the new backing association still requires in-game verification. No game, model or account request is made by these tests.
