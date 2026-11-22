"""Offline allowlist and relocation check for the Dalamud release ZIP."""

from __future__ import annotations

from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tools"))
from prepare_python_runtime import read_pin, verify_bundle
from publication_guard import check_bytes
from bridge.companion_process import close_owned_process, start_owned_process

PYTHON_PIN = read_pin()
HASH_CHUNK_BYTES = 1024 * 1024
MAX_MANIFEST_BYTES = 1024 * 1024
MAX_TEXT_BYTES = 4 * 1024 * 1024
MAX_ENTRY_BYTES = 512 * 1024 * 1024
MAX_ARCHIVE_BYTES = 1024 * 1024 * 1024
HELP_TIMEOUT_SECONDS = 30
SMOKE_TIMEOUT_SECONDS = 120
BINARY_SUFFIXES = {".dll", ".exe", ".pyd", ".zip", ".cat"}
ARCHIVE = Path(__file__).resolve().parent / "obj/companion-package-release/CompAI/latest.zip"
RELEASE_MANIFEST = ROOT / "CompAI/obj/companion-package-release/CompAI.json"
ASSETS = [
    "LICENSE",
    "CREDITS.md",
    "THIRD-PARTY-NOTICES.md",
    "licenses/codex/LICENSE.txt",
    "licenses/codex/NOTICE.txt",
    "licenses/codex/THIRD-PARTY-NOTICES.txt",
    "licenses/codex/ATTRIBUTION-LIMITS.txt",
    "licenses/codex/README.txt",
    "licenses/grok/LICENSE.txt",
    "licenses/grok/THIRD_PARTY_NOTICES.md",
    "licenses/grok/THIRD-PARTY-NOTICES.upstream-2bdd1d6.txt",
    "licenses/grok/LICENSE-MPL-2.0.txt",
    "licenses/grok/README.md",
    "licenses/manifest.json",
    "companion.py",
    "currency_lookup.py",
    "duty_lookup.py",
    "collection_lookup.py",
    "progression_lookup.py",
    "crafting_log_lookup.py",
    "menu_lookup.py",
    "activity_lookup.py",
    "game_definitions_lookup.py",
    "gc_supply_lookup.py",
    "npc_target_lookup.py",
    "chat_attachments.py",
    "companion_request.py",
    "companion_tools.py",
    "deferred_tools.py",
    "saved_item_search.py",
    "saved_item_valuation.py",
    "market_batch.py",
    "market_request_queue.py",
    "valuation_checkpoint.py",
    "companion_provider.py",
    "codex_tool_policy.py",
    "grok_provider.py",
    "grok_credentials.py",
    "grok_oauth.py",
    "credential_store.py",
    "grok_runtime_pin.json",
    "runtime/grok-win-x64/grok.exe",
    "python-runtime.json",
    "companion_metrics.py",
    "companion_process.py",
    "streamed_answer.py",
    "prepare_isolation_catalog.py",
    "runtime_bundle.py",
    "runtime/win-x64/codex.exe",
    "runtime/win-x64/codex-code-mode-host.exe",
    "inventory_question.py",
    "inventory_lookup.py",
    "storage_lookup.py",
    "game_context_lookup.py",
    "npc_dialogue_lookup.py",
    "character_details_lookup.py",
    "chat_history_lookup.py",
    "personality.py",
    "personality_details_lookup.py",
    "game_data_tools.py",
    "gear_plan_lookup.py",
    "quest_context.py",
    "quest_lookup.py",
    "model_selection.py",
    "item_sources.json",
    *[
        f".isolation-probe/{name}.json"
        for name in (
            "no-patch-models", "runtime-pin", "validation-success", "web-search-validation", "model-selection-validation",
            "verified-shell-control", "verified-text", "verified-snapshot",
            "verified-deny-command", "verified-deny-patch", "verified-inspect",
            "verified-mcp-resources", "verified-mcp-inherited-server",
        )
    ],
]
EXPECTED = {"CompAI.dll", "CompAI.deps.json", "CompAI.json"} | {
    f"companion/{name}" for name in ASSETS
} | {f"companion/python/{name}" for name in PYTHON_PIN['files']}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def stream_sha256(stream, limit: int) -> str:
    digest = hashlib.sha256()
    count = 0
    while block := stream.read(min(HASH_CHUNK_BYTES, limit - count + 1)):
        count += len(block)
        if count > limit:
            raise AssertionError("Payload exceeds its checked size")
        digest.update(block)
    return digest.hexdigest()


