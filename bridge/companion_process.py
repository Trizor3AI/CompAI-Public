"""Contain only the companion-owned Windows runtime and its descendants."""
import ctypes
from ctypes import wintypes
import os
import subprocess


class ProcessContainmentError(OSError):
    """The private runtime could not establish its required Windows boundary."""


class _BasicLimits(ctypes.Structure):
    _fields_ = [("PerProcessUserTimeLimit", ctypes.c_int64), ("PerJobUserTimeLimit", ctypes.c_int64),
                ("LimitFlags", wintypes.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wintypes.DWORD),
                ("Affinity", ctypes.c_size_t), ("PriorityClass", wintypes.DWORD),
                ("SchedulingClass", wintypes.DWORD)]


class _IoCounters(ctypes.Structure):
    _fields_ = [(name, ctypes.c_uint64) for name in
                ("ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
                 "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]


class _ExtendedLimits(ctypes.Structure):
    _fields_ = [("BasicLimitInformation", _BasicLimits), ("IoInfo", _IoCounters),
                ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]


class _ThreadEntry(ctypes.Structure):
    _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                ("th32ThreadID", wintypes.DWORD), ("th32OwnerProcessID", wintypes.DWORD),
                ("tpBasePri", wintypes.LONG), ("tpDeltaPri", wintypes.LONG), ("dwFlags", wintypes.DWORD)]


def _kernel():
    api = ctypes.WinDLL("kernel32", use_last_error=True)
    definitions = {
        "CreateJobObjectW": ([ctypes.c_void_p, wintypes.LPCWSTR], wintypes.HANDLE),
        "SetInformationJobObject": ([wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD], wintypes.BOOL),
        "AssignProcessToJobObject": ([wintypes.HANDLE, wintypes.HANDLE], wintypes.BOOL),
        "CreateToolhelp32Snapshot": ([wintypes.DWORD, wintypes.DWORD], wintypes.HANDLE),
        "Thread32First": ([wintypes.HANDLE, ctypes.POINTER(_ThreadEntry)], wintypes.BOOL),
        "Thread32Next": ([wintypes.HANDLE, ctypes.POINTER(_ThreadEntry)], wintypes.BOOL),
        "OpenThread": ([wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
        "ResumeThread": ([wintypes.HANDLE], wintypes.DWORD),
        "CloseHandle": ([wintypes.HANDLE], wintypes.BOOL),
    }
    for name, (arguments, result) in definitions.items():
        function = getattr(api, name)
        function.argtypes, function.restype = arguments, result
    return api


def _resume_process(api, process):
    # Popen closes CreateProcess's primary-thread handle. The process is still
    # suspended: find only its primary thread, while its process handle pins PID.
    snapshot = api.CreateToolhelp32Snapshot(4, 0)  # TH32CS_SNAPTHREAD
    if snapshot == ctypes.c_void_p(-1).value:
        raise ProcessContainmentError("Unable to inspect the owned suspended runtime")
    try:
        entry = _ThreadEntry()
        entry.dwSize = ctypes.sizeof(entry)
        found = api.Thread32First(snapshot, ctypes.byref(entry))
        while found:
            if entry.th32OwnerProcessID == process.pid:
                thread = api.OpenThread(2, False, entry.th32ThreadID)  # THREAD_SUSPEND_RESUME
                if not thread:
                    raise ProcessContainmentError("Unable to open the owned suspended runtime thread")
                try:
                    if api.ResumeThread(thread) == 0xFFFFFFFF:
                        raise ProcessContainmentError("Unable to resume the contained runtime")
                    return
                finally:
                    api.CloseHandle(thread)
            found = api.Thread32Next(snapshot, ctypes.byref(entry))
        raise ProcessContainmentError("Owned suspended runtime thread unavailable")
    finally:
        api.CloseHandle(snapshot)


def start_owned_process(command, **kwargs):
    if os.name != "nt":
        raise ProcessContainmentError("The private runtime requires Windows process containment")
    try:
        api = _kernel()
        job = api.CreateJobObjectW(None, None)  # Unnamed, non-inheritable; no shared job is modified.
    except OSError:
        raise ProcessContainmentError("Unable to create private runtime containment") from None
    if not job:
        raise ProcessContainmentError("Unable to create private runtime containment")
    process = None
    try:
        limits = _ExtendedLimits()
        limits.BasicLimitInformation.LimitFlags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not api.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            raise ProcessContainmentError("Unable to configure private runtime containment")
        flags = kwargs.pop("creationflags", 0) | subprocess.CREATE_NO_WINDOW | 4  # CREATE_SUSPENDED
        process = subprocess.Popen(command, creationflags=flags, **kwargs)
        if not api.AssignProcessToJobObject(job, wintypes.HANDLE(int(process._handle))):
            raise ProcessContainmentError("Unable to contain the owned suspended runtime")
        process._companion_job = (api, job)
        try:
            _resume_process(api, process)
        except OSError:
            raise ProcessContainmentError("Unable to resume the contained runtime") from None
        return process
    except BaseException:
        try:
            if process is not None:
                process._companion_job = None
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=5)
        finally:
            api.CloseHandle(job)
            if process is not None:
                for stream in (process.stdin, process.stdout, process.stderr):
                    if stream is not None:
                        stream.close()
        raise


def close_owned_process(process):
    job = getattr(process, "_companion_job", None)
    if job is not None:
        process._companion_job = None
        api, handle = job
        # Always close, including after the root process exited on its own.
        # Windows terminates only members of this job, including descendants.
        api.CloseHandle(handle)
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
    for stream in (process.stdin, process.stdout, process.stderr):
        if stream is not None:
            stream.close()
