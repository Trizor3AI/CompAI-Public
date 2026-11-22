# Cosmic Exploration currency mapping

The installed API exposes `AgentWKSHud.HudInfo` with State, CosmoCredits/Max/Icon,
ZoneCredits/Max/ItemId/Icon and `WKSManager` with IsLoaded/TerritoryId. The documented
HUD states 1/2 are fetching data for the current zone; 3 is ready. The manager's
loaded flag is cleared on unload. CompAI only reads these typed fields; it does not
invoke Load, send commands, open menus or read unnamed storage.

Primary contracts checked on 2026-10-26:
- [AgentWKSHud](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentWKSHud.cs)
- [WKSManager](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/Game/WKS/WKSManager.cs)

A standalone offline metadata/public-sheet probe confirmed the installed fields and
these local Item definitions: 45690 Cosmocredit (icon 65112, stack 30000),
45691 Lunar Credit (65126, stack 10000), 48146 Phaenna Credit (65127, stack 10000).
The 0.47.1 follow-up scanned the whole installed public Item filter group 56 and
also confirmed 48147 Oizys Credit (65128, stack 10000) and 48148 Auxesia Credit
(65129, stack 10000). Those were omitted by the original name-filtered probe,
not absent from the game or unreadable through the typed HUD. All four are now
explicitly supported and exercised through the same capture/transport path.
The production reader uses local names/icons and the typed HUD's planet item ID;
it does not hardcode translated names or infer a planet from a generic balance.
Stack sizes are evidence only; validation uses the corresponding observed HUD cap.
Other planet IDs remain unsupported until corroborated.

The same managed capture seam used in production validates two complete samples
around public metadata resolution. Fixtures cover loading/zone/identity changes,
invalid caps/counts, definition changes, explicit zero and conflicting ordinary
balances. Lumina returns new typed sheet wrappers, so production metadata identity
uses RawSheet, GameData and language rather than wrapper identity.

The two returned balances join the existing per-answer currency snapshot and are
not persisted or polled. A new question outside the loaded zone cannot reuse a
previous HUD observation. Unavailable/absent balances mean unknown, not zero.
Existing ordinary currencies remain independent of an unready Cosmic HUD.
Actual in-game values, hydration and zone-exit behavior still require comparison.
