"""Synthetic release-draft checks; package relocation is mocked and no network is used."""

from datetime import datetime
import importlib.util
import io
import json
from pathlib import Path
import stat
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import zipfile


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("compai_prepare_release", ROOT / "tools/prepare_release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)

# Synthetic paths on a public host; no remote resource is fetched or claimed to exist.
ASSET_BASE = "https://github.com/fixture-owner/fixture-repo/releases/download/v0.53.0"
REPO_URL = "https://github.com/fixture-owner/fixture-repo"


def manifest():
    return {"Author": "Trizor3", "Name": "CompAI", "InternalName": "CompAI", "AssemblyVersion": "0.53.0.0",
            "DalamudApiLevel": 15, "LoadSync": False, "ApplicableVersion": "any", "Tags": ["CompAI"],
            "Description": "Synthetic package", "FutureMetadata": {"enabled": True, "count": 1}}


class ReleaseDraftChecks(unittest.TestCase):
    def setUp(self):
        scratch = ROOT / ".test-tmp"
        scratch.mkdir(exist_ok=True)
        temporary = tempfile.TemporaryDirectory(prefix="release-tool-fixture-", dir=scratch)
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        (self.root / "CompAI").mkdir()
        (self.root / "CompAI/CompAI.csproj").write_text(
            "<Project><PropertyGroup><Version>0.53.0</Version></PropertyGroup></Project>", encoding="utf-8")
        self.generated = self.root / "generated.json"
        self.generated.write_text(json.dumps(manifest()), encoding="utf-8")
        self.archive = self.root / "latest.zip"
        with zipfile.ZipFile(self.archive, "w") as bundle:
            bundle.writestr("CompAI.json", json.dumps(manifest()))
            bundle.writestr("CompAI.dll", b"synthetic - not executable")
        self.output = self.root / ".test-tmp/release-draft"
        for attribute, value in (("ROOT", self.root), ("SCRATCH", self.root / ".test-tmp")):
            mocked = patch.object(release, attribute, value)
            mocked.start()
            self.addCleanup(mocked.stop)
        mocked = patch.object(release.verifier, "RELEASE_MANIFEST", self.generated)
        mocked.start()
        self.addCleanup(mocked.stop)
        # Keep the verifier's owned temporary root inside this writable fixture,
        # independent of the host process's restricted Windows TEMP location.
        mocked = patch.object(release.verifier.tempfile, "tempdir", str(self.root))
        mocked.start()
        self.addCleanup(mocked.stop)

    def prepare(self, **kwargs):
        return release.prepare(kwargs.get("archive", self.archive), kwargs.get("output", self.output),
                               kwargs.get("asset_base_url", ASSET_BASE), kwargs.get("repo_url", REPO_URL))

    def test_verified_copy_metadata_version_dates_and_hashes(self):
        original = self.archive.read_bytes()
        with patch.object(release, "verify_archive") as verify:
            self.prepare()
        draft = self.output / "CompAI-0.53.0.0-PRIVATE-DRAFT.zip"
        verify.assert_called_once_with(draft)
        self.assertEqual(draft.read_bytes(), original)
        self.assertEqual(self.archive.read_bytes(), original)
        feed_path = self.output / "pluginmaster.PRIVATE-DRAFT.json"
        feed = json.loads(feed_path.read_text())[0]
        for key, value in manifest().items():
            self.assertEqual(feed[key], value)
        self.assertEqual(feed["DownloadLinkInstall"], ASSET_BASE + "/CompAI-0.53.0.0.zip")
        self.assertEqual(feed["DownloadLinkUpdate"], feed["DownloadLinkInstall"])
        self.assertTrue(feed["IsHide"])
        self.assertFalse(feed["IsTestingExclusive"])
        self.assertNotIn("DownloadLinkTesting", feed)
        status = json.loads((self.output / "DRAFT-STATUS.json").read_text())
        self.assertFalse(status["publication_approved"])
        self.assertFalse(status["clean_pc_install_verified"])
        self.assertFalse(status["url_availability_checked"])
        self.assertEqual(int(datetime.fromisoformat(status["prepared_at_utc"]).timestamp()), feed["LastUpdate"])
        self.assertEqual(status["last_update_unix_seconds"], feed["LastUpdate"])
        self.assertEqual(status["planned_final_archive_name"], "CompAI-0.53.0.0.zip")
        for name, expected in status["sha256"].items():
            self.assertEqual(release.digest(self.output / name), expected)
        self.assertEqual((self.output / "SHA256SUMS.PRIVATE-DRAFT.txt").read_text(),
                         "".join(f"{value}  {name}\n" for name, value in status["sha256"].items()))
        self.assertEqual(len(list(self.output.iterdir())), 4)

    def test_verifier_failure_removes_only_new_draft(self):
        original = self.archive.read_bytes()
        with patch.object(release, "verify_archive", side_effect=subprocess.CalledProcessError(1, "verifier")):
            with self.assertRaises(subprocess.CalledProcessError):
                self.prepare()
        self.assertFalse(self.output.exists())
        self.assertEqual(self.archive.read_bytes(), original)

    def test_verifier_timeout_removes_new_draft_and_preserves_source(self):
        original = self.archive.read_bytes()
        with patch.object(release, "verify_archive", side_effect=subprocess.TimeoutExpired("verifier", 600)):
            with self.assertRaises(subprocess.TimeoutExpired):
                self.prepare()
        self.assertFalse(self.output.exists())
        self.assertEqual(self.archive.read_bytes(), original)

    def test_timeout_cli_error_is_controlled_without_command_contents(self):
        args = ["prepare_release.py", "--asset-base-url", ASSET_BASE, "--repo-url", REPO_URL,
                "--output", str(self.output)]
        with patch.object(release.sys, "argv", args), patch.object(release.sys, "stderr", new_callable=io.StringIO) as stderr:
            with patch.object(release, "prepare", side_effect=subprocess.TimeoutExpired("unprinted-command", 600)):
                with self.assertRaises(SystemExit) as caught:
                    release.main()
        self.assertEqual(caught.exception.code, 1)
        self.assertIn("timed out after 600 seconds", stderr.getvalue())
        self.assertNotIn("unprinted-command", stderr.getvalue())

    def test_failed_cleanup_preserves_unexpected_concurrent_file(self):
        def reject(_archive):
            (self.output / "owner-file.txt").write_text("retain", encoding="utf-8")
            raise ValueError("synthetic failure")
        with patch.object(release, "verify_archive", side_effect=reject):
            with self.assertRaisesRegex(ValueError, "synthetic failure"):
                self.prepare()
        self.assertEqual([path.name for path in self.output.iterdir()], ["owner-file.txt"])

    def test_existing_output_and_paths_outside_scratch_are_rejected(self):
        self.output.mkdir(parents=True)
        existing = self.output / "existing.txt"
        existing.write_text("keep", encoding="utf-8")
        for output in (self.output, self.root, self.root / ".test-tmp", self.root / "outside",
                       self.root / ".test-tmp/child/../other"):
            with self.subTest(path=output), patch.object(release, "verify_archive") as verify:
                with self.assertRaises(ValueError):
                    self.prepare(output=output)
                verify.assert_not_called()
        self.assertEqual(existing.read_text(), "keep")

    def test_reparse_parent_and_symlink_parent_are_rejected(self):
        self.output.parent.mkdir(parents=True)
        original = Path.lstat
        for mode, attributes in ((stat.S_IFDIR, stat.FILE_ATTRIBUTE_REPARSE_POINT), (stat.S_IFLNK, 0)):
            def marked(path):
                if path == self.output.parent:
                    return SimpleNamespace(st_mode=mode, st_file_attributes=attributes)
                return original(path)
            with self.subTest(mode=mode), patch.object(Path, "lstat", marked):
                with self.assertRaisesRegex(ValueError, "reparse"):
                    release.checked_output(self.output)

    def test_failed_cleanup_does_not_follow_replaced_directory(self):
        def reject(_archive):
            raise ValueError("synthetic failure")
        real_check = release.checked_output
        def check(path, *, require_new=True):
            if not require_new:
                raise ValueError("Output became a reparse point")
            return real_check(path, require_new=require_new)
        with patch.object(release, "verify_archive", side_effect=reject), patch.object(release, "checked_output", check):
            with self.assertRaisesRegex(ValueError, "synthetic failure"):
                self.prepare()
        self.assertEqual(len(list(self.output.iterdir())), 1)

    def test_generated_manifest_mismatch_cannot_create_feed(self):
        changed = manifest()
        changed["Description"] = "different build"
        self.generated.write_text(json.dumps(changed), encoding="utf-8")
        with patch.object(release, "verify_archive"):
            with self.assertRaisesRegex(AssertionError, "generated Release manifest"):
                self.prepare()
        self.assertFalse(self.output.exists())

    def test_unsafe_urls_reject_before_verification_without_echoing_input(self):
        for value in ("http://github.com/a/b", "https://name:secret@github.com/a", "https://github.com/a?secret=x",
                      "https://github.com/a?", "https://github.com/a#", "https://github.com/a#fragment",
                      "https://github.com/a%2fb", "https://github.com/a\\b", " https://github.com/a",
                      "https://github.com/a\n", "https://github.com/<owner>/repo", "https://localhost/a",
                      "https://127.0.0.1/a", "https://[::1]/a", "https://host.local/a", "https://example.com/a",
                      "https://127.1/a", "https://0x7f.0.0.1/a", "https://0177.0.0.1/a", "https://1.2.3.0xff/a",
                      "https://downloads.example.org/a",
                      "https://github.com:443/a", "https://github.com/a/../b", "https://github.com/api_key/secret",
                      "https://github.com/" + "gh" + "p_" + "x" * 40):
            with self.subTest(value=value), patch.object(release, "verify_archive") as verify:
                with self.assertRaises(ValueError) as caught:
                    self.prepare(asset_base_url=value)
                self.assertNotIn(value, str(caught.exception))
                verify.assert_not_called()
                self.assertFalse(self.output.exists())

    def test_trailing_slash_is_normalized_and_inherited_urls_are_checked(self):
        feed, name = release.make_feed(manifest(), ASSET_BASE + "/", REPO_URL + "/", 1)
        self.assertEqual(feed[0]["DownloadLinkInstall"], ASSET_BASE + "/" + name)
        for key, value in (("IconUrl", REPO_URL + "?token=x"), ("ImageUrls", [REPO_URL + "#x"]),
                           ("ImageUrls", REPO_URL), ("TestingAssemblyVersion", "1.0.0.0")):
            entry = manifest()
            entry[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                release.make_feed(entry, ASSET_BASE, REPO_URL, 1)

    def test_package_icon_stays_with_its_release_and_feed_uses_its_asset_directory(self):
        entry = manifest()
        entry["IconUrl"] = REPO_URL + "/releases/download/v0.52.0.0/icon.png"
        with self.assertRaisesRegex(ValueError, "Packaged icon URL"):
            release.make_feed(entry, ASSET_BASE, REPO_URL, 1)
        entry["IconUrl"] = REPO_URL + "/releases/download/v0.53.0.0/icon.png"
        mirror = "https://downloads.fixture-host.org/compai"
        feed, _ = release.make_feed(entry, mirror, REPO_URL, 1)
        self.assertEqual(feed[0]["IconUrl"], mirror + "/icon.png")

    def test_existing_verifier_is_called_with_exact_draft_and_no_bypass(self):
        observed = {}
        def inspect(command, **kwargs):
            observed.update(command=command, **kwargs)
            temporary = Path(kwargs["env"]["TEMP"])
            self.assertTrue(temporary.is_dir())
            # Simulate a forcibly stopped verifier leaving its own nested scratch.
            (temporary / "child-scratch").mkdir()
            (temporary / "child-scratch/data.txt").write_text("synthetic", encoding="utf-8")
        with patch.object(release.verifier, "run_checked_process", side_effect=inspect) as run:
            release.verify_archive(self.archive)
        run.assert_called_once()
        self.assertEqual(observed["command"], [release.sys.executable, "-B", str(release.VERIFIER_PATH), str(self.archive)])
        self.assertEqual(observed["cwd"], self.root)
        self.assertEqual(observed["timeout"], release.VERIFY_TIMEOUT_SECONDS)
        self.assertFalse(observed["capture_output"])
        self.assertEqual(observed["env"]["TMP"], observed["env"]["TEMP"])
        self.assertEqual(observed["env"]["TMPDIR"], observed["env"]["TEMP"])
        self.assertFalse(Path(observed["env"]["TEMP"]).exists())

    def test_outer_timeout_cleans_nested_verifier_scratch(self):
        paths = []
        def timeout(_command, **kwargs):
            temporary = Path(kwargs["env"]["TEMP"])
            paths.append(temporary)
            (temporary / "child.txt").write_text("synthetic", encoding="utf-8")
            raise subprocess.TimeoutExpired("verifier", kwargs["timeout"])
        with patch.object(release.verifier, "run_checked_process", side_effect=timeout):
            with self.assertRaises(subprocess.TimeoutExpired):
                release.verify_archive(self.archive)
        self.assertFalse(paths[0].exists())


if __name__ == "__main__":
    unittest.main()
