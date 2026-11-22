# Third-party notices

CompAI's own source is copyright 2026 Trizor3 and uses the [GNU Affero General Public License, version 3 only](LICENSE). The software, artwork and services listed below have their own terms.

When you [install CompAI through Dalamud](INSTALL.md), the plugin comes with its provider software and notices. You can find the Codex and Grok notices in the installed plugin's `companion/licenses/` folder and the Python license in `companion/python/LICENSE.txt`.

To inspect or rebuild the software, use the source archives and checksums on the [0.53.23 release page](https://github.com/Trizor3AI/CompAI-Public/releases/tag/v0.53.23.0). The [build guide](BUILDING.md) covers the matching CompAI source. The [Codex notes](bridge/licenses/codex/README.txt) and [Grok notes](bridge/licenses/grok/README.md) identify the separate provider downloads.

## Ko-fi artwork

The unchanged `kofi_symbol.png` icon is an official [Ko-fi brand asset](https://more.ko-fi.com/brand-assets), embedded in the plugin to link to the developer's Ko-fi page. Ko-fi permits its logos and buttons for [promoting a Ko-fi page](https://help.ko-fi.com/hc/en-us/articles/360007169493-Can-I-use-the-Ko-fi-logo-to-promote-my-page), subject to its guidelines. The artwork and trademarks remain Ko-fi's; CompAI's AGPL license does not relicense them or imply endorsement. The image is loaded locally, without a remote widget.

The Settings button uses `kofi_wordmark.png`, the same wordmark and right-hand cup supplied for the Trizor3 site header. It is also embedded locally and links to the developer's Ko-fi page.

## Runtime and development dependencies

| Component | Role | Upstream license or terms |
| --- | --- | --- |
| [Dalamud](https://github.com/goatcorp/Dalamud/blob/master/LICENSE) | Host-provided plugin framework | AGPL-3.0 |
| [Dalamud.NET.Sdk](https://github.com/goatcorp/Dalamud.NET.Sdk), 15.0.0 | Build tooling | MIT, as declared by the installed NuGet package |
| [DalamudPackager](https://github.com/goatcorp/DalamudPackager), 15.0.0 | Build tooling | EUPL-1.2, as declared by the installed NuGet package |
| [DotNet.ReproducibleBuilds](https://github.com/dotnet/reproducible-builds), 1.2.39 | Build tooling | MIT, as declared by the installed NuGet package |
| [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs/blob/main/LICENSE) | Host-provided native game bindings | MIT; copyright aers and contributors |
| [Lumina](https://github.com/NotAdam/Lumina/blob/master/LICENSE) and [Lumina.Excel](https://github.com/NotAdam/Lumina.Excel/blob/master/LICENSE) | Host-provided game-data libraries | WTFPL-2.0 |
| [Dear ImGui](https://github.com/ocornut/imgui/blob/master/LICENSE.txt) | UI, through host-provided bindings | MIT; copyright Omar Cornut and contributors |
| [OpenAI Codex](https://github.com/openai/codex/blob/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a/LICENSE), 0.167.0 | Bundled companion runtime | Upstream source is Apache-2.0; see distribution status below |
| [Grok Build](bridge/licenses/grok/README.md), 1.0.62 | Bundled companion runtime | The official Windows npm package declares Apache-2.0. The Grok notes explain the included third-party notices and source coverage. |
| [Python 3.14.8](https://www.python.org/downloads/release/python-3148/) | Bundled Windows x64 embedded interpreter | Python Software Foundation license and included third-party notices; upstream `LICENSE.txt` is retained in `companion/python/` |

Dalamud supplies its framework libraries; the plugin package does not redistribute those DLLs or the build tools. This distinction is an inventory description, not a conclusion that license obligations cannot apply. CompAI's Python modules use the standard library and project-local modules; no third-party Python package is currently required. Segoe UI is loaded from the user's Windows installation when available; its font file is not bundled.

HaselDebug and SamplePlugin are source references, not packaged libraries. Both publish AGPL-3.0 source. The [HaselDebug revision referenced by the quest reader](https://github.com/Haselnussbomber/HaselDebug/blob/91b4e56b626d0347edf22f3825b187ac07bc5996/LICENSE) documents the license for that reference. Do not treat credit as permission to copy code under a different license.

## Codex runtime distribution status

CompAI 0.53.23 includes the official Windows x64 Codex 0.167.0 programs, `codex.exe` and `codex-code-mode-host.exe`, with their licenses, notices and integrity checks.

Both components are in OpenAI's [0.167.0 release](https://github.com/openai/codex/releases/tag/rust-v0.167.0). Their exact source revision is [`086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a`](https://github.com/openai/codex/tree/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a). The [code-mode host manifest](https://github.com/openai/codex/blob/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a/codex-rs/code-mode-host/Cargo.toml) inherits the workspace's Apache-2.0 license. The exact [LICENSE](https://github.com/openai/codex/blob/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a/LICENSE) and [NOTICE](https://github.com/openai/codex/blob/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a/NOTICE), which credits Ratatui, are retained with their source/checksum mapping.

The matching runtime configuration passed local checks using synthetic data. These checks do not establish live account entitlement, a clean installation or the correctness of a later package or configuration.

The installed Codex notice text maps 1,077 selected packages to 709 retained text sections. The separate notice archive preserves original text bytes, public source mappings and exact-revision recoveries. It also records 483 native-source notice mappings and a separate 263-package producer full-lock inventory. Seven producer-only packages have license declarations without established separate copyright/license notices; standard terms are supplied and that attribution limit remains explicit. Listing a source component does not assert every file is present in each executable. See the installed README and ATTRIBUTION-LIMITS.txt for the complete scope.

A local modification and relinking check rebuilt the V8 152.2 native library and the code-mode host, then ran an empty-tool calculation that returned `42`. Only the host was rebuilt in that consumer check; it did not rebuild the Codex CLI or network proxy or reproduce the official executable bytes. The verification binaries and their test marker are not release runtime files.

The [0.53.23 release](https://github.com/Trizor3AI/CompAI-Public/releases/tag/v0.53.23.0) includes `native-sources-rusty-v8-152.2.0-win-x64.zip`, `native-producer-dependencies-152.2.0.zip`, `codex-0.167.0-sources-and-dependencies.zip` and `codex-third-party-notices-0.167.0-windows-x64.zip`. The source assembly and archive checks passed. The documented MXC projection omits one unused WSL nupkg only for the checked Windows default three-root selection. The original source and license material and the scope of that omission are retained.

## Additional account runtimes

Grok Build 1.0.62 comes from `https://x.ai/cli/grok-1.0.62-windows-x86_64.exe`. Its Windows signature was checked as valid for X Corp. A passive comparison established that its program image matches the unsigned executable in the exact official `@xai-official/grok-win32-x64` 1.0.62 npm package. That package explicitly declares Apache-2.0. The ZIP now retains the full license text and its unchanged supplied third-party notice under `companion/licenses/grok/`, with provenance in `companion/licenses/manifest.json`.

The supplied npm notice covers the tool crate. The package also retains the complete broader notice from pinned public source commit `2bdd1d6a6369de0e8c68132ea4539e9abd9e14a8`, clearly labelled as a supplementary reference. None of the 50 published `SOURCE_REV` revisions checked on 2026-11-06 matches the binary's revision; that mismatch is an evidence limit, not by itself an Apache-2.0 redistribution prohibition.

Passive version/source-path markers in the executable corroborate identified MPL-covered CSS and Nucleo components. Their matching public original sources, related notices and libgit2 source are in [grok-1.0.62-attribution-source-candidate.zip](https://github.com/Trizor3AI/CompAI-Public/releases/download/v0.53.23.0/grok-1.0.62-attribution-source-candidate.zip). The installed `companion/licenses/grok/README.md` gives the archive's checksum and source coverage. No positive evidence of missing provider modifications was found; their absence has not been independently proved. The libgit2 linking exception is preserved rather than treating its GPL text as an unqualified requirement for the whole executable.

The installer includes the Grok executable. xAI service terms and account entitlement apply separately.

The [account runtime manifest](bridge/account-runtimes.json) records exact official source URLs, sizes and SHA-256/SHA-512 digests. Adapter pins separately record the runtime/tool configuration that was checked. These are integrity and provenance records, not a blanket license grant or service entitlement.

For reference, GitHub Copilot CLI 1.0.98 was evaluated from the official `@github/copilot-win32-x64` npm package. CompAI 0.53.23 ships neither the Copilot CLI nor its SDK. The **GitHub Copilot CLI License** governs that binary. Its redistribution conditions include supplying it unmodified as part of an application with material additional functionality and retaining notices. The SDK has a separate MIT license, and GitHub service terms apply separately.

## Embedded Python provenance

The Python payload is the official `python-3.14.8-embed-amd64.zip` from python.org, verified against the release page's SHA-256 `a93abe456ab01bd96d7a085b3cdb6566b3063f4241360d114142fbdb07f0a310`. All original payload files, including `LICENSE.txt`, are retained. The only modified file is `python314._pth`: it restricts imports to the bundled standard library, interpreter directory and adjacent CompAI bridge, without importing `site`. `python-runtime.json` records both the upstream archive digest and the resulting per-file digests. The source repository carries preparation/check scripts and the manifest; interpreter binaries remain excluded from Git.

## Data services and game content

XIVAPI and Universalis are contacted as services when permitted, rather than bundled as libraries. Their service and underlying data terms apply separately from any server-source license. Links and timestamps are kept with returned data. The bundled item reference identifies its Eorzea Database and community wiki sources in `item_sources.json`; it does not grant redistribution rights to other game or wiki content.
