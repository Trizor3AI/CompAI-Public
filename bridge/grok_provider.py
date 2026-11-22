"""Grok Build Free adapter with reverse MCP tools.

The signed runtime is trusted, but the model receives only the native use_tool
wrapper with exact registered schemas. Native filesystem/shell/web permissions are denied, including the
use_tool file-input shortcut. No keys, bought credits, paid plan, or fallback.
Authenticated gateway removal and a synthetic Free tool roundtrip are verified. Native
OAuth persistence is blocked; only the validated session access token is kept
under Windows DPAPI. Expired access tokens require official sign-in again.
"""
from datetime import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import queue
import re
import subprocess
import threading
import time
import uuid

from companion_process import start_owned_process, close_owned_process, ProcessContainmentError
from companion_provider import CompanionFailure, ProviderEvent, RuntimeInbox, provider_failure_kind
from streamed_answer import AnswerPreviewDecoder
from chat_attachments import provider_inputs
from credential_store import CredentialStore, CredentialStoreError
from grok_credentials import (NativeAuthGuard, valid_credential, helper_command,
                              protect_pending_credential, PENDING_CREDENTIAL_ENV)
from grok_oauth import device_authorize

VERSION = '1.0.62'
EXE_SHA256 = '86c6c766b7961597c2a327f77e43a7b73cbc44f5cee9da6dd74ba73b45ae8b68'
FRAME_LIMIT = 2_000_000
FREE_ALLOWANCE_EXHAUSTED = 'Grok Free allowance is exhausted; wait for its reset.'
PIN_PATH = Path(__file__).with_name('grok_runtime_pin.json')
NATIVE_TOOLS = [{'id':'GrokBuild:use_tool','kind':'use_tool'}]
CONFIG = '''[cli]
auto_update=false
use_leader=false
[session]
load_envrc=false
[ui]
permission_mode="dontAsk"
prompt_suggestions=false
[goal]
enabled=false
[grok_com_config]
disable_api_key_auth=true
[permission]
rules=[{action="deny",tool="read"},{action="deny",tool="edit"},{action="deny",tool="bash"},{action="deny",tool="grep"},{action="deny",tool="webfetch"},{action="deny",tool="websearch"},{action="allow",tool="mcp",pattern="compai__*"}]
'''
REQUIREMENTS = '[grok_com]\ndisable_api_key_auth=true\n'


def failure(message, kind='provider', stage='provider.request'):
    return CompanionFailure(kind,message,stage=stage)


def grok_failure_kind(error):
    # ACP 1 AuthRequired. Do not classify provider prose: it may contain secrets
    # or describe a transient network failure rather than a rejected sign-in.
    if isinstance(error,dict) and type(error.get('code')) is int and error['code']==-32000:
        return 'authentication'
    return provider_failure_kind(error)


def rpc_failure(error):
    kind=grok_failure_kind(error)
    if kind=='authentication':
        return failure('Grok requires sign-in again; use Sign in with Grok. No automatic retry.',
            kind,'provider.authentication')
    if kind=='quota':
        return failure('Grok allowance blocked the request; refresh account status. No automatic retry.',
            kind,'provider.quota')
    return failure('Grok rejected the request; no automatic retry.',kind)


def exported_token(response):
    value=response.get('result',response)
    return value.get('token') if isinstance(value,dict) else None


def require_unlinked(path):
    for part in (Path(path), *Path(path).parents):
        if part.exists() and (part.is_symlink() or part.is_junction()):
            raise failure('Linked Grok runtime/profile paths are unsupported.',stage='runtime.integrity')


