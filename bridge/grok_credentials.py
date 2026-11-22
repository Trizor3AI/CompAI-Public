"""Grok session-token handoff and native persistence guard.

CompAI device authorization supplies an access token to the official ACP runtime
for validation. Only its session credential is saved under DPAPI; refresh tokens
are discarded. Pending credentials cross the helper boundary as DPAPI ciphertext
in memory, never plaintext environment variables, command arguments or files.
The external session-auth command reads that vault in memory. Expiry requires
official sign-in again. No API-key authentication or token logging is used.
"""
import base64
import ctypes
from ctypes import wintypes
import json
import hashlib
import hmac
import math
import os
from pathlib import Path
import stat
import sys
import time
import uuid

sys.path.insert(0,str(Path(__file__).resolve().parent))
from credential_store import CredentialStore, CredentialStoreError, _dpapi

PENDING_CREDENTIAL_ENV = 'COMPAI_GROK_PENDING_CREDENTIAL'
MAX_PENDING_CIPHERTEXT = 24000  # Below Windows' per-variable environment bound.


class NativeAuthGuard:
    """Deny native credential persistence using an exclusively held empty file.

    Denying read forces the pinned runtime's documented memory-only update
    branch; denying write/delete/replacement holds that condition for its full
    lifetime. A unique guard per process permits concurrent questions. This is
    a persistence block, not a sandbox against same-user malware.
    """
    def __init__(self, profile):
        if os.name != 'nt':
            raise CredentialStoreError('Grok secure sign-in requires Windows.')
        self.path=Path(profile)/('auth-persistence-disabled-'+uuid.uuid4().hex)
        self.handle=None
        self.kernel=ctypes.WinDLL('kernel32.dll',use_last_error=True,winmode=0x800)
        self.kernel.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,
            ctypes.c_void_p,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE]
        self.kernel.CreateFileW.restype=wintypes.HANDLE
        self.kernel.CloseHandle.argtypes=[wintypes.HANDLE]
        self.kernel.CloseHandle.restype=wintypes.BOOL
        for path in (self.path,*self.path.parents):
            try:info=path.lstat()
            except FileNotFoundError:continue
            if stat.S_ISLNK(info.st_mode) or getattr(info,'st_file_attributes',0)&0x400:
                raise CredentialStoreError('Linked Grok credential paths are unsupported.')
        # CREATE_NEW; OPEN_REPARSE_POINT; GENERIC_READ; no sharing at all.
        handle=self.kernel.CreateFileW(str(self.path),0x80000000,0,None,1,0x00200000,None)
        if handle in (None,ctypes.c_void_p(-1).value):
            raise CredentialStoreError('Cannot prevent native Grok credential persistence.')
        self.handle=handle
        try:
            info=self.path.lstat()
            if not stat.S_ISREG(info.st_mode) or info.st_size!=0 or getattr(info,'st_file_attributes',0)&0x400:
                raise CredentialStoreError('Unexpected Grok credential persistence guard.')
        except BaseException:
            self.close()
            raise

    @property
    def auth_path(self):return self.path

    def close(self):
        handle,self.handle=self.handle,None
        if handle is not None:self.kernel.CloseHandle(handle)
        try:
            info=self.path.lstat()
            if stat.S_ISREG(info.st_mode) and info.st_size==0 and not getattr(info,'st_file_attributes',0)&0x400:
                self.path.unlink()
        except OSError:pass


def credential_for_token(token, now=None):
    """Read expiry only for conservative local lifetime; server validates token."""
    now=time.time() if now is None else now
    if type(token) is not str or not 32<=len(token)<=32768 or any(c.isspace() for c in token):
        raise CredentialStoreError('Grok returned an invalid session credential.')
    try:
        segment=token.split('.')[1]
        claims=json.loads(base64.urlsafe_b64decode(segment+'='*(-len(segment)%4)))
        expiry=claims.get('exp')
        if type(expiry) not in (int,float) or not math.isfinite(expiry) or not now+60<expiry<=now+366*86400:
            raise ValueError()
        if claims.get('iss')!='https://auth.x.ai':raise ValueError()
    except (IndexError,ValueError,TypeError,UnicodeError):
        raise CredentialStoreError('Grok session expiry could not be verified; sign-in was not saved.') from None
    return {'schema':1,'access_token':token,'expires_at':expiry,'issuer':'https://auth.x.ai'}


