"""Read-only at-rest verification of the explicitly selected CompAI profile.

Credentials are compared only in process memory; output contains booleans/counts.
Static native-runtime files are covered by their existing exact hash manifest.
"""
import argparse
import json
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'bridge'))
from credential_store import CredentialStore
from copilot_provider import VERSION, validate_runtime


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--profile',type=Path,required=True)
    profile=parser.parse_args().profile
    if not profile.is_absolute():parser.error('profile must be absolute')
    validate_runtime(ROOT/'.test-tmp'/'copilot-runtime'/'copilot-win32-x64'/'package'/'copilot.exe',profile)
    data=CredentialStore(profile,'copilot').load()
    if not data:raise RuntimeError('No encrypted sign-in exists.')
    if not isinstance(data.get('access_token'),str) or not data['access_token']:
        raise RuntimeError('Encrypted sign-in has no supported access credential.')
    markers=[value.encode(encoding) for key in ('access_token','refresh_token')
             if isinstance(value:=data.get(key),str) for encoding in ('utf-8','utf-16-le')]
    cache=profile/'local'/'copilot'/'pkg'/'win32-x64'/VERSION
    checked=0;found=False
    for path in profile.rglob('*'):
        if path.is_symlink() or path.is_junction():raise RuntimeError('Linked profile files are unsupported.')
        if not path.is_file() or path.is_relative_to(cache):continue
        if path.stat().st_size>8*1024*1024:raise RuntimeError('Profile file exceeds verification bound.')
        content=path.read_bytes();checked+=1
        found=found or any(marker in content for marker in markers)
    print(json.dumps({'encrypted_signin_present':True,'plaintext_token_found':found,
                      'dynamic_files_checked':checked,'immutable_runtime_pin_verified':True}))


if __name__=='__main__':main()