def private_environment(profile):
    profile=Path(profile).absolute()
    require_unlinked(profile)
    for name in ('work','tmp','home','local','roaming'):
        target=profile/name
        require_unlinked(target)
        target.mkdir(parents=True,exist_ok=True)
    for name,content in [('config.toml',CONFIG),('requirements.toml',REQUIREMENTS)]:
        target=profile/name
        require_unlinked(target)
        if target.exists() and target.read_text(encoding='utf-8')==content:continue
        temporary=target.with_name(target.name+'.'+uuid.uuid4().hex+'.tmp')
        try:
            temporary.write_text(content,encoding='utf-8')
            os.replace(temporary,target)
        finally:temporary.unlink(missing_ok=True)
    env={k:v for k,v in os.environ.items() if k.upper() in {'SYSTEMROOT','WINDIR','COMSPEC','SYSTEMDRIVE','PATHEXT'}}
    env.update(PATH=str(Path(os.environ['SYSTEMROOT'])/'System32'),
        USERPROFILE=str(profile/'home'),HOME=str(profile/'home'),APPDATA=str(profile/'roaming'),
        LOCALAPPDATA=str(profile/'local'),TEMP=str(profile/'tmp'),TMP=str(profile/'tmp'),GROK_HOME=str(profile),
        GROK_DISABLE_AUTOUPDATER='1',GROK_MEMORY='0',GROK_SUBAGENTS='0',GROK_GOAL='0',
        GROK_WRITE_FILE='0',GROK_WEB_FETCH='0',GROK_LSP_TOOLS='0',GROK_TOOL_SEARCH='0',
        GROK_AGENT_DASHBOARD='0',GROK_TITLE_REFRESH='0',GROK_TURN_SUMMARY='0',
        GROK_SESSION_RECAP='0',GROK_TURN_TRANSIENT_RETRY='0',
        GROK_MANAGED_MCPS_ENABLED='0',GROK_MANAGED_MCP_GATEWAY_TOOLS_ENABLED='0')
    for product in ('CURSOR','CLAUDE','CODEX'):
        for feature in ('SKILLS','RULES','AGENTS','MCPS','HOOKS','SESSIONS'):
            env[f'GROK_{product}_{feature}_ENABLED']='0'
    return env


def validate_runtime(executable):
    require_unlinked(executable)
    try:
        pin=json.loads(PIN_PATH.read_text(encoding='utf-8'))
        with Path(executable).open('rb') as source:
            digest=hashlib.file_digest(source,'sha256').hexdigest()
        if digest!=EXE_SHA256 or pin.get('version')!=VERSION or pin.get('executable_sha256')!=EXE_SHA256:
            raise ValueError()
    except (OSError,ValueError,TypeError):
        raise failure('Grok runtime is missing or differs from its validated pin.',stage='runtime.integrity') from None


def free_quota(subscription,billing,topup,*,report_exhausted=False):
    if subscription.get('authenticated') is not True:
        raise failure('Sign in with a Grok Free account.','authentication','provider.authentication')
    config=billing.get('config')
    if billing.get('subscription_tier')!='Free' or not isinstance(config,dict) or not isinstance(topup,dict):
        raise failure('Grok Free entitlement is not verified; no question was sent.','quota','provider.quota')
    prepaid=config.get('prepaidBalance')
    cap=config.get('onDemandCap')
    rule=topup.get('rule')
    # Pinned protocol serializes Cent.val as an integer, including proto3 zero.
    # A missing PAYG cap is not a positive cap; disabled topup may omit enabled.
    if (not isinstance(prepaid,dict) or type(prepaid.get('val')) is not int or prepaid['val']!=0 or
        (cap is not None and (not isinstance(cap,dict) or type(cap.get('val')) is not int or cap['val']!=0)) or
        (billing.get('on_demand_enabled') is not None and billing.get('on_demand_enabled') is not False) or
        (rule is not None and (not isinstance(rule,dict) or rule.get('enabled',False) is not False))):
        raise failure('Grok free-only billing boundary is not verified; no question was sent.','quota','provider.quota')
    used=config.get('creditUsagePercent')
    if used is None:
        # The service may expose a Free entitlement without numeric allowance.
        # Let its quota rejection stop the request; never invent a percentage.
        return {}
    if type(used) not in (int,float) or not math.isfinite(used) or not 0<=used<=100:
        raise failure('Invalid Grok allowance.','quota','provider.quota')
    if used>=100 and not report_exhausted:
        raise failure(FREE_ALLOWANCE_EXHAUSTED,'quota','provider.quota')
    reset=None
    try:reset=int(datetime.fromisoformat(config['currentPeriod']['end'].replace('Z','+00:00')).timestamp())
    except (KeyError,TypeError,ValueError):pass
    return {'primary':{'usedPercent':used,'resetsAt':reset}}


def tool_instructions(tools):
    # search_tool is classified as native filesystem read by this pin. Avoid it:
    # advertise the already validated application registry directly instead.
    registry=[dict(entry, name='compai__'+entry['name']) for entry in tools]
    return ('\nReturn the final answer as exactly one JSON object with only the key "answer" '
        'and a string of at most 6000 characters. Write actual Unicode characters in prose and macro/code blocks; '
        'do not replace them with literal backslash-u escape text. JSON-encode the answer exactly once. '
        'Preserve literal escapes only when the user requests them or they belong to the requested code. '
        'Native web, filesystem, shell, subagents '
        'and external connectors are unavailable. Call registered CompAI tools through '
        'use_tool with tool_name exactly as listed below and tool_input as an inline JSON '
        'object matching inputSchema. Never pass file/path inputs to use_tool. '
        'No search_tool is available. Registered tools:\n'+json.dumps(registry,ensure_ascii=False,separators=(',',':')))


