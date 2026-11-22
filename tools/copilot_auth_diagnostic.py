"""Sanitized status-only diagnostics; no model request, credential or identity output."""
import argparse
import json
from pathlib import Path
import re
import sys

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'bridge'))
from copilot_provider import CopilotRuntime, CompanionFailure

WORDS=set('status token authentication authorization authorized forbidden scope copilot subscription oauth expired failed invalid public permission app free not supported required missing user no provided fetched entitlement cache credential account integration policy blocked github server unexpected unauthorized login identity consistent refresh initial 400 401 402 403 404 422 cancelled cancelled callback provider githubtokenprovider'.split())


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--profile',type=Path,required=True)
    args=parser.parse_args()
    runtime=CopilotRuntime(ROOT/'.test-tmp'/'copilot-runtime'/'copilot-win32-x64'/'package'/'copilot.exe',args.profile)
    original_send,original_receive=runtime.send,runtime.receive
    requests={};diagnostics=[]
    def send(message):
        if message.get('method'):requests[message.get('id')]=message['method']
        original_send(message)
    def receive(*args,**kwargs):
        message=original_receive(*args,**kwargs)
        error=message.get('error')
        if isinstance(error,dict):
            reason=str(error.get('message',''))
            words=[word.lower() for word in re.findall(r'[A-Za-z0-9]+',reason) if word.lower() in WORDS]
            diagnostics.append({'method':requests.get(message.get('id')),
                'code':error.get('code') if type(error.get('code')) is int else None,
                'reason_keywords':words[:80], 'has_data':bool(error.get('data'))})
        return message
    runtime.send,runtime.receive=send,receive
    try:
        result=runtime.status()
        print(json.dumps(result),flush=True)
    except CompanionFailure as exc:
        print(json.dumps({'error':str(exc),'kind':exc.kind,'diagnostics':diagnostics}),flush=True)
    except Exception as exc:
        print(json.dumps({'error_kind':type(exc).__name__,'diagnostics':diagnostics}),flush=True)
    finally:runtime.close()


if __name__=='__main__':main()
