"""Offline regression of native persistence-error recovery; no native process."""
import base64
import json
from pathlib import Path
import sys
import time
import uuid
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'bridge'))
from companion_provider import CompanionFailure
from credential_store import CredentialStore
from grok_provider import GrokRuntime

def main():
    root=Path(__file__).resolve().parents[1]/'.test-tmp'/('grok-login-check-'+uuid.uuid4().hex[:10])
    encode=lambda value:base64.urlsafe_b64encode(json.dumps(value).encode()).decode().rstrip('=')
    token=encode({'alg':'none'})+'.'+encode({'iss':'https://auth.x.ai','exp':int(time.time())+3600})+'.SYNTHETIC-NOT-A-CREDENTIAL'
    for index,(exported,account_valid,expected) in enumerate([(token,True,True),(None,True,False),(token,False,False)]):
        client=object.__new__(GrokRuntime)
        client.close=lambda:None;client._start=lambda:None
        client.seq=0;client.send=lambda _:None;client.emit=lambda _:None
        client.credentials=CredentialStore(root/str(index),'grok')
        client.receive=lambda _: {'id':1,'error':{'code':-32000,'message':'persistence intentionally unavailable'}}
        requests=[]
        def call(method,*args,**kwargs):
            requests.append(method)
            if method=='_x.ai/auth/getBearerToken':return {'result':{'token':exported}}
            if method=='authenticate':return {}
            if method=='_x.ai/auth/check_subscription':return {'authenticated':account_valid}
            raise AssertionError('Unexpected request')
        client.call=call
        try:client.login()
        except CompanionFailure:
            assert not expected
        else:assert expected
        saved=client.credentials.load()
        assert (saved is not None)==expected
        if expected:assert requests==['_x.ai/auth/getBearerToken','authenticate','_x.ai/auth/check_subscription']
    print('PASS: persistence error requires valid bearer + cached authentication + authenticated account before DPAPI save.')
    print('PASS: absent bearer and unvalidated account save no credential. No native process/network/model use.')

if __name__=='__main__':main()
