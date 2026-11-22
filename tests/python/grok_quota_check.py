"""Synthetic Grok allowance status; no runtime process, account or network."""
from copy import deepcopy
import unittest
from unittest.mock import Mock

from companion_provider import CompanionFailure
from grok_provider import GrokRuntime, free_quota


BILLING = {
    'subscription_tier': 'Free',
    'config': {
        'prepaidBalance': {'val': 0},
        'creditUsagePercent': 100,
        'currentPeriod': {'end': '2026-10-20T00:00:00+00:00'},
    },
}
SUBSCRIPTION = {'authenticated': True}


class GrokQuotaChecks(unittest.TestCase):
    @staticmethod
    def runtime(billing, topup=None):
        runtime = GrokRuntime.__new__(GrokRuntime)
        runtime._subscription = Mock(return_value=SUBSCRIPTION.copy())
        runtime.models = Mock(return_value=[])
        responses = {'_x.ai/billing': billing, '_x.ai/auto-topup-rule': {} if topup is None else topup}
        runtime.call = Mock(side_effect=lambda method: responses[method])
        return runtime

    def test_exhausted_status_retains_percentage_reset_and_block_reason(self):
        runtime = self.runtime(deepcopy(BILLING))
        status = runtime.status()
        self.assertEqual(status['account']['type'], 'grok')
        self.assertEqual(status['quota']['primary'], {'usedPercent': 100, 'resetsAt': 1792454400})
        self.assertEqual(status['quota_guard'], 'blocked')
        self.assertIn('exhausted', status['quota_block_reason'])
        self.assertEqual([call.args[0] for call in runtime.call.call_args_list],
                         ['_x.ai/billing', '_x.ai/auto-topup-rule'])

    def test_exhaustion_still_blocks_readiness_and_default_quota_check(self):
        for check in (self.runtime(deepcopy(BILLING)).readiness,
                      lambda: free_quota(SUBSCRIPTION, deepcopy(BILLING), {})):
            with self.assertRaises(CompanionFailure) as caught:
                check()
            self.assertEqual(caught.exception.kind, 'quota')
            self.assertEqual(caught.exception.stage, 'provider.quota')
            self.assertIn('exhausted', str(caught.exception))

    def test_available_and_unknown_numeric_allowance_remain_unchanged(self):
        for used in (0, 37.5, 99, None):
            with self.subTest(used=used):
                billing = deepcopy(BILLING)
                billing['config']['creditUsagePercent'] = used
                runtime = self.runtime(billing)
                expected = {} if used is None else {'primary': {'usedPercent': used, 'resetsAt': 1792454400}}
                status = runtime.status()
                self.assertEqual(status['quota'], expected)
                self.assertEqual(status['quota_guard'], 'available')
                self.assertNotIn('quota_block_reason', status)
                self.assertEqual(runtime.readiness(), expected)

    def test_exhausted_status_does_not_invent_missing_reset(self):
        billing = deepcopy(BILLING)
        billing['config'].pop('currentPeriod')
        self.assertEqual(self.runtime(billing).status()['quota']['primary'],
                         {'usedPercent': 100, 'resetsAt': None})

    def test_invalid_or_paid_billing_stays_blocked_without_percentage(self):
        cases = []
        for used in (-1, 101, True, '100', float('nan')):
            billing = deepcopy(BILLING)
            billing['config']['creditUsagePercent'] = used
            cases.append((billing, {}))
        for field, value in (('prepaidBalance', {'val': 1}), ('onDemandCap', {'val': 1})):
            billing = deepcopy(BILLING)
            billing['config'][field] = value
            cases.append((billing, {}))
        paid = deepcopy(BILLING)
        paid['subscription_tier'] = 'Pro'
        cases.extend([(paid, {}), ({'subscription_tier': 'Free'}, {}),
                      (deepcopy(BILLING), {'rule': {'enabled': True}})])
        for index, (billing, topup) in enumerate(cases):
            with self.subTest(case=index):
                runtime = self.runtime(billing, topup)
                status = runtime.status()
                self.assertEqual(status['quota_guard'], 'blocked')
                self.assertNotIn('quota', status)
                self.assertIn('quota_block_reason', status)
                with self.assertRaises(CompanionFailure):
                    runtime.readiness()


if __name__ == '__main__':
    unittest.main()
