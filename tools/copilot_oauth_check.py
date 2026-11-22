"""Synthetic device OAuth, real DPAPI, and scoped in-memory token callback checks.

No account, token exchange, model, or external network request is made.
"""
from collections import deque
from pathlib import Path
import json
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/'bridge'))
from copilot_oauth import CopilotOAuth, DEVICE_URL, TOKEN_URL, IDENTITY_URL, VERIFY_URL, NoRedirect
from copilot_provider import CopilotRuntime, CompanionFailure, private_environment
from credential_store import CredentialStore

CLIENT = 'CompAI_fixture_client'
SECRET = 'gho_CompAI_synthetic_access_1234567890'
REFRESH = 'ghr_CompAI_synthetic_refresh_1234567890'


class Clock:
    value = 10000
    def __call__(self): return self.value
    def is_set(self): return getattr(self,'cancelled',False)
    def wait(self, seconds): self.value += seconds; return False


class OAuthChecks(unittest.TestCase):
    def setUp(self):
        root = ROOT/'.test-tmp'/'copilot-runtime'
        self.temp = tempfile.TemporaryDirectory(prefix='secure-fixture-', dir=root)
        self.path = Path(self.temp.name)
        self.clock = Clock()
        self.events, self.requests = [], []
        self.responses = deque()
        def transport(url, fields=None, token=None):
            self.requests.append((url, fields, token))
            return self.responses.popleft()
        self.oauth = CopilotOAuth(self.path, CLIENT, self.clock, self.events.append,
                                 transport=transport, clock=self.clock)
    def tearDown(self): self.temp.cleanup()
    def challenge(self):
        return {'device_code':'synthetic_device_1234567890', 'user_code':'ABCD-EFGH',
                'verification_uri':VERIFY_URL, 'expires_in':900, 'interval':5}
    def token_response(self, **extra):
        return dict(access_token=SECRET, refresh_token=REFRESH, token_type='bearer',
                    expires_in=28800, refresh_token_expires_in=15897600, scope='offline_access', **extra)
    def test_login_dpapi_restart_refresh_logout(self):
        self.responses.extend([self.challenge(), {'error':'authorization_pending'},
            {'error':'slow_down','interval':10}, self.token_response(), {'id':42,'login':'not-retained'}])
        self.oauth.login()
        self.assertEqual(self.clock(),10020)
        self.assertEqual(self.events[0]['authUrl'],VERIFY_URL)
        self.assertEqual(self.events[-1]['login'],'completed')
        self.assertNotIn(SECRET,json.dumps(self.events))
        self.assertNotIn('not-retained',json.dumps(self.oauth.saved()))
        persisted = [p for p in self.path.rglob('*') if p.is_file()]
        self.assertTrue(any(p.suffix=='.dpapi' for p in persisted))
        for path in persisted:
            self.assertNotIn(SECRET.encode(),path.read_bytes())
            self.assertNotIn(REFRESH.encode(),path.read_bytes())
        # A new vault instance can decrypt under this Windows account.
        self.assertEqual(CredentialStore(self.path,'copilot').load()['access_token'],SECRET)
        self.assertEqual(self.oauth.token()['accessToken'],SECRET)
        self.assertEqual(len(self.requests),5)
        self.clock.value += 27000
        self.responses.extend([self.token_response(),{'id':42}])
        self.assertGreater(self.oauth.token()['expiresIn'],27000)
        self.assertEqual(self.requests[-2][1]['grant_type'],'refresh_token')
        self.assertNotIn('client_secret',self.requests[-2][1])
        self.oauth.logout()
        self.assertIsNone(self.oauth.saved())
    def test_no_app_id_no_network_or_storage(self):
        self.oauth.client_id=None
        with self.assertRaisesRegex(CompanionFailure,'registered CompAI'):self.oauth.login()
        self.assertEqual(self.requests,[])
        self.assertEqual(list(self.path.rglob('*.dpapi')),[])
    def test_broader_scopes_and_unknown_lifetime_rejected(self):
        for change in ({'scope':'repo'}, {'expires_in':None}, {'expires_in':3600}, {'token_type':42}, {'scope':3}):
            self.responses.clear()
            token=self.token_response();token.update(change)
            self.responses.extend([self.challenge(),token])
            with self.assertRaises(CompanionFailure):self.oauth.login()
            self.assertIsNone(self.oauth.saved())
    def test_wrong_identity_refresh_rejected(self):
        self.responses.extend([self.challenge(),self.token_response(),{'id':42}])
        self.oauth.login()
        self.clock.value += 28000
        self.responses.extend([self.token_response(),{'id':43}])
        with self.assertRaisesRegex(CompanionFailure,'identity changed'):self.oauth.token()
    def test_account_binding_rejects_replaced_login_without_request(self):
        self.responses.extend([self.challenge(),self.token_response(),{'id':42}])
        self.oauth.login()
        before=len(self.requests)
        with self.assertRaisesRegex(CompanionFailure,'account changed'):
            self.oauth.token(expected_account=43)
        self.assertEqual(len(self.requests),before)
    def test_cancelled_rotation_is_saved_but_never_delivered(self):
        self.responses.extend([self.challenge(),self.token_response(),{'id':42}])
        self.oauth.login()
        self.clock.value += 28000
        rotated=self.token_response();rotated['access_token']=SECRET+'rotated'
        self.responses.extend([rotated,{'id':42}])
        original=self.oauth.transport
        def transport(*args,**kwargs):
            value=original(*args,**kwargs)
            if args[0]==TOKEN_URL:self.clock.cancelled=True
            return value
        self.oauth.transport=transport
        with self.assertRaises(InterruptedError):self.oauth.token(expected_account=42)
        self.assertEqual(self.oauth.saved()['access_token'],SECRET+'rotated')
        self.assertNotIn(SECRET,json.dumps(self.events))
    def test_host_redirect_and_challenge_rejected(self):
        with self.assertRaisesRegex(CompanionFailure,'redirected'):
            NoRedirect().redirect_request(None,None,302,'',{},'https://elsewhere.invalid')
        challenge=self.challenge();challenge['verification_uri']='https://elsewhere.invalid'
        self.responses.append(challenge)
        with self.assertRaises(CompanionFailure):self.oauth.login()
        self.assertEqual(self.events,[])
    def test_scoped_token_callback_and_no_ambient_env(self):
        runtime=object.__new__(CopilotRuntime)
        runtime.token_registrations={'registration':'session'}
        runtime.account_id=None;runtime.cancel_event=threading.Event()
        calls=[];wire=[]
        def token(**kwargs):
            self.assertEqual(kwargs,{'expected_account':42})
            calls.append(True);return {'kind':'token','accessToken':SECRET,'expiresIn':28800}
        runtime.oauth=SimpleNamespace(saved=lambda:{'account_id':42},token=token);runtime.send=wire.append
        message={'id':22,'method':'gitHubToken.getToken','params':{'registrationId':'registration',
                 'sessionId':'session','reason':'initial','host':'https://github.com'}}
        for field,value in [('host','https://elsewhere.invalid'),('registrationId','foreign'),('sessionId','foreign'),('reason','other')]:
            runtime.handle_request(dict(message,params={**message['params'],field:value}))
        self.assertEqual(calls,[])
        self.assertNotIn(SECRET,json.dumps(wire))
        runtime.handle_request(message)
        self.assertEqual(calls,[True]);self.assertEqual(wire[-1]['result']['accessToken'],SECRET)
        env=private_environment(self.path)
        self.assertEqual(env['COPILOT_DISABLE_KEYTAR'],'1')
        self.assertFalse(any(key in env for key in ('GH_TOKEN','GITHUB_TOKEN','COPILOT_GITHUB_TOKEN','OPENAI_API_KEY','SSLKEYLOGFILE')))
        runtime.oauth.token=lambda **_: (_ for _ in ()).throw(CompanionFailure('authentication','fixture auth failure'))
        with self.assertRaisesRegex(CompanionFailure,'fixture auth failure'):runtime.handle_request(message)
        self.assertEqual(wire[-1]['result'],{'kind':'cancelled'})


if __name__ == '__main__': unittest.main()
