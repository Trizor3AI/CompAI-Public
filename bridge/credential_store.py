"""Current-user Windows DPAPI storage for CompAI's isolated account credentials.

No plaintext files, logging, provider requests, global credential access, or
machine-wide DPAPI scope. Python objects necessarily hold plaintext in memory;
native DPAPI buffers are cleared before release. Same-user malware is outside
DPAPI's boundary. Hold locked() only around credential read/refresh/write, never
around inference. See Microsoft's CryptProtectData/CryptUnprotectData docs.
"""
from contextlib import contextmanager
import ctypes
from ctypes import wintypes
import errno
import hashlib
import hmac
import json
import math
import os
from pathlib import Path
import re
import stat
import threading
import time
import uuid


PROVIDERS = frozenset({"copilot", "grok"})
MAX_PLAINTEXT_BYTES = 128 * 1024
MAX_FILE_BYTES = MAX_PLAINTEXT_BYTES + 16 * 1024
HEADER = b"CompAI-DPAPI\x00\x01"
UI_FORBIDDEN = 0x1  # Deliberately excludes CRYPTPROTECT_LOCAL_MACHINE (0x4).


class CredentialStoreError(RuntimeError):
    """Fixed, nonsecret user-facing storage error."""


def _require_windows():
    if os.name != "nt":
        raise CredentialStoreError("Secure account storage requires Windows DPAPI.")


def _unlinked(path):
    for component in (path, *path.parents):
        try:
            info = component.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise CredentialStoreError("Linked account-storage paths are unsupported.")


def _validate_json(value):
    pending, count = [(value, 0)], 0
    while pending:
        item, depth = pending.pop()
        count += 1
        if depth > 32 or count > 16384:
            raise CredentialStoreError("Account credential structure exceeds its bound.")
        kind = type(item)
        if kind is dict:
            if any(type(key) is not str for key in item):
                raise CredentialStoreError("Account credentials must contain JSON string keys.")
            pending.extend((child, depth + 1) for child in item.values())
        elif kind is list:
            pending.extend((child, depth + 1) for child in item)
        elif kind not in (str, int, float, bool, type(None)) or kind is float and not math.isfinite(item):
            raise CredentialStoreError("Account credentials must be finite JSON data.")


def _encode(value):
    _validate_json(value)
    try:
        data = bytearray(json.dumps(value, ensure_ascii=False, allow_nan=False,
                                    sort_keys=True, separators=(",", ":")).encode("utf-8"))
    except (TypeError, ValueError, UnicodeError, RecursionError):
        raise CredentialStoreError("Account credential JSON is invalid.") from None
    if len(data) > MAX_PLAINTEXT_BYTES:
        data[:] = b"\0" * len(data)
        raise CredentialStoreError("Account credentials exceed their size bound.")
    return data


def _pairs(entries):
    result = {}
    for key, value in entries:
        if key in result:
            raise ValueError()
        result[key] = value
    return result


class _Blob(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_ubyte))]


def _dpapi(data, provider, *, decrypt):
    _require_windows()
    if type(provider) is not str or provider not in PROVIDERS or not 0 < len(data) <= MAX_FILE_BYTES:
        raise CredentialStoreError("Invalid account encryption scope or size.")
    # Both provider identity and schema must match to decrypt a copied blob.
    entropy = b"CompAI.account.credentials\x00v1\x00" + provider.encode("ascii")
    source = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    extra = (ctypes.c_ubyte * len(entropy)).from_buffer_copy(entropy)
    blob, optional, output = _Blob(len(data), source), _Blob(len(entropy), extra), _Blob()
    crypt = ctypes.WinDLL("crypt32.dll", use_last_error=True, winmode=0x800)
    kernel = ctypes.WinDLL("kernel32.dll", use_last_error=True, winmode=0x800)
    function = crypt.CryptUnprotectData if decrypt else crypt.CryptProtectData
    function.argtypes = [ctypes.POINTER(_Blob), ctypes.c_void_p, ctypes.POINTER(_Blob),
                        ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(_Blob)]
    function.restype = wintypes.BOOL
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p
    try:
        if not function(ctypes.byref(blob), None, ctypes.byref(optional), None, None,
                        UI_FORBIDDEN, ctypes.byref(output)):
            raise CredentialStoreError("Windows could not protect or unlock this account credential.")
        limit = MAX_PLAINTEXT_BYTES if decrypt else MAX_FILE_BYTES - len(HEADER)
        if not output.pbData or not 0 < output.cbData <= limit:
            raise CredentialStoreError("Windows returned an invalid protected credential.")
        return bytearray(ctypes.string_at(output.pbData, output.cbData))
    finally:
        ctypes.memset(source, 0, len(data))
        if output.pbData:
            ctypes.memset(output.pbData, 0, output.cbData)
            kernel.LocalFree(output.pbData)


