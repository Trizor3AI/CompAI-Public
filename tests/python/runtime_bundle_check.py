"""Offline private-runtime selection and integrity fixtures; never launch Codex."""

import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import companion
import runtime_bundle
from prepare_isolation_catalog import digest
from runtime_bundle import resolve_runtime, validate_runtime


class RuntimeBundleTests(unittest.TestCase):
    def setUp(self):
        # Use the writable workspace; sandboxed Windows TEMP can be redirected.
        self.temporary = tempfile.TemporaryDirectory(prefix=".runtime-fixture-", dir=Path(__file__).resolve().parent)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "companion"
        self.folder = self.root / "runtime" / "win-x64"
        self.folder.mkdir(parents=True)
        self.executable = self.folder / "codex.exe"
        self.host = self.folder / "codex-code-mode-host.exe"
        self.executable.write_bytes(b"private CLI fixture")
        self.host.write_bytes(b"private code host fixture")
        self.pin = {"binary_sha256": digest(self.executable), "runtime_files": {
            path.name: digest(path) for path in (self.executable, self.host)}}

    def test_private_runtime_ignores_path_and_desktop_version(self):
        desktop = self.root.parent / "OpenAI" / "Codex" / "bin" / "new-release"
        desktop.mkdir(parents=True)
        (desktop / "codex.exe").write_bytes(b"different desktop version")
        with patch.dict(os.environ, {"PATH": str(desktop), "LOCALAPPDATA": str(self.root.parent)}):
            self.assertEqual(resolve_runtime(self.root), self.executable)
            validate_runtime(self.executable, self.pin)
            self.executable.unlink()
            with self.assertRaisesRegex(RuntimeError, "bundle"):
                resolve_runtime(self.root)

    def test_missing_host_and_unexpected_dll_are_rejected(self):
        self.host.unlink()
        with self.assertRaises(RuntimeError):
            validate_runtime(self.executable, self.pin)
        self.host.write_bytes(b"private code host fixture")
        (self.folder / "unexpected.dll").write_bytes(b"unlisted")
        with self.assertRaisesRegex(RuntimeError, "differ"):
            validate_runtime(self.executable, self.pin)

    def test_each_binary_is_checked(self):
        for path in (self.executable, self.host):
            with self.subTest(file=path.name):
                original = path.read_bytes()
                path.write_bytes(b"tampered")
                with self.assertRaisesRegex(RuntimeError, "pin changed"):
                    validate_runtime(self.executable, self.pin)
                path.write_bytes(original)

    def test_manifest_requires_exact_names_and_valid_matching_hashes(self):
        changes = (
            lambda value: value.pop("runtime_files"),
            lambda value: value["runtime_files"].pop("codex-code-mode-host.exe"),
            lambda value: value["runtime_files"].update({"../codex.exe": "a" * 64}),
            lambda value: value["runtime_files"].update({"C:\\outside.exe": "a" * 64}),
            lambda value: value["runtime_files"].update({"codex.exe": "not a hash"}),
            lambda value: value.update(binary_sha256="a" * 64),
        )
        for change in changes:
            value = json.loads(json.dumps(self.pin))
            change(value)
            with self.assertRaisesRegex(RuntimeError, "manifest"):
                validate_runtime(self.executable, value)

    def test_non_private_executable_rejected(self):
        for path in (self.root.parent / "codex.exe", Path("runtime/win-x64/codex.exe"), self.host):
            with self.assertRaisesRegex(RuntimeError, "private"):
                validate_runtime(path, self.pin)

    def test_reparse_files_and_directories_rejected(self):
        # Portable fixture also covers Windows junctions without symlink privileges.
        real_lstat = Path.lstat
        for blocked in (self.root, self.folder.parent, self.folder, self.executable, self.host):
            def fake_lstat(path, *args, **kwargs):
                info = real_lstat(path, *args, **kwargs)
                if path == blocked:
                    class Reparse:
                        st_mode = info.st_mode
                        st_file_attributes = 0x400
                    return Reparse()
                return info
            with self.subTest(path=blocked), patch.object(Path, "lstat", fake_lstat):
                with self.assertRaisesRegex(RuntimeError, "symlinks or junctions"):
                    validate_runtime(self.executable, self.pin)

    def prepare_preflight(self):
        evidence = self.root / ".isolation-probe"
        evidence.mkdir()
        catalog = evidence / "no-patch-models.json"
        catalog.write_text("{}", encoding="utf-8")
        pin = {**self.pin, "model": "fixture", "catalog_sha256": digest(catalog)}
        cases = []
        for name in ("shell-control", "text", "snapshot", "deny-command", "deny-patch", "inspect"):
            report = evidence / f"verified-{name}.json"
            report.write_text(json.dumps({"fixture": name}), encoding="utf-8")
            cases.append({"name": name, "file": report.name, "sha256": digest(report), "passed": True})
        (evidence / "runtime-pin.json").write_text(json.dumps(pin), encoding="utf-8")
        (evidence / "validation-success.json").write_text(
            json.dumps({**pin, "all_passed": True, "cases": cases}), encoding="utf-8")
        return catalog, pin

    def test_full_preflight_hashes_each_executable_once(self):
        catalog, pin = self.prepare_preflight()
        hashed = []
        def record(path):
            hashed.append(Path(path))
            return digest(path)
        with patch.object(companion, "CATALOG", catalog), \
                patch.object(companion, "digest", side_effect=record), \
                patch.object(runtime_bundle, "digest", side_effect=record):
            self.assertEqual(companion.checked_pin(self.executable), pin)
        self.assertEqual(hashed.count(self.executable), 1)
        self.assertEqual(hashed.count(self.host), 1)
        self.assertEqual(hashed.count(catalog), 1)
        self.assertEqual(len(hashed), 9)  # Two executables, catalog, six evidence files.

    def test_full_preflight_still_rejects_runtime_catalog_and_evidence_changes(self):
        catalog, _ = self.prepare_preflight()
        for path in (self.executable, self.host, catalog, catalog.parent / "verified-deny-command.json"):
            with self.subTest(file=path.name):
                original = path.read_bytes()
                try:
                    path.write_bytes(b"changed fixture")
                    with patch.object(companion, "CATALOG", catalog), self.assertRaises(RuntimeError):
                        companion.checked_pin(self.executable)
                finally:
                    path.write_bytes(original)


if __name__ == "__main__":
    unittest.main()
