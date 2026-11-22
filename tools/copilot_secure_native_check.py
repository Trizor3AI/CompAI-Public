"""Native pinned-runtime token callback cancellation, with no credential supplied.

The callback cancels before authentication; no model/session.send is invoked.
"""
import json
from pathlib import Path
import sys
from types import SimpleNamespace

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'bridge'))
from copilot_provider import CopilotRuntime, CompanionFailure


def main():
    stage=ROOT/'.test-tmp'/'copilot-runtime'
    runtime=CopilotRuntime(stage/'copilot-win32-x64'/'package'/'copilot.exe',stage/'probe-profile')
    runtime.oauth=SimpleNamespace(saved=lambda:{'account_id':42},token=lambda **_: {'kind':'cancelled'})
    seen=[]
    original=runtime.handle_request
    def request(message):
        if message.get('method')=='gitHubToken.getToken':
            params=message.get('params',{})
            seen.append({'method':message['method'],'host':params.get('host'),
                'reason':params.get('reason'),
                'matched_session':runtime.token_registrations.get(params.get('registrationId'))==params.get('sessionId')})
        return original(message)
    runtime.handle_request=request
    try:
        try:runtime.create_session('Synthetic cancellation only.',[])
        except CompanionFailure:pass
        else:raise AssertionError('Cancelled token provider unexpectedly created a session')
        assert seen==[{'method':'gitHubToken.getToken','host':'https://github.com',
                     'reason':'initial','matched_session':True}],seen
        report={'runtime':'1.0.98','credential_supplied':False,'model_requests':0,
            'registered_callback_observed':seen,'cancelled_session_rejected':True}
        (stage/'secure-native-check.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
        print(json.dumps(report))
    finally:runtime.close()


if __name__=='__main__':main()