def read_bounded(stream, limit: int) -> bytes:
    payload = stream.read(limit + 1)
    if len(payload) > limit:
        raise AssertionError("Text payload exceeds its size limit")
    return payload


def read_bounded_file(path: Path, limit: int) -> bytes:
    if path.stat().st_size > limit:
        raise AssertionError(f"Text file exceeds its size limit: {path.name}")
    with path.open("rb") as stream:
        return read_bounded(stream, limit)


def read_zip_text(bundle, info, limit: int) -> bytes:
    if info.file_size > limit:
        raise AssertionError(f"Archive text exceeds its size limit: {info.filename}")
    with bundle.open(info) as stream:
        return read_bounded(stream, limit)


def package_sources() -> dict[str, Path]:
    sources = {
        f"companion/{name}": (ROOT if name in ("LICENSE", "CREDITS.md", "THIRD-PARTY-NOTICES.md")
                              else ROOT / "bridge") / name
        for name in ASSETS
    }
    sources.update({name: ROOT / "CompAI/bin/Release" / name for name in ("CompAI.dll", "CompAI.deps.json")})
    sources.update({f"companion/python/{name}": ROOT / "bridge/runtime/python-win-x64" / name
                    for name in PYTHON_PIN['files']})
    return sources


def check_entry_sizes(entries, sources) -> None:
    # Inspect every entry before reading any payload. Only the generated manifest
    # may differ in byte length: its bounded JSON is compared semantically below.
    if sum(info.file_size for info in entries.values()) > MAX_ARCHIVE_BYTES:
        raise AssertionError("Archive exceeds the total uncompressed size limit")
    for name, info in entries.items():
        limit = (MAX_MANIFEST_BYTES if name == "CompAI.json" else
                 MAX_ENTRY_BYTES if Path(name).suffix.lower() in BINARY_SUFFIXES else MAX_TEXT_BYTES)
        if not 0 <= info.file_size <= limit:
            raise AssertionError(f"Archive entry exceeds its size limit: {name}")
        if name != "CompAI.json" and info.file_size != sources[name].stat().st_size:
            raise AssertionError(f"Packed entry size differs from source: {name}")


def run_checked_process(command, *, cwd, timeout: int, env=None, capture_output=True):
    # Assign the suspended root to a kill-on-close job before any child can start.
    process = start_owned_process(command, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                                  stdout=subprocess.PIPE if capture_output else None,
                                  stderr=subprocess.PIPE if capture_output else None, text=True)
    try:
        stdout, stderr = process.communicate(timeout=timeout)
        if process.returncode:
            raise subprocess.CalledProcessError(process.returncode, command, stdout, stderr)
        return subprocess.CompletedProcess(command, process.returncode, stdout, stderr)
    finally:
        close_owned_process(process)


@contextmanager
def relocation_temporary_directory():
    temporary = tempfile.TemporaryDirectory(prefix="compai-relocation-")
    try:
        yield Path(temporary.name)
    finally:
        # Job termination is asynchronous; retry only our newly created directory.
        for attempt in range(21):
            try:
                temporary.cleanup()
                break
            except PermissionError:
                if attempt == 20:
                    raise
                time.sleep(0.1)


