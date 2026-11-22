"""Pinned Copilot runtime research probe. No production provider or account request."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / '.test-tmp' / 'copilot-runtime'
EXE = STAGE / 'copilot-win32-x64' / 'package' / 'copilot.exe'
EXE_HASH = '23e44448b2589401081aa90ee04201fa4b17e4e36d2ac2a10f57e5e1a317cc54'

def environment(profile):
    result = {k: v for k, v in os.environ.items() if k.upper() in {
        'SYSTEMROOT', 'WINDIR', 'COMSPEC', 'SYSTEMDRIVE', 'PATHEXT'}}
    for name in ('home', 'local', 'roaming', 'tmp', 'work'):
        (profile / name).mkdir(parents=True, exist_ok=True)
    result.update(USERPROFILE=str(profile/'home'), HOME=str(profile/'home'),
        APPDATA=str(profile/'roaming'), LOCALAPPDATA=str(profile/'local'),
        TEMP=str(profile/'tmp'), TMP=str(profile/'tmp'),
        COPILOT_HOME=str(profile/'home'/'copilot'), COPILOT_DISABLE_KEYTAR='1',
        NO_PROXY='127.0.0.1,localhost', PATH=str(Path(os.environ['SYSTEMROOT'])/'System32'))
    return result

def verify():
    if hashlib.sha256(EXE.read_bytes()).hexdigest() != EXE_HASH:
        raise RuntimeError('Copilot executable pin mismatch')

def session_config(profile, endpoint='http://127.0.0.1:9/v1'):
    return dict(model='gpt-4.1', workingDirectory=str(profile/'work'),
        provider={'type':'openai','baseUrl':endpoint,'wireApi':'completions'},
        availableTools=['fixture_read'], excludedTools=[], toolFilterPrecedence='excluded',
        tools=[{'name':'fixture_read','description':'Synthetic fixture only',
            'parameters':{'type':'object','properties':{},'additionalProperties':False},
            'skipPermission':True}],
        systemMessage={'mode':'replace','content':'Synthetic local tool isolation check.'},
        requestPermission=True, requestUserInput=False, hooks=False, streaming=False,
        mcpServers={}, customAgents=[], customAgentsLocalOnly=True,
        configDir=str(profile/'home'/'copilot'), enableConfigDiscovery=False,
        enableSessionTelemetry=False, enableCitations=False, enableFileChangeTracking=False,
        enableOnDemandInstructionDiscovery=False, enableFileHooks=False,
        enableHostGitOperations=False, enableSessionStore=False, enableGitHubCacheStore=False,
        enableSkills=False, skillDirectories=[], agentDirectories=[], hookDirectories=[],
        pluginDirectories=[], instructionDirectories=[], skipEmbeddingRetrieval=True,
        embeddingCacheStorage='in-memory', mcpOAuthTokenStorage='in-memory',
        memory={'enabled':False}, infiniteSessions={'enabled':False},
        isExperimentalMode=False, enableManagedSettings=False)

class Runtime:
    def __init__(self, profile):
        verify()
        sys.path.insert(0, str(ROOT/'bridge'))
        from companion_process import start_owned_process
        self.queue = queue.Queue()
        self.seq = 0
        self.events = []
        self.process = start_owned_process([str(EXE), '--headless', '--no-auto-update',
            '--stdio', '--no-auto-login', '--log-level', 'error'], cwd=profile/'work',
            env=environment(profile), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, bufsize=0)
        threading.Thread(target=self._read, daemon=True).start()
    def _read(self):
        try:
            while True:
                headers = {}
                while True:
                    line = self.process.stdout.readline()
                    if not line: return
                    if line == b'\r\n': break
                    key, value = line.decode('ascii').strip().split(':', 1)
                    headers[key.lower()] = value.strip()
                n = int(headers['content-length'])
                if not 0 < n <= 2_000_000: raise RuntimeError('Frame too large')
                chunks = bytearray()
                while len(chunks) < n:
                    part = self.process.stdout.read(n-len(chunks))
                    if not part: raise EOFError()
                    chunks.extend(part)
                self.queue.put(json.loads(chunks))
        except Exception as exc:
            self.queue.put({'error_type':type(exc).__name__})
        finally:
            self.queue.put(None)
    def send(self, value):
        body = json.dumps(value,separators=(',',':')).encode()
        self.process.stdin.write(f'Content-Length: {len(body)}\r\n\r\n'.encode()+body)
        self.process.stdin.flush()
    def call(self, method, params=None, timeout=20):
        self.seq += 1
        ident = self.seq
        self.send({'jsonrpc':'2.0','id':ident,'method':method,'params':params or {}})
        deadline = time.monotonic()+timeout
        while True:
            result = self.queue.get(timeout=max(0.01,deadline-time.monotonic()))
            if result is None: raise RuntimeError('Runtime exited')
            if result.get('id') == ident and 'method' not in result:
                if 'error' in result: raise RuntimeError(json.dumps(result['error']))
                return result.get('result')
            self.events.append(result)
            if result.get('method') == 'session.event':
                params=result.get('params',{})
                event=params.get('event',{})
                data=event.get('data',{})
                if event.get('type') == 'external_tool.requested':
                    self.seq+=1
                    allowed=data.get('toolName') == 'fixture_read' and data.get('arguments') == {}
                    self.send({'jsonrpc':'2.0','id':self.seq,'method':'session.tools.handlePendingToolCall',
                        'params':{'sessionId':params['sessionId'],'requestId':data['requestId'],
                            **({'result':'{"fixture_value":42}'} if allowed else {'error':'Rejected synthetic tool'})}})
                if event.get('type') == 'permission.requested':
                    self.seq+=1
                    self.send({'jsonrpc':'2.0','id':self.seq,'method':'session.permissions.handlePendingPermissionRequest',
                        'params':{'sessionId':params['sessionId'],'requestId':data['requestId'],
                            'result':{'kind':'denied-interactively-by-user'}}})
            if 'id' in result and 'method' in result:
                self.send({'jsonrpc':'2.0','id':result['id'],'error':{'code':-32601,'message':'Unsupported probe request'}})
    def close(self):
        self.process.terminate()
        self.process.wait(timeout=5)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['help','connect','boundary','login'])
    args = parser.parse_args()
    profile = STAGE/('account-profile' if args.action == 'login' else 'probe-profile')
    env = environment(profile)
    verify()
    if args.action == 'login':
        process=subprocess.Popen([str(EXE),'login','--device-code','--host','https://github.com'],
            env=env,cwd=profile/'work',stdout=subprocess.PIPE,stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL,encoding='utf-8')
        try:
            for line in process.stdout:
                print(line.rstrip(),flush=True)
            return process.wait(timeout=5)
        finally:
            if process.poll() is None: process.terminate()
    if args.action == 'help':
        result = subprocess.run([str(EXE),'--help'],env=env,cwd=profile/'work',
            capture_output=True,timeout=35,encoding='utf-8')
        print(result.stdout)
        print('exit:',result.returncode)
        return
    runtime = Runtime(profile)
    try:
        print(json.dumps(runtime.call('connect', {'supportedTaskKinds':[]})))
        print(json.dumps(runtime.call('ping',{'message':'synthetic-local-probe'})))
        if args.action == 'boundary':
            session = runtime.call('session.create',session_config(profile))
            sid=session['sessionId']
            print('session_created')
            runtime.call('session.options.update',{'sessionId':sid,
                'skipCustomInstructions':True,'customAgentsLocalOnly':True,
                'coauthorEnabled':False,'manageScheduleEnabled':False,
                'installedPlugins':[],'includedBuiltinSkills':[]})
            runtime.call('session.tools.initializeAndValidate',{'sessionId':sid})
            print('metadata',json.dumps(runtime.call('session.tools.getCurrentMetadata',{'sessionId':sid})))
            print('positive',json.dumps(runtime.call('session.tools.execute',{'sessionId':sid,'name':'fixture_read','arguments':{}})))
            for name in ['shell','powershell','view','create','web_fetch','web_search','task','fixture_foreign']:
                try:
                    result=runtime.call('session.tools.execute',{'sessionId':sid,'name':name,'arguments':{}})
                    print('denial',name,json.dumps(result))
                except RuntimeError as exc:
                    print('denial',name,str(exc))
            print('status',json.dumps(runtime.call('auth.getStatus')))
    finally:
        runtime.close()

if __name__ == '__main__':
    main()
