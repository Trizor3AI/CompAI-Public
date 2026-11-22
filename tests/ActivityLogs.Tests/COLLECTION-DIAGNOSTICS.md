# Manual collection and Mahjong evidence

These diagnostic extensions are user-triggered clipboard captures, not AI readers. They do not establish unlocks, missing entries or Mahjong statistics until matched against the visible game UI. Visible text may include custom names; review before sharing.

## Pinned source

The installed FFXIVClientStructs commit is `dd57986b8287e7c6ed9c27198f8c76cb04a81dfe`.

- [AddonGoldSaucerInfo](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/AddonGoldSaucerInfo.cs) supplies the selected category and embedded addon control. Category 5 is Mahjong. The earlier live diagnostic contained only this parent's tab labels.
- [AtkAddonControl](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Component/GUI/AtkAddonControl.cs) supplies parent ownership, setup state and the direct child list. Child `TabIndex` is not assumed to equal category 5. Exactly one visible, ready, linked child is eligible for text copying; ambiguity keeps the parent-only capture.
- [StdList](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/STD/StdList.cs) exposes `WithOps.Head` and `Size`. The diagnostic must not call helpers which allocate a missing sentinel. Traversal is bounded, validates links and rejects changing storage.
- [AddonMYCWarResultNotebook](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/AddonMYCWarResultNotebook.cs) defines three raw display integers: `MaxNoteIndex`, `CurrentNoteIndex`, `CurrentPageIndex`. Exact addon ownership/name is required before the cast. These are not registration flags; public sheet row IDs also differ from displayed record numbers in some cases.
- [AgentModule](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentModule.cs) defines `Description = 320`. No typed AgentDescription or verified Portable Archive addon mapping is available. The candidate uses ordinary exact agent-owned addon resolution, without an assumed window name or derived cast. ArchiveItem remains a separate target.

## Useful captures

In Settings → Diagnostics → Reader diagnostics, use **Copy selected menu diagnostic**:

1. Gold Saucer: open its Mahjong tab, let it load, and copy once. Include the visible rating/rank labels or a screenshot for comparison. The report says which addon supplied its text.
2. Field Record: open the actual Field Record book, select a registered record, then use **Copy Field Record diagnostic**. This direct button bypasses the menu picker. If an unregistered entry is available, repeat for that entry. Include each displayed number/title and registration state; no need to unlock anything. The separate Collection menu target captures the launcher, not this book.
3. Portable Archive candidate (Description): open Portable Archive and copy. Include the window title and selected entry. An unavailable result is useful evidence; do not substitute an unrelated item window.

All paths retain repeated character/menu identity checks, two matching text copies, bounded text traversal, explicit omissions and a 16 KiB total output cap. Raw pointers and character IDs are excluded. No model request, disk capture, global window scan or game-server request is made.

## Offline verification

`dotnet run --project tests/ActivityLogs.Tests/ActivityLogs.Tests.csproj -- --diagnostics-only`

Synthetic fixtures cover list ownership/bounds, changed or ambiguous children, exact scalar projection gates, selection changes and output limits. They do not verify actual game layout correspondence.
