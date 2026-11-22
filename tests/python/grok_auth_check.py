"""Offline Grok auth recovery, browser ownership and encrypted replacement checks."""
import base64
import hashlib
import json
import os
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import Mock, patch
from types import SimpleNamespace

import companion

from companion_provider import CompanionFailure
from credential_store import CredentialStore, CredentialStoreError
from grok_credentials import (credential_for_token, PENDING_CREDENTIAL_ENV, pending_credential,
                              protect_pending_credential, helper_command)
from grok_provider import GrokRuntime, GrokTurn, VERSION, rpc_failure, private_environment


ROOT = Path(__file__).resolve().parents[2] / '.test-tmp'
URL = 'https://accounts.x.ai/oauth2/device?user_code=SYNTHETIC-CODE'


def synthetic_token(label, expires_in=3600):
    encode = lambda value: base64.urlsafe_b64encode(json.dumps(value).encode()).decode().rstrip('=')
    return (encode({'alg': 'none'}) + '.' + encode({'iss': 'https://auth.x.ai',
        'exp': int(time.time()) + expires_in, 'sub': label}) + '.SYNTHETIC-NOT-A-CREDENTIAL')


class GrokAuthChecks(unittest.TestCase):
    def setUp(self):
        ROOT.mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix='grok-auth-check-', dir=ROOT)
        self.addCleanup(self.temporary.cleanup)
        self.profile = Path(self.temporary.name)
        self.old = synthetic_token('old', -3600)
        self.fresh = synthetic_token('fresh')

    def local_logout(self):
        stdin = SimpleNamespace(reconfigure=lambda **_: None, readline=lambda *_: '')
        with patch.object(companion, 'PROFILE', self.profile), \
             patch('sys.argv', ['companion.py', '--provider', 'grok', 'bridge', 'logout']), \
             patch('sys.stdin', stdin), patch.object(companion, 'emit') as emitted, \
             patch.object(companion, 'AccountCompanion', side_effect=AssertionError('No runtime construction')), \
             patch.object(companion, 'Companion', side_effect=AssertionError('No fallback runtime')), \
             patch('grok_provider.validate_runtime', side_effect=AssertionError('No binary validation')), \
             patch('grok_provider.start_owned_process', side_effect=AssertionError('No provider process')):
            code = companion.main()
        return code, [call.args[0] for call in emitted.call_args_list]

    def test_local_logout_recovers_without_runtime_and_preserves_other_data(self):
        store = CredentialStore(self.profile / 'providers/grok', 'grok')
        store.save({'synthetic': 'not a real credential'})
        sentinel = self.profile / 'unrelated.txt'
        sentinel.write_bytes(b'keep saved chats and other provider state')
        for payload in (store.path.read_bytes(), b'SYNTHETIC-CORRUPT-VAULT'):
            store.path.write_bytes(payload)
            self.assertEqual(self.local_logout(), (0, [{'provider': 'grok', 'local_logout': True}]))
            self.assertFalse(store.path.exists())
            self.assertEqual(sentinel.read_bytes(), b'keep saved chats and other provider state')
        self.assertEqual(self.local_logout(), (0, [{'provider': 'grok', 'local_logout': True}]))

    def test_failed_local_logout_preserves_vault_and_does_not_report_success(self):
        store = CredentialStore(self.profile / 'providers/grok', 'grok')
        store.save({'synthetic': 'not a real credential'})
        before = store.path.read_bytes()
        with patch.object(CredentialStore, 'delete', side_effect=CredentialStoreError('Synthetic removal failure')):
            code, output = self.local_logout()
        self.assertEqual(code, 1)
        self.assertEqual(len(output), 1)
        self.assertEqual(output[0]['error_kind'], 'authentication')
        self.assertNotIn('local_logout', output[0])
        self.assertEqual(store.path.read_bytes(), before)

    def runtime(self):
        runtime = GrokRuntime.__new__(GrokRuntime)
        runtime.process = object()
        runtime.seq = 0
        runtime.inbox = companion.RuntimeInbox(label='Grok')
        runtime.close = Mock()
        runtime._start = Mock()
        runtime.send = Mock()
        runtime.emit = Mock()
        runtime.cancel_event = threading.Event()
        runtime.credentials = CredentialStore(self.profile, 'grok')
        runtime.credentials.save({'schema': 1, 'issuer': 'https://auth.x.ai',
                                 'expires_at': int(time.time()) - 3600, 'access_token': self.old})
        return runtime

    def login_runtime(self, *, token=None, valid_account=True, auth_error=-32000):
        runtime = self.runtime()
        def authorize(cancel, emit, *, open_browser):
            emit({'provider': 'grok', 'login': 'waiting for user', 'authUrl': URL,
                  'user_code': 'SYNTHETIC-CODE', 'openBrowser': open_browser})
            return credential_for_token(self.fresh)
        auth = patch('grok_provider.device_authorize', side_effect=authorize)
        runtime.authorize = auth.start()
        self.addCleanup(auth.stop)
        def call(method, params=None, **kwargs):
            if method == 'authenticate':
                if params['methodId'] == 'grok.com' and auth_error is not None:
                    raise rpc_failure({'code': auth_error})
                return {}
            if method == '_x.ai/auth/getBearerToken':return {'result': {'token': token}}
            if method == '_x.ai/auth/check_subscription':return {'authenticated': valid_account}
            raise AssertionError('Unexpected auth request')
        runtime.call = Mock(side_effect=call)
        return runtime

    def test_rpc_auth_required_is_sanitized_and_has_authentication_stage(self):
        runtime = self.runtime()
        runtime.receive = Mock(return_value={'id': 1, 'error': {'code': -32000,
            'message': 'PRIVATE-SERVER-DETAIL', 'data': {'token': self.fresh}}})
        with self.assertRaises(CompanionFailure) as caught:
            runtime.call('authenticate')
        self.assertEqual((caught.exception.kind, caught.exception.stage),
                         ('authentication', 'provider.authentication'))
        self.assertIn('Sign in with Grok', str(caught.exception))
        self.assertNotIn('PRIVATE-SERVER-DETAIL', str(caught.exception))
        self.assertNotIn(self.fresh, str(caught.exception))
        self.assertEqual(runtime.send.call_count, 1)

    def test_error_prose_and_unknown_codes_do_not_prove_sign_out(self):
        for error in ({'code': -32603, 'message': 'unauthorized expired login'},
                      {'code': '-32000'}, {'code': False}, None):
            self.assertEqual(rpc_failure(error).kind, 'provider')
        self.assertEqual(rpc_failure({'code': -32602}).kind, 'protocol')

    def test_turn_auth_required_remains_authentication_failure(self):
        runtime = self.runtime()
        runtime.receive = Mock(return_value={'id': 3, 'error': {'code': -32000}})
        event = GrokTurn(runtime, 'synthetic-session', 3, Mock()).read(1)
        self.assertEqual((event.status, event.failure_kind), ('failed', 'authentication'))

    def test_fresh_login_replaces_expired_encrypted_vault_and_suppresses_ui_browser(self):
        runtime = self.login_runtime(token=self.fresh)
        runtime.login(open_browser=False)
        self.assertTrue(runtime.credentials.load()['access_token'] == self.fresh)
        self.assertNotIn(self.fresh.encode(), runtime.credentials.path.read_bytes())
        self.assertEqual(runtime.emit.call_args_list[0].args[0], {'provider': 'grok',
            'login': 'waiting for user', 'authUrl': URL, 'user_code': 'SYNTHETIC-CODE', 'openBrowser': False})
        self.assertEqual(runtime.emit.call_args_list[-1].args[0]['login'], 'completed')
        self.assertEqual([call.args[0] for call in runtime.call.call_args_list],
                         ['authenticate', '_x.ai/auth/getBearerToken', 'authenticate', '_x.ai/auth/check_subscription'])
        self.assertEqual(runtime.call.call_args_list[0].args[1], {'methodId': 'grok.com',
                         '_meta': {'headless': True}})
        runtime.send.assert_not_called()
        self.assertFalse(runtime.interactive_login)

    def test_default_login_requests_browser_once_and_validates_successful_import(self):
        runtime = self.login_runtime(token=self.fresh, auth_error=None)
        runtime.login()
        self.assertTrue(runtime.emit.call_args_list[0].args[0]['openBrowser'])
        runtime.authorize.assert_called_once()
        self.assertEqual(runtime.call.call_args_list[1].args[0], '_x.ai/auth/getBearerToken')
        self.assertEqual(runtime.credentials.load()['access_token'], self.fresh)

    def test_failed_or_cancelled_login_preserves_previous_ciphertext(self):
        for case in ('missing_token', 'rejected_account', 'cancelled', 'protocol_error'):
            with self.subTest(case=case):
                runtime = self.login_runtime(token=self.fresh if case != 'missing_token' else None,
                    valid_account=case != 'rejected_account', auth_error=-32602 if case == 'protocol_error' else -32000)
                before = runtime.credentials.path.read_bytes()
                if case == 'cancelled':
                    runtime.authorize.side_effect = InterruptedError()
                with self.assertRaises((CompanionFailure, InterruptedError)):
                    runtime.login()
                self.assertTrue(runtime.credentials.path.read_bytes() == before)
                self.assertFalse(runtime.interactive_login)
                self.assertFalse(any(call.args[0].get('login') == 'completed'
                                     for call in runtime.emit.call_args_list))
                if case == 'cancelled':
                    runtime.call.assert_not_called()
                elif case == 'protocol_error':
                    self.assertEqual(runtime.call.call_count, 1)

    def test_cancel_or_changed_vault_before_save_does_not_overwrite(self):
        for case in ('cancelled', 'changed', 'deleted'):
            with self.subTest(case=case):
                runtime = self.login_runtime(token=self.fresh)
                original = runtime.call.side_effect
                before = runtime.credentials.path.read_bytes()
                def call(method, *args, **kwargs):
                    result = original(method, *args, **kwargs)
                    if method == '_x.ai/auth/check_subscription':
                        if case == 'cancelled':runtime.cancel_event.set()
                        elif case == 'deleted':runtime.credentials.delete()
                        else:runtime.credentials.save({'different': 'synthetic account operation'})
                    return result
                runtime.call.side_effect = call
                with self.assertRaises((CompanionFailure, InterruptedError)):
                    runtime.login()
                if case == 'cancelled':self.assertEqual(runtime.credentials.path.read_bytes(), before)
                elif case == 'deleted':self.assertIsNone(runtime.credentials.load())
                else:self.assertEqual(runtime.credentials.load(), {'different': 'synthetic account operation'})

    def test_wrong_exported_token_cannot_replace_old_vault(self):
        runtime = self.login_runtime(token=synthetic_token('different-account'))
        before = runtime.credentials.path.read_bytes()
        with self.assertRaises(CompanionFailure):runtime.login()
        self.assertEqual(runtime.credentials.path.read_bytes(), before)
        self.assertEqual(runtime.call.call_count, 2)

    def test_corrupt_vault_fails_before_browser_and_stays_intact(self):
        runtime = self.login_runtime(token=self.fresh)
        runtime.credentials.path.write_bytes(b'SYNTHETIC-CORRUPT-VAULT')
        with self.assertRaises(CompanionFailure) as caught:
            runtime.login()
        self.assertEqual(caught.exception.kind, 'authentication')
        self.assertIn('Clear saved sign-in', str(caught.exception))
        runtime._start.assert_not_called()
        runtime.authorize.assert_not_called()
        runtime.send.assert_not_called()
        self.assertEqual(runtime.credentials.path.read_bytes(), b'SYNTHETIC-CORRUPT-VAULT')

    def test_pending_start_imports_only_protected_fresh_token_without_browser(self):
        runtime = self.runtime()
        runtime.process = None
        runtime.executable = self.profile / 'synthetic.exe'
        runtime.profile = self.profile
        runtime.environment = {'GROK_AUTH_PROVIDER_COMMAND': 'synthetic-old-helper',
                               'COMPAI_GROK_SESSION_SHA256': 'old', 'GROK_AUTH': 'old'}
        runtime.interactive_login = True
        runtime.pending_credential = credential_for_token(self.fresh)
        runtime.credentials = Mock()
        runtime._inspect = Mock()
        runtime._preflight_pending_helper = Mock()
        runtime.call = Mock(return_value={'protocolVersion': 1,
            '_meta': {'agentVersion': VERSION, 'x.ai/mcp/sdk': True}})
        guard = Mock(auth_path=self.profile / 'synthetic-auth-guard')
        with patch('grok_provider.validate_runtime'), patch('grok_provider.NativeAuthGuard', return_value=guard), \
             patch('grok_provider.start_owned_process'), patch('grok_provider.threading.Thread'):
            GrokRuntime._start(runtime)
        runtime.credentials.load.assert_not_called()
        runtime._preflight_pending_helper.assert_called_once()
        self.assertIn('--pending-session-token', runtime.environment['GROK_AUTH_PROVIDER_COMMAND'])
        self.assertEqual(runtime.environment['COMPAI_GROK_SESSION_SHA256'], hashlib.sha256(self.fresh.encode()).hexdigest())
        self.assertNotIn(self.fresh, str(runtime.environment))
        with patch.dict('os.environ', runtime.environment, clear=True):
            self.assertEqual(pending_credential()['access_token'], self.fresh)
        for key in ('GROK_LOGIN_DEVICE_FLOW', 'GROK_AUTH'):
            self.assertNotIn(key, runtime.environment)

    def test_close_removes_pending_handoff_from_parent_environment(self):
        runtime = self.runtime()
        runtime.process = None
        runtime.auth_guard = Mock()
        runtime.pending_credential = credential_for_token(self.fresh)
        runtime.environment = {PENDING_CREDENTIAL_ENV: 'synthetic-ciphertext'}
        runtime.pending_tools = {}
        GrokRuntime.close(runtime)
        self.assertIsNone(runtime.pending_credential)
        self.assertNotIn(PENDING_CREDENTIAL_ENV, runtime.environment)

    def test_fixed_helper_preflight_with_synthetic_dpapi_token_and_wrong_hash(self):
        runtime = self.runtime()
        runtime.profile = self.profile
        runtime.environment = private_environment(self.profile)
        runtime.environment[PENDING_CREDENTIAL_ENV] = protect_pending_credential(credential_for_token(self.fresh))
        runtime.environment['GROK_AUTH_PROVIDER_COMMAND'] = helper_command(self.profile, pending=True)
        runtime.environment['COMPAI_GROK_SESSION_SHA256'] = hashlib.sha256(self.fresh.encode()).hexdigest()
        runtime.credential_hash = runtime.environment['COMPAI_GROK_SESSION_SHA256']
        before = runtime.credentials.path.read_bytes()
        GrokRuntime._preflight_pending_helper(runtime)
        self.assertEqual(runtime.credentials.path.read_bytes(), before)
        runtime.environment['COMPAI_GROK_SESSION_SHA256'] = 'incorrect'
        with self.assertRaises(CompanionFailure) as caught:
            GrokRuntime._preflight_pending_helper(runtime)
        self.assertNotIn(self.fresh, str(caught.exception))
        self.assertEqual(runtime.credentials.path.read_bytes(), before)

    def test_helper_preflight_timeout_is_sanitized_and_owned_process_closed(self):
        runtime = self.runtime()
        runtime.profile = self.profile
        runtime.environment = {'SYSTEMROOT': os.environ['SYSTEMROOT'], 'GROK_AUTH_PROVIDER_COMMAND': 'synthetic'}
        import subprocess
        process = Mock()
        process.communicate.side_effect = subprocess.TimeoutExpired('SYNTHETIC-SECRET', 10)
        with patch('grok_provider.start_owned_process', return_value=process), \
             patch('grok_provider.close_owned_process') as close:
            with self.assertRaises(CompanionFailure) as caught:
                GrokRuntime._preflight_pending_helper(runtime)
        close.assert_called_once_with(process)
        self.assertNotIn('SYNTHETIC-SECRET', str(caught.exception))

    def subscription_runtime(self, failure_kind='authentication', token=None):
        runtime = self.runtime()
        runtime.authenticated = False
        runtime.credentials.save(credential_for_token(self.fresh))
        runtime.credential_hash = hashlib.sha256(self.fresh.encode()).hexdigest()
        calls = []
        def call(method, *args, **kwargs):
            calls.append(method)
            if len(calls) == 1:
                raise CompanionFailure(failure_kind, 'synthetic import failure')
            if method == '_x.ai/auth/getBearerToken':
                return {'result': {'token': token}}
            if method == 'authenticate':
                return {}
            if method == '_x.ai/auth/check_subscription':
                return {'authenticated': True}
            raise AssertionError('Unexpected auth request')
        runtime.call = Mock(side_effect=call)
        return runtime

    def test_import_persistence_recovery_requires_exact_bound_token(self):
        runtime = self.subscription_runtime(token=self.fresh)
        self.assertTrue(runtime._subscription()['authenticated'])
        self.assertEqual([call.args[0] for call in runtime.call.call_args_list],
                         ['authenticate', '_x.ai/auth/getBearerToken', 'authenticate', '_x.ai/auth/check_subscription'])
        for token in (None, self.old):
            runtime = self.subscription_runtime(token=token)
            with self.assertRaises(CompanionFailure) as caught:
                runtime._subscription()
            self.assertEqual((caught.exception.kind, caught.exception.stage),
                             ('authentication', 'provider.authentication'))
            self.assertEqual(runtime.call.call_count, 2)

    def test_non_auth_import_failure_is_not_recovered_or_retried(self):
        for kind in ('provider', 'protocol', 'quota'):
            runtime = self.subscription_runtime(failure_kind=kind, token=self.fresh)
            with self.assertRaises(CompanionFailure) as caught:
                runtime._subscription()
            self.assertEqual(caught.exception.kind, kind)
            self.assertEqual(runtime.call.call_count, 1)

    def test_status_does_not_hide_auth_failure_inside_quota_reason(self):
        runtime = self.runtime()
        runtime._subscription = Mock(return_value={'authenticated': True})
        runtime.models = Mock(return_value=[])
        runtime.call = Mock(side_effect=[{'subscription_tier': 'Free',
            'config': {'prepaidBalance': {'val': 0}}}, rpc_failure({'code': -32000})])
        with self.assertRaises(CompanionFailure) as caught:
            runtime.status()
        self.assertEqual((caught.exception.kind, caught.exception.stage),
                         ('authentication', 'provider.authentication'))


if __name__ == '__main__':
    unittest.main()
