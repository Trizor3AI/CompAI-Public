"""Cross-language checks for detached, short-lived menu observations."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import unittest

import collection_lookup
import gc_supply_lookup


class RecentReaderChecks(unittest.TestCase):
    def test_detached_csharp_observations_validate_and_do_not_leak_native_identity(self):
        path = Path(__file__).resolve().parents[2] / ".test-tmp/recent-reader-results.json"
        self.assertTrue(path.is_file(), "Run RecentReaders.Tests --produce-fixtures .test-tmp/recent-reader-results.json first")
        fixtures = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual({item["name"] for item in fixtures}, {"gc_recent", "bestiary_recent"})
        for fixture in fixtures:
            with self.subTest(name=fixture["name"]):
                value = deepcopy(fixture["result"])
                # Refresh only synthetic age in memory, maintaining an age that
                # the former live-only GC contract rejected.
                value["captured_at_utc"] = (datetime.now(timezone.utc) - timedelta(minutes=2)).isoformat()
                if fixture["name"] == "gc_recent":
                    gc_supply_lookup.validate_result(value, {"scope": gc_supply_lookup.SCOPE}, fixture["arguments"])
                    self.assertEqual(value["readiness"], "last_observed_menu")
                    self.assertTrue(all(row["turn_in_available"] is None for row in value["entries"]))
                else:
                    collection_lookup.validate_result(value,
                        {"scope": collection_lookup.SCOPE, "categories": ["beastmaster_bestiary"]}, fixture["arguments"])
                    self.assertEqual(value["readiness"], "last_observed_positive_observations")
                for field in ("owner_content_id", "menu_address", "agent_address", "OwnerContentId", "MenuAddress", "AgentAddress"):
                    self.assertNotIn(field, json.dumps(value))


if __name__ == "__main__":
    unittest.main()
