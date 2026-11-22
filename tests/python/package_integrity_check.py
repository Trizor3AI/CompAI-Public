"""Synthetic package bounds and process-lifecycle checks; no runtime is launched."""

import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
import zipfile


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("compai_integrity_verifier", ROOT / "CompAI/verify_package.py")
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)


class PackageBoundsChecks(unittest.TestCase):
    def test_all_sizes_are_checked_before_any_zip_payload_is_opened(self):
        source = Mock()
        source.stat.return_value.st_size = 3
        for name, size, expected in (("payload.exe", verifier.MAX_ENTRY_BYTES + 1, "size limit"),
                                     ("payload.json", verifier.MAX_TEXT_BYTES + 1, "size limit"),
                                     ("CompAI.json", verifier.MAX_MANIFEST_BYTES + 1, "size limit"),
                                     ("payload.exe", 4, "differs from source")):
            info = zipfile.ZipInfo(name)
            info.file_size = size
            bundle = Mock()
            bundle.infolist.return_value = [info]
            context = Mock()
            context.__enter__ = Mock(return_value=bundle)
            context.__exit__ = Mock(return_value=False)
            with self.subTest(name=name, size=size), patch.object(verifier.zipfile, "ZipFile", return_value=context):
                with patch.object(verifier, "EXPECTED", {name}), patch.object(verifier, "package_sources", return_value={name: source}):
                    with self.assertRaisesRegex(AssertionError, expected):
                        verifier.main()
            bundle.open.assert_not_called()
            bundle.read.assert_not_called()

    def test_total_limit_and_missing_source_fail_without_payload_reads(self):
        entries = {str(i): SimpleNamespace(file_size=verifier.MAX_ENTRY_BYTES) for i in range(3)}
        with self.assertRaisesRegex(AssertionError, "total uncompressed"):
            verifier.check_entry_sizes(entries, {})
        source = Mock()
        source.stat.side_effect = FileNotFoundError("synthetic source")
        with self.assertRaises(FileNotFoundError):
            verifier.check_entry_sizes({"payload.exe": SimpleNamespace(file_size=3)}, {"payload.exe": source})

    def test_stream_hash_is_chunked_and_rejects_growth(self):
        payload = b"synthetic" * 10000
        stream = Mock(wraps=io.BytesIO(payload))
        with patch.object(verifier, "HASH_CHUNK_BYTES", 1024):
            self.assertEqual(verifier.stream_sha256(stream, len(payload)), hashlib.sha256(payload).hexdigest())
        self.assertGreater(stream.read.call_count, 2)
        self.assertTrue(all(0 < call.args[0] <= 1024 for call in stream.read.call_args_list))
        with self.assertRaisesRegex(AssertionError, "checked size"):
            verifier.stream_sha256(io.BytesIO(payload), len(payload) - 1)

    def test_stream_hash_still_checks_zip_crc(self):
        buffer = io.BytesIO()
        payload = b"unique-synthetic-payload"
        with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_STORED) as bundle:
            bundle.writestr("entry", payload)
        damaged = buffer.getvalue().replace(payload, b"X" + payload[1:], 1)
        with zipfile.ZipFile(io.BytesIO(damaged)) as bundle:
            with bundle.open("entry") as stream, self.assertRaises(zipfile.BadZipFile):
                verifier.stream_sha256(stream, len(payload))

    def test_text_and_manifest_caps_apply_before_decoding(self):
        with patch.object(verifier, "MAX_MANIFEST_BYTES", 3):
            with self.assertRaisesRegex(AssertionError, "size limit"):
                verifier.read_manifest(b"{}  ", "synthetic")
        with self.assertRaisesRegex(AssertionError, "size limit"):
            verifier.read_bounded(io.BytesIO(b"1234"), 3)
        bundle = Mock()
        with self.assertRaisesRegex(AssertionError, "size limit"):
            verifier.read_zip_text(bundle, SimpleNamespace(file_size=4, filename="synthetic"), 3)
        bundle.open.assert_not_called()


