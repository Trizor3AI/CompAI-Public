"""Verify account runtimes, or explicitly stage a pinned official download/archive.

No arguments verify both existing bundles. Downloads require --provider NAME
--download; --archive reuses a local pinned artifact. No runtimes are launched,
globally installed, auto-updated, or replaced. Existing invalid bundles stay intact.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import tarfile
import tempfile
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener

ROOT = Path(__file__).resolve().parents[1]
PIN = ROOT / "bridge/account-runtimes.json"
RUNTIME = ROOT / "bridge/runtime"
MAX_BYTES = 256 * 1024 * 1024
CHUNK = 1024 * 1024


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate runtime metadata field")
        result[key] = value
    return result


def read_pins(path=PIN):
    pin = json.loads(Path(path).read_text(encoding="utf-8"), object_pairs_hook=unique_object)
    if (not isinstance(pin, dict) or type(pin.get("schema")) is not int or pin["schema"] != 1 or pin.get("arch") != "win-x64" or
            not isinstance(pin.get("providers"), dict) or set(pin["providers"]) != {"copilot", "grok"}):
        raise ValueError("Invalid account runtime metadata")
    for provider, row in pin["providers"].items():
        if not isinstance(row, dict):
            raise ValueError("Invalid provider runtime entry")
        version = row.get("version", "")
        if not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+", version):
            raise ValueError("Invalid runtime version")
        url = (f"https://registry.npmjs.org/@github/copilot-win32-x64/-/copilot-win32-x64-{version}.tgz"
               if provider == "copilot" else f"https://x.ai/cli/grok-{version}-windows-x86_64.exe")
        names = {"copilot.exe", "LICENSE.md", "README.md", "package.json"} if provider == "copilot" else {"grok.exe"}
        if (row.get("source_url") != url or row.get("executable") != provider + ".exe" or
                row.get("format") != ("tar.gz" if provider == "copilot" else "exe") or
                not isinstance(row.get("files"), dict) or set(row["files"]) != names):
            raise ValueError("Unexpected runtime source or file membership")
        validate_hashes({"bytes": row.get("archive_bytes"), "sha256": row.get("archive_sha256"),
                         "sha512": row.get("archive_sha512")})
        for name, entry in row["files"].items():
            validate_hashes(entry)
            if entry.get("member") != ("package/" + name if provider == "copilot" else None):
                raise ValueError("Unexpected archive member")
        if sum(entry["bytes"] for entry in row["files"].values()) > MAX_BYTES:
            raise ValueError("Runtime exceeds the extracted size limit")
        if provider == "grok":
            entry = row["files"]["grok.exe"]
            if any(entry[key] != row["archive_" + key] for key in ("bytes", "sha256", "sha512")):
                raise ValueError("Direct executable and archive pins differ")
    return pin["providers"]


def validate_hashes(entry):
    if (not isinstance(entry, dict) or type(entry.get("bytes")) is not int or not 0 < entry["bytes"] <= MAX_BYTES or
            any(not isinstance(entry.get(key), str) or not re.fullmatch(r"[0-9a-f]{" + str(size) + "}", entry[key])
                for key, size in (("sha256", 64), ("sha512", 128)))):
        raise ValueError("Invalid runtime size/hash pin")


def reject_links(path):
    path = Path(path).absolute()
    for current in [*reversed(path.parents), path]:
        try:
            info = current.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked runtime paths are unsupported")


def checked_copy(source, expected, output=None):
    """Bound reads even when a stream lies about its advertised size."""
    hashes = {name: hashlib.new(name) for name in ("sha256", "sha512")}
    count = 0
    while chunk := source.read(min(CHUNK, expected["bytes"] - count + 1)):
        count += len(chunk)
        if count > expected["bytes"]:
            raise ValueError("Runtime artifact exceeds its pinned size")
        for value in hashes.values():
            value.update(chunk)
        if output is not None:
            output.write(chunk)
    if count != expected["bytes"] or any(value.hexdigest() != expected[name] for name, value in hashes.items()):
        raise ValueError("Runtime artifact size/hash mismatch")


def check_file(path, expected):
    reject_links(path)
    if not Path(path).is_file() or Path(path).stat().st_size != expected["bytes"]:
        raise ValueError("Missing or wrong-size runtime artifact")
    with Path(path).open("rb") as source:
        checked_copy(source, expected)


def verify_bundle(directory, pin):
    directory = Path(directory)
    reject_links(directory)
    if not directory.is_dir() or {path.name for path in directory.iterdir()} != set(pin["files"]):
        raise ValueError("Runtime bundle missing or file membership changed; existing files are preserved")
    for name, expected in pin["files"].items():
        check_file(directory / name, expected)


def unpack_archive(archive, destination, pin):
    expected = {key: pin["archive_" + key] for key in ("bytes", "sha256", "sha512")}
    check_file(archive, expected)
    destination = Path(destination)
    reject_links(destination)
    if any(destination.iterdir()):
        raise ValueError("Staging directory must be empty")

    def save(name, source):
        with (destination / name).open("xb") as output:
            checked_copy(source, pin["files"][name], output)
            output.flush()
            os.fsync(output.fileno())

    if pin["format"] == "exe":
        with Path(archive).open("rb") as source:
            save(pin["executable"], source)
    else:
        # Read only exact regular members. Never extract paths, links, permissions,
        # or arbitrary archive contents into the filesystem.
        members = {entry["member"]: name for name, entry in pin["files"].items()}
        seen = set()
        with tarfile.open(archive, mode="r|gz") as bundle:
            for member in bundle:
                name = members.get(member.name)
                if (name is None or member.name in seen or not member.isreg() or
                        member.size != pin["files"][name]["bytes"]):
                    raise ValueError("Unexpected, duplicate, linked or oversized archive entry")
                seen.add(member.name)
                with bundle.extractfile(member) as source:
                    save(name, source)
        if seen != set(members):
            raise ValueError("Missing pinned archive member")
    verify_bundle(destination, pin)


class NoRedirects(HTTPRedirectHandler):
    def redirect_request(self, request, file, code, message, headers, newurl):
        raise ValueError("Runtime downloads may not redirect to another URL")


def download(pin, destination):
    opener = build_opener(ProxyHandler({}), NoRedirects())
    request = Request(pin["source_url"], headers={"Accept-Encoding": "identity"})
    expected = {key: pin["archive_" + key] for key in ("bytes", "sha256", "sha512")}
    with opener.open(request, timeout=30) as response, Path(destination).open("xb") as output:
        if response.geturl() != pin["source_url"] or response.headers.get("Content-Encoding", "identity") != "identity":
            raise ValueError("Unexpected runtime download response")
        checked_copy(response, expected, output)


def stage(provider, pin, archive=None, fetch=False, runtime_root=RUNTIME):
    if provider not in ("copilot", "grok"):
        raise ValueError("Unknown runtime provider")
    runtime_root = Path(runtime_root)
    target = runtime_root / (provider + "-win-x64")
    reject_links(target)
    if target.exists():
        verify_bundle(target, pin)
        return "already verified"
    if archive is None and not fetch:
        raise ValueError(f"{provider} is missing; use --provider {provider} --archive FILE or --download")
    runtime_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=provider + "-stage-", dir=runtime_root) as temporary:
        temporary = Path(temporary)
        if fetch:
            archive = temporary / "download"
            download(pin, archive)
        payload = temporary / "payload"
        payload.mkdir()
        unpack_archive(archive, payload, pin)
        # Same-volume directory rename publishes only the completely verified set.
        # Windows rename refuses an existing target. Explicitly refuse elsewhere too.
        if target.exists():
            raise ValueError("Runtime destination appeared during staging; preserved")
        payload.rename(target)
    return "staged and verified"


def self_test():
    import copy
    pins = read_pins()
    payloads = {name: (b"MZ fixture\n" if name.endswith(".exe") else name.encode()) for name in pins["copilot"]["files"]}
    pin = copy.deepcopy(pins["copilot"])
    for name, data in payloads.items():
        pin["files"][name].update(bytes=len(data), sha256=hashlib.sha256(data).hexdigest(), sha512=hashlib.sha512(data).hexdigest())
    scratch = ROOT / ".test-tmp"
    reject_links(scratch)
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="provider-stage-test-", dir=scratch) as temporary:
        temporary = Path(temporary)
        archive, destination = temporary / "fixture.tgz", temporary / "out"
        destination.mkdir()
        for extra in (None, "duplicate", "../escape", "link"):
            with tarfile.open(archive, "w:gz") as bundle:
                for name, data in payloads.items():
                    info = tarfile.TarInfo("package/" + name)
                    info.size = len(data)
                    bundle.addfile(info, io.BytesIO(data))
                if extra:
                    info = tarfile.TarInfo("package/copilot.exe" if extra == "duplicate" else extra)
                    if extra == "link":
                        info.type, info.linkname = tarfile.SYMTYPE, "../escape"
                    bundle.addfile(info, io.BytesIO())
            data = archive.read_bytes()
            pin.update(archive_bytes=len(data), archive_sha256=hashlib.sha256(data).hexdigest(), archive_sha512=hashlib.sha512(data).hexdigest())
            output = destination / (extra.replace("/", "_") if extra else "valid")
            output.mkdir()
            try:
                unpack_archive(archive, output, pin)
                if extra:
                    raise AssertionError("Unsafe archive accepted")
            except ValueError:
                if extra is None:
                    raise
        valid = destination / "valid"
        verify_bundle(valid, pin)
        original = (valid / "copilot.exe").read_bytes()
        (valid / "copilot.exe").write_bytes(original + b"changed")
        try:
            verify_bundle(valid, pin)
            raise AssertionError("Changed runtime accepted")
        except ValueError:
            assert (valid / "copilot.exe").read_bytes() == original + b"changed"
        incomplete = temporary / "runtime" / "copilot-win-x64"
        incomplete.mkdir(parents=True)
        (incomplete / "copilot.exe").write_bytes(b"preserve existing invalid file")
        try:
            stage("copilot", pin, archive=archive, runtime_root=incomplete.parent)
            raise AssertionError("Existing invalid bundle was replaced")
        except ValueError:
            assert (incomplete / "copilot.exe").read_bytes() == b"preserve existing invalid file"
        try:
            checked_copy(io.BytesIO(b"oversized"), {"bytes": 1, "sha256": "0" * 64, "sha512": "0" * 128})
            raise AssertionError("Unbounded input accepted")
        except ValueError:
            pass
    print("PASS pinned metadata, exact extraction, duplicate/path/link rejection, existing invalid bundle preservation and bounded reads")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--provider", choices=("copilot", "grok"))
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--download", action="store_true")
    source.add_argument("--archive", type=Path)
    parser.add_argument("--self-test", action="store_true", help="Synthetic local validation; no network or runtime launch")
    args = parser.parse_args()
    if args.self_test:
        if args.provider or args.download or args.archive:
            parser.error("--self-test cannot be combined with staging options")
        self_test()
        return
    if (args.download or args.archive) and not args.provider:
        parser.error("Choose --provider explicitly when staging a runtime")
    pins = read_pins()
    for provider in ([args.provider] if args.provider else pins):
        result = stage(provider, pins[provider], args.archive, args.download)
        print(f"{provider} {pins[provider]['version']}: {result}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, tarfile.TarError) as error:
        raise SystemExit(f"Runtime preparation refused: {error}") from None
