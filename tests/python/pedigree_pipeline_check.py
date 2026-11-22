"""C# scan-to-frozen-query fixture through current bridge preparation and transport.

Generate the required synthetic input with InventoryLookup.Tests --fixture <path>.
No provider process, network, credentials, or real game data is used here.
"""
import deferred_fixture
import argparse
import json
import unittest
from pathlib import Path
from unittest.mock import patch

import companion
from companion_architecture_check import wire_request
from inventory_lookup import TOOL, validate_result, validate_summary
from inventory_lookup_check import arguments, inventory_message
from inventory_question_check import QuestionChecks
from quest_lookup_check import completed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fixture", type=Path)
    args = parser.parse_args()
    fixture = json.loads(args.fixture.read_text(encoding="utf-8"))["auto_fledgling"]
    summary, result = fixture["summary"], fixture["result"]
    query = arguments(view="pedigrees")
    assert validate_result(result, validate_summary(summary), query) == result
    filtered = validate_result(fixture["filtered_result"], validate_summary(summary),
                               arguments(view="pedigrees", query="PRIVATE_SYNTHETIC_BIRD"))
    assert filtered["rows"] == [] and filtered["verified_pedigree_count"] == 1
    supplied = result["rows"][0]["pedigree"]
    assert supplied["status"] == "decoded_tooltip"
    assert (supplied["kind"], supplied["grade"], supplied["sex"]) == ("fledgling", 7, "male")
    assert supplied["father_stars"] == [1, 2, 3, 4, 1]
    assert supplied["mother_stars"] == [4, 3, 2, 1, 4]

    payload = wire_request(14)
    payload.update(version=15, currency_lookup=None, inventory_lookup=summary,
                   question="Read the fledgling's two parent rows.")
    client, calls = deferred_fixture.client([inventory_message(arguments=query), *completed()])
    outputs, callbacks = [], []
    client.send = outputs.append

    def fetch(arguments):
        callbacks.append(arguments)
        return result

    with patch.object(companion, "emit") as emit:
        client.ask(payload, inventory_query=fetch)
    thread = next(value for method, value in calls if method == "thread/start")
    deferred_fixture.assert_exposed(unittest.TestCase(), client, calls, [TOOL])
    policy = deferred_fixture.instructions(client, calls)
    assert "parent_star_order" not in policy
    assert "maximum_speed, acceleration, endurance, stamina, cunning" in policy
    assert "first use view=pedigrees with empty query, ids and bags" in policy
    assert "An empty filtered result cannot prove missing pedigrees" in policy
    assert "decoded_tooltip or user_verified_tooltip" in policy
    assert "need no manual confirmation" in policy
    assert "scan and verify" not in policy
    assert "all allowed context is already supplied" not in policy
    turn = next(value for method, value in calls if method == "turn/start")
    context = json.loads(turn["input"][1]["text"].split("\n", 1)[1])
    assert context["inventory_lookup"] == validate_summary(summary)
    assert context["inventory"] is None and "father_stars" not in json.dumps(turn)
    assert callbacks == [query] and outputs[0]["result"]["success"]
    transported = json.loads(outputs[0]["result"]["contentItems"][0]["text"])
    assert transported == result
    assert "Pedigrees: 1 available in sent snapshot; 1 returned by inventory lookups." in emit.call_args.args[0]["question_context"]
    print("PASS automatic C# tooltip source, frozen slot query, Python validator, v15 preparation and provider transport")


if __name__ == "__main__":
    main()