class SyntheticArchiveChecks(unittest.TestCase):
    def setUp(self):
        scratch = ROOT / ".test-tmp"
        scratch.mkdir(exist_ok=True)
        temporary = tempfile.TemporaryDirectory(prefix="package-integrity-fixture-", dir=scratch)
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        self.archive = root / "synthetic.zip"
        manifest = {"Author": "Trizor3", "Name": "CompAI", "InternalName": "CompAI", "AssemblyVersion": "1.2.3.0"}
        self.payloads = {"CompAI.json": json.dumps(manifest, indent=4).encode(), "CompAI.dll": b"fake DLL",
                         "CompAI.deps.json": b"{}", "companion/asset.txt": b"synthetic text",
                         "companion/python/python.exe": b"fake Python"}
        for name, payload in self.payloads.items():
            relative = ("bridge/asset.txt" if name == "companion/asset.txt" else
                        "bridge/runtime/python-win-x64/python.exe" if name.startswith("companion/python/") else
                        "CompAI/bin/Release/" + name)
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)
        generated = root / "generated.json"
        generated.write_text(json.dumps(manifest), encoding="utf-8")
        (root / "CompAI/CompAI.csproj").write_text(
            "<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>", encoding="utf-8")
        self.payloads["companion/licenses/manifest.json"] = b'{"schema":1,"files":[]}'
        license_manifest = root / "bridge/licenses/manifest.json"
        license_manifest.parent.mkdir(parents=True, exist_ok=True)
        license_manifest.write_bytes(self.payloads["companion/licenses/manifest.json"])
        for name, value in (("ROOT", root), ("RELEASE_MANIFEST", generated), ("EXPECTED", set(self.payloads)),
                            ("ASSETS", ["asset.txt", "licenses/manifest.json"]), ("PYTHON_PIN", {"files": {"python.exe": hashlib.sha256(b"fake Python").hexdigest()}})):
            mocked = patch.object(verifier, name, value)
            mocked.start()
            self.addCleanup(mocked.stop)

    def verify(self):
        with zipfile.ZipFile(self.archive, "w") as bundle:
            for name, payload in self.payloads.items():
                bundle.writestr(name, payload)
        with patch.object(verifier.sys, "argv", ["verify_package.py", str(self.archive)]):
            verifier.main()

    def test_formatting_differences_pass_hashes_and_reach_relocation(self):
        with patch.object(verifier, "relocation_temporary_directory", side_effect=RuntimeError("relocation boundary")) as relocation:
            with self.assertRaisesRegex(RuntimeError, "relocation boundary"):
                self.verify()
        relocation.assert_called_once()

    def test_same_size_corruption_fails_source_and_python_pin_comparison(self):
        for name in ("CompAI.dll", "companion/asset.txt", "companion/python/python.exe"):
            original = self.payloads[name]
            self.payloads[name] = b"X" + original[1:]
            with self.subTest(name=name), patch.object(verifier, "relocation_temporary_directory") as relocation:
                with self.assertRaisesRegex(AssertionError, "differs from source or pin"):
                    self.verify()
                relocation.assert_not_called()
            self.payloads[name] = original


class ProbeLifecycleChecks(unittest.TestCase):
    def test_owned_job_is_closed_on_success_failure_and_timeout(self):
        for outcome in (0, 1, subprocess.TimeoutExpired("synthetic", 7)):
            process = Mock(returncode=outcome if isinstance(outcome, int) else None)
            process.communicate.return_value = ("output", "error")
            if not isinstance(outcome, int):
                process.communicate.side_effect = outcome
            with self.subTest(outcome=outcome), patch.object(verifier, "start_owned_process", return_value=process) as start:
                with patch.object(verifier, "close_owned_process") as close:
                    if outcome == 0:
                        self.assertEqual(verifier.run_checked_process(["synthetic"], cwd=ROOT, timeout=7).returncode, 0)
                    else:
                        expected = subprocess.CalledProcessError if outcome == 1 else subprocess.TimeoutExpired
                        with self.assertRaises(expected):
                            verifier.run_checked_process(["synthetic"], cwd=ROOT, timeout=7)
                    close.assert_called_once_with(process)
            process.communicate.assert_called_once_with(timeout=7)
            self.assertEqual(start.call_args.kwargs["stdin"], subprocess.DEVNULL)

    def test_temporary_cleanup_retries_file_locks_after_failure(self):
        temporary = Mock(name="owned temporary")
        temporary.name = "synthetic-owned-directory"
        temporary.cleanup.side_effect = [PermissionError("held by terminating child"), None]
        with patch.object(verifier.tempfile, "TemporaryDirectory", return_value=temporary), patch.object(verifier.time, "sleep") as sleep:
            with self.assertRaisesRegex(RuntimeError, "probe failed"):
                with verifier.relocation_temporary_directory():
                    raise RuntimeError("probe failed")
        self.assertEqual(temporary.cleanup.call_count, 2)
        sleep.assert_called_once_with(0.1)

    def test_temporary_cleanup_has_a_finite_retry_limit(self):
        temporary = Mock()
        temporary.name = "synthetic-owned-directory"
        temporary.cleanup.side_effect = PermissionError("still locked")
        with patch.object(verifier.tempfile, "TemporaryDirectory", return_value=temporary), patch.object(verifier.time, "sleep") as sleep:
            with self.assertRaises(PermissionError):
                with verifier.relocation_temporary_directory():
                    pass
        self.assertEqual(temporary.cleanup.call_count, 21)
        self.assertEqual(sleep.call_count, 20)


if __name__ == "__main__":
    unittest.main()
