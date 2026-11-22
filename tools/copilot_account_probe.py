"""Explicit official OAuth/status probe. No model, game data, or API-credit route."""
import argparse
import json
from pathlib import Path
import sys
import time

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'bridge'))
from copilot_provider import CopilotRuntime, CompanionFailure


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('action',choices=('login','status','verify'))
    parser.add_argument('--profile',type=Path,required=True)
    args=parser.parse_args()
    if not args.profile.is_absolute():parser.error('profile must be absolute')
    runtime=None
    def emit(value): print(json.dumps(value),flush=True)
    try:
        runtime=CopilotRuntime(ROOT/'.test-tmp'/'copilot-runtime'/'copilot-win32-x64'/'package'/'copilot.exe',
                               args.profile,emit=emit)
        if args.action=='login':runtime.login()
        status=runtime.status()
        emit(status)
        if args.action=='verify':
            if status.get('quota_guard')!='available':raise CompanionFailure('quota','Free quota guard did not pass.')
            provider=runtime.question_provider()
            sid=provider.open('Synthetic CompAI test. Call fixture_read exactly once and answer with its integer value only.',[{
                'name':'fixture_read','description':'Read one synthetic integer; no user or game data.',
                'inputSchema':{'type':'object','properties':{},'additionalProperties':False}}])
            marker=ROOT/'.test-tmp'/'copilot-runtime'/'free-proof.started'
            with marker.open('x',encoding='utf-8') as stream:stream.write('One authorized synthetic turn; do not automatically retry.\n')
            preview=[]
            turn=provider.start(sid,'Read the synthetic fixture value using fixture_read and answer with the number only.',{},False,preview.append)
            deadline=time.monotonic()+120
            answer=None;tool_calls=0;completed=False;event_types=set()
            try:
                while time.monotonic()<deadline:
                    try:event=turn.read(min(5,max(.01,deadline-time.monotonic())))
                    except TimeoutError:continue
                    event_types.add(event.kind)
                    if event.kind=='tool':
                        if not event.strictly_correlated or event.tool!='fixture_read' or event.arguments!={} or tool_calls!=0:
                            turn.reject_tool(event,'Unexpected test lookup')
                            raise CompanionFailure('protocol','Unexpected synthetic tool call; no retry.')
                        tool_calls+=1
                        turn.reply_tool(event,True,{'fixture_value':42})
                    elif event.is_request:turn.reject_request(event)
                    elif event.kind=='answer':answer=event.text
                    elif event.kind=='completed':
                        completed=event.status=='completed'
                        break
                parsed=json.loads(answer) if isinstance(answer,str) else None
                passed=completed and tool_calls==1 and parsed=={'answer':'42'}
                report={'runtime':'1.0.98','passed':passed,'logical_questions':1,'tool_calls':tool_calls,
                    'completed':completed,'answer_is_42':parsed=={'answer':'42'},'usage':turn.usage,
                    'event_types':sorted(value for value in event_types if isinstance(value,str)),
                    'application_retries':0,'native_internal_retries':'not fully observable',
                    'game_data_sent':False,'api_key_route':False}
                (marker.parent/'free-proof.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
                emit(report)
            finally:
                if not completed:
                    try:turn.cancel()
                    except Exception:pass
    except CompanionFailure as exc:
        emit({'error':str(exc),'kind':exc.kind})
    except Exception as exc:
        emit({'error':'Copilot account probe failed','kind':type(exc).__name__})
    finally:
        if runtime is not None:runtime.close()


if __name__=='__main__':main()
