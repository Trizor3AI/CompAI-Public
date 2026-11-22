"""Offline checks for Grok's billing gate and reverse-tool protocol boundary."""
from copy import deepcopy
from pathlib import Path
import json
import queue
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'bridge'))
from companion_provider import CompanionFailure
from grok_provider import GrokRuntime,GrokTurn,agent_profile,free_quota,validate_profile

def rejected(callback):
    try:callback()
    except (CompanionFailure,ValueError):return
    raise AssertionError('Expected rejection')

def main():
    account={'authenticated':True}
    billing={'subscription_tier':'Free','config':{'prepaidBalance':{'val':0}}}
    assert free_quota(account,billing,{'rule':None})=={}
    for changed in [
        {'subscription_tier':'SuperGrok'},
        {'config':{'prepaidBalance':{'val':1}}},
        {'config':{'prepaidBalance':{'val':0},'onDemandCap':{'val':1}}},
        {'config':{'prepaidBalance':{'val':0},'creditUsagePercent':100}},
        {'config':{}}, {'on_demand_enabled':True}, {'on_demand_enabled':0}]:
        rejected(lambda changed=changed:free_quota(account,billing|changed,{}))
    rejected(lambda:free_quota(account,billing,{'rule':{'enabled':True}}))
    rejected(lambda:free_quota({},billing,{}))
    profile=agent_profile()
    for row in profile['toolConfig']['tools']:
        row.update(params=None,name_override=None,params_name_overrides=None)
    validate_profile(profile)
    injected=deepcopy(profile);injected['tools'].append('run_terminal_command')
    rejected(lambda:validate_profile(injected))
    injected=deepcopy(profile);injected['hooks']={'stop':'bad'}
    rejected(lambda:validate_profile(injected))
    client=object.__new__(GrokRuntime)
    client.server_id='synthetic';client.active=True;client.pending_tools={}
    client.tools={'probe':{'name':'probe','description':'Synthetic','inputSchema':{'type':'object'}}}
    sent=[];client.send=sent.append
    def call(name,args=None,server='synthetic'):
        return {'id':1,'method':'_x.ai/mcp/sdk_call','params':{'serverId':server,
            'message':{'jsonrpc':'2.0','id':2,'method':'tools/call','params':{'name':name,'arguments':args or {}}}}}
    rejected(lambda:client._reverse(call('unknown'),True))
    rejected(lambda:client._reverse(call('probe',server='other'),True))
    rejected(lambda:client._reverse({'id':3,'method':'fs/read_text_file','params':{}},True))
    rejected(lambda:client._reverse({'id':3,'method':'session/request_permission','params':{}},True))
    event=client._reverse(call('probe',{'value':7}),True)
    assert event.tool=='probe' and event.strictly_correlated and event.arguments=={'value':7}
    turn=GrokTurn(client,'sid',5,lambda _:None)
    turn.reply_tool(event,True,{'value':14})
    assert sent[-1]['result']['id']==2 and json.loads(sent[-1]['result']['result']['content'][0]['text'])=={'value':14}
    client.receive=lambda _: (_ for _ in ()).throw(queue.Empty())
    try:turn.read(.01)
    except TimeoutError:pass
    else:raise AssertionError('Quiet inference must map to TimeoutError')
    events=iter([{'method':'session/update','params':{'sessionId':'sid','update':{
        'sessionUpdate':'agent_message_chunk','content':{'text':'{"answer":"14"}'}}}},
        {'id':5,'result':{'stopReason':'end_turn'}}])
    client.receive=lambda _:next(events)
    turn.read(1)
    assert turn.read(1).text=='{"answer":"14"}'
    assert turn.read(1).status=='completed'
    print('Grok offline billing and protocol boundary checks passed; no account/network/model use.')

if __name__=='__main__':main()
