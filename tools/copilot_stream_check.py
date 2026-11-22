"""Native Copilot loopback model/tool fixture. No external inference or credentials."""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import queue
import sys
import threading
import time

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'bridge'))
from copilot_provider import CopilotRuntime, session_options

class Handler(BaseHTTPRequestHandler):
    requests=[]
    cancellation_started=threading.Event()
    release_cancel=threading.Event()
    def log_message(self,*args):pass
    def do_POST(self):
        size=int(self.headers.get('Content-Length',0))
        assert 0<size<200_000
        request=json.loads(self.rfile.read(size))
        type(self).requests.append(request)
        assert self.path=='/v1/chat/completions'
        assert [t['function']['name'] for t in request.get('tools',[])]==['fixture_read']
        round=len(type(self).requests)
        if round>4:raise AssertionError('Unexpected retries')
        if round==4:
            type(self).cancellation_started.set()
            type(self).release_cancel.wait(5)
            self.close_connection=True
            return
        if round==1:
            delta={'role':'assistant','tool_calls':[{'index':0,'id':'call_fixture','type':'function',
                'function':{'name':'fixture_read','arguments':'{}'}}]}
            finish='tool_calls'
        elif round==2:
            delta={'role':'assistant','tool_calls':[{'index':0,'id':'call_denied','type':'function',
                'function':{'name':'powershell','arguments':'{"command":"echo SHOULD_NOT_RUN"}'}}]}
            finish='tool_calls'
        else:
            delta={'role':'assistant','content':'{"answer":"Synthetic fixture: 42. Forbidden tool rejected."}'}
            finish='stop'
        self.send_response(200)
        self.send_header('Content-Type','text/event-stream')
        self.end_headers()
        for change,reason in [(delta,None),({},finish)]:
            chunk={'id':f'chatcmpl-fixture-{round}','object':'chat.completion.chunk',
                'created':1,'model':'gpt-4.1','choices':[{'index':0,'delta':change,'finish_reason':reason}]}
            if reason:chunk['usage']={'prompt_tokens':10,'completion_tokens':5,'total_tokens':15}
            self.wfile.write(('data: '+json.dumps(chunk)+'\n\n').encode())
            self.wfile.flush()
        self.wfile.write(b'data: [DONE]\n\n')
        self.wfile.flush()

def main():
    server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
    thread=threading.Thread(target=server.serve_forever,daemon=True)
    thread.start()
    stage=ROOT/'.test-tmp'/'copilot-runtime'
    runtime=CopilotRuntime(stage/'copilot-win32-x64'/'package'/'copilot.exe',stage/'probe-profile')
    original=runtime.call
    def call(method,params=None,**kwargs):
        if method=='session.create':
            params=dict(params,model='gpt-4.1',provider={'type':'openai',
                'baseUrl':f'http://127.0.0.1:{server.server_port}/v1','wireApi':'completions'})
        return original(method,params,**kwargs)
    runtime.call=call
    # Explicit local fixture transport, no OAuth credential or external route.
    runtime.create_session=lambda instructions,tools:runtime.call('session.create',session_options(runtime.profile,instructions,tools))['sessionId']
    runtime.secure_token=lambda:None
    original_receive=runtime.receive
    def receive(*args,**kwargs):
        result=original_receive(*args,**kwargs)
        event=result.get('params',{}).get('event',{})
        if event.get('type')=='session.error':print('synthetic error:',json.dumps(event.get('data')),flush=True)
        if event.get('type')=='assistant.usage':
            # The test-only loopback route is intentionally BYOK with no key.
            # Suppress this test transport marker so we can exercise event mapping;
            # production rejects it and never permits custom provider settings.
            assert event.get('data',{}).get('isByok') is True
            event['data']['isByok']=False
        return result
    runtime.receive=receive
    preview=[]
    try:
        provider=runtime.question_provider()
        sid=provider.open('Synthetic fixture. Return JSON answer only.',[{
            'name':'fixture_read','description':'Synthetic fixture only',
            'inputSchema':{'type':'object','properties':{},'additionalProperties':False}}])
        turn=provider.start(sid,'Read the synthetic value.',{},False,preview.append)
        deadline=time.monotonic()+35
        answer=None
        tools=[]
        while time.monotonic()<deadline:
            try:event=turn.read(1)
            except queue.Empty:continue
            if event.kind=='tool':
                assert event.tool=='fixture_read' and event.arguments=={}
                tools.append(event.tool)
                turn.reply_tool(event,True,{'fixture_value':42})
            elif event.kind=='answer':answer=event.text
            elif event.kind=='completed':
                assert event.status=='completed',f'{len(Handler.requests)} loopback requests'
                break
        else:raise AssertionError('No completed turn')
        assert json.loads(answer)['answer']=='Synthetic fixture: 42. Forbidden tool rejected.'
        assert tools==['fixture_read'] and len(Handler.requests)==3
        assert any('does not exist' in str(message) and 'powershell' in str(message)
            for message in Handler.requests[-1]['messages'])
        sid2=provider.open('Synthetic cancellation fixture.',[{
            'name':'fixture_read','description':'Synthetic fixture only',
            'inputSchema':{'type':'object','properties':{},'additionalProperties':False}}])
        cancel_turn=provider.start(sid2,'Wait for the synthetic response.',{},False,lambda _:None)
        assert Handler.cancellation_started.wait(3)
        started=time.monotonic()
        cancel_turn.cancel()
        assert time.monotonic()-started<3
        Handler.release_cancel.set()
        report={'route':'127.0.0.1 mock only','completed_stub_requests':3,'cancelled_stub_requests':1,'only_custom_tool_offered':True,
            'custom_tool_result':True,'model_requested_forbidden_tool_rejected':True,
            'final_answer':True,'preview_events':len(preview),'cancellation_rpc':True}
        (stage/'stream-check.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
        print(json.dumps(report))
    finally:
        runtime.close()
        Handler.release_cancel.set()
        server.shutdown()
        server.server_close()

if __name__=='__main__':main()
