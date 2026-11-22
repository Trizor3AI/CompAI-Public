"""Offline credential guard for staged source and textual package assets.

This detects forbidden private paths and recognizable credential formats, not
every possible secret. Binary integrity remains the package verifier's job.
No profile discovery, credential comparison, or network calls. The repository's
pre-push hook uses the outgoing-history mode before Git uploads any objects.
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
from pathlib import Path
import re
import stat
import subprocess
import sys
from typing import Iterable


MAX_TEXT_BYTES = 8 * 1024 * 1024
_OID = re.compile(r"[0-9a-f]{40}(?:[0-9a-f]{24})?")
_PRIVATE_FOLDERS = frozenset({
    ".companion", ".codex", ".aws", ".agents", ".git", ".test-tmp",
    ".feasibility-context", ".pedigree-probe", "credentials", "pluginconfigs",
    "public-data-cache", "valuation-resume-v1", "market-scan-evidence-v1",
})
_BINARY_SUFFIXES = frozenset({
    ".exe", ".dll", ".pyd", ".zip", ".7z", ".gz", ".png", ".jpg",
    ".jpeg", ".gif", ".webp", ".ico", ".pdf", ".woff", ".woff2", ".ttf",
})
_CREDENTIAL_NAME = re.compile(
    r"(?:^auth\.json(?:\.|$)|^credentials?\.json(?:\.|$)|"
    r"\.(?:dpapi|key|pem|pfx|p12)(?:\.|$))", re.IGNORECASE)
_PATTERNS = (
    ("private_key", re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----")),
    ("github_token", re.compile(rb"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b")),
    ("openai_token", re.compile(rb"\bsk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{40,}\b")),
    ("xai_token", re.compile(rb"\bxai-[A-Za-z0-9_-]{40,}\b")),
    ("jwt", re.compile(rb"\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\b")),
)


@dataclass(frozen=True)
class Finding:
    category: str
    path: str


class GuardError(Exception):
    """Sanitized operational failure; never includes subprocess output."""


def _normalized(path: str) -> str:
    return str(path).replace("\\", "/")


def check_path(path: str) -> list[Finding]:
    """Reject private paths before callers open a file or read a Git blob."""
    normalized = _normalized(path)
    parts = normalized.split("/")
    if (not normalized or normalized.startswith("/") or ":" in normalized
            or "\0" in normalized or any(part in ("", ".", "..") for part in parts)):
        return [Finding("unsafe_path", normalized)]
    # Windows removes trailing dots/spaces when resolving many file paths.
    folded = [part.rstrip(" .").casefold() for part in parts]
    if any(part in _PRIVATE_FOLDERS for part in folded):
        return [Finding("private_profile_path", normalized)]
    name = folded[-1]
    if re.fullmatch(r"market-quotes-v[0-9]+\.sqlite3(?:-journal|-wal|-shm)?", name):
        return [Finding("private_profile_path", normalized)]
    if (name == ".env" or name.startswith(".env.")) and parts[-1] != ".env.example":
        return [Finding("credential_file", normalized)]
    if _CREDENTIAL_NAME.search(name) or name == ".netrc":
        return [Finding("credential_file", normalized)]
    return []


def _is_binary_path(path: str) -> bool:
    return Path(_normalized(path)).suffix.casefold() in _BINARY_SUFFIXES


def check_bytes(path: str, data: bytes) -> list[Finding]:
    """Return category/path only; never retain or return matched credentials."""
    findings = check_path(path)
    if findings or _is_binary_path(path):
        return findings
    normalized = _normalized(path)
    if len(data) > MAX_TEXT_BYTES:
        return [Finding("text_size_limit", normalized)]
    # UTF-16 text may otherwise hide a token behind alternating zero bytes.
    candidates = [data]
    if data.startswith((b"\xff\xfe", b"\xfe\xff")):
        try:
            candidates.append(data.decode("utf-16").encode("utf-8"))
        except UnicodeError:
            return [Finding("invalid_text_encoding", normalized)]
    return [Finding(category, normalized) for category, pattern in _PATTERNS
            if any(pattern.search(candidate) for candidate in candidates)]


def _git(root: Path, *args: str, input_bytes: bytes | None = None) -> bytes:
    try:
        result = subprocess.run(["git", "-C", str(root), *args], input=input_bytes,
                                capture_output=True, check=False, timeout=30)
    except (OSError, subprocess.TimeoutExpired):
        raise GuardError("Git inspection could not complete") from None
    if result.returncode:
        raise GuardError("Git inspection failed")
    return result.stdout


def _index(root: Path) -> list[tuple[str, str, str, str]]:
    entries = []
    for record in _git(root, "ls-files", "--stage", "-z").split(b"\0"):
        if not record:
            continue
        try:
            metadata, name = record.split(b"\t", 1)
            mode, oid, stage = metadata.decode("ascii").split()
            if not _OID.fullmatch(oid):
                raise ValueError()
            entries.append((name.decode("utf-8", "surrogateescape"), mode, oid, stage))
        except (ValueError, UnicodeError):
            raise GuardError("Git index format could not be verified") from None
    return entries


def scan_index(root: Path) -> tuple[int, list[Finding]]:
    """Inspect actual staged blobs, independent of the working tree contents."""
    return _scan_entries(root, _index(root))


def _scan_entries(root: Path, entries: list[tuple[str, str, str, str]]) -> tuple[int, list[Finding]]:
    findings = []
    pending = []
    for path, mode, oid, stage in entries:
        problems = check_path(path)
        if stage != "0":
            problems.append(Finding("unmerged_index", _normalized(path)))
        if mode not in ("100644", "100755"):
            problems.append(Finding("unsupported_file_type", _normalized(path)))
        findings.extend(problems)
        if not problems and not _is_binary_path(path):
            pending.append((path, oid))
    if pending:
        request = ("\n".join(oid for _, oid in pending) + "\n").encode("ascii")
        metadata = _git(root, "cat-file", "--batch-check", input_bytes=request).splitlines()
        if len(metadata) != len(pending):
            raise GuardError("Git blob metadata could not be verified")
        readable = []
        for (path, oid), line in zip(pending, metadata):
            try:
                actual, kind, size = line.decode("ascii").split()
                if actual != oid or kind != "blob" or int(size) < 0:
                    raise ValueError()
                if int(size) > MAX_TEXT_BYTES:
                    findings.append(Finding("text_size_limit", _normalized(path)))
                else:
                    readable.append((path, oid, int(size)))
            except (ValueError, UnicodeError):
                raise GuardError("Git blob metadata could not be verified") from None
        # Bounded batches avoid collecting the entire repository's content at once.
        for start in range(0, len(readable), 16):
            batch = readable[start:start + 16]
            request = ("\n".join(oid for _, oid, _ in batch) + "\n").encode("ascii")
            content = _git(root, "cat-file", "--batch", input_bytes=request)
            position = 0
            for path, oid, size in batch:
                end = content.find(b"\n", position)
                if end < 0 or content[position:end] != f"{oid} blob {size}".encode("ascii"):
                    raise GuardError("Git blob content could not be verified")
                position = end + 1
                if position + size >= len(content) or content[position + size:position + size + 1] != b"\n":
                    raise GuardError("Git blob content could not be verified")
                findings.extend(check_bytes(path, content[position:position + size]))
                position += size + 1
            if position != len(content):
                raise GuardError("Git blob content could not be verified")
    return len(entries), findings


def _object(root: Path, oid: str) -> tuple[str, bytes]:
    """Read bounded Git metadata, never a filesystem path or unchecked ref."""
    if not _OID.fullmatch(oid):
        raise GuardError("A complete Git object ID is required")
    metadata = _git(root, "cat-file", "--batch-check", input_bytes=(oid + "\n").encode()).split()
    if len(metadata) != 3 or metadata[0] != oid.encode() or metadata[1] not in (b"commit", b"tag"):
        raise GuardError("Only commit and commit-tag publication is supported")
    try:
        size = int(metadata[2])
    except ValueError:
        raise GuardError("Git object metadata could not be verified") from None
    if not 0 <= size <= MAX_TEXT_BYTES:
        raise GuardError("Git object metadata exceeds its inspection bound")
    content = _git(root, "cat-file", "-p", oid)
    if len(content) != size:
        raise GuardError("Git object content could not be verified")
    return metadata[1].decode("ascii"), content


def _commit_tree(root: Path, oid: str) -> list[tuple[str, str, str, str]]:
    entries = []
    for record in _git(root, "ls-tree", "-r", "-z", oid).split(b"\0"):
        if not record:
            continue
        try:
            metadata, path = record.split(b"\t", 1)
            mode, kind, blob = metadata.decode("ascii").split()
            if kind not in ("blob", "commit") or not _OID.fullmatch(blob):
                raise ValueError()
            entries.append((path.decode("utf-8", "surrogateescape"), mode, blob, "0"))
        except (ValueError, UnicodeError):
            raise GuardError("Git tree content could not be verified") from None
    return entries


def scan_outgoing(root: Path, updates: Iterable[tuple[str, str | None]]) -> tuple[int, list[Finding]]:
    """Inspect outgoing history, including earlier versions deleted at the tip.

    Each update is (new object ID, previous remote object ID or None). New refs
    and remote bases absent locally scan full reachable history rather than
    trusting other tracking refs. No remote or working-tree files are read.
    """
    commits = set()
    findings = []
    for new, old in updates:
        if not _OID.fullmatch(new) or (old is not None and not _OID.fullmatch(old)):
            raise GuardError("A complete Git object ID is required")
        if set(new) == {"0"}:
            continue  # Ref deletion uploads no local content.
        tip = new
        for depth in range(16):
            kind, content = _object(root, tip)
            if kind == "commit":
                break
            findings.extend(check_bytes(f"git-metadata/tag/{tip}.txt", content))
            first = content.split(b"\n", 1)[0]
            if not first.startswith(b"object "):
                raise GuardError("Git tag target could not be verified")
            try:
                tip = first[7:].decode("ascii")
            except UnicodeError:
                raise GuardError("Git tag target could not be verified") from None
        else:
            raise GuardError("Git tag chain exceeds its inspection bound")
        revision_args = [tip]
        if old is not None and set(old) != {"0"}:
            try:
                base = _git(root, "rev-parse", "--verify", old + "^{commit}").decode("ascii").strip()
                if not _OID.fullmatch(base):
                    raise GuardError("Git remote base could not be verified")
            except (GuardError, UnicodeError):
                base = None  # Missing remote history: inspect all local history.
            if base:
                revision_args.append("^" + base)
        try:
            outgoing = _git(root, "rev-list", *revision_args, "--").decode("ascii").splitlines()
        except UnicodeError:
            raise GuardError("Git outgoing history could not be verified") from None
        if any(not _OID.fullmatch(oid) for oid in outgoing):
            raise GuardError("Git outgoing history could not be verified")
        commits.update(outgoing)
    entries = set()
    for oid in sorted(commits):
        kind, content = _object(root, oid)
        if kind != "commit":
            raise GuardError("Git outgoing commit could not be verified")
        findings.extend(check_bytes(f"git-metadata/commit/{oid}.txt", content))
        entries.update(_commit_tree(root, oid))
    count, tree_findings = _scan_entries(root, sorted(entries))
    # Identical files may occur at many commits; diagnostics need list each issue once.
    return count, list(dict.fromkeys([*findings, *tree_findings]))


def parse_push_updates(data: bytes) -> list[tuple[str, str]]:
    if len(data) > 65536:
        raise GuardError("Push update list exceeds its inspection bound")
    updates = []
    for line in data.splitlines():
        fields = line.split()
        if len(fields) != 4:
            raise GuardError("Push update list could not be verified")
        try:
            new, old = fields[1].decode("ascii"), fields[3].decode("ascii")
        except UnicodeError:
            raise GuardError("Push object IDs could not be verified") from None
        if not _OID.fullmatch(new) or not _OID.fullmatch(old):
            raise GuardError("Push object IDs could not be verified")
        updates.append((new, old))
    return updates


def _regular_file(root: Path, path: str) -> Path | None:
    target = root
    for part in _normalized(path).split("/"):
        target /= part
        info = target.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            return None
    return target if stat.S_ISREG(info.st_mode) else None


def scan_worktree(root: Path) -> tuple[int, list[Finding]]:
    """Inspect tracked and nonignored untracked files without reading profiles."""
    entries = _index(root)
    indexed = {path: (mode, stage) for path, mode, _, stage in entries}
    names = set(indexed)
    names.update(name.decode("utf-8", "surrogateescape") for name in
                 _git(root, "ls-files", "--others", "--exclude-standard", "-z").split(b"\0") if name)
    findings = []
    for path in sorted(names):
        problems = check_path(path)
        if path in indexed:
            mode, stage = indexed[path]
            if mode not in ("100644", "100755"):
                problems.append(Finding("unsupported_file_type", _normalized(path)))
            if stage != "0":
                problems.append(Finding("unmerged_index", _normalized(path)))
        if problems:
            findings.extend(problems)
            continue
        try:
            target = _regular_file(root, path)
            if target is None:
                findings.append(Finding("unsupported_file_type", _normalized(path)))
            elif not _is_binary_path(path):
                with target.open("rb") as stream:
                    findings.extend(check_bytes(path, stream.read(MAX_TEXT_BYTES + 1)))
        except FileNotFoundError:
            # A tracked deletion is allowed in the working view; the index view
            # still checks its staged content until the deletion is staged.
            if path not in indexed:
                findings.append(Finding("file_changed_during_scan", _normalized(path)))
        except OSError:
            findings.append(Finding("file_unreadable", _normalized(path)))
    return len(names), findings


def print_findings(findings: Iterable[Finding]) -> None:
    for finding in findings:
        print(f"{finding.category}: {json.dumps(finding.path, ensure_ascii=True)}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--worktree", action="store_true", help="Inspect tracked/untracked nonignored working files before staging")
    modes.add_argument("--pre-push", action="store_true", help="Read Git pre-push updates from stdin and inspect outgoing history")
    modes.add_argument("--outgoing", metavar="OBJECT_ID", help="Inspect committed history before a connector/API upload; full object ID required")
    parser.add_argument("--base", metavar="REMOTE_OBJECT_ID", help="Previously published remote object ID, only with --outgoing")
    args = parser.parse_args(argv)
    if args.base and not args.outgoing:
        parser.error("--base requires --outgoing")
    try:
        # Executed from the repository being published, not an implicit profile.
        root = Path.cwd()
        if args.pre_push:
            count, findings = scan_outgoing(root, parse_push_updates(sys.stdin.buffer.read(65537)))
            mode = "outgoing committed file versions"
        elif args.outgoing:
            count, findings = scan_outgoing(root, [(args.outgoing, args.base)])
            mode = "outgoing committed file versions"
        elif args.worktree:
            count, findings = scan_worktree(root)
            mode = "working files"
        else:
            count, findings = scan_index(root)
            mode = "staged files"
    except GuardError as error:
        print(f"Publication guard failed: {error}")
        return 2
    if findings:
        print_findings(findings)
        print(f"Publication blocked: {len(findings)} finding(s). No matching content was printed.")
        return 1
    print(f"Publication guard passed for {count} {mode}; recognizable formats only.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
