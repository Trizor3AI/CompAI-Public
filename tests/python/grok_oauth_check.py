"""Offline device-flow and protected pending-token handoff checks; no accounts."""
import hashlib
import io
import json
import os
from pathlib import Path
import threading
import time
import unittest
from unittest.mock import Mock, patch

from companion_provider import CompanionFailure
import grok_credentials as credentials
import grok_oauth as oauth
from grok_auth_check import synthetic_token
from grok_provider import VERSION


class FakeTime:
    def __init__(self):
        self.now = 100.0
        self.cancelled = False
        self.waits = []
    def clock(self):return self.now
    def is_set(self):return self.cancelled
    def wait(self, seconds):
        self.waits.append(seconds)
        self.now += seconds
        return self.cancelled


def challenge(**changes):
    value = {'device_code': 'SYNTHETIC-DEVICE-CODE-NOT-REAL', 'user_code': 'TEST-CODE',
        'verification_uri': oauth.VERIFY_URL, 'expires_in': 600, 'interval': 5}
    value.update(changes)
    return value


class DeviceChecks(unittest.TestCase):
    def setUp(self):
        self.time = FakeTime()
        self.emit = Mock()
        self.token = synthetic_token('synthetic-oauth')

    def authorize(self, rows, **kwargs):
        self.transport = Mock(side_effect=rows)
        return oauth.device_authorize(self.time, self.emit, transport=self.transport,
            clock=self.time.clock, **kwargs)

    def test_success_pending_slowdown_and_no_refresh_token_retention(self):
        value = self.authorize([challenge(), {'error': 'authorization_pending'},
            {'error': 'slow_down'}, {'access_token': self.token, 'refresh_token': 'DISCARD',
                'id_token': 'DISCARD', 'token_type': 'Bearer', 'scope': 'grok-cli:access'}])
        self.assertEqual(value, credentials.credential_for_token(self.token))
        self.assertEqual(self.time.waits, [5, 5, 10])
        self.emit.assert_called_once_with({'provider': 'grok', 'login': 'waiting for user',
            'authUrl': oauth.VERIFY_URL + '?user_code=TEST-CODE', 'user_code': 'TEST-CODE', 'openBrowser': True})
        first = self.transport.call_args_list[0].args
        self.assertEqual(first[0], oauth.DEVICE_URL)
        self.assertEqual(first[1], {'client_id': oauth.CLIENT_ID,
            'scope': ' '.join(oauth.SCOPES), 'referrer': 'grok-build'})
        for call in self.transport.call_args_list[1:]:
            self.assertEqual(call.args[0], oauth.TOKEN_URL)
            self.assertEqual(call.args[1], {'client_id': oauth.CLIENT_ID,
                'device_code': 'SYNTHETIC-DEVICE-CODE-NOT-REAL',
                'grant_type': 'urn:ietf:params:oauth:grant-type:device_code'})
        self.assertNotIn(self.token, str(self.emit.call_args_list))
        self.assertEqual(oauth.RUNTIME_VERSION, VERSION)

    def test_explicit_no_browser_retains_manual_link(self):
        self.authorize([challenge(verification_uri_complete=oauth.VERIFY_URL + '?user_code=TEST-CODE'),
                        {'access_token': self.token}], open_browser=False)
        self.assertFalse(self.emit.call_args.args[0]['openBrowser'])

    def test_bad_challenges_fail_before_browser_or_poll(self):
        cases = [{'verification_uri': 'https://example.invalid/'},
            {'verification_uri': oauth.VERIFY_URL + '?another=1'},
            {'verification_uri_complete': oauth.VERIFY_URL + '?user_code=DIFFERENT'},
            {'user_code': 'BAD&CODE'}, {'device_code': 'bad\nsecret'}, {'expires_in': False},
            {'expires_in': 0}, {'expires_in': 1801}, {'interval': 0}, {'interval': 61}, {'interval': float('nan')}]
        for changed in cases:
            with self.subTest(changed=changed):
                self.emit.reset_mock()
                with self.assertRaises(CompanionFailure):self.authorize([challenge(**changed)])
                self.emit.assert_not_called()
                self.assertEqual(self.transport.call_count, 1)

    def test_denied_expired_unknown_error_redact_response(self):
        for code in ('access_denied', 'expired_token', 'server_failure', 'PRIVATE-SERVER-SECRET'):
            with self.subTest(code=code):
                with self.assertRaises(CompanionFailure) as caught:
                    self.authorize([challenge(), {'error': code, 'error_description': self.token}])
                self.assertNotIn(self.token, str(caught.exception))
                self.assertNotIn('PRIVATE-SERVER-SECRET', str(caught.exception))
                self.assertEqual(self.transport.call_count, 2)

    def test_expiry_and_maximum_login_duration_stop_polling(self):
        for duration, expected in ((7, 7), (1800, oauth.LOGIN_SECONDS)):
            with self.subTest(duration=duration):
                self.time = FakeTime()
                with self.assertRaises(CompanionFailure):
                    self.authorize([challenge(expires_in=duration)] + [{'error': 'authorization_pending'}] * 200)
                self.assertEqual(self.time.now, 100 + expected)

    def test_late_token_is_not_returned(self):
        def exchange(url, fields, cancel, deadline):
            if url == oauth.DEVICE_URL:return challenge(expires_in=7)
            self.time.now = deadline
            return {'access_token': self.token}
        with self.assertRaises(CompanionFailure):
            oauth.device_authorize(self.time, self.emit, transport=exchange, clock=self.time.clock)

    def test_cancel_before_request_and_after_token(self):
        self.time.cancelled = True
        with self.assertRaises(InterruptedError):self.authorize([])
        self.transport.assert_not_called()
        self.time.cancelled = False
        def exchange(url, fields, cancel, deadline):
            if url == oauth.DEVICE_URL:return challenge()
            self.time.cancelled = True
            return {'access_token': self.token}
        with self.assertRaises(InterruptedError):
            oauth.device_authorize(self.time, self.emit, transport=exchange, clock=self.time.clock)

    def test_bad_token_or_broader_scope_cannot_be_returned(self):
        for row in ({'access_token': 'not-a-session'}, {'access_token': self.token, 'token_type': 'api_key'},
                    {'access_token': self.token, 'scope': 'grok-cli:access admin:all'},
                    {'access_token': self.token, 'scope': []}):
            with self.subTest(row=list(row)):
                with self.assertRaises(CompanionFailure):self.authorize([challenge(), row])


