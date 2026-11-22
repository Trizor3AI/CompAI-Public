"""Offline native protocol/tool checks; no account or provider request."""
import json
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'bridge'))
from copilot_provider import CopilotRuntime, free_quota, session_options, CompanionFailure

def main():
    stage=ROOT/'.test-tmp'/'copilot-runtime'
    profile=stage/'probe-profile'
    runtime=CopilotRuntime(stage/'copilot-win32-x64'/'package'/'copilot.exe',profile)
    original=runtime.call
    def call(method,params=None,**kwargs):
        if method=='session.create':
            params=dict(params,model='gpt-4.1',provider={
                'type':'openai','baseUrl':'http://127.0.0.1:9/v1','wireApi':'completions'})
        return original(method,params,**kwargs)
    runtime.call=call
    # This fixture has no OAuth account. Only its explicit loopback provider is
    # allowed to bypass the production secure-auth create_session preflight.
    runtime.create_session=lambda instructions,tools:runtime.call('session.create',session_options(profile,instructions,tools))['sessionId']
    tool={'name':'fixture_read','description':'Synthetic data only',
          'inputSchema':{'type':'object','properties':{},'additionalProperties':False}}
    try:
        provider=runtime.question_provider()
        sid=provider.open('Synthetic fixture.',[tool])
        metadata=runtime.call('session.tools.getCurrentMetadata',{'sessionId':sid})
        assert [t['name'] for t in metadata['tools']]==['fixture_read']
        denied=[]
        for name in ('shell','powershell','view','create','web_fetch','web_search','task','fixture_foreign'):
            result=runtime.call('session.tools.execute',{'sessionId':sid,'name':name,'arguments':{}})
            assert result.get('resultType')=='failure'
            denied.append(name)
        options=session_options(profile,'fixture',[tool])
        assert options['enableConfigDiscovery'] is False and options['enableFileHooks'] is False
        assert options['enableSkills'] is False and options['mcpServers']=={}
        row={'remainingPercentage':100,'overageAllowedWithExhaustedQuota':False,
             'overage':0,'isUnlimitedEntitlement':False}
        good={'quotaSnapshots':{'chat':row}}
        free={'isAuthenticated':True,'copilotPlan':'free'}
        assert free_quota(free,good)['primary']['usedPercent']==0
        for status,response in [({'isAuthenticated':False},good),
            ({'isAuthenticated':True,'copilotPlan':'individual_pro'},good),
            (free,{}),(free,{'quotaSnapshots':{'chat':dict(row,remainingPercentage=0)}}),
            (free,{'quotaSnapshots':{'chat':dict(row,overageAllowedWithExhaustedQuota=True)}})]:
            try:free_quota(status,response)
            except CompanionFailure:pass
            else:raise AssertionError('Free quota gate accepted invalid input')
        report={'runtime':'1.0.98','route':'native local RPC only; no account or model request',
            'only_custom_tool':True,'denied_tools':denied,'quota_fixture_cases':6}
        (stage/'adapter-check.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
        print(json.dumps(report))
    finally:runtime.close()

if __name__=='__main__':main()
