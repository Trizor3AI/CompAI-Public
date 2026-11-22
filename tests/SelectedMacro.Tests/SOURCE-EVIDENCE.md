# Selected loaded macro observation

Checked 2026-10-26. This reader copies one selected loaded record while the ordinary Macro window is ready and visible. It does not claim that the record includes an uncommitted editor draft or is already saved to disk. Name and all fifteen positional lines are untrusted data. No command is executed, submitted, saved or modified.

## Native source and selection

The public FFXIVClientStructs links below are pinned to revision `b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c`. Installed metadata was independently checked: file version `7.57.0.9414`, informational version `1.0.0+dd57986b8287e7c6ed9c27198f8c76cb04a81dfe`. The required field types, offsets, struct sizes and managed fixed-array accessor IL agree. This does not assert that the entire installed assembly equals that upstream revision.

- [AgentMacro](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentMacro.cs) exposes current SelectedMacroSet and SelectedMacroIndex. [MacroMate's set enum](https://github.com/grittyfrog/MacroMate/blob/4b41fc0b40c6156fe67b0a3f64d83a4f00e430ae/MacroMate/Extensions/Dalamud/Macros/VanillaMacroSet.cs) maps individual=0, shared=1; its [Macro context-menu implementation](https://github.com/grittyfrog/MacroMate/blob/4b41fc0b40c6156fe67b0a3f64d83a4f00e430ae/MacroMate/ContextMenu/ContextMenuManager.cs) corroborates using those agent fields to identify the loaded record. No context-menu hooks or actions are reused.
- [AddonMacro](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/UI/AddonMacro.cs) identifies the Macro addon. Its own SelectedMacroIndex is only set on page changes and is deliberately ignored.
- [RaptureMacroModule](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/UI/Misc/RaptureMacroModule.cs) has fixed100 Individual/Shared arrays. Each Macro contains one Name and fixed15 Lines. Installed generated span getters call managed InlineArrayAsSpan with100/100/15. The reader indexes exactly one record and does not invoke GetMacro, GetLineCount, IsNotEmpty or modification methods.
- [UserFileManager.UserFileEvent](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/UI/Misc/UserFileManager.cs) supplies the inherited CharacterContentId. It must match the loaded player; persistence flags are not treated as readiness.
- [Utf8String](https://github.com/aers/FFXIVClientStructs/blob/b72bc1f73e5f2b09ba460a9238fbf3b9a5bbeb3c/FFXIVClientStructs/FFXIV/Client/System/String/Utf8String.cs) uses BufUsed, including the final NUL, for Length/AsSpan. StringLength is not used for copying. RawMacroString, ParsedMacroString, clipboard, history, other macros and private user files are not read.

## Capture contract

`SelectedMacroReader(IGameGui)` exposes `Capture(out bool menuObserved)`. The flag becomes true after the exact agent-owned ready visible Macro addon is matched, before selection/module/string validation. The caller can invalidate an old retained selection after an unreadable new selection or edit. A normal closed window may retain its last observation under the existing fifteen-minute/current-login rules.

The detached result is `SelectedMacroObservation(CapturedAtUtc, int Set, int Index, string Name, IReadOnlyList<string> Lines)`. Index is native0..99. Lines always contains15 strings; empty names and positional empty lines are preserved. Every native string must be initialized, bounded and NUL terminated, so zeroed/uninitialized buffers are unavailable rather than blank.

Capture policy limits are128 UTF-8 bytes for Name and512 bytes per line, excluding the terminator; these are not claims about game limits. No truncation or whitespace normalization occurs. Strict UTF-8 rejects malformed encodings. Name rejects C0/C1 controls; lines allow TAB but reject the others, including CR/LF and SeString payload markers. Unsupported auto-translate payloads make the whole observation unavailable rather than silently changing a command.

Native structure pointers must be positive and pointer-size aligned. Byte buffers need no structure alignment. Owner, module, selected set/index, addon, every string pointer/used-length/capacity and bytes must remain stable across bounded copies and repeated owner resolutions. No source offsets are hardcoded. Readiness resolution follows the existing framework-thread agent/addon pattern.

## Focused validation

Run from the repository root:

```text
dotnet run --project tests/SelectedMacro.Tests/SelectedMacro.Tests.csproj
```

The fixture uses synthetic unmanaged allocations and managed seams only. It never invokes the production singleton resolvers, attaches to a game process, reads player files or calls a provider. Final result:21 groups passed, exit0, no warnings. Coverage includes both sets, indexes0/99, exact fifteen-line ordering, Unicode/whitespace/TAB, initialized blanks, malformed encodings/payloads, byte limits, owner/selection/addon changes, misaligned structures, valid unaligned byte buffers, string metadata/content changes and detached immutable output.

In-game selected-record/draft correspondence remains unverified. The useful user comparison is one harmless macro in each set, a selection change and same-selection edit, plus close/revoke behavior. No full macro-library dump is needed.