def agent_profile():
    # An empty toolConfig or legacy tools:[] inherits native tools in this pin.
    return {'name':'compai','description':'CompAI application-owned tools','promptMode':'full',
        'toolConfig':{'tools':[dict(row) for row in NATIVE_TOOLS]},'injectDefaultTools':False,'tools':['use_tool'],
        'agentsMd':False,'discoverSkills':False,'inheritSkills':False,'permissionMode':'dontAsk',
        'mcpInheritance':{'named':['compai']},'maxTurns':19}


def validate_profile(profile):
    expected=agent_profile()
    actual=profile.get('toolConfig',{}).get('tools') if isinstance(profile,dict) else None
    if (not isinstance(profile,dict) or not isinstance(actual,list) or len(actual)!=len(NATIVE_TOOLS) or
        [{key:row.get(key) for key in ('id','kind')} for row in actual]!=NATIVE_TOOLS or
        any(any(value is not None for key,value in row.items() if key not in ('id','kind')) for row in actual) or
        any(profile.get(key)!=value for key,value in expected.items() if key!='toolConfig') or
        any(profile.get(key) for key in ('skills','hooks','memory','mcpServers','disallowedTools','initialPrompt'))):
        raise failure('Unexpected Grok tool profile; question not sent.','protocol')


class GrokRuntime:
    def __init__(self,executable,profile,cancel_event=None,model='auto',effort='none',web_search=False,emit=None):
        self.executable,self.profile=Path(executable).absolute(),Path(profile).absolute()
        self.cancel_event=cancel_event or threading.Event()
        self.emit=emit or (lambda _:None)
        self.model,self.effort,self.web_search=model,effort,False
        self.process=None
        self.inbox = RuntimeInbox(frame_limit=FRAME_LIMIT, label='Grok')
        self.seq=0
        self.write_lock=threading.Lock()
        self.initialized={}
        self.server_id=None
        self.sid=None
        self.tools={}
        self.pending_tools={}
        self.active=False
        self.auth_guard=None
        self.interactive_login=False
        self.pending_credential=None
        self.authenticated=False
        self.credential_hash=None
        validate_runtime(self.executable)
        self.environment=private_environment(self.profile)
        self.credentials=CredentialStore(self.profile,'grok')

    def _command(self,*args):return [str(self.executable),'--no-auto-update',*args]

    def _inspect(self):
        process=start_owned_process(self._command('inspect','--json'),cwd=self.profile/'work',env=self.environment,
            stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL)
        try:
            output,_=process.communicate(timeout=20)
            if process.returncode!=0 or len(output)>FRAME_LIMIT:raise ValueError()
            result=json.loads(output)
            if (result.get('grokVersion')!=VERSION or
                any(result.get(key) for key in ('projectInstructions','hooks','plugins','marketplaces','mcpServers','lspServers')) or
                any(row.get('source',{}).get('type')!='bundled' for row in result.get('skills',[])) or
                result.get('permissions',{}).get('skipped') or
                result.get('permissions',{}).get('loaded')!=7 or
                result.get('loginPolicy',{}).get('apiKeyAuthDisabled') is not True or
                result.get('externalCompat',{}).get('remoteSettingsLoaded') is not False or
                any(row.get('enabled') is not False for row in result.get('externalCompat',{}).get('cells',[]))):
                raise ValueError()
            # No additional project/team/system configuration may augment ours.
            layers=result.get('configSources',{}).get('layers')
            if (not isinstance(layers,list) or len(layers)!=2 or
                {(row.get('role'),str(Path(row.get('path','')).absolute()).casefold()) for row in layers} !=
                {('user',str(self.profile/'config.toml').casefold()),('requirements',str(self.profile/'requirements.toml').casefold())}):raise ValueError()
            self.inspected_layers=layers
        except (ValueError,KeyError,TypeError,subprocess.TimeoutExpired):
            raise failure('Unexpected Grok settings or capabilities; runtime blocked.',stage='runtime.integrity') from None
        finally:close_owned_process(process)

    def _preflight_pending_helper(self):
        # Exercise the same fixed shell-safe command and protected environment
        # as the native helper. Never print its captured session-token output.
        process=None
        try:
            command=self.environment['GROK_AUTH_PROVIDER_COMMAND']
            shell=Path(self.environment['SYSTEMROOT'])/'System32'/'cmd.exe'
            process=start_owned_process([str(shell),'/d','/c',command],cwd=self.profile/'work',
                env=self.environment,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL)
            output,_=process.communicate(timeout=10)
            if process.returncode!=0 or len(output)>65536:raise ValueError()
            result=json.loads(output)
            if (type(result) is not dict or set(result)!={'access_token','issuer','expires_in'} or
                    result.get('issuer')!='https://auth.x.ai' or
                    type(result.get('expires_in')) is not int or not 1<=result['expires_in']<=366*86400 or
                    type(result.get('access_token')) is not str or
                    hashlib.sha256(result['access_token'].encode()).hexdigest()!=self.credential_hash):
                raise ValueError()
        except (OSError,ValueError,TypeError,KeyError,subprocess.TimeoutExpired,ProcessContainmentError):
            raise failure('Grok secure sign-in helper could not be verified; no credential was saved.',
                'authentication','provider.authentication') from None
        finally:
            if process is not None:close_owned_process(process)

    def _start(self):
        if self.process is not None:return
        validate_runtime(self.executable)
        try:
            if (self.profile/'auth.json').exists():
                raise CredentialStoreError('Legacy Grok sign-in must be removed before secure login.')
            self.auth_guard=NativeAuthGuard(self.profile)
            self.environment['GROK_AUTH_PATH']=str(self.auth_guard.auth_path)
            self.environment.pop('GROK_AUTH',None)
            self.environment.pop('GROK_LOGIN_DEVICE_FLOW',None)
            if self.interactive_login:
                # Only the newly authorized token may enter this runtime. The
                # helper receives DPAPI ciphertext, never a plaintext env token.
                pending=valid_credential(self.pending_credential)
                if pending is None:raise CredentialStoreError('Grok pending sign-in is unavailable.')
                self.environment['GROK_AUTH_PROVIDER_COMMAND']=helper_command(self.profile,pending=True)
                self.environment[PENDING_CREDENTIAL_ENV]=protect_pending_credential(pending)
                self.credential_hash=hashlib.sha256(pending['access_token'].encode()).hexdigest()
                self.environment['COMPAI_GROK_SESSION_SHA256']=self.credential_hash
            else:
                self.environment.pop(PENDING_CREDENTIAL_ENV,None)
                self.environment['GROK_AUTH_PROVIDER_COMMAND']=helper_command(self.profile)
                stored=valid_credential(self.credentials.load())
                self.credential_hash=hashlib.sha256(stored['access_token'].encode()).hexdigest() if stored else None
                self.environment['COMPAI_GROK_SESSION_SHA256']=self.credential_hash or ''
        except CredentialStoreError as exc:
            self.close();raise failure(str(exc),'authentication',stage='provider.authentication') from None
        if self.interactive_login:self._preflight_pending_helper()
        self._inspect()
        try:
            self.process=start_owned_process(self._command('--disable-web-search','--no-subagents','--no-plan',
                '--permission-mode','dontAsk','agent','--no-leader','stdio'),cwd=self.profile/'work',env=self.environment,
                stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,bufsize=0)
        except (OSError,ProcessContainmentError):
            raise failure('Grok runtime could not start inside its owned process boundary.',stage='runtime.containment') from None
        threading.Thread(target=self._read,args=(self.process,self.inbox),daemon=True).start()
        self.initialized=self.call('initialize',{'protocolVersion':1,'clientCapabilities':{
            'fs':{'readTextFile':False,'writeTextFile':False},'terminal':False}})
        meta=self.initialized.get('_meta',{})
        if self.initialized.get('protocolVersion')!=1 or meta.get('agentVersion')!=VERSION or meta.get('x.ai/mcp/sdk') is not True or meta.get('mcpServers'):
            self.close();raise failure('Unexpected Grok protocol or inherited tools.','protocol')

    def _read(self,process,inbox):
        try:
            while True:
                line=process.stdout.readline(FRAME_LIMIT+1)
                if not line:
                    return
                if not line.endswith(b'\n') and len(line) <= FRAME_LIMIT:
                    inbox.finish('Invalid Grok protocol message; no automatic retry')
                    return
                if not inbox.feed(line):
                    return
        except (OSError, ValueError):
            inbox.finish('Grok runtime pipe could not be read; no automatic retry')
        finally:
            inbox.finish()

    def send(self,message):
        data=json.dumps(message,ensure_ascii=False,separators=(',',':')).encode()+b'\n'
        if len(data)>FRAME_LIMIT:raise failure('Grok request is too large.','protocol')
        with self.write_lock:
            self.inbox.require_writable()
            self.process.stdin.write(data);self.process.stdin.flush()

    def receive(self, timeout=1, ignore_cancel=False, *, response_id=None):
        if not ignore_cancel and self.cancel_event.is_set():
            raise InterruptedError()
        return self.inbox.get(response_id, timeout=timeout, include_requests=response_id is not None)

    def _reply_mcp(self,outer,inner,result=None,error=None):
        answer={'jsonrpc':'2.0','id':inner}
        answer.update({'error':error} if error else {'result':result or {}})
        self.send({'jsonrpc':'2.0','id':outer,'result':answer})

    def _reverse(self,message,allow_tools=False):
        method=message.get('method')
        if method!='_x.ai/mcp/sdk_call':
            if method=='session/request_permission':
                self.send({'jsonrpc':'2.0','id':message['id'],'result':{'outcome':{'outcome':'cancelled'}}})
            else:self.send({'jsonrpc':'2.0','id':message['id'],'error':{'code':-32601,'message':'CompAI capability disabled'}})
            if self.active:raise failure('Grok requested a disabled capability; question stopped.','protocol')
            return ProviderEvent()
        params=message.get('params',{})
        inner=params.get('message',{})
        if params.get('serverId')!=self.server_id or not self.server_id or not isinstance(inner,dict):
            self.send({'jsonrpc':'2.0','id':message['id'],'error':{'code':-32601,'message':'Unknown companion server'}})
            raise failure('Unexpected Grok reverse tool server.','protocol')
        method=inner.get('method')
        if method=='initialize':
            result={'protocolVersion':'2024-11-05','capabilities':{'tools':{}},'serverInfo':{'name':'compai','version':'1'}}
        elif method=='tools/list':result={'tools':list(self.tools.values())}
        elif method=='notifications/initialized':result={}
        elif method=='tools/call' and allow_tools and self.active:
            args=inner.get('params',{})
            name=args.get('name')
            if name not in self.tools or not isinstance(args.get('arguments'),dict):
                self._reply_mcp(message['id'],inner.get('id'),error={'code':-32602,'message':'Unregistered companion tool'})
                raise failure('Unexpected Grok tool call.','protocol')
            if message['id'] in self.pending_tools:raise failure('Duplicate Grok tool request.','protocol')
            self.pending_tools[message['id']]=inner.get('id')
            return ProviderEvent(kind='tool',request_id=message['id'],is_request=True,strictly_correlated=True,
                tool=name,arguments=args['arguments'])
        else:
            self._reply_mcp(message['id'],inner.get('id'),error={'code':-32601,'message':'Unsupported companion MCP request'})
            if self.active or method=='tools/call':raise failure('Grok requested a disabled MCP operation.','protocol')
            return ProviderEvent()
        self._reply_mcp(message['id'],inner.get('id'),result)
        return ProviderEvent()

    def call(self,method,params=None,timeout=30,ignore_cancel=False):
        if self.process is None:self._start()
        self.seq+=1;ident=self.seq
        self.send({'jsonrpc':'2.0','id':ident,'method':method,'params':params or {}})
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                message = self.receive(min(1, max(.01, deadline-time.monotonic())), ignore_cancel,
                                       response_id=ident)
            except queue.Empty:
                continue
            if 'method' in message:
                self._reverse(message)
                continue
            if 'error' in message:
                raise rpc_failure(message['error'])
            result = message.get('result', {})
            if not isinstance(result, dict):
                raise failure('Invalid Grok response.', 'protocol')
            return result
        raise TimeoutError('Grok request timed out; no automatic retry.')

    def _subscription(self):
        self._start()
        if not self.authenticated:
            try:stored=valid_credential(self.credentials.load())
            except CredentialStoreError as exc:raise failure(str(exc),'authentication',stage='provider.authentication') from None
            if stored is None:return {'authenticated':False}
            if hashlib.sha256(stored['access_token'].encode()).hexdigest()!=self.credential_hash:
                raise failure('Grok account changed; refresh account status before sending.','authentication','provider.authentication')
            # The fixed helper supplies only a DPAPI-unlocked session token.
            # No API keys or user commands are supplied. Upstream can still open
            # its browser if the fixed helper fails; that is not a new account
            # unless the exact imported token passes the checks below.
            try:self.call('authenticate',{'methodId':'grok.com','_meta':{'headless':True}})
            except CompanionFailure as exc:
                if exc.kind!='authentication':raise
                # Native update retains memory but reports the intentionally
                # blocked disk write. Require our exact imported token before
                # using the documented cached-token auth path, which validates
                # the account and performs no credential persistence.
                token=exported_token(self.call('_x.ai/auth/getBearerToken'))
                if not isinstance(token,str) or hashlib.sha256(token.encode()).hexdigest()!=self.credential_hash:
                    raise failure('Grok saved sign-in could not be restored; sign in again.',
                        'authentication','provider.authentication') from None
                self.call('authenticate',{'methodId':'cached_token','_meta':{'headless':True}})
            self.authenticated=True
        return self.call('_x.ai/auth/check_subscription')

    def models(self):
        self._start()
        state=self.call('_x.ai/models/list').get('result',{})
        if not isinstance(state,dict) or not isinstance(state.get('availableModels'),list):
            raise failure('Unexpected Grok model catalog.','protocol')
        self.model_state=state
        rows=[{'id':'auto','name':'Grok Auto','efforts':['none'],'default_effort':'none','supports_images':False}]
        for row in state.get('availableModels',[]):
            ident=row.get('modelId');meta=row.get('_meta',{})
            if not isinstance(ident,str) or not re.fullmatch(r'grok-[0-9]+\.[0-9]+',ident):continue
            efforts=[x.get('id') for x in meta.get('reasoningEfforts',[]) if x.get('id') in ('low','medium','high','xhigh')]
            default=meta.get('reasoningEffort')
            if not efforts or default not in efforts:continue
            rows.append({'id':ident,'name':str(row.get('name',ident))[:80],'efforts':efforts,'default_effort':default,'supports_images':False})
        return rows
    available_models=models
    def question_provider(self):return self

    def readiness(self):
        return free_quota(self._subscription(),self.call('_x.ai/billing'),self.call('_x.ai/auto-topup-rule'))

    def status(self):
        subscription=self._subscription()
        result={'provider':'grok','runtime_pin':'verified','account':{'type':'grok' if subscription.get('authenticated') is True else None},
            'quota_guard':'blocked','models':self.models()}
        if subscription.get('authenticated') is True:
            billing=self.call('_x.ai/billing')
            result['account']['planType']=billing.get('subscription_tier')
            try:
                # Status must retain the observed percentage/reset even when sending is blocked.
                result['quota']=free_quota(subscription,billing,self.call('_x.ai/auto-topup-rule'),report_exhausted=True)
                exhausted=result['quota'].get('primary',{}).get('usedPercent',0)>=100
                result['quota_guard']='blocked' if exhausted else 'available'
                if exhausted:result['quota_block_reason']=FREE_ALLOWANCE_EXHAUSTED
            except CompanionFailure as exc:
                if exc.kind!='quota':raise
                result['quota_block_reason']=str(exc)
        return result

    def open(self, instructions, tools):
        sid, _ = self.open_with_quota(instructions, tools)
        return sid

    def open_with_quota(self, instructions, tools):
        quota = self.readiness()
        if self.sid is not None:raise failure('A Grok runtime accepts one isolated question session.','protocol')
        names=[entry['name'] for entry in tools]
        if len(names)!=len(set(names)) or any(not re.fullmatch(r'[a-z][a-z0-9_]{0,63}',name) for name in names):
            raise failure('Invalid companion tool registry.','protocol')
        self.tools={entry['name']:{key:entry[key] for key in ('name','description','inputSchema')} for entry in tools}
        self.server_id='compai-'+uuid.uuid4().hex
        models=self.models()
        selected=next((row for row in models if row['id']==self.model),None)
        if selected is None or self.effort not in selected['efforts']:raise failure('Unsupported Grok model/reasoning selection.','protocol')
        effective=self.model_state.get('currentModelId') if self.model=='auto' else self.model
        if effective not in {row['id'] for row in models if row['id']!='auto'}:raise failure('Unexpected Grok default model.','protocol')
        profile=agent_profile()
        profile['model']=effective
        meta={'sessionKind':'headless','agentProfile':profile,'modelId':effective,
            'systemPromptOverride':instructions+tool_instructions(self.tools.values()),
            'x.ai/mcp/servers':[{'name':'compai','serverId':self.server_id}]}
        if self.effort!='none':meta['reasoningEffort']=self.effort
        session=self.call('session/new',{'cwd':str(self.profile/'work'),'mcpServers':[],'_meta':meta})
        self.sid=session.get('sessionId')
        if not isinstance(self.sid,str) or not 1<=len(self.sid)<=128:raise failure('Invalid Grok session.','protocol')
        state=self.call('_x.ai/session/state',{'sessionId':self.sid,'cwd':str(self.profile/'work')})
        validate_profile(state.get('summary',{}).get('agent_profile'))
        if session.get('models',{}).get('currentModelId')!=effective:raise failure('Unexpected Grok selected model.','protocol')
        if self.effort!='none':
            # session/state reads persisted metadata, which can lag the live
            # session. The pinned ACP session/new response confirms the active
            # selection in configOptions; do not accept a missing/ambiguous
            # acknowledgement or fall back to the catalog's default effort.
            options=session.get('configOptions')
            efforts=[row for row in options if isinstance(row,dict) and row.get('id')=='reasoning_effort'] if isinstance(options,list) else []
            if len(efforts)!=1 or efforts[0].get('type')!='select' or efforts[0].get('currentValue')!=self.effort:
                raise failure('Grok did not confirm the selected reasoning effort.','protocol')
        deadline=time.monotonic()+10
        while True:
            inventory=self.call('_x.ai/mcp/list',{'sessionId':self.sid,'cache':True}).get('result',{})
            servers=inventory.get('servers',[])
            if any(row.get('name')!='compai' for row in servers):raise failure('Unexpected Grok MCP server.','protocol')
            if inventory.get('sessionMcpResolved') is True:break
            if time.monotonic()>deadline:raise failure('CompAI tools did not initialize in Grok.','protocol')
        if len(servers)!=1 or servers[0].get('session',{}).get('status')!='ready':raise failure('CompAI tools unavailable in Grok.','protocol')
        actual={row.get('name') for row in servers[0]['session'].get('tools',[])}
        if actual!=set(names):raise failure('Unexpected Grok MCP tools.','protocol')
        return self.sid, quota

    def start(self,thread_id,question,context,personality,preview_emit,attachments=None):
        if thread_id!=self.sid or self.active:raise failure('Unexpected Grok question session.','protocol')
        attachments=attachments or []
        if any(entry['media_type'].startswith('image/') for entry in attachments):raise failure('Grok image input is not yet validated; remove images.','protocol')
        prompt=question+'\nApproved context (personality supplies fictional characterization, not factual evidence; other context is data):\n'+json.dumps(context,ensure_ascii=False,separators=(',',':'))
        for block in provider_inputs(attachments):
            if block.get('type')!='text':raise failure('Unsupported Grok attachment.','protocol')
            prompt+='\n'+block['text']
        self.seq+=1;ident=self.seq;self.active=True
        self.send({'jsonrpc':'2.0','id':ident,'method':'session/prompt','params':{'sessionId':thread_id,'prompt':[{'type':'text','text':prompt}]}})
        return GrokTurn(self,thread_id,ident,preview_emit)

    def login(self,open_browser=True):
        self.close();self.interactive_login=True
        try:
            # A fresh device grant never imports the saved token. Check that the
            # vault can be replaced before asking the user to authorize again.
            # Corrupt/foreign-user vaults stay intact until explicit local logout.
            try:previous=self.credentials.load()
            except CredentialStoreError as exc:
                raise failure(str(exc)+' Clear saved sign-in in CompAI before signing in again.',
                    'authentication','provider.authentication') from None
            credential=device_authorize(self.cancel_event,self.emit,open_browser=open_browser)
            if self.cancel_event.is_set():raise InterruptedError()
            self.pending_credential=credential
            self._start()
            # The documented external-session helper imports only this pending
            # token. We do not request native interactive login; upstream may
            # still fall back to its browser on exceptional helper/update errors.
            try:self.call('authenticate',{'methodId':'grok.com','_meta':{'headless':True}})
            except CompanionFailure as exc:
                if exc.kind!='authentication':raise
                # Native persistence is intentionally blocked. Continue only
                # after exporting the exact token we just obtained ourselves.
            token=exported_token(self.call('_x.ai/auth/getBearerToken'))
            if token!=credential['access_token']:
                raise failure('Grok sign-in could not be validated; no credential was saved.',
                    'authentication','provider.authentication')
            self.call('authenticate',{'methodId':'cached_token','_meta':{'headless':True}})
            if self.call('_x.ai/auth/check_subscription').get('authenticated') is not True:
                raise failure('Grok sign-in could not be validated; no credential was saved.','authentication')
            try:
                with self.credentials.locked():
                    if self.cancel_event.is_set():raise InterruptedError()
                    if self.credentials.load()!=previous:
                        raise failure('Grok saved sign-in changed; sign in again.',
                            'authentication','provider.authentication')
                    if self.cancel_event.is_set():raise InterruptedError()
                    self.credentials.save(credential)
            except CredentialStoreError as exc:raise failure(str(exc),'authentication',stage='provider.authentication') from None
            self.emit({'provider':'grok','login':'completed'})
        finally:
            self.close();self.interactive_login=False

    def logout(self):
        self.close()
        try:self.credentials.delete()
        except CredentialStoreError as exc:raise failure(str(exc),'authentication',stage='provider.authentication') from None
        result={'provider':'grok','logout':'completed'}
        self.emit(result)
        return result

    def close(self):
        process,self.process=self.process,None
        if process is not None:close_owned_process(process)
        guard,self.auth_guard=self.auth_guard,None
        if guard is not None:guard.close()
        self.pending_credential=None
        self.environment.pop(PENDING_CREDENTIAL_ENV,None)
        self.inbox.finish('Grok runtime closed')
        self.inbox = RuntimeInbox(frame_limit=FRAME_LIMIT, label='Grok')
        self.sid=None;self.server_id=None;self.active=False;self.authenticated=False;self.pending_tools.clear()


