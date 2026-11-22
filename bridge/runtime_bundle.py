"""Resolve only the companion's private, hash-validated Windows runtime."""

from pathlib import Path
import re
import stat

from prepare_isolation_catalog import digest


RUNTIME_FILES = frozenset({"codex.exe", "codex-code-mode-host.exe"})
_HASH = re.compile(r"[0-9a-f]{64}\Z")


def _plain_path(path, *, directory):
    try:
        info = path.lstat()
    except OSError as exc:
        raise RuntimeError("Private companion runtime is missing; reinstall the plugin runtime bundle") from exc
    if (stat.S_ISLNK(info.st_mode) or
            getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT):
        raise RuntimeError("Private companion runtime must not contain symlinks or junctions")
    if not (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)):
        raise RuntimeError("Private companion runtime contains an invalid file or directory")


def resolve_runtime(root):
    """No PATH lookup, desktop discovery, download, or version fallback."""
    root = Path(root).absolute()
    folder = root / "runtime" / "win-x64"
    for path in (root, root / "runtime", folder):
        _plain_path(path, directory=True)
    try:
        if {entry.name for entry in folder.iterdir()} != RUNTIME_FILES:
            raise RuntimeError("Private companion runtime files differ from the required bundle")
        for name in RUNTIME_FILES:
            _plain_path(folder / name, directory=False)
    except OSError as exc:
        raise RuntimeError("Private companion runtime could not be inspected") from exc
    return folder / "codex.exe"


def validate_runtime(executable, pin):
    """Pin the code host as well as the main executable before every launch."""
    executable = Path(executable)
    if (not executable.is_absolute() or executable.name != "codex.exe" or
            executable.parent.name != "win-x64" or executable.parent.parent.name != "runtime"):
        raise RuntimeError("Companion runtime must use the private runtime/win-x64 bundle")
    expected = resolve_runtime(executable.parent.parent.parent)
    if executable != expected:
        raise RuntimeError("Companion runtime must use the private runtime/win-x64 bundle")
    files = pin.get("runtime_files") if isinstance(pin, dict) else None
    if (not isinstance(files, dict) or set(files) != RUNTIME_FILES or
            any(not isinstance(value, str) or not _HASH.fullmatch(value) for value in files.values()) or
            pin.get("binary_sha256") != files["codex.exe"]):
        raise RuntimeError("Private runtime manifest is invalid; rebuild and revalidate the runtime bundle")
    try:
        for name, expected_hash in files.items():
            if digest(executable.parent / name) != expected_hash:
                raise RuntimeError("Private runtime pin changed; reinstall or revalidate the runtime bundle")
    except OSError as exc:
        raise RuntimeError("Private companion runtime could not be verified") from exc
