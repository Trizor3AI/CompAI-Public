"""CompAI-owned GitHub device OAuth, with encrypted storage and no CLI login.

Only an explicitly configured public OAuth application client ID is accepted.
No app secret, PAT, ambient account, token files or model API keys are used.
"""
import json
import math
import re
import ssl
import time
from urllib import error, parse, request

from credential_store import CredentialStore, CredentialStoreError
from companion_provider import CompanionFailure

DEVICE_URL = 'https://github.com/login/device/code'
TOKEN_URL = 'https://github.com/login/oauth/access_token'
VERIFY_URL = 'https://github.com/login/device'
IDENTITY_URL = 'https://api.github.com/user'
MAX_RESPONSE = 65536
# Public identifier of CompAI's registered OAuth app; deliberately not a secret.
CLIENT_ID = 'Ov23liuZxhibDyUMMrKg'


def failure(message):
    return CompanionFailure('authentication', message, stage='provider.authentication')


class NoRedirect(request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise failure('GitHub authentication redirected unexpectedly; sign-in stopped.')


def github_request(url, fields=None, token=None):
    """Fixed endpoints only; do not expose exception bodies or secret request data."""
    if url not in (DEVICE_URL, TOKEN_URL, IDENTITY_URL):
        raise failure('Unsupported GitHub authentication endpoint.')
    if (url == IDENTITY_URL) != (token is not None):
        raise failure('Invalid GitHub authentication request.')
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.load_default_certs()
    opener = request.build_opener(request.ProxyHandler({}), NoRedirect(), request.HTTPSHandler(context=context))
    headers = {'Accept':'application/json', 'User-Agent':'CompAI-account-login'}
    data = None if fields is None else parse.urlencode(fields).encode('ascii')
    if data is not None:
        if len(data) > 8192: raise failure('GitHub authentication request exceeds its bound.')
        headers['Content-Type'] = 'application/x-www-form-urlencoded'
    if token is not None: headers['Authorization'] = 'Bearer ' + token
    try:
        with opener.open(request.Request(url, data=data, headers=headers), timeout=15) as response:
            if response.geturl() != url: raise failure('Unexpected GitHub authentication response.')
            raw = response.read(MAX_RESPONSE + 1)
            if len(raw) > MAX_RESPONSE: raise ValueError()
            result = json.loads(raw)
            if not isinstance(result, dict): raise ValueError()
            return result
    except (OSError, ValueError, error.URLError):
        raise failure('GitHub authentication could not be completed; no automatic retry.') from None


def _seconds(value, maximum):
    if type(value) not in (int, float) or not math.isfinite(value) or not 0 < value <= maximum:
        raise failure('GitHub returned an invalid credential lifetime.')
    return float(value)


def _secret(value, prefixes):
    if not isinstance(value, str) or not value.startswith(prefixes) or not re.fullmatch(r'[A-Za-z0-9_]{20,512}', value):
        raise failure('GitHub returned an unsupported account credential.')
    return value


class CopilotOAuth:
    def __init__(self, profile, client_id, cancel_event, emit, *, transport=github_request, store=None, clock=time.time):
        self.client_id = client_id
        if client_id is not None and (not isinstance(client_id, str) or not re.fullmatch(r'[A-Za-z0-9_.-]{8,128}', client_id)):
            raise failure('CompAI GitHub OAuth client ID is invalid.')
        self.cancel_event, self.emit = cancel_event, emit
        self.transport, self.clock = transport, clock
        self.store = store or CredentialStore(profile, 'copilot')

    def _configured(self):
        if self.client_id is None:
            raise failure('Copilot needs a registered CompAI GitHub OAuth app with device flow enabled.')

    def saved(self):
        self._configured()
        try:
            value = self.store.load()
        except CredentialStoreError:
            raise failure('Copilot encrypted sign-in could not be read; clear saved sign-in, then sign in again.') from None
        if value is None: return None
        if (not isinstance(value, dict) or value.get('schema') != 1 or value.get('client_id') != self.client_id
                or type(value.get('account_id')) is not int or value['account_id'] <= 0):
            raise failure('Copilot saved sign-in does not match this OAuth app; sign in again.')
        _secret(value.get('access_token'), ('gho_', 'ghu_'))
        _seconds(value.get('expires_at'), 1e12)
        if value.get('refresh_token') is not None:
            _secret(value['refresh_token'], ('ghr_',))
            _seconds(value.get('refresh_expires_at'), 1e12)
        return value

    def _normalize(self, result, expected_account=None):
        if result.get('error'):
            raise failure('GitHub declined the account credential; sign in again.')
        if not isinstance(result.get('token_type'), str) or result['token_type'].lower() != 'bearer':
            raise failure('GitHub returned an unsupported account credential.')
        if not isinstance(result.get('scope', ''), str):
            raise failure('GitHub returned invalid account scopes.')
        scopes = set(filter(None, re.split(r'[, ]+', result.get('scope', ''))))
        if scopes - {'offline_access'}:
            raise failure('GitHub granted broader scopes than requested; sign-in was not saved.')
        access = _secret(result.get('access_token'), ('gho_', 'ghu_'))
        lifetime = _seconds(result.get('expires_in'), 86400)
        if lifetime <= 3600:
            raise failure('GitHub credential lifetime is too short for the Copilot runtime; sign in again.')
        # Never invent a token lifetime. Expiring device OAuth is required.
        normalized = {'schema':1, 'client_id':self.client_id, 'access_token':access,
                      'expires_at':self.clock() + lifetime}
        if result.get('refresh_token') is not None:
            normalized.update(refresh_token=_secret(result['refresh_token'], ('ghr_',)),
                refresh_expires_at=self.clock() + _seconds(result.get('refresh_token_expires_in'), 366*86400))
        identity = self.transport(IDENTITY_URL, token=access)
        account_id = identity.get('id')
        if type(account_id) is not int or account_id <= 0 or expected_account is not None and account_id != expected_account:
            raise failure('GitHub account identity changed; sign in again.')
        normalized['account_id'] = account_id
        return normalized

    def _save(self, value):
        try: self.store.save(value)
        except CredentialStoreError:
            raise failure('Copilot sign-in could not be encrypted; no plaintext credential was saved.') from None

    def login(self):
        self._configured()
        if self.cancel_event.is_set(): raise InterruptedError()
        challenge = self.transport(DEVICE_URL, {'client_id':self.client_id, 'scope':'offline_access'})
        if challenge.get('error'):
            raise failure('GitHub device sign-in is unavailable; check the CompAI OAuth app registration.')
        device = challenge.get('device_code')
        code = challenge.get('user_code')
        if (not isinstance(device, str) or not re.fullmatch(r'[A-Za-z0-9_-]{20,256}', device)
                or not isinstance(code, str) or not re.fullmatch(r'[A-Z0-9]{4}-[A-Z0-9]{4}', code)
                or challenge.get('verification_uri') != VERIFY_URL):
            raise failure('GitHub returned an invalid device sign-in challenge.')
        duration = _seconds(challenge.get('expires_in'), 1800)
        interval = _seconds(challenge.get('interval', 5), 60)
        deadline = self.clock() + duration
        self.emit({'provider':'copilot', 'login':'waiting for user', 'authUrl':VERIFY_URL, 'user_code':code})
        while self.clock() < deadline:
            if self.cancel_event.wait(min(interval, max(0, deadline - self.clock()))): raise InterruptedError()
            if self.clock() >= deadline: break
            response = self.transport(TOKEN_URL, {'client_id':self.client_id, 'device_code':device,
                'grant_type':'urn:ietf:params:oauth:grant-type:device_code'})
            state = response.get('error')
            if state == 'authorization_pending': continue
            if state == 'slow_down':
                interval = max(interval + 5, _seconds(response.get('interval', interval + 5), 1800))
                continue
            if state: raise failure('GitHub device sign-in was denied or expired; sign in again.')
            value = self._normalize(response)
            if self.cancel_event.is_set(): raise InterruptedError()
            self._save(value)
            self.emit({'provider':'copilot', 'login':'completed'})
            return
        raise failure('GitHub device sign-in expired; sign in again.')

    def token(self, expected_account=None):
        """Refresh under the encrypted store lock, never while holding an inference lock."""
        self._configured()
        if self.cancel_event.is_set(): raise InterruptedError()
        try:
            with self.store.locked():
                if self.cancel_event.is_set(): raise InterruptedError()
                value = self.saved()
                if value is None: raise failure('Sign in with GitHub Copilot Free.')
                if expected_account is not None and value['account_id'] != expected_account:
                    raise failure('Copilot account changed during the request; refresh account status.')
                remaining = value['expires_at'] - self.clock()
                if remaining <= 3600:
                    if not value.get('refresh_token') or value['refresh_expires_at'] <= self.clock():
                        raise failure('Copilot sign-in expired; sign in again.')
                    if self.cancel_event.is_set(): raise InterruptedError()
                    response = self.transport(TOKEN_URL, {'client_id':self.client_id,
                        'grant_type':'refresh_token', 'refresh_token':value['refresh_token']})
                    value = self._normalize(response, expected_account=value['account_id'])
                    self._save(value)
                    remaining = value['expires_at'] - self.clock()
                if self.cancel_event.is_set(): raise InterruptedError()
                if int(remaining) <= 3600:
                    raise failure('Copilot sign-in could not be refreshed sufficiently; sign in again.')
                return {'kind':'token', 'accessToken':value['access_token'], 'expiresIn':int(remaining)}
        except CredentialStoreError:
            raise failure('Copilot encrypted sign-in is unavailable; retry after the current account operation.') from None

    def logout(self):
        try: self.store.delete()
        except CredentialStoreError:
            raise failure('Copilot encrypted sign-in could not be removed.') from None
