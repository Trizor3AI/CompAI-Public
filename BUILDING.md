# Build CompAI on Windows

To use CompAI in game, follow the [installation guide](INSTALL.md). To rebuild 0.53.23, use the matching source and player ZIPs. The source ZIP supplies the plugin code, and the player ZIP supplies the exact Python and provider binaries used in that release. [0.53.23 downloads](https://github.com/Trizor3AI/CompAI-Public/releases/tag/v0.53.23.0)

## What you need

- 64-bit Windows, PowerShell, the .NET 10 SDK and Python 3.12 or later. The Python on your PATH runs the build checks. The plugin ships its own Python 3.14.8.
- Dalamud API 15 development files. The release was built with Dalamud 15.0.4.1 and `Dalamud.NET.Sdk` 15.0.0. Set `DALAMUD_HOME` to the directory containing `Dalamud.dll` if the SDK cannot find it or you keep several versions.
- These three release files together in one folder: `CompAI-0.53.23.0-source.zip`, `CompAI-0.53.23.0.zip` and `SHA256SUMS`.

## Prepare the source and runtimes

1. In the download folder, run the following command. Compare each hash with the line for that filename in `SHA256SUMS`. Stop if either differs.

   ```powershell
   Get-FileHash -Algorithm SHA256 -Path .\CompAI-0.53.23.0-source.zip, .\CompAI-0.53.23.0.zip
   ```

2. Use fresh folders for the source and the unpacked player ZIP. Run these commands from the same download folder. The last command enters the source directory containing `CompAI\CompAI.csproj`.

   ```powershell
   Expand-Archive -LiteralPath .\CompAI-0.53.23.0-source.zip -DestinationPath .\source
   Expand-Archive -LiteralPath .\CompAI-0.53.23.0.zip -DestinationPath .\player
   $companion = (Resolve-Path -LiteralPath .\player\companion).Path
   Set-Location -LiteralPath .\source\CompAI-0.53.23.0-source
   ```

3. Copy the three runtime directories into the source tree. Together they supply embedded Python, Grok Build 1.0.62 and the matched Codex 0.167.0 executable and host. Keep the rest of the source tree as extracted for an exact release comparison.

   ```powershell
   New-Item -ItemType Directory -Path .\bridge\runtime -ErrorAction Stop | Out-Null
   Copy-Item -LiteralPath "$companion\python" -Destination .\bridge\runtime\python-win-x64 -Recurse
   Copy-Item -LiteralPath "$companion\runtime\grok-win-x64" -Destination .\bridge\runtime\grok-win-x64 -Recurse
   Copy-Item -LiteralPath "$companion\runtime\win-x64" -Destination .\bridge\runtime\win-x64 -Recurse
   ```

## Build and check the package

From the source directory, run each command after the previous one succeeds. On a new machine, restore needs access to NuGet or a cache containing the locked packages.

```powershell
dotnet restore CompAI/CompAI.csproj --locked-mode
dotnet build CompAI/CompAI.csproj -c Release --no-restore
python -B CompAI/verify_package.py
```

The release ZIP is `CompAI/obj/companion-package-release/CompAI/latest.zip`. The verifier checks the packaged files and their versions, validates the runtime pins and starts the relocated companion with a temporary profile. It runs offline without signing in to an AI account. [Package verifier](CompAI/verify_package.py)

The 0.53.23 release check rebuilt the frozen source on the same Windows PC with cached dependencies and the shipped runtimes. All 118 files inside the ZIP matched the original byte for byte, including `CompAI.dll`. The two ZIPs had different hashes because their headers stored different timestamps. Reproduction on a second machine is unverified.

## Provider source

Rebuilding CompAI with the steps above reuses the shipped provider binaries. To work on Codex itself, start with `README-consumer.txt` and `README-native.txt` in `codex-0.167.0-sources-and-dependencies.zip`. They describe the additional native archives and toolchain. The Grok source download covers identified third-party components. It contains no recipe for recreating the full shipped Grok executable. [Codex source notes](bridge/licenses/codex/README.txt) · [Grok source notes](bridge/licenses/grok/README.md) · [Third-party notices](THIRD-PARTY-NOTICES.md)