class CredentialStore:
    def __init__(self, profile_root, provider):
        _require_windows()
        if type(provider) is not str or provider not in PROVIDERS:
            raise CredentialStoreError("Unsupported account credential provider.")
        self.provider = provider
        if not Path(profile_root).is_absolute():
            raise CredentialStoreError("Account credentials require an absolute profile path.")
        root = Path(os.path.abspath(profile_root))
        if not re.fullmatch(r"[A-Za-z]:\\", root.anchor) or any(":" in part for part in root.parts[1:]):
            raise CredentialStoreError("Account credentials require a local drive path.")
        self.directory = root / "credentials"
        self.path = self.directory / (provider + ".dpapi")
        self.lock_path = self.directory / (provider + ".lock")
        self._thread_lock = threading.RLock()
        self._lock_depth = 0
        try:
            _unlinked(self.directory)
        except OSError:
            raise CredentialStoreError("Account-storage path could not be checked.") from None

    @contextmanager
    def locked(self, timeout=15):
        """Serialize only credential operations, including refresh-token rotation.

        Reentrant on this instance/thread. Independent instances/processes use
        the same Windows byte-range lock; the lock file contains no credentials.
        """
        import msvcrt
        if type(timeout) not in (int, float) or not math.isfinite(timeout) or not 0 <= timeout <= 60:
            raise CredentialStoreError("Invalid account-storage lock timeout.")
        deadline = time.monotonic() + timeout
        if not self._thread_lock.acquire(timeout=timeout):
            raise CredentialStoreError("Another account operation is in progress; try again.")
        descriptor, acquired = None, False
        try:
            if self._lock_depth:
                self._lock_depth += 1
                try:
                    yield self
                finally:
                    self._lock_depth -= 1
                return
            try:
                _unlinked(self.lock_path)
                self.directory.mkdir(parents=True, exist_ok=True)
                _unlinked(self.lock_path)
                descriptor = os.open(self.lock_path, os.O_RDWR | os.O_CREAT | os.O_BINARY, 0o600)
                if os.fstat(descriptor).st_size == 0:
                    os.write(descriptor, b"\0")
                while True:
                    os.lseek(descriptor, 0, os.SEEK_SET)
                    try:
                        msvcrt.locking(descriptor, msvcrt.LK_NBLCK, 1)
                        acquired = True
                        break
                    except OSError as exc:
                        if exc.errno not in (errno.EACCES, errno.EAGAIN, errno.EDEADLK):
                            raise
                        if time.monotonic() >= deadline:
                            raise CredentialStoreError("Another account operation is in progress; try again.") from None
                        time.sleep(min(.05, max(0, deadline - time.monotonic())))
                _unlinked(self.path)
            except OSError:
                raise CredentialStoreError("Secure account storage could not be opened.") from None
            self._lock_depth = 1
            try:
                yield self
            finally:
                self._lock_depth = 0
        finally:
            try:
                if descriptor is not None:
                    try:
                        if acquired:
                            os.lseek(descriptor, 0, os.SEEK_SET)
                            msvcrt.locking(descriptor, msvcrt.LK_UNLCK, 1)
                    finally:
                        os.close(descriptor)
            finally:
                self._thread_lock.release()

    def load(self):
        """Return provider JSON or None. Corrupt/unreadable files remain intact."""
        with self.locked():
            try:
                _unlinked(self.path)
                with self.path.open("rb") as source:
                    data = source.read(MAX_FILE_BYTES + 1)
            except FileNotFoundError:
                return None
            except OSError:
                raise CredentialStoreError("Protected account credentials could not be read.") from None
            if not data.startswith(HEADER) or len(data) > MAX_FILE_BYTES:
                raise CredentialStoreError("Protected account credentials have an unsupported format.")
            plaintext = _dpapi(data[len(HEADER):], self.provider, decrypt=True)
            canonical = None
            try:
                value = json.loads(plaintext, object_pairs_hook=_pairs)
                if (type(value) is not dict or set(value) != {"schema", "provider", "credential", "digest"}
                        or type(value["schema"]) is not int or value["schema"] != 1
                        or value["provider"] != self.provider or type(value["credential"]) is not dict
                        or type(value["digest"]) is not str):
                    raise ValueError()
                canonical = _encode(value["credential"])
                if not hmac.compare_digest(value["digest"], hashlib.sha256(canonical).hexdigest()):
                    raise ValueError()
                return value["credential"]
            except (ValueError, TypeError, UnicodeError, RecursionError):
                raise CredentialStoreError("Protected account credentials failed integrity validation.") from None
            finally:
                plaintext[:] = b"\0" * len(plaintext)
                if canonical is not None:
                    canonical[:] = b"\0" * len(canonical)

    def save(self, credential):
        """Atomically replace only a valid vault; failed saves retain old bytes."""
        if type(credential) is not dict:
            raise CredentialStoreError("Account credentials must be a JSON object.")
        canonical, plaintext, ciphertext, temporary = None, None, None, None
        with self.locked():
            try:
                self.load()  # Preserve an existing unsupported or corrupt vault.
                canonical = _encode(credential)
                plaintext = _encode({"schema": 1, "provider": self.provider, "credential": credential,
                                     "digest": hashlib.sha256(canonical).hexdigest()})
                ciphertext = _dpapi(plaintext, self.provider, decrypt=False)
                temporary = self.directory / ("." + self.provider + "." + uuid.uuid4().hex + ".tmp")
                _unlinked(temporary)
                with temporary.open("xb") as target:
                    target.write(HEADER)
                    target.write(ciphertext)
                    target.flush()
                    os.fsync(target.fileno())
                _unlinked(self.path)
                _unlinked(temporary)
                os.replace(temporary, self.path)
                temporary = None
            except OSError:
                raise CredentialStoreError("Protected account credentials could not be saved; previous credentials were retained.") from None
            finally:
                for data in (canonical, plaintext, ciphertext):
                    if data is not None:
                        data[:] = b"\0" * len(data)
                if temporary is not None:
                    try:
                        _unlinked(temporary)
                        temporary.unlink(missing_ok=True)
                    except (OSError, CredentialStoreError):
                        pass  # An interrupted ciphertext file contains no plaintext.

    def delete(self):
        """Remove only this provider's encrypted vault, including corrupt vaults.

        This is local sign-out storage removal, not provider-side revocation or
        a claim of secure erasure from backups/SSD media.
        """
        with self.locked():
            try:
                _unlinked(self.path)
                self.path.unlink()
                return True
            except FileNotFoundError:
                return False
            except OSError:
                raise CredentialStoreError("Protected account credentials could not be removed.") from None
