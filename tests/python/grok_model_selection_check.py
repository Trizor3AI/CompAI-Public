"""Offline Grok session selection checks; no account, runtime, or model calls."""
from copy import deepcopy
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'bridge'))
from companion_provider import CompanionFailure
from grok_provider import GrokRuntime, agent_profile


class SessionSelectionChecks(unittest.TestCase):
    def runtime(self, *, effort='high', model='grok-4.7', saved_effort=None,
                config_options=None, selected_model='grok-4.7'):
        client = object.__new__(GrokRuntime)
        client.sid = None
        client.model = model
        client.effort = effort
        client.profile = Path('synthetic-profile')
        client.readiness = lambda: {}
        client.model_state = {'currentModelId': 'grok-4.7'}
        client.models = lambda: [
            {'id': 'auto', 'efforts': ['none'], 'default_effort': 'none'},
            {'id': 'grok-4.7', 'efforts': ['low', 'medium', 'high', 'xhigh'],
             'default_effort': 'medium'},
        ]
        # Shape confirmed with the pinned 1.0.62 ACP runtime in an isolated
        # localhost-only fixture. Disk summary is deliberately independent.
        session = {
            'sessionId': 'synthetic-session',
            'models': {'currentModelId': selected_model},
            'configOptions': config_options if config_options is not None else [
                {'id': 'model', 'type': 'select', 'currentValue': selected_model},
                {'id': 'reasoning_effort', 'type': 'select', 'currentValue': effort,
                 'options': [{'value': x, 'name': x} for x in ('low', 'medium', 'high', 'xhigh')]},
            ],
        }
        client.calls = []
        def call(method, params=None):
            client.calls.append((method, deepcopy(params)))
            if method == 'session/new':
                return deepcopy(session)
            if method == '_x.ai/session/state':
                summary = {'agent_profile': agent_profile()}
                if saved_effort is not None:
                    summary['reasoning_effort'] = saved_effort
                return {'summary': summary}
            if method == '_x.ai/mcp/list':
                return {'result': {'sessionMcpResolved': True, 'servers': [
                    {'name': 'compai', 'session': {'status': 'ready', 'tools': []}},
                ]}}
            raise AssertionError('Unexpected operation: ' + method)
        client.call = call
        return client

    def test_live_effort_is_authoritative_over_missing_or_stale_disk_summary(self):
        for requested in ('low', 'medium', 'high', 'xhigh'):
            for persisted in (None, 'low', 'medium'):
                with self.subTest(requested=requested, persisted=persisted):
                    client = self.runtime(effort=requested, saved_effort=persisted)
                    self.assertEqual(client.open('Synthetic instructions.', []), 'synthetic-session')
                    request = client.calls[0][1]
                    self.assertEqual(request['_meta']['modelId'], 'grok-4.7')
                    self.assertEqual(request['_meta']['reasoningEffort'], requested)
                    self.assertEqual([x[0] for x in client.calls],
                                     ['session/new', '_x.ai/session/state', '_x.ai/mcp/list'])

    def test_wrong_live_effort_rejected_even_if_persisted_summary_matches(self):
        client = self.runtime(saved_effort='high', config_options=[
            {'id': 'reasoning_effort', 'type': 'select', 'currentValue': 'medium'},
        ])
        with self.assertRaisesRegex(CompanionFailure, 'did not confirm the selected reasoning effort'):
            client.open('Synthetic instructions.', [])
        self.assertNotIn('_x.ai/mcp/list', [x[0] for x in client.calls])

    def test_missing_malformed_and_ambiguous_acknowledgements_rejected(self):
        valid = {'id': 'reasoning_effort', 'type': 'select', 'currentValue': 'high'}
        for options in ([], {}, 'high', [None], [valid, valid],
                        [dict(valid, type='text')], [dict(valid, currentValue=None)],
                        [dict(valid, id='unrelated')], [{'id': 'reasoning_effort'}]):
            with self.subTest(options=options):
                client = self.runtime(saved_effort='high', config_options=options)
                with self.assertRaisesRegex(CompanionFailure, 'did not confirm the selected reasoning effort'):
                    client.open('Synthetic instructions.', [])

    def test_model_identity_still_required(self):
        client = self.runtime(selected_model='grok-4.6', saved_effort='high')
        with self.assertRaisesRegex(CompanionFailure, 'Unexpected Grok selected model'):
            client.open('Synthetic instructions.', [])

    def test_question_start_returns_the_single_checked_quota_and_blocks_exhaustion(self):
        from companion import AccountCompanion
        client = self.runtime()
        wrapper = AccountCompanion.__new__(AccountCompanion)
        wrapper.runtime = client
        quota = {'primary': {'usedPercent': 12}}
        client.readiness = Mock(return_value=quota)
        provider, session, reported = wrapper.open_question('Synthetic instructions.', [])
        self.assertIs(provider, client)
        self.assertEqual(session, 'synthetic-session')
        self.assertIs(reported, quota)
        client.readiness.assert_called_once_with()
        blocked = self.runtime()
        blocked.readiness = Mock(side_effect=CompanionFailure('quota', 'exhausted'))
        wrapper.runtime = blocked
        with self.assertRaisesRegex(CompanionFailure, 'exhausted'):
            wrapper.open_question('Synthetic instructions.', [])
        self.assertEqual(blocked.calls, [])

    def test_auto_does_not_impose_an_explicit_effort(self):
        client = self.runtime(model='auto', effort='none', config_options=[])
        self.assertEqual(client.open('Synthetic instructions.', []), 'synthetic-session')
        self.assertNotIn('reasoningEffort', client.calls[0][1]['_meta'])


if __name__ == '__main__':
    unittest.main()