def valid_credential(value, now=None):
    if type(value) is not dict or value.get('schema')!=1 or value.get('issuer')!='https://auth.x.ai':return None
    try:checked=credential_for_token(value.get('access_token'),now)
    except CredentialStoreError:return None
    return checked if checked['expires_at']==value.get('expires_at') else None


def protect_pending_credential(credential):
    checked = valid_credential(credential)
    if checked is None:
        raise CredentialStoreError('Grok returned an invalid pending session credential.')
    plaintext = bytearray(json.dumps(checked, separators=(',', ':')).encode('utf-8'))
    protected = None
    try:
        protected = _dpapi(plaintext, 'grok', decrypt=False)
        encoded = base64.b64encode(protected).decode('ascii')
        if len(encoded) > MAX_PENDING_CIPHERTEXT:
            raise CredentialStoreError('Grok pending session credential exceeds its bound.')
        return encoded
    finally:
        plaintext[:] = b'\0' * len(plaintext)
        if protected is not None:
            protected[:] = b'\0' * len(protected)


def pending_credential():
    """The explicit pending helper never consults an existing account vault."""
    encoded = os.environ.get(PENDING_CREDENTIAL_ENV, '')
    if not 1 <= len(encoded) <= MAX_PENDING_CIPHERTEXT:
        raise CredentialStoreError('Grok pending credential is unavailable.')
    plaintext = None
    try:
        protected = base64.b64decode(encoded, validate=True)
        plaintext = _dpapi(protected, 'grok', decrypt=True)
        value = valid_credential(json.loads(plaintext))
        if value is None:
            raise CredentialStoreError('Grok pending credential is invalid or expired.')
        return value
    except (ValueError, TypeError, UnicodeError, RecursionError):
        raise CredentialStoreError('Grok pending credential could not be unlocked.') from None
    finally:
        if plaintext is not None:
            plaintext[:] = b'\0' * len(plaintext)


def helper_command(profile, *, pending=False):
    # The pinned Rust runtime's cmd /C argument handling breaks nested quotes.
    # Use only literal shell-safe path arguments, or their existing Windows
    # short-path aliases. Never interpolate a user-configurable command.
    mode = '--pending-session-token' if pending else '--session-token'
    parts=[sys.executable,'-I','-B',str(Path(__file__).resolve()),mode,str(Path(profile).absolute())]
    unsafe=lambda value:any(c.isspace() or c in '"%!^&|<>()' for c in value)
    kernel=ctypes.WinDLL('kernel32.dll',use_last_error=True,winmode=0x800)
    kernel.GetShortPathNameW.argtypes=[wintypes.LPCWSTR,wintypes.LPWSTR,wintypes.DWORD]
    kernel.GetShortPathNameW.restype=wintypes.DWORD
    for index,part in enumerate(parts):
        if not unsafe(part):continue
        buffer=ctypes.create_unicode_buffer(32768)
        length=kernel.GetShortPathNameW(part,buffer,len(buffer))
        if not 0<length<len(buffer) or unsafe(buffer.value):
            raise CredentialStoreError('This installation path cannot safely run Grok secure sign-in.')
        parts[index]=buffer.value
    return ' '.join(parts)


def main():
    # stdout is an explicitly documented native auth-provider pipe, never UI.
    if len(sys.argv)!=3 or sys.argv[1] not in ('--session-token', '--pending-session-token'):return 2
    try:
        value=(pending_credential() if sys.argv[1]=='--pending-session-token'
               else valid_credential(CredentialStore(Path(sys.argv[2]),'grok').load()))
        if value is None:return 1
        expected=os.environ.get('COMPAI_GROK_SESSION_SHA256','')
        if not hmac.compare_digest(expected,hashlib.sha256(value['access_token'].encode()).hexdigest()):return 1
        result={'access_token':value['access_token'],'issuer':value['issuer'],
            'expires_in':max(1,int(value['expires_at']-time.time()))}
        sys.stdout.write(json.dumps(result,separators=(',',':')))
        return 0
    except Exception:return 1  # Never expose token-bearing exceptions.


if __name__=='__main__':raise SystemExit(main())
