"""Bounded synthetic Grok adapter proof; never accepts game data or API keys."""
import argparse
import json
from pathlib import Path
import queue
import sys
import time
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'bridge'))
from grok_provider import GrokRuntime

ROOT=Path(__file__).resolve().parents[1]/'.test-tmp'/'grok-runtime'
TOOLS=[{'name':'synthetic_double','description':'Return twice the synthetic integer supplied.',
    'inputSchema':{'type':'object','properties':{'value':{'type':'integer'}},
                   'required':['value'],'additionalProperties':False}}]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action',choices=['status','open','live-once','live-qualified-once'])
    parser.add_argument('--plugin-profile',action='store_true',help='Use the authorized DPAPI-only CompAI Grok profile.')
    args=parser.parse_args()
    if args.plugin_profile:
        from grok_secure_login import plugin_profile
        profile=plugin_profile()
    else:profile=ROOT/'profile'
    runtime=GrokRuntime(ROOT/'grok.exe',profile)
    original_call=runtime.call
    def observed_call(method,*values,**named):
        result=original_call(method,*values,**named)
        if method=='session/new':print(json.dumps({'session_model':result.get('models',{}).get('currentModelId'),
            'session_meta_model':result.get('_meta',{}).get('modelState',{}).get('currentModelId')}),flush=True)
        return result
    runtime.call=observed_call
    try:
        print(json.dumps(runtime.status()),flush=True)
        if args.action=='status':return
        sid=runtime.open('This is a synthetic CompAI tool integration test. Use synthetic_double exactly once with value 7, then answer with the returned integer. No real user information exists.',TOOLS)
        print('Isolated Grok session and exact app tool inventory verified.',flush=True)
        if args.action not in ('live-once','live-qualified-once'):return
        # Exclusive marker prevents accidentally repeating allowance-consuming proof.
        with (ROOT/(args.action+'.started')).open('x',encoding='utf-8') as record:
            record.write('One user-authorized synthetic tool roundtrip started. No game data.\n')
        turn=runtime.start(sid,'Call synthetic_double with value 7, then return that result.',{},False,lambda _:None)
        deadline=time.monotonic()+120;calls=[];answers=[]
        while time.monotonic()<deadline:
            try:event=turn.read(1)
            except (queue.Empty,TimeoutError):continue
            if event.kind=='tool':
                if event.tool!='synthetic_double' or event.arguments!={'value':7} or calls:
                    turn.reject_tool(event,'Synthetic check');raise RuntimeError('Unexpected synthetic tool request; stopped.')
                calls.append(event.arguments);turn.reply_tool(event,True,{'value':14})
            elif event.kind=='answer':answers.append(event.text)
            elif event.kind=='completed':
                result={'status':event.status,'tool_calls':calls,'answers':answers}
                (ROOT/(args.action+'.result.json')).write_text(json.dumps(result,indent=2),encoding='utf-8')
                print(json.dumps(result),flush=True)
                if event.status!='completed' or len(calls)!=1:raise RuntimeError('Synthetic roundtrip did not complete successfully.')
                return
            elif event.is_request:turn.reject_request(event);raise RuntimeError('Unexpected runtime request; stopped.')
        turn.cancel();raise RuntimeError('Synthetic roundtrip timed out; no retry.')
    finally:runtime.close()

if __name__=='__main__':main()
