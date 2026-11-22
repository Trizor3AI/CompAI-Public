"""Pinned native offline tool fixture. Synthetic loopback model; no accounts."""
from pathlib import Path
import json
import queue
import subprocess
import sys
import threading
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'bridge'))
import grok_runtime_probe as g
from grok_provider import CONFIG, agent_profile, tool_instructions

ROOT=g.ROOT.parent/('grok-permission-'+uuid.uuid4().hex[:8])
for name in ('profile','workspace','temp'):(ROOT/name).mkdir(parents=True,exist_ok=True)
requests=[];calls=[];main_turns=[];permissions=[]
sentinel='PRIVATE_SYNTHETIC_INPUT_MUST_NOT_REACH_MODEL'
source=ROOT/'private-input.json'
source.write_text(json.dumps({'tool_name':'compai__probe','tool_input':{'value':999999,'private':sentinel}}),encoding='utf8')
tool={'name':'probe','description':'Return double the synthetic value',
    'inputSchema':{'type':'object','properties':{'value':{'type':'integer'}},'required':['value'],'additionalProperties':False}}
cases=[('read_file',{'target_file':str(source)}),
       ('run_terminal_command',{'command':'echo denied'}),
       ('use_tool',{'file':str(source)}),
       ('use_tool',{'tool_name':'compai__probe','tool_input':{'value':7}}),
       ('use_tool',{'tool_name':'probe','tool_input':{'value':8}})]

