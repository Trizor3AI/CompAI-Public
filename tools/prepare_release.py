"""Prepare private local release drafts; never publish or approve redistribution."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
from urllib.parse import urlsplit
import zipfile


ROOT = Path(__file__).resolve().parents[1]
SCRATCH = ROOT / ".test-tmp"
VERIFIER_PATH = ROOT / "CompAI/verify_package.py"
VERIFY_TIMEOUT_SECONDS = 600
spec = importlib.util.spec_from_file_location("compai_release_verifier", VERIFIER_PATH)
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)


def public_url(value: str, label: str) -> str:
    """Accept simple public HTTPS URLs, without silently repairing unsafe input."""
    message = f"{label} must be a public HTTPS URL without credentials, tokens, query, fragment or escapes"
    if not isinstance(value, str) or not value.isascii() or re.search(r"[\s\\%?#<>]", value):
        raise ValueError(message)
    try:
        parsed = urlsplit(value)
        hostname = parsed.hostname or ""
        port = parsed.port
    except ValueError as error:
        raise ValueError(message) from error
    if (parsed.scheme != "https" or not value.startswith("https://") or parsed.username is not None
            or parsed.password is not None or port is not None or not hostname
            or not re.fullmatch(r"[A-Za-z0-9.-]+", hostname)
            or not re.fullmatch(r"(?:/[A-Za-z0-9._~-]+)*/?", parsed.path)):
        raise ValueError(message)
    labels = hostname.split(".")
    if (len(labels) < 2 or not re.fullmatch(r"[A-Za-z][A-Za-z0-9-]*", labels[-1])
            or any(not part or len(part) > 63 or part.startswith("-") or part.endswith("-")
                               for part in labels)
            or labels[-1].lower() in {"localhost", "local", "internal", "invalid", "test", "example"}
            or any(hostname.lower() == reserved or hostname.lower().endswith("." + reserved)
                   for reserved in ("example.com", "example.org", "example.net"))
            or any(part in {".", ".."} for part in parsed.path.split("/"))):
        raise ValueError(message)
    try:
        ipaddress.ip_address(hostname)
    except ValueError:
        pass
    else:
        raise ValueError(message)
    if (verifier.check_bytes("release-url.txt", value.encode())
            or re.search(r"(?:^|/)(?:token|secret|password|authorization|api[-_]?key)(?:[-_=]|/|$)",
                         parsed.path, re.IGNORECASE)):
        raise ValueError(message)
    return value.rstrip("/")


def checked_output(path: Path, *, require_new: bool = True) -> Path:
    """Keep all drafts in ignored workspace scratch, without following reparse points."""
    if ".." in path.parts:
        raise ValueError("Output must not contain parent-directory traversal")
    output = Path(os.path.abspath(path))
    if output == SCRATCH or not output.is_relative_to(SCRATCH):
        raise ValueError("Output must be a new directory below this checkout's .test-tmp")
    current = ROOT
    for part in output.relative_to(ROOT).parts:
        current = current / part
        try:
            info = current.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError("Output path must not contain links or Windows reparse points")
        if not stat.S_ISDIR(info.st_mode):
            raise ValueError("An output parent is not a directory")
    if require_new and output.exists():
        raise ValueError("Output already exists; choose a new draft directory")
    return output


def verify_archive(archive: Path) -> None:
    # This is the sole package verification authority, including offline relocation.
    # Own its job and temporary root too: an outer deadline must not orphan a
    # relocated Python/native child or scratch directory when Python is stopped.
    with verifier.relocation_temporary_directory() as temporary:
        env = os.environ.copy()
        env.update({name: str(temporary) for name in ("TEMP", "TMP", "TMPDIR")})
        verifier.run_checked_process([sys.executable, "-B", str(VERIFIER_PATH), str(archive)],
                                     cwd=ROOT, env=env, timeout=VERIFY_TIMEOUT_SECONDS, capture_output=False)


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def make_feed(manifest: dict, asset_base_url: str, repo_url: str, timestamp: int) -> tuple[list, str]:
    version = verifier.normalized_version(manifest.get("AssemblyVersion"), "packaged AssemblyVersion")
    if type(timestamp) is not int or timestamp <= 0:
        raise ValueError("The preparation timestamp must be positive Unix seconds")
    asset_base_url = public_url(asset_base_url, "Asset base URL")
    repo_url = public_url(repo_url, "Repository URL")
    final_name = f"CompAI-{version}.zip"
    download_url = public_url(f"{asset_base_url}/{final_name}", "Download URL")
    entry = dict(manifest)
    if any(key in entry for key in ("TestingAssemblyVersion", "TestingDalamudApiLevel", "DownloadLinkTesting")):
        raise ValueError("Testing-channel metadata needs separate review before drafting a release feed")
    entry.update(RepoUrl=repo_url, DownloadLinkInstall=download_url, DownloadLinkUpdate=download_url,
                 LastUpdate=timestamp, IsHide=True, IsTestingExclusive=False)
    if entry.get("IconUrl"):
        icon_url = public_url(entry["IconUrl"], "Packaged icon URL")
        release_prefix = f"{repo_url}/releases/download/"
        if icon_url.startswith(release_prefix) and icon_url != f"{release_prefix}v{version}/icon.png":
            raise ValueError("Packaged icon URL points to the wrong release or file")
        entry["IconUrl"] = f"{asset_base_url}/icon.png"
    if entry.get("ImageUrls") is not None:
        if not isinstance(entry["ImageUrls"], list):
            raise ValueError("ImageUrls must be a list of public HTTPS URLs")
        for value in entry["ImageUrls"]:
            public_url(value, "ImageUrls entry")
    return [entry], final_name


def prepare(archive: Path, output: Path, asset_base_url: str, repo_url: str) -> Path:
    # Validate user input before copying a potentially large archive or running checks.
    asset_base_url = public_url(asset_base_url, "Asset base URL")
    repo_url = public_url(repo_url, "Repository URL")
    output = checked_output(output)
    archive = archive.resolve(strict=True)
    if not archive.is_file():
        raise ValueError("The source archive must be an existing ZIP file")
    version = verifier.project_version((ROOT / "CompAI/CompAI.csproj").read_bytes())
    draft_name = f"CompAI-{version}-PRIVATE-DRAFT.zip"
    output.parent.mkdir(parents=True, exist_ok=True)
    checked_output(output)
    output.mkdir()  # Exclusive creation: an earlier draft is never overwritten.
    directory_identity = output.stat()
    created: list[tuple[Path, os.stat_result]] = []

    def write(name: str, payload: bytes) -> Path:
        path = output / name
        with path.open("xb") as stream:
            created.append((path, os.fstat(stream.fileno())))
            stream.write(payload)
        return path

    try:
        staged = output / draft_name
        with archive.open("rb") as source, staged.open("xb") as destination:
            created.append((staged, os.fstat(destination.fileno())))
            shutil.copyfileobj(source, destination)
        verify_archive(staged)
        with zipfile.ZipFile(staged) as bundle:
            manifest = verifier.read_manifest(
                verifier.read_zip_text(bundle, bundle.getinfo("CompAI.json"), verifier.MAX_MANIFEST_BYTES),
                "verified package")
        # Reuse the existing version/metadata checker as well: never maintain a second contract.
        verifier.verify_manifest(json.dumps(manifest).encode(),
                                 verifier.read_bounded_file(verifier.RELEASE_MANIFEST, verifier.MAX_MANIFEST_BYTES),
                                 (ROOT / "CompAI/CompAI.csproj").read_bytes())
        now = datetime.now(timezone.utc)
        timestamp = int(now.timestamp())
        feed, final_name = make_feed(manifest, asset_base_url, repo_url, timestamp)
        if verifier.normalized_version(manifest["AssemblyVersion"], "packaged AssemblyVersion") != version:
            raise ValueError("Project version changed while preparing the draft; retry in a new directory")
        encoded_feed = (json.dumps(feed, indent=2, ensure_ascii=False, allow_nan=False) + "\n").encode()
        feed_path = write("pluginmaster.PRIVATE-DRAFT.json", encoded_feed)
        hashes = {staged.name: digest(staged), feed_path.name: digest(feed_path)}
        write("SHA256SUMS.PRIVATE-DRAFT.txt", "".join(f"{value}  {name}\n" for name, value in hashes.items()).encode())
        status = {
            "status": "PRIVATE LOCAL DRAFT - NOT APPROVED FOR PUBLICATION",
            "publication_approved": False,
            "prepared_at_utc": now.isoformat(timespec="seconds"),
            "last_update_unix_seconds": timestamp,
            "assembly_version": version,
            "package_verification": "Passed existing verify_package.py, including offline relocation",
            "url_availability_checked": False,
            "clean_pc_install_verified": False,
            "sha256": hashes,
            "planned_final_archive_name": final_name,
            "planned_final_feed_name": "pluginmaster.json",
            "feed_hidden": True,
            "before_publication": [
                "Resolve exact bundled-binary redistribution rights/notices and provide the corresponding licensed CompAI source.",
                "Complete separate install, update and recovery checks on a clean Windows installation.",
                "Obtain publication approval for the exact reviewed package and destination URLs.",
                "Prepare approved final filenames and change IsHide to false; recompute the feed checksum.",
                "Confirm public download URLs work before making the final feed available.",
            ],
        }
        write("DRAFT-STATUS.json", (json.dumps(status, indent=2) + "\n").encode())
        return output
    except BaseException:
        # Delete only files this invocation created, never recursively remove a directory.
        # Unexpected concurrent files are preserved and prevent rmdir from succeeding.
        try:
            checked_output(output, require_new=False)
            if not os.path.samestat(directory_identity, output.lstat()):
                raise ValueError("Draft directory changed; leave it for manual inspection")
            for path, identity in reversed(created):
                try:
                    if os.path.samestat(identity, path.lstat()):
                        path.unlink()
                except OSError:
                    pass
            output.rmdir()
        except (OSError, ValueError):
            pass
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--asset-base-url", required=True, help="Final public HTTPS directory for the versioned ZIP")
    parser.add_argument("--repo-url", required=True, help="Final public HTTPS project page; not the installer feed URL")
    parser.add_argument("--archive", type=Path, default=verifier.ARCHIVE, help="Existing Release ZIP to verify and copy")
    parser.add_argument("--output", type=Path, required=True, help="New local draft directory below .test-tmp")
    args = parser.parse_args()
    try:
        output = prepare(args.archive, args.output, args.asset_base_url, args.repo_url)
    except subprocess.TimeoutExpired as error:
        parser.exit(1, f"Release draft was not completed: package verification timed out after {error.timeout} seconds.\n")
    except (ValueError, AssertionError, OSError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Release draft was not completed: {error}\n")
    print(f"Prepared PRIVATE LOCAL DRAFT: {output}")
    print("Nothing was uploaded. The feed is hidden; publication and redistribution remain unapproved.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
