"""Stage the pinned official Python embed ZIP; downloads happen only with --download."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import tempfile
from urllib.request import urlopen
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PIN = ROOT / "bridge/python-runtime.json"
DESTINATION = ROOT / "bridge/runtime/python-win-x64"
MAX_BYTES = 64 * 1024 * 1024


def read_pin(path=PIN):
    pin = json.loads(Path(path).read_text(encoding="utf-8"))
    files = pin.get("files")
    if (pin.get("schema") != 1 or pin.get("arch") != "win-x64" or
            pin.get("executable") != "python.exe" or
            not re.fullmatch(r"3\.\d+\.\d+", pin.get("version", "")) or
            not isinstance(files, dict) or not 1 <= len(files) <= 100 or
            len({name.casefold() for name in files}) != len(files) or
            any(not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]*", name) or
                not isinstance(digest, str) or not re.fullmatch(r"[a-f0-9]{64}", digest)
                for name, digest in files.items())):
        raise ValueError("Invalid embedded Python pin")
    expected_url = f"https://www.python.org/ftp/python/{pin['version']}/python-{pin['version']}-embed-amd64.zip"
    if (pin.get("source_url") != expected_url or
            not re.fullmatch(r"[a-f0-9]{64}", pin.get("archive_sha256", "")) or
            not isinstance(pin.get("archive_bytes"), int) or not 0 < pin["archive_bytes"] <= MAX_BYTES):
        raise ValueError("Invalid embedded Python archive pin")
    minor = ''.join(pin['version'].split('.')[:2])
    if (pin.get("path_file") != f"python{minor}._pth" or
            pin.get("path_file_text") != f"python{minor}.zip\n.\n..\n" or
            not {"python.exe", f"python{minor}.dll", f"python{minor}.zip", pin['path_file'], "LICENSE.txt"} <= files.keys()):
        raise ValueError("Invalid embedded Python isolated search path")
    return pin


def verify_bundle(directory, pin):
    directory = Path(directory)
    entries = list(directory.iterdir())
    if directory.is_symlink() or directory.is_junction() or {p.name for p in entries} != set(pin['files']):
        raise ValueError("Embedded Python file membership differs from pin; restore the complete bundle")
    for path in entries:
        if path.is_symlink() or path.is_junction() or not path.is_file() or path.stat().st_size > MAX_BYTES:
            raise ValueError("Unsafe embedded Python entry")
        if hashlib.sha256(path.read_bytes()).hexdigest() != pin['files'][path.name]:
            raise ValueError(f"Embedded Python hash mismatch: {path.name}")


def unpack_archive(archive, destination, pin):
    archive = Path(archive)
    if archive.stat().st_size != pin['archive_bytes'] or hashlib.sha256(archive.read_bytes()).hexdigest() != pin['archive_sha256']:
        raise ValueError("Python archive does not match the published pinned SHA-256")
    with zipfile.ZipFile(archive) as bundle:
        entries = bundle.infolist()
        if len(entries) != len(pin['files']) or {i.filename for i in entries} != set(pin['files']):
            raise ValueError("Python archive contents differ from the pinned file list")
        if any(i.is_dir() or i.file_size > MAX_BYTES for i in entries) or sum(i.file_size for i in entries) > MAX_BYTES:
            raise ValueError("Python archive exceeds the extraction limit")
        for entry in entries:
            data = (pin['path_file_text'].encode('ascii') if entry.filename == pin['path_file'] else bundle.read(entry))
            (Path(destination) / entry.filename).write_bytes(data)
    verify_bundle(destination, pin)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group()
    source.add_argument('--download', action='store_true', help='Fetch only the official pinned archive')
    source.add_argument('--archive', type=Path, help='Stage from an already downloaded pinned archive')
    args = parser.parse_args()
    pin = read_pin()
    if DESTINATION.exists():
        verify_bundle(DESTINATION, pin)
        print(f"Embedded Python {pin['version']}: verified {len(pin['files'])} files; no download needed")
        return
    if not args.download and args.archive is None:
        parser.error('Embedded Python is missing. Use --download or --archive to prepare the build prerequisite.')
    scratch = ROOT / '.test-tmp'
    scratch.mkdir(exist_ok=True)
    DESTINATION.parent.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='python-archive-', dir=scratch) as temporary:
        archive = args.archive
        if args.download:
            archive = Path(temporary) / 'python-embed.zip'
            with urlopen(pin['source_url'], timeout=30) as response, archive.open('wb') as output:
                if response.geturl() != pin['source_url']:
                    raise ValueError('Unexpected Python download redirect')
                total = 0
                while chunk := response.read(1024 * 1024):
                    total += len(chunk)
                    if total > pin['archive_bytes']:
                        raise ValueError('Python download exceeds pinned size')
                    output.write(chunk)
        # No existing bundle is overwritten. Keep updates deliberate and recoverable.
        with tempfile.TemporaryDirectory(prefix='python-stage-', dir=DESTINATION.parent) as staging:
            unpack_archive(archive, staging, pin)
            Path(staging).rename(DESTINATION)
    print(f"Prepared embedded Python {pin['version']}: {len(pin['files'])} verified files")


if __name__ == '__main__':
    main()