def normalized_version(value: object, source: str) -> str:
    # Release versions are explicit numeric triples/quads, not wildcard, prerelease,
    # property-expanded or truncated versions. 65535 is reserved by assembly metadata.
    if (not isinstance(value, str) or len(value) > 23 or
            not re.fullmatch(r"(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2,3}", value)):
        raise AssertionError(f"Unsupported {source}: expected three or four numeric version components")
    parts = [int(part) for part in value.split(".")]
    if any(part >= 65535 for part in parts):
        raise AssertionError(f"Unsupported {source}: version component exceeds 65534")
    return ".".join(str(part) for part in parts + [0] * (4 - len(parts)))


def project_version(project_xml: bytes) -> str:
    try:
        project = ET.fromstring(project_xml)
    except ET.ParseError as error:
        raise AssertionError("Malformed project XML while checking release Version") from error
    versions = [(group, version) for group in project.findall("./{*}PropertyGroup")
                for version in group.findall("{*}Version")]
    if len(versions) != 1:
        raise AssertionError("Project must declare exactly one unconditional Version")
    group, version = versions[0]
    if "Condition" in group.attrib or "Condition" in version.attrib or len(version):
        raise AssertionError("Project must declare exactly one unconditional Version")
    return normalized_version(version.text, "project Version")


def read_manifest(payload: bytes, source: str) -> dict:
    if len(payload) > MAX_MANIFEST_BYTES:
        raise AssertionError(f"The {source} plugin manifest exceeds its size limit")

    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("Duplicate JSON property")
            result[key] = value
        return result

    def invalid_constant(_value):
        raise ValueError("Nonstandard JSON number")

    try:
        manifest = json.loads(payload, object_pairs_hook=unique_object, parse_constant=invalid_constant)
    except (ValueError, UnicodeError) as error:
        raise AssertionError(f"Malformed {source} plugin manifest") from error
    if not isinstance(manifest, dict):
        raise AssertionError(f"Malformed {source} plugin manifest: expected an object")
    return manifest


def verify_manifest(packaged_json: bytes, generated_json: bytes, project_xml: bytes) -> str:
    expected_version = project_version(project_xml)
    packaged = read_manifest(packaged_json, "packaged")
    generated = read_manifest(generated_json, "generated Release")
    for field, expected in {"Author": "Trizor3", "Name": "CompAI", "InternalName": "CompAI"}.items():
        if packaged.get(field) != expected:
            raise AssertionError(f"Plugin manifest {field} differs from the public project identity")
    for source, manifest in (("packaged", packaged), ("generated Release", generated)):
        if normalized_version(manifest.get("AssemblyVersion"), f"{source} AssemblyVersion") != expected_version:
            raise AssertionError(f"The {source} plugin manifest AssemblyVersion differs from project Version")
    # Compare all SDK-generated metadata, not a handpicked field subset. Canonical
    # JSON ignores property order/spacing while retaining JSON types (true != 1).
    canonical = lambda value: json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False)
    if canonical(packaged) != canonical(generated):
        raise AssertionError("Packaged plugin manifest differs from the generated Release manifest")
    return expected_version


def verify_compiled_metadata(dll: Path, version: str, temporary: Path) -> None:
    # Use the SDK's metadata reader without loading the plugin. The checker needs
    # only the installed framework, so its NuGet source and caches can be empty.
    scratch = temporary / "metadata-check"
    feed = scratch / "empty-feed"
    feed.mkdir(parents=True)
    env = os.environ.copy()
    env.update(TEMP=str(scratch), TMP=str(scratch), TMPDIR=str(scratch),
               APPDATA=str(scratch / "roaming"), LOCALAPPDATA=str(scratch / "local"),
               DOTNET_CLI_HOME=str(scratch), NUGET_PACKAGES=str(scratch / "packages"),
               DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_ADD_GLOBAL_TOOLS_TO_PATH="false",
               DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE="true", MSBuildEnableWorkloadResolver="false")
    run_checked_process(["dotnet", "run", "--disable-build-servers", "-p:UseSharedCompilation=false",
                         f"-p:RestoreSources={feed}", "--file", str(ROOT / "tools/verify_release_metadata.cs"),
                         "--", str(dll), version], cwd=ROOT, env=env, timeout=120, capture_output=False)


