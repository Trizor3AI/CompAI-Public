"""Official Grok device authorization with browser ownership left to CompAI.

Contract: xai-org/grok-build commit 2bdd1d6a6369de0e8c68132ea4539e9abd9e14a8,
xai-grok-login/src/{config,device_code}.rs. The public client ID is not a secret.
This follows the native account flow, not API-key authentication. Only the access
token is retained in memory; the runtime must validate it before permanent save.
"""
import json
import queue
import re
import ssl
import threading
import time
from urllib import error, parse, request

from companion_provider import CompanionFailure
from credential_store import CredentialStoreError
from grok_credentials import credential_for_token

DEVICE_URL = 'https://auth.x.ai/oauth2/device/code'
TOKEN_URL = 'https://auth.x.ai/oauth2/token'
VERIFY_URL = 'https://accounts.x.ai/oauth2/device'
CLIENT_ID = 'b1a00492-073a-47ea-816f-4c329264a828'
SCOPES = ('openid', 'profile', 'email', 'offline_access', 'grok-cli:access',
          'api:access', 'conversations:read', 'conversations:write',
          'workspaces:read', 'workspaces:write')
RUNTIME_VERSION = '1.0.62'
MAX_RESPONSE = 65536
REQUEST_SECONDS = 15
LOGIN_SECONDS = 900


def failure(message):
    return CompanionFailure('authentication', message, stage='provider.authentication')