class GrokTurn:
    def __init__(self,runtime,sid,ident,emit):
        self.runtime,self.sid,self.ident,self.emit=runtime,sid,ident,emit
        self.decoder=AnswerPreviewDecoder();self.text='';self.last_emit=0
        self.completed=None;self.cancelled=False

    def read(self,timeout):
        if self.completed is not None:
            event,self.completed=self.completed,None
            return event
        try:message=self.runtime.receive(timeout)
        except queue.Empty:raise TimeoutError() from None
        if 'method' in message and 'id' in message:return self.runtime._reverse(message,allow_tools=True)
        if message.get('id')==self.ident and 'method' not in message:
            self.runtime.active=False
            if 'error' in message:return ProviderEvent(kind='completed',status='failed',strictly_correlated=True,
                failure_kind=grok_failure_kind(message['error']))
            result=message.get('result',{})
            if result.get('stopReason')!='end_turn':return ProviderEvent(kind='completed',status='cancelled' if self.cancelled else 'failed',strictly_correlated=True)
            self.completed=ProviderEvent(kind='completed',status='completed',strictly_correlated=True)
            content=self.text
            try:parsed=json.loads(content)
            except ValueError:parsed=None
            if not isinstance(parsed,dict):
                if not content.strip() or len(content)>6000:raise failure('Grok returned an invalid answer length.','protocol')
                content=json.dumps({'answer':content},ensure_ascii=False)
            return ProviderEvent(kind='answer',text=content,strictly_correlated=True)
        params=message.get('params',{})
        if not isinstance(params,dict) or params.get('sessionId')!=self.sid:return ProviderEvent(correlated=False)
        update=params.get('update',{})
        if message.get('method')=='session/update' and update.get('sessionUpdate')=='agent_message_chunk':
            chunk=update.get('content',{}).get('text')
            if not isinstance(chunk,str):return ProviderEvent()
            self.text+=chunk
            if len(self.text)>73000:raise failure('Grok answer exceeded its bound.','protocol')
            try:
                preview=self.decoder.feed(chunk)
                if time.monotonic()-self.last_emit>=.1:
                    self.emit({'question_partial':preview});self.last_emit=time.monotonic()
            except ValueError:pass
        return ProviderEvent()

    def reply_tool(self,event,success,result):
        if event.request_id not in self.runtime.pending_tools:raise failure('Unknown Grok tool reply.','protocol')
        inner=self.runtime.pending_tools.pop(event.request_id)
        self.runtime._reply_mcp(event.request_id,inner,{'content':[{'type':'text','text':json.dumps(result,ensure_ascii=False,separators=(',',':'))}], 'isError':not success})
        # Intervening assistant prose before a tool is not the final response.
        self.text='';self.decoder=AnswerPreviewDecoder()
    def reject_tool(self,event,label):self.reply_tool(event,False,{'error':label+' unavailable or rejected.'})
    def reject_request(self,event):
        self.runtime.send({'jsonrpc':'2.0','id':event.request_id,'error':{'code':-32601,'message':'CompAI capability disabled'}})
    def cancel(self):
        if not self.cancelled:
            self.cancelled=True
            self.runtime.send({'jsonrpc':'2.0','method':'session/cancel','params':{'sessionId':self.sid}})
    def finish(self):self.emit({'question_partial':''})