class Response(io.BytesIO):
    def __init__(self, payload, status=200, url=oauth.DEVICE_URL):
        super().__init__(payload)
        self.status, self.url = status, url
    def geturl(self):return self.url


class TransportChecks(unittest.TestCase):
    def test_no_redirects_and_fixed_endpoint_allowlist(self):
        with self.assertRaises(CompanionFailure):
            oauth.NoRedirect().redirect_request(None, None, 307, '', {}, 'https://example.invalid')
        with patch.object(oauth, '_exchange') as exchange:
            with self.assertRaises(CompanionFailure):
                oauth.grok_request('https://auth.x.ai.evil.invalid/oauth2/token', {}, threading.Event(), time.monotonic()+30)
            exchange.assert_not_called()

    def exchange(self, response):
        opener = Mock()
        opener.open.return_value = response
        with patch.object(oauth.request, 'build_opener', return_value=opener) as build:
            result = oauth._exchange(oauth.DEVICE_URL, b'client_id=synthetic', threading.Event(),
                                     threading.Event(), time.monotonic()+30)
        self.assertTrue(any(isinstance(x, oauth.NoRedirect) for x in build.call_args.args))
        proxy = next(x for x in build.call_args.args if isinstance(x, oauth.request.ProxyHandler))
        self.assertEqual(proxy.proxies, {})
        return result

    def test_bounded_json_rejects_duplicate_fields_redirects_and_large_results(self):
        for response in (Response(b'{"x":1,"x":2}'), Response(b'{"x":NaN}'),
                         Response(b'[]'), Response(b'{}', 302),
                         Response(b'{}', url='https://example.invalid'),
                         Response(b'x' * (oauth.MAX_RESPONSE+1))):
            with self.subTest(status=response.status):
                with self.assertRaises(CompanionFailure):self.exchange(response)
                self.assertTrue(response.closed)
        self.assertEqual(self.exchange(Response(b'{"error":"authorization_pending"}', 400)),
                         {'error': 'authorization_pending'})

    def test_network_errors_are_redacted(self):
        opener = Mock()
        opener.open.side_effect = OSError('SYNTHETIC-SECRET-IN-ERROR')
        with patch.object(oauth.request, 'build_opener', return_value=opener):
            with self.assertRaises(CompanionFailure) as caught:
                oauth._exchange(oauth.DEVICE_URL, b'', threading.Event(), threading.Event(), time.monotonic()+30)
        self.assertNotIn('SYNTHETIC-SECRET', str(caught.exception))

    def test_deadline_or_cancel_discards_late_worker_result(self):
        for cancel_now in (False, True):
            release, started, cancel = threading.Event(), threading.Event(), threading.Event()
            def exchange(*args):
                started.set()
                if cancel_now:cancel.set()
                release.wait(1)
                return {'access_token': 'NEVER-RETURN'}
            try:
                with patch.object(oauth, '_exchange', side_effect=exchange), patch.object(oauth, 'REQUEST_SECONDS', .05):
                    with self.assertRaises((CompanionFailure, InterruptedError)):
                        oauth.grok_request(oauth.TOKEN_URL, {}, cancel, time.monotonic()+5)
                self.assertTrue(started.is_set())
            finally:release.set()


@unittest.skipUnless(os.name == 'nt', 'DPAPI requires Windows')
class PendingHelperChecks(unittest.TestCase):
    def test_protected_memory_helper_outputs_only_bound_token_without_vault_access(self):
        token = synthetic_token('pending-helper')
        value = credentials.credential_for_token(token)
        protected = credentials.protect_pending_credential(value)
        self.assertNotIn(token, protected)
        env = {credentials.PENDING_CREDENTIAL_ENV: protected,
               'COMPAI_GROK_SESSION_SHA256': hashlib.sha256(token.encode()).hexdigest()}
        with patch.dict(os.environ, env, clear=True), \
             patch('sys.argv', ['grok_credentials.py', '--pending-session-token', str(Path.cwd())]), \
             patch.object(credentials, 'CredentialStore', side_effect=AssertionError('No existing vault')), \
             patch('sys.stdout', new_callable=io.StringIO) as output:
            self.assertEqual(credentials.main(), 0)
            self.assertEqual(json.loads(output.getvalue())['access_token'], token)
        for changes in ({'COMPAI_GROK_SESSION_SHA256': 'wrong'},
                        {credentials.PENDING_CREDENTIAL_ENV: 'bad-ciphertext'},
                        {credentials.PENDING_CREDENTIAL_ENV: ''}):
            with patch.dict(os.environ, {**env, **changes}, clear=True), \
                 patch('sys.argv', ['grok_credentials.py', '--pending-session-token', str(Path.cwd())]), \
                 patch.object(credentials, 'CredentialStore', side_effect=AssertionError('No fallback vault')), \
                 patch('sys.stdout', new_callable=io.StringIO) as output:
                self.assertEqual(credentials.main(), 1)
                self.assertEqual(output.getvalue(), '')


if __name__ == '__main__':unittest.main()