class Handler(BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def do_GET(self):
        self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers()
        self.wfile.write(json.dumps({'data':[{'id':'fixture','object':'model','context_window':8192}]}).encode())
    def do_POST(self):
        body=json.loads(self.rfile.read(int(self.headers.get('Content-Length','0'))));requests.append(body)
        names=[entry.get('function',{}).get('name') for entry in body.get('tools',[])]
        data={'id':'offline','object':'chat.completion.chunk','created':1,'model':'fixture',
            'choices':[{'index':0,'delta':{'role':'assistant','content':'offline fixture'},'finish_reason':None}]}
        finish='stop'
        if names and names!=['session_title']:
            assert names==['use_tool'],names
            main_turns.append(body);n=len(main_turns)
            if n<=len(cases):
                name,args=cases[n-1]
                data['choices'][0]['delta']={'role':'assistant','tool_calls':[{'index':0,'id':f'call{n}','type':'function',
                    'function':{'name':name,'arguments':json.dumps(args)}}]};finish='tool_calls'
            else:data['choices'][0]['delta']['content']='{"answer":"Synthetic value is 14."}'
        self.send_response(200);self.send_header('Content-Type','text/event-stream');self.end_headers()
        self.wfile.write(('data: '+json.dumps(data)+'\n\n').encode())
        data['choices'][0]={'index':0,'delta':{},'finish_reason':finish}
        self.wfile.write(('data: '+json.dumps(data)+'\n\ndata: [DONE]\n\n').encode());self.wfile.flush()

server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
threading.Thread(target=server.serve_forever,daemon=True).start()
# Only the closed-loop fixture permits a deliberately invalid API key for its
# local HTTP server. Production config and requirements forbid API-key auth.
config=CONFIG.replace('[grok_com_config]\ndisable_api_key_auth=true\n','')
config+='''
[model.fixture]
model="fixture"
base_url="http://127.0.0.1:%d/v1"
api_key="offline-fixture-not-a-real-key"
api_backend="chat_completions"
supports_backend_search=false
max_retries=0
[models]
default="fixture"
session_summary="fixture"
allowed_models=["fixture"]
'''%server.server_port
(ROOT/'profile/config.toml').write_text(config,encoding='utf8')

class Client(g.AcpProbe):
    def _read(self):
        for line in self.process.stdout:
            obj=json.loads(line)
            if obj.get('method')=='session/request_permission':permissions.append(obj)
            if obj.get('method')=='_x.ai/mcp/sdk_call':
                params=obj['params'];assert params['serverId']=='probe'
                msg=params['message'];method=msg.get('method');result={}
                if method=='initialize':result={'protocolVersion':'2024-11-05','capabilities':{'tools':{}},'serverInfo':{'name':'compai','version':'1'}}
                elif method=='tools/list':result={'tools':[tool]}
                elif method=='tools/call':
                    assert msg['params']['name']=='probe'
                    args=msg['params']['arguments'];assert args=={'value':7},args
                    calls.append(args);result={'content':[{'type':'text','text':json.dumps({'value':14})}]}
                self._write({'jsonrpc':'2.0','id':obj['id'],'result':{'jsonrpc':'2.0','id':msg.get('id'),'result':result}})
            else:self.lines.put(obj)
        self.lines.put(RuntimeError('closed'))
    def __init__(self):
        env=dict(g.environment(ROOT),XAI_API_KEY='offline-synthetic-not-a-valid-key',
            GROK_TITLE_REFRESH='0',GROK_TURN_SUMMARY='0',GROK_SESSION_RECAP='0',GROK_TURN_TRANSIENT_RETRY='0',
            GROK_MANAGED_MCPS_ENABLED='0',GROK_MANAGED_MCP_GATEWAY_TOOLS_ENABLED='0',
            GROK_XAI_API_BASE_URL=f'http://127.0.0.1:{server.server_port}/v1',
            GROK_MODELS_LIST_URL=f'http://127.0.0.1:{server.server_port}/v1/models',
            HTTPS_PROXY=f'http://127.0.0.1:{server.server_port}',HTTP_PROXY=f'http://127.0.0.1:{server.server_port}',NO_PROXY='127.0.0.1,localhost')
        self.process=subprocess.Popen(g.command('--disable-web-search','--no-subagents','--no-plan','agent','--no-leader','--model','fixture','stdio'),
            cwd=ROOT/'workspace',env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,encoding='utf8')
        self.lines=queue.Queue();self.next_id=0
        threading.Thread(target=self._read,daemon=True).start()

client=Client()
try:
    client.request('initialize',{'protocolVersion':1,'clientCapabilities':{'fs':{'readTextFile':False,'writeTextFile':False},'terminal':False}})
    client.request('authenticate',{'methodId':'xai.api_key'})
    session=client.request('session/new',{'cwd':str(ROOT/'workspace'),'mcpServers':[],
        '_meta':{'sessionKind':'headless','x.ai/mcp/servers':[{'name':'compai','serverId':'probe'}],
            'agentProfile':agent_profile(),'systemPromptOverride':'Synthetic offline fixture.'+tool_instructions([tool])}})
    client.request('session/prompt',{'sessionId':session['sessionId'],'prompt':[{'type':'text','text':'Offline synthetic fixture'}]},timeout=30)
    assert calls==[{'value':7}],calls
    assert sentinel not in json.dumps(requests),'Private input reached model'
    results=[msg for msg in main_turns[-1]['messages'] if msg['role']=='tool']
    assert len(results)==len(cases)-1,results
    assert len(permissions)==1 and permissions[0]['params']['toolCall']['rawInput']=={
        'variant':'UseTool','tool_name':'probe','tool_input':{'value':8}},permissions
    evidence={'requests':len(requests),'main_turns':len(main_turns),'mcp_calls':calls,
        'permissions':permissions,'tool_results':results,'private_input_leaked':False}
    (ROOT/'evidence.json').write_text(json.dumps(evidence,indent=2),encoding='utf8')
    print(json.dumps(evidence,indent=2))
finally:
    (ROOT/'observations.json').write_text(json.dumps({'requests':len(requests),'main_turns':len(main_turns),'calls':calls,
        'permissions':permissions,'tool_results':[msg for body in main_turns[-1:] for msg in body['messages'] if msg['role']=='tool']},indent=2),encoding='utf8')
    client.close();server.shutdown()
