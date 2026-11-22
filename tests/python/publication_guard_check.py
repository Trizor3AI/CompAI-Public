"""Synthetic publication checks; never inspect real provider credentials."""
from contextlib import redirect_stdout
import ast
import io
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import publication_guard as guard


ROOT = Path(__file__).resolve().parents[2]
TEST_ROOT = ROOT / ".test-tmp/publication-guard-tests"


def synthetic_token() -> bytes:
    return b"ghp_" + b"SYNTHETIC" * 5


class PublicationGuardChecks(unittest.TestCase):
    def setUp(self):
        TEST_ROOT.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=TEST_ROOT)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.git("init", "--quiet")

    def git(self, *args, input_bytes=None):
        return subprocess.run(["git", "-C", str(self.root), *args], input=input_bytes,
                              capture_output=True, check=True).stdout

    def write(self, path, data):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)

    def commit(self, message=b"Synthetic fixture commit"):
        # These repositories are disposable and never contact a remote.
        self.git("-c", "user.name=Publication Fixture", "-c", "user.email=fixture@invalid",
                 "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + str(self.root / "unused-hooks"),
                 "commit", "--quiet", "--allow-empty", "-F", "-", input_bytes=message)
        return self.git("rev-parse", "HEAD").strip().decode()

    def public_commit(self):
        self.write("public.txt", b"public")
        self.git("add", "--", "public.txt")
        return self.commit()

    def test_private_paths_reject_case_slash_and_backup_bypasses(self):
        for path in ("AUTH.JSON", "x\\Auth.Json.backup", "nested/secret.DPAPI", "token.dpapi.tmp",
                     "nested/.ENV", ".env.example.bak", ".ENV.EXAMPLE", "KEY.PEM", "key.PFX.bak",
                     "x/.Companion/profile/file.txt", "x\\Credentials\\data.txt", "PluginConfigs/data.json",
                     ".test-tmp/private.txt", "./auth.json", "x/../auth.json", "C:/auth.json", "auth.json. ",
                     "public-data-cache/quote.json", "x/MARKET-SCAN-EVIDENCE-V1/ledger.json",
                     "valuation-resume-v1/checkpoint.json", "market-quotes-v3.sqlite3",
                     "export/market-quotes-v3.sqlite3-journal"):
            with self.subTest(path=path):
                self.assertTrue(guard.check_bytes(path, b"harmless"))

    def test_public_ids_placeholder_and_source_literals_allowed(self):
        for path in ("bridge/credential_store.py", "bridge/copilot_oauth.py", "tools/-safe.txt", ".env.example"):
            with self.subTest(path=path):
                self.assertEqual([], guard.check_bytes(path, b'Ov23liuZxhibDyUMMrKg; ghp_<your-token>; sk-placeholder; access_token="SYNTHETIC"'))
        self.assertEqual([], guard.check_bytes("pattern.py", rb"gh[pousr]_[A-Za-z0-9]{30,}"))

    def test_shapes_in_arbitrary_filename_and_safe_diagnostics(self):
        cases = [("github_token", synthetic_token()),
                 ("openai_token", b"sk-proj-" + b"EXAMPLE" * 8),
                 ("xai_token", b"xai-" + b"EXAMPLE" * 8),
                 ("jwt", b"eyJ" + b"A" * 24 + b"." + b"B" * 24 + b"." + b"C" * 24),
                 ("private_key", b"-----BEGIN " + b"PRIVATE KEY-----")]
        for kind, token in cases:
            with self.subTest(kind=kind):
                findings = guard.check_bytes("innocent.dat", b"value=" + token)
                self.assertEqual([guard.Finding(kind, "innocent.dat")], findings)
                output = io.StringIO()
                with redirect_stdout(output):
                    guard.print_findings(findings)
                self.assertNotIn(token.decode(), output.getvalue())
                self.assertEqual([guard.Finding(kind, "utf16.txt")], guard.check_bytes("utf16.txt", token.decode().encode("utf-16")))
        output = io.StringIO()
        with redirect_stdout(output):
            guard.print_findings([guard.Finding("credential_file", "odd\nname.json")])
        self.assertEqual(1, len(output.getvalue().splitlines()))

    def test_staged_content_is_checked_not_worktree(self):
        self.write("safe.txt", b"public")
        self.git("add", "--", "safe.txt")
        self.write("safe.txt", synthetic_token())
        self.assertEqual([], guard.scan_index(self.root)[1])
        self.assertEqual("github_token", guard.scan_worktree(self.root)[1][0].category)
        self.git("add", "--", "safe.txt")
        self.write("safe.txt", b"public")
        self.assertEqual("github_token", guard.scan_index(self.root)[1][0].category)
        self.assertEqual([], guard.scan_worktree(self.root)[1])

    def test_ignored_private_files_are_not_read_and_dash_path_works(self):
        self.write(".gitignore", b".companion/\nauth.json\n")
        self.write(".companion/auth.json", synthetic_token())
        self.write("auth.json", synthetic_token())
        self.write("-option.txt", b"public")
        self.git("add", "--", ".gitignore", "-option.txt")
        self.assertEqual((2, []), guard.scan_worktree(self.root))
        self.assertEqual((2, []), guard.scan_index(self.root))
        self.git("add", "-f", "--", "auth.json")
        # Private tracked names fail before content inspection.
        with patch.object(guard, "_regular_file", side_effect=AssertionError("must not read")):
            with patch.object(guard, "_index", return_value=[("auth.json", "100644", "0" * 40, "0")]):
                with patch.object(guard, "_git", return_value=b""):
                    self.assertEqual("credential_file", guard.scan_worktree(self.root)[1][0].category)
        self.assertEqual("credential_file", guard.scan_index(self.root)[1][0].category)

    def test_symlink_index_rejected_without_following(self):
        oid = self.git("hash-object", "-w", "--stdin", input_bytes=b"outside-secret.txt").strip().decode()
        self.git("update-index", "--add", "--cacheinfo", f"120000,{oid},link.txt")
        self.assertEqual("unsupported_file_type", guard.scan_index(self.root)[1][0].category)
        self.write("link.txt", b"public")
        self.assertEqual("unsupported_file_type", guard.scan_worktree(self.root)[1][0].category)

    def test_worktree_link_rejected_when_available(self):
        self.write("target.txt", synthetic_token())
        try:
            (self.root / "link.txt").symlink_to(self.root / "target.txt")
        except OSError:
            self.skipTest("Host does not permit creating synthetic symlinks")
        self.assertIsNone(guard._regular_file(self.root, "link.txt"))

    def test_git_errors_are_sanitized_and_binary_content_is_not_scanned(self):
        with patch.object(guard.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, b"", synthetic_token())):
            with self.assertRaisesRegex(guard.GuardError, "^Git inspection failed$"):
                guard.scan_index(self.root)
        self.assertEqual([], guard.check_bytes("pinned.dll", synthetic_token()))
        self.assertEqual("text_size_limit", guard.check_bytes("huge.txt", b"x" * (guard.MAX_TEXT_BYTES + 1))[0].category)

    def test_codex_configuration_requires_keyring_without_runtime_start(self):
        tree = ast.parse((ROOT / "bridge/companion.py").read_text(encoding="utf-8"))
        assignments = [node for node in tree.body if isinstance(node, ast.Assign)
                       and any(isinstance(target, ast.Name) and target.id == "CONFIG" for target in node.targets)]
        self.assertEqual(1, len(assignments))
        config = ast.literal_eval(assignments[0].value)
        storage = [value for value in config if value.split("=", 1)[0].strip() == "cli_auth_credentials_store"]
        self.assertEqual(['cli_auth_credentials_store="keyring"'], storage)

    def test_outgoing_deleted_secret_is_blocked_with_clean_index(self):
        base = self.public_commit()
        self.write("ordinary.txt", synthetic_token())
        self.git("add", "--", "ordinary.txt")
        leaked = self.commit()
        self.git("rm", "--", "ordinary.txt")
        tip = self.commit()
        self.assertEqual([], guard.scan_index(self.root)[1])
        self.assertEqual([], guard.scan_worktree(self.root)[1])
        self.assertIn(guard.Finding("github_token", "ordinary.txt"), guard.scan_outgoing(self.root, [(tip, base)])[1])
        # A known published base is excluded; only the corrective commit is new.
        self.assertEqual([], guard.scan_outgoing(self.root, [(tip, leaked)])[1])

    def test_new_branch_and_unknown_base_check_full_history_and_private_rename(self):
        self.public_commit()
        self.write("auth.json", b"public synthetic value")
        self.git("add", "--", "auth.json")
        self.commit()
        self.git("mv", "--", "auth.json", "renamed.txt")
        tip = self.commit()
        for base in (None, "0" * 40, "f" * 40):
            with self.subTest(base=base):
                self.assertIn(guard.Finding("credential_file", "auth.json"), guard.scan_outgoing(self.root, [(tip, base)])[1])
        self.assertEqual((0, []), guard.scan_outgoing(self.root, [("0" * 40, tip)]))

    def test_tags_and_commit_messages_are_inspected_without_printing_content(self):
        base = self.public_commit()
        self.assertEqual([], guard.scan_outgoing(self.root, [(base, None)])[1])  # Lightweight tag.
        self.git("-c", "user.name=Publication Fixture", "-c", "user.email=fixture@invalid",
                 "-c", "tag.gpgsign=false", "tag", "-a", "synthetic-tag", "-F", "-", input_bytes=synthetic_token())
        tag = self.git("rev-parse", "refs/tags/synthetic-tag").strip().decode()
        self.assertEqual("github_token", guard.scan_outgoing(self.root, [(tag, None)])[1][0].category)
        tip = self.commit(message=synthetic_token())
        self.assertEqual("github_token", guard.scan_outgoing(self.root, [(tip, base)])[1][0].category)
        blob = self.git("hash-object", "-w", "--stdin", input_bytes=b"public").strip().decode()
        with self.assertRaisesRegex(guard.GuardError, "Only commit and commit-tag"):
            guard.scan_outgoing(self.root, [(blob, None)])

    def test_pre_push_hook_blocks_locally_without_any_push(self):
        base = self.public_commit()
        self.write("ordinary.txt", synthetic_token())
        self.git("add", "--", "ordinary.txt")
        self.commit()
        self.git("rm", "--", "ordinary.txt")
        tip = self.commit()
        self.write("tools/publication_guard.py", (ROOT / "tools/publication_guard.py").read_bytes())
        self.write(".githooks/pre-push", (ROOT / ".githooks/pre-push").read_bytes())
        (self.root / ".githooks/pre-push").chmod(0o755)
        self.git("config", "--local", "core.hooksPath", ".githooks")
        update = f"refs/heads/test {tip} refs/heads/test {base}\n".encode()
        self.write("push.stdin", update)
        self.assertEqual([(tip, base)], guard.parse_push_updates(update))
        with self.assertRaises(guard.GuardError):
            guard.parse_push_updates(b"malformed\n")
        result = subprocess.run(["git", "-C", str(self.root), "hook", "run",
                                 "--to-stdin=push.stdin", "pre-push"], capture_output=True, check=False)
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"Publication blocked", result.stdout + result.stderr)
        self.assertNotIn(synthetic_token(), result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