class NoRedirect(request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise failure('Grok authentication redirected unexpectedly; sign-in stopped.')


def _pairs(entries):
    result = {}
    for key, value in entries:
        if key in result:
            raise ValueError()
        result[key] = value
    return result


def _exchange(url, data, cancel_event, stop_event, deadline):
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.load_default_certs()
    opener = request.build_opener(request.ProxyHandler({}), NoRedirect(), request.HTTPSHandler(context=context))
    headers = {'Accept': 'application/json', 'Content-Type': 'application/x-www-form-urlencoded',
               'User-Agent': 'CompAI-account-login', 'x-grok-client-version': RUNTIME_VERSION,
               'x-grok-client-surface': 'ui'}
    response = None
    try:
        try:
            response = opener.open(request.Request(url, data=data, headers=headers), timeout=REQUEST_SECONDS)
        except error.HTTPError as exc:
            # RFC 8628 pending/slow_down are normal HTTP 400 responses. Do not
            # follow redirects or include any response/error body in diagnostics.
            if exc.code != 400:
                exc.close()
                raise ValueError() from None
            response = exc
        with response:
            if response.geturl() != url or response.status not in (200, 400):
                raise ValueError()
            raw = bytearray()
            while True:
                if cancel_event.is_set() or stop_event.is_set():
                    raise InterruptedError()
                if time.monotonic() >= deadline:
                    raise TimeoutError()
                chunk = response.read1(min(4096, MAX_RESPONSE + 1 - len(raw)))
                raw.extend(chunk)
                if len(raw) > MAX_RESPONSE:
                    raise ValueError()
                if not chunk:
                    break
            value = json.loads(raw, object_pairs_hook=_pairs,
                               parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
            if type(value) is not dict or response.status == 400 and 'error' not in value:
                raise ValueError()
            return value
    except InterruptedError:
        raise
    except Exception:
        raise failure('Grok authentication could not be completed; no automatic retry.') from None


def grok_request(url, fields, cancel_event, deadline):
    """Fixed HTTPS endpoints, no proxy inheritance/redirects, bounded wall time.

    The daemon only returns data. A late network result after cancellation or the
    deadline cannot authorize a runtime or write credentials. At most one HTTP
    operation remains in flight, with its own socket timeout, on a failed login.
    """
    if url not in (DEVICE_URL, TOKEN_URL):
        raise failure('Unsupported Grok authentication endpoint.')
    if cancel_event.is_set():
        raise InterruptedError()
    data = parse.urlencode(fields).encode('ascii')
    if len(data) > 8192:
        raise failure('Grok authentication request exceeds its bound.')
    end = min(deadline, time.monotonic() + REQUEST_SECONDS)
    if time.monotonic() >= end:
        raise failure('Grok sign-in expired; try again.')
    result = queue.Queue(maxsize=1)
    stop = threading.Event()

    def run():
        try:
            value = _exchange(url, data, cancel_event, stop, end)
        except InterruptedError:
            value = InterruptedError()
        except Exception:
            value = failure('Grok authentication could not be completed; no automatic retry.')
        if not stop.is_set():
            result.put_nowait(value)

    threading.Thread(target=run, daemon=True).start()
    try:
        while True:
            if cancel_event.is_set():
                raise InterruptedError()
            remaining = end - time.monotonic()
            if remaining <= 0:
                raise failure('Grok sign-in request timed out; try again.')
            try:
                value = result.get(timeout=min(.1, remaining))
            except queue.Empty:
                continue
            if cancel_event.is_set():
                raise InterruptedError()
            if time.monotonic() >= end:
                raise failure('Grok sign-in request timed out; try again.')
            if isinstance(value, Exception):
                raise value
            return value
    finally:
        stop.set()


def _seconds(value, maximum):
    if type(value) is not int or not 0 < value <= maximum:
        raise failure('Grok returned an invalid sign-in lifetime or polling interval.')
    return value


def device_authorize(cancel_event, emit, *, open_browser=True, transport=grok_request, clock=time.monotonic):
    """Always obtain fresh user consent; never reuse or import an old account."""
    if cancel_event.is_set():
        raise InterruptedError()
    started = clock()
    deadline = started + LOGIN_SECONDS
    challenge = transport(DEVICE_URL, {'client_id': CLIENT_ID, 'scope': ' '.join(SCOPES),
                                       'referrer': 'grok-build'}, cancel_event, deadline)
    if cancel_event.is_set():
        raise InterruptedError()
    if type(challenge) is not dict or challenge.get('error'):
        raise failure('Grok device sign-in is unavailable; try again later.')
    device = challenge.get('device_code')
    code = challenge.get('user_code')
    if (type(device) is not str or not re.fullmatch(r'[\x21-\x7e]{16,2048}', device)
            or type(code) is not str or not re.fullmatch(r'[A-Za-z0-9-]{1,32}', code)
            or challenge.get('verification_uri') != VERIFY_URL):
        raise failure('Grok returned an invalid device sign-in challenge.')
    url = VERIFY_URL + '?user_code=' + code
    if challenge.get('verification_uri_complete') not in (None, url):
        raise failure('Unexpected Grok sign-in destination.')
    deadline = min(deadline, started + _seconds(challenge.get('expires_in'), 1800))
    interval = _seconds(challenge.get('interval', 5), 60)
    if clock() >= deadline:
        raise failure('Grok sign-in expired; try again.')
    emit({'provider': 'grok', 'login': 'waiting for user', 'authUrl': url,
          'user_code': code, 'openBrowser': bool(open_browser)})
    while clock() < deadline:
        if cancel_event.wait(min(interval, max(0, deadline - clock()))):
            raise InterruptedError()
        if clock() >= deadline:
            break
        value = transport(TOKEN_URL, {'client_id': CLIENT_ID, 'device_code': device,
            'grant_type': 'urn:ietf:params:oauth:grant-type:device_code'}, cancel_event, deadline)
        if cancel_event.is_set():
            raise InterruptedError()
        if clock() >= deadline:
            break
        if type(value) is not dict:
            raise failure('Grok returned an invalid sign-in response.')
        state = value.get('error')
        if state == 'authorization_pending':
            continue
        if state == 'slow_down':
            interval += 5
            continue
        if state:
            raise failure('Grok device sign-in was denied or expired; sign in again.')
        if 'token_type' in value and (type(value['token_type']) is not str or value['token_type'].lower() != 'bearer'):
            raise failure('Grok returned an unsupported account credential.')
        if value.get('scope') is not None:
            scope = value['scope']
            if type(scope) is not str or not set(scope.split()) <= set(SCOPES) or 'grok-cli:access' not in scope.split():
                raise failure('Grok returned unsupported account scopes; sign-in was not saved.')
        try:
            # Discard refresh/id tokens and all account display data. Expiry is
            # checked locally; exact-token import and native account-state checks
            # remain mandatory before replacing CompAI's permanent credential.
            return credential_for_token(value.get('access_token'))
        except CredentialStoreError as exc:
            raise failure(str(exc)) from None
    raise failure('Grok sign-in expired; try again.')
