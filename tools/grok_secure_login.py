"""Explicit official login into CompAI's DPAPI-only Grok profile; no inference."""
import json
import os
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'bridge'))
from grok_provider import GrokRuntime,require_unlinked

def plugin_profile():
    parent=Path(os.environ['APPDATA'])/'XIVLauncher/pluginConfigs'
    settings=parent/'CompAI'
    marker=settings/'.legacy-inspector-profile'
    require_unlinked(marker)
    if marker.exists():
        if marker.stat().st_size>256 or marker.read_text(encoding='utf-8')!='CompAI uses the existing Inspector companion profile.\n':
            raise RuntimeError('Invalid plugin storage migration marker.')
        settings=parent/'Inspector'
    return settings/'companion/providers/grok'

def main():
    executable=Path(__file__).resolve().parents[1]/'.test-tmp/grok-runtime/grok.exe'
    runtime=GrokRuntime(executable,plugin_profile(),emit=lambda event:print(json.dumps(event),flush=True))
    try:runtime.login()
    finally:runtime.close()

if __name__=='__main__':main()
