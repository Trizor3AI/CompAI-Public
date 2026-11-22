"""Pinned-native synthetic proof: DPAPI -> official session hook, no plaintext.

No real credentials, OAuth login, or inference. HTTP proxies point to a closed
loopback port. The synthetic JWT is deliberately unsigned and unusable online.
"""
import base64
import json
import os
from pathlib import Path
import sys
import subprocess
import time
import uuid

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'bridge'))
from companion_provider import CompanionFailure
from grok_provider import GrokRuntime
from grok_credentials import NativeAuthGuard,credential_for_token,valid_credential,helper_command

ROOT=Path(__file__).resolve().parents[1]/'.test-tmp'/('grok-memory-auth-'+uuid.uuid4().hex[:10])

def main():
    runtime=GrokRuntime(ROOT.parent/'grok-runtime/grok.exe',ROOT/'profile')
    marker='SYNTHETIC-NOT-A-CREDENTIAL-'+uuid.uuid4().hex
    encode=lambda obj:base64.urlsafe_b64encode(json.dumps(obj).encode()).decode().rstrip('=')
    token=encode({'alg':'none'})+'.'+encode({'iss':'https://auth.x.ai','exp':int(time.time())+3600,'sub':marker})+'.'+marker
    runtime.credentials.save(credential_for_token(token))
    assert valid_credential(runtime.credentials.load())['access_token']==token
    assert valid_credential(runtime.credentials.load(),time.time()+7200) is None
    runtime.environment.update(HTTPS_PROXY='http://127.0.0.1:9',HTTP_PROXY='http://127.0.0.1:9',ALL_PROXY='http://127.0.0.1:9')
    try:
        runtime._start()
        assert not any(row['id']=='xai.api_key' for row in runtime.initialized.get('authMethods',[]))
        guard=runtime.auth_guard.path
        for operation in (lambda:guard.read_bytes(),lambda:guard.unlink(),lambda:guard.write_bytes(b'changed'),
                          lambda:os.replace(guard,guard.with_name('moved-guard'))):
            try:operation()
            except PermissionError:pass
            else:raise AssertionError('Active persistence guard was replaceable')
        second_guard=NativeAuthGuard(runtime.profile)
        assert second_guard.path!=guard
        second_guard.close()
        try:runtime._subscription()
        except CompanionFailure:pass
        response=runtime.call('_x.ai/auth/getBearerToken')
        assert response.get('result',response).get('token')==token
        assert guard.is_file() and guard.stat().st_size==0
        replacement=encode({'alg':'none'})+'.'+encode({'iss':'https://auth.x.ai','exp':int(time.time())+3600,'sub':'different-account'})+'.different-synthetic-signature'
        runtime.credentials.save(credential_for_token(replacement))
        result=subprocess.run(helper_command(runtime.profile),shell=True,env=runtime.environment,
            stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=10)
        assert result.returncode==1 and not result.stdout and not result.stderr
        runtime.credentials.save(credential_for_token(token))
    finally:runtime.close()
    leaked=[]
    for file in ROOT.rglob('*'):
        if file.is_file() and (marker.encode() in file.read_bytes() or file.name=='auth.json'):
            leaked.append(file.relative_to(ROOT).as_posix())
    assert not leaked,leaked
    print('PASS: DPAPI helper -> pinned native session token; API-key auth disabled.')
    print('PASS: active guard rejects read/write/delete/rename; independent guards support concurrency.')
    print('PASS: no fallback auth.json or full synthetic secret in isolated files; expiry requires login.')
    print('PASS: a replaced vault token cannot enter an already checked native runtime.')
    print('No real login, account request, or model inference was made.')

if __name__=='__main__':main()
