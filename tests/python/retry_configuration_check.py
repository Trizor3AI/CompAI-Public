"""Guard emitted retry/agent settings without starting a runtime or listener."""
import ast
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import companion
from model_selection import DEFAULT_EFFORT, DEFAULT_MODEL

ROOT = Path(__file__).resolve().parents[2]
EXPECTED = {
    "features.unbounded_connection_retries": "false",
    "features.multi_agent": "false",
    "features.multi_agent_v2": "false",
    "agents.enabled": "false",
}


class CapturedStart(RuntimeError):
    pass


class RetryConfigurationChecks(unittest.TestCase):
    def assert_settings(self, values):
        for key, expected in EXPECTED.items():
            self.assertEqual([value.split("=", 1)[1] for value in values if value.startswith(key + "=")], [expected])

    def test_companion_emits_retry_off_with_and_without_web(self):
        scratch = ROOT / ".test-tmp"
        scratch.mkdir(exist_ok=True)
        for web in (False, True):
            with self.subTest(web=web), tempfile.TemporaryDirectory(prefix="retry-config-", dir=scratch) as temporary:
                calls = []

                def capture(command, **kwargs):
                    calls.append((command, kwargs))
                    raise CapturedStart("No process started")

                with patch.object(companion, "PROFILE", Path(temporary)), \
                        patch.object(companion, "find_runtime", return_value=Path("synthetic.exe")), \
                        patch.object(companion, "checked_pin", return_value={}), \
                        patch.object(companion, "checked_web_pin"), \
                        patch.object(companion, "checked_model_selection", return_value={DEFAULT_MODEL: [DEFAULT_EFFORT]}), \
                        patch.object(companion, "start_owned_process", side_effect=capture):
                    with self.assertRaises(CapturedStart):
                        companion.Companion(web_search=web)
                self.assertEqual(len(calls), 1)
                command, _ = calls[0]
                self.assertEqual(command[1:3], ["app-server", "--strict-config"])
                values = [command[index + 1] for index, value in enumerate(command) if value == "-c"]
                self.assert_settings(values)
                self.assertIn('web_search="live"' if web else 'web_search="disabled"', values)

    def test_probe_config_keeps_the_same_retry_and_agent_boundary(self):
        # Inspect the literal list that probe.main extends into its child argv;
        # importing/running the probe would create a listener and app-server process.
        tree = ast.parse((ROOT / "tools/isolation_mock_probe.py").read_text(encoding="utf-8"))
        values = [node.value for assignment in ast.walk(tree)
                  if isinstance(assignment, ast.Assign) and isinstance(assignment.value, ast.List)
                  and any(isinstance(target, ast.Name) and target.id == "config" for target in assignment.targets)
                  for node in assignment.value.elts
                  if isinstance(node, ast.Constant) and isinstance(node.value, str)]
        self.assert_settings(values)


if __name__ == "__main__":
    unittest.main()
