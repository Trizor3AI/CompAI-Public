"""Synthetic CurrentUser DPAPI vault checks; no account, provider, or network."""
import ctypes
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import credential_store as vault


ROOT = Path(__file__).resolve().parents[2]
TEST_ROOT = ROOT / ".test-tmp" / "credential-vault-tests"
SYNTHETIC = {"access_token": "SYNTHETIC-ONLY-NOT-A-REAL-TOKEN", "refresh_token": "SYNTHETIC-ROTATION", "expires": 42}


@unittest.skipUnless(os.name == "nt", "CurrentUser DPAPI requires Windows")
class CredentialStoreChecks(unittest.TestCase):
    def setUp(self):
        self.assertTrue(TEST_ROOT.resolve().is_relative_to(ROOT.resolve()))
        TEST_ROOT.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=TEST_ROOT)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def test_actual_dpapi_roundtrip_scope_and_ciphertext_only(self):
        copilot = vault.CredentialStore(self.root, "copilot")
        grok = vault.CredentialStore(self.root, "grok")
        self.assertIsNone(copilot.load())
        with copilot.locked():
            copilot.save(SYNTHETIC)
            self.assertEqual(copilot.load(), SYNTHETIC)
        grok.save({"token": "SYNTHETIC-OTHER-PROVIDER"})
        self.assertEqual(grok.load(), {"token": "SYNTHETIC-OTHER-PROVIDER"})
        self.assertEqual(copilot.load(), SYNTHETIC)
        for path in self.root.rglob("*"):
            if path.is_file():
                data = path.read_bytes()
                self.assertNotIn(b"SYNTHETIC", data)
                self.assertNotIn(b"access_token", data)
                self.assertIn(path.suffix, (".lock", ".dpapi"))
        self.assertEqual(vault.UI_FORBIDDEN, 1)  # No machine scope (4) or audit (16).
        self.assertTrue(copilot.delete())
        self.assertFalse(copilot.delete())
        self.assertIsNone(copilot.load())
        self.assertIsNotNone(grok.load())

    def test_wrong_provider_and_tamper_fail_without_overwriting(self):
        source = vault.CredentialStore(self.root, "copilot")
        other = vault.CredentialStore(self.root, "grok")
        source.save(SYNTHETIC)
        original = source.path.read_bytes()
        other.path.write_bytes(original)
        with self.assertRaises(vault.CredentialStoreError):
            other.load()
        with self.assertRaises(vault.CredentialStoreError):
            other.save({"new": "SYNTHETIC"})
        self.assertEqual(other.path.read_bytes(), original)
        damaged = bytearray(original)
        damaged[len(damaged) // 2] ^= 1
        source.path.write_bytes(damaged)
        with self.assertRaises(vault.CredentialStoreError):
            source.load()
        with self.assertRaises(vault.CredentialStoreError):
            source.save(SYNTHETIC)
        self.assertEqual(source.path.read_bytes(), damaged)
        self.assertTrue(source.delete())  # Explicit local logout can remove corruption.

    def test_input_bounds_and_bad_files_are_preserved(self):
        for provider in ("codex", "Grok", "../grok", "grok/extra", None, []):
            with self.assertRaises(vault.CredentialStoreError):
                vault.CredentialStore(self.root, provider)
        with self.assertRaises(vault.CredentialStoreError):
            vault.CredentialStore("relative", "grok")
        store = vault.CredentialStore(self.root, "grok")
        store.save(SYNTHETIC)
        original = store.path.read_bytes()
        for invalid in ([], {1: "bad"}, {"value": float("nan")}, {"value": set()}, {"value": "x" * vault.MAX_PLAINTEXT_BYTES}):
            with self.assertRaises(vault.CredentialStoreError):
                store.save(invalid)
            self.assertEqual(store.path.read_bytes(), original)
        for malformed in (b"future-format", vault.HEADER + b"x" * vault.MAX_FILE_BYTES):
            store.path.write_bytes(malformed)
            with self.assertRaises(vault.CredentialStoreError):
                store.load()
            with self.assertRaises(vault.CredentialStoreError):
                store.save(SYNTHETIC)
            self.assertEqual(store.path.read_bytes(), malformed)

    def test_atomic_write_failure_retains_previous_vault(self):
        store = vault.CredentialStore(self.root, "grok")
        store.save(SYNTHETIC)
        original = store.path.read_bytes()
        with patch.object(vault.os, "replace", side_effect=OSError("Do not expose this text")):
            with self.assertRaises(vault.CredentialStoreError) as error:
                store.save({"rotated": "SYNTHETIC"})
        self.assertNotIn("Do not expose", str(error.exception))
        self.assertEqual(store.path.read_bytes(), original)
        self.assertEqual(store.load(), SYNTHETIC)
        self.assertEqual(list(store.directory.glob("*.tmp")), [])
        original_dpapi = vault._dpapi
        def failing_encrypt(data, provider, *, decrypt):
            if not decrypt:
                raise vault.CredentialStoreError("Synthetic encryption failure.")
            return original_dpapi(data, provider, decrypt=decrypt)
        with patch.object(vault, "_dpapi", side_effect=failing_encrypt):
            with self.assertRaises(vault.CredentialStoreError):
                store.save({"rotated": "SYNTHETIC"})
        self.assertEqual(store.path.read_bytes(), original)

    def test_schema_digest_and_duplicate_keys_fail_closed(self):
        store = vault.CredentialStore(self.root, "grok")
        store.load()
        invalid = [b'{"schema":1,"schema":1}',
                   b'{"schema":2,"provider":"grok","credential":{},"digest":"x"}',
                   b'{"schema":1,"provider":"copilot","credential":{},"digest":"x"}',
                   b'{"schema":1,"provider":"grok","credential":{},"digest":"wrong"}']
        for data in invalid:
            encrypted = vault.HEADER + vault._dpapi(data, "grok", decrypt=False)
            store.path.write_bytes(encrypted)
            with self.assertRaises(vault.CredentialStoreError):
                store.load()
            self.assertEqual(store.path.read_bytes(), encrypted)

    def test_reparse_paths_are_rejected(self):
        store = vault.CredentialStore(self.root, "grok")
        store.save(SYNTHETIC)
        original = Path.lstat
        def fake_reparse(path, *args, **kwargs):
            info = original(path, *args, **kwargs)
            if path == store.path:
                class Reparse:
                    st_mode = info.st_mode
                    st_file_attributes = 0x400
                return Reparse()
            return info
        with patch.object(Path, "lstat", fake_reparse):
            for action in (store.load, lambda: store.save(SYNTHETIC), store.delete):
                with self.assertRaisesRegex(vault.CredentialStoreError, "Linked"):
                    action()
        self.assertEqual(store.load(), SYNTHETIC)

    def test_cross_process_lock_and_provider_independence(self):
        store = vault.CredentialStore(self.root, "grok")
        other = vault.CredentialStore(self.root, "copilot")
        second = vault.CredentialStore(self.root, "grok")
        code = """import sys
sys.path.insert(0,sys.argv[1])
from credential_store import CredentialStore, CredentialStoreError
try:
    with CredentialStore(sys.argv[2],'grok').locked(timeout=.1): pass
except CredentialStoreError:
    raise SystemExit(7)
raise SystemExit(0)
"""
        with store.locked():
            with self.assertRaisesRegex(vault.CredentialStoreError, "in progress"):
                with second.locked(timeout=.05):
                    pass
            other.save(SYNTHETIC)  # Different provider must remain independent.
            result = subprocess.run([sys.executable, "-B", "-c", code, str(ROOT / "bridge"), str(self.root)],
                                    capture_output=True, timeout=10)
            self.assertEqual(result.returncode, 7)
            self.assertEqual(result.stdout, b"")
            self.assertEqual(result.stderr, b"")
        with second.locked(timeout=.1):
            second.save(SYNTHETIC)
        self.assertEqual(store.load(), SYNTHETIC)


if __name__ == "__main__":
    unittest.main()