def verify_license_manifest(payload: bytes, read_file) -> None:
    manifest = read_manifest(payload, "provider license")
    expected = {name.removeprefix("licenses/") for name in ASSETS
                if name.startswith("licenses/") and name != "licenses/manifest.json"}
    rows = manifest.get("files")
    if type(manifest.get("schema")) is not int or manifest["schema"] != 1 or type(rows) is not list:
        raise AssertionError("Invalid provider license manifest schema or file list")
    seen = set()
    for row in rows:
        if type(row) is not dict:
            raise AssertionError("Invalid provider license file record")
        name, digest = row.get("path"), row.get("sha256")
        if type(name) is not str or name not in expected or name in seen:
            raise AssertionError("Unexpected or duplicate provider license path")
        if type(digest) is not str or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
            raise AssertionError("Invalid provider license checksum")
        if sha256(read_file(name)) != digest:
            raise AssertionError(f"Provider license checksum differs: {name}")
        seen.add(name)
    if seen != expected:
        raise AssertionError("Provider license manifest does not cover all installed notices")


def main() -> int:
    archive = Path(sys.argv[1]) if len(sys.argv) > 1 else ARCHIVE
    with zipfile.ZipFile(archive) as bundle:
        entries = {info.filename.replace("\\", "/"): info for info in bundle.infolist()}
        if len(entries) != len(bundle.infolist()) or set(entries) != EXPECTED:
            raise AssertionError(f"Archive membership differs from allowlist: {sorted(set(entries) ^ EXPECTED)}")
        if len({name.casefold() for name in entries}) != len(entries):
            raise AssertionError("Case-insensitive duplicate archive path")
        sources = package_sources()
        check_entry_sizes(entries, sources)
        for name in entries:
            parts = Path(name).parts
            if name.startswith("/") or ":" in name or ".." in parts or not parts:
                raise AssertionError(f"Unsafe archive path: {name}")
            # Native payloads are pinned separately; inspect every textual asset
            # before relocation, without including matched credential data in errors.
            if Path(name).suffix.lower() not in BINARY_SUFFIXES:
                findings = check_bytes(name, read_zip_text(bundle, entries[name], MAX_TEXT_BYTES))
                if findings:
                    raise AssertionError(f"Publication guard rejected package entry: {name}: {findings}")
        version = verify_manifest(read_zip_text(bundle, entries["CompAI.json"], MAX_MANIFEST_BYTES),
                                  read_bounded_file(RELEASE_MANIFEST, MAX_MANIFEST_BYTES),
                                  (ROOT / "CompAI/CompAI.csproj").read_bytes())
        verify_license_manifest(read_zip_text(bundle, entries["companion/licenses/manifest.json"], MAX_MANIFEST_BYTES),
            lambda name: read_zip_text(bundle, entries["companion/licenses/" + name], MAX_TEXT_BYTES))
        for name, source in sources.items():
            info = entries[name]
            with bundle.open(info) as stream:
                packed_hash = stream_sha256(stream, info.file_size)
            if name.startswith("companion/python/"):
                expected_hash = PYTHON_PIN['files'][name.removeprefix("companion/python/")]
            else:
                with source.open("rb") as stream:
                    expected_hash = stream_sha256(stream, info.file_size)
            if packed_hash != expected_hash:
                raise AssertionError(f"Packed entry differs from source or pin: {name}")

        with relocation_temporary_directory() as temporary:
            moved = temporary / "unrelated-install-location"
            for name, info in entries.items():
                target = moved / name
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.open(info) as packed, target.open("wb") as output:
                    shutil.copyfileobj(packed, output)

            verify_compiled_metadata(moved / "CompAI.dll", version, temporary)
            companion = moved / "companion"
            verify_bundle(companion / 'python', PYTHON_PIN)
            python = str(companion / 'python' / PYTHON_PIN['executable'])
            evidence = companion / ".isolation-probe"
            pin = json.loads((evidence / "runtime-pin.json").read_text(encoding="utf-8"))
            validation = json.loads((evidence / "validation-success.json").read_text(encoding="utf-8"))
            if sha256((evidence / "no-patch-models.json").read_bytes()) != pin["catalog_sha256"]:
                raise AssertionError("Relocated catalog does not match runtime pin")
            if not validation["all_passed"] or any(
                validation[key] != pin[key] for key in ("binary_sha256", "catalog_sha256", "model", "runtime_files")
            ):
                raise AssertionError("Relocated validation does not match runtime pin")
            for case in validation["cases"]:
                name = case["file"]
                if Path(name).name != name or sha256((evidence / name).read_bytes()) != case["sha256"]:
                    raise AssertionError(f"Relocated evidence does not match: {name}")
            env = os.environ.copy()
            env["PATH"] = ""
            env["PYTHONHOME"] = str(moved / 'untrusted-python-home')
            env["PYTHONPATH"] = str(moved / 'untrusted-python-path')
            flags = ['-I', '-S', '-B', '-X', 'utf8']
            run_checked_process([python, *flags, str(companion / "companion.py"), "--help"],
                                cwd=companion, env=env, timeout=HELP_TIMEOUT_SECONDS)
            check = (
                "import sys, json, ssl, ctypes, urllib.request, inventory_question as q, companion; "
                f"assert tuple(sys.version_info[:3]) == {tuple(int(x) for x in PYTHON_PIN['version'].split('.'))!r}; "
                "assert sys.flags.isolated and sys.flags.no_site; "
                "assert all('untrusted-python' not in p for p in sys.path); "
                "ssl.create_default_context(); "
                "import sqlite3; db=sqlite3.connect(':memory:'); "
                "db.execute('create table smoke (value integer)'); db.execute('insert into smoke values (27)'); "
                "assert db.execute('select value from smoke').fetchone() == (27,); db.close(); "
                "import grok_provider, grok_credentials, credential_store; "
                "grok_provider.validate_runtime(__import__('pathlib').Path('runtime/grok-win-x64/grok.exe')); "
                "import os; os.environ['PATH']=''; os.environ['LOCALAPPDATA']=''; "
                "runtime=companion.find_runtime(); pin=companion.checked_pin(runtime); "
                "companion.checked_web_pin(pin); "
                "name=json.load(open('item_sources.json', encoding='utf-8'))['items'][0]['name']; "
                "assert q.source_context(name)['items']; "
                "import tempfile; "
                f"scratch=tempfile.TemporaryDirectory(dir={str(moved)!r}); "
                "companion.PROFILE=__import__('pathlib').Path(scratch.name); "
                "import time; client=None\n"
                "try:\n"
                "    client=companion.Companion()\n"
                "finally:\n"
                "    try:\n"
                "        if client is not None: client.close()\n"
                "    finally:\n"
                "        # Closing the Windows job terminates children asynchronously.\n"
                "        for attempt in range(21):\n"
                "            try:\n"
                "                scratch.cleanup()\n"
                "                break\n"
                "            except PermissionError:\n"
                "                if attempt == 20: raise\n"
                "                time.sleep(0.1)\n"
            )
            run_checked_process([python, *flags, "-c", check], cwd=companion,
                                env=env, timeout=SMOKE_TIMEOUT_SECONDS)

    print(f"Verified {len(EXPECTED)} allowlisted archive files and offline relocation: {archive}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except subprocess.TimeoutExpired as error:
        print(f"Package verification timed out after {error.timeout} seconds.", file=sys.stderr)
        raise SystemExit(1) from None
