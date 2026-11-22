"""Isolated Grok Build runtime checks. No inference or game data is accepted.

The executable is deliberately not distributed with CompAI. Download the exact
official Windows artifact into .test-tmp/grok-runtime before using this probe.
"""
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

VERSION = "1.0.62"
SHA256 = "86c6c766b7961597c2a327f77e43a7b73cbc44f5cee9da6dd74ba73b45ae8b68"
SOURCE = "https://x.ai/cli/grok-1.0.62-windows-x86_64.exe"
ROOT = Path(__file__).resolve().parents[1] / ".test-tmp" / "grok-runtime"


def environment(root: Path = ROOT) -> dict[str, str]:
    # Build a fresh environment: never inherit API keys, auth-provider commands,
    # proxies, endpoint/model overrides, plugins, or arbitrary parent settings.
    env = {k: os.environ[k] for k in ("SYSTEMROOT", "WINDIR") if k in os.environ}
    if "SYSTEMROOT" not in env:
        raise RuntimeError("This pinned probe supports Windows only.")
    env.update(
        PATH=str(Path(env["SYSTEMROOT"]) / "System32"),
        TEMP=str(root / "temp"), TMP=str(root / "temp"),
        GROK_HOME=str(root / "profile"), GROK_DISABLE_AUTOUPDATER="1",
        GROK_MEMORY="0", GROK_SUBAGENTS="0", GROK_GOAL="0",
        GROK_WRITE_FILE="0", GROK_WEB_FETCH="0", GROK_LSP_TOOLS="0",
        GROK_TOOL_SEARCH="0", GROK_AGENT_DASHBOARD="0",
    )
    for product in ("CURSOR", "CLAUDE", "CODEX"):
        for feature in ("SKILLS", "RULES", "AGENTS", "MCPS", "HOOKS", "SESSIONS"):
            env[f"GROK_{product}_{feature}_ENABLED"] = "0"
    return env


def executable(root: Path = ROOT) -> Path:
    path = root / "grok.exe"
    if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != SHA256:
        raise RuntimeError("Pinned official Grok executable missing or changed; no launch permitted.")
    return path


def command(*args: str, root: Path = ROOT) -> list[str]:
    return [str(executable(root)), "--no-auto-update", *args]


class AcpProbe:
    """Short-lived ACP client. Reject every server-initiated capability request."""

    def __init__(self):
        self.process = subprocess.Popen(command("--disable-web-search", "--no-subagents",
            "--no-plan", "--permission-mode", "dontAsk", "agent", "--no-leader", "stdio"),
            cwd=ROOT / "workspace", env=environment(), stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf8")
        self.lines = queue.Queue()
        self.next_id = 0
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        for line in self.process.stdout:
            if len(line) > 1_000_000:
                self.lines.put(RuntimeError("Oversized ACP message"))
                return
            self.lines.put(json.loads(line))
        self.lines.put(RuntimeError("ACP closed"))

    def request(self, method, params, timeout=25):
        self.next_id += 1
        request_id = self.next_id
        self._write({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            item = self.lines.get(timeout=max(.01, deadline - time.monotonic()))
            if isinstance(item, Exception):
                raise item
            if "method" in item and "id" in item:
                if item["method"] == "session/request_permission":
                    self._write({"jsonrpc": "2.0", "id": item["id"],
                        "result": {"outcome": {"outcome": "cancelled"}}})
                else:
                    self._write({"jsonrpc": "2.0", "id": item["id"],
                        "error": {"code": -32601, "message": "Capability disabled"}})
            elif item.get("id") == request_id:
                if "error" in item:
                    # Do not print server error bodies: they may contain account data.
                    raise RuntimeError(f"{method} failed (code={item['error'].get('code')})")
                return item.get("result", {})
        raise RuntimeError("ACP request timed out")

    def _write(self, message):
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def close(self):
        self.process.terminate()
        self.process.wait(timeout=5)


def account_status():
    client = AcpProbe()
    try:
        initialized = client.request("initialize", {"protocolVersion": 1,
            "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False},
                                   "terminal": False}})
        methods = [m["id"] for m in initialized.get("authMethods", [])]
        if "cached_token" not in methods:
            return {"authenticated": False, "inference_permitted": False}
        client.request("authenticate", {"methodId": "cached_token", "_meta": {"headless": True}})
        subscription = client.request("_x.ai/auth/check_subscription", {})
        billing = client.request("_x.ai/billing", {})
        topup = client.request("_x.ai/auto-topup-rule", {})
        config = billing.get("config") or {}
        # Deliberately return billing metadata only, never identity/auth metadata.
        return {
            "authenticated": subscription.get("authenticated") is True,
            "subscription_tier": billing.get("subscription_tier"),
            "credit_usage_percent": config.get("creditUsagePercent"),
            "period": config.get("currentPeriod"),
            "on_demand_enabled": billing.get("on_demand_enabled"),
            "on_demand_cap": config.get("onDemandCap"),
            "prepaid_balance": config.get("prepaidBalance"),
            "auto_topup_rule": topup.get("rule"),
            "inference_permitted": False,
            "reason": "Native tool isolation and no-paid gate still require validation.",
        }
    finally:
        client.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("version", "inspect", "login", "status", "self-test"))
    options = parser.parse_args()
    for name in ("workspace", "profile", "temp"):
        (ROOT / name).mkdir(parents=True, exist_ok=True)
    if options.action == "self-test":
        current = environment()
        assert set(current).isdisjoint({"XAI_API_KEY", "GROK_MODELS_BASE_URL", "GROK_OIDC_ISSUER", "HTTP_PROXY"})
        assert all(value == "0" for key, value in current.items() if key.endswith("_ENABLED"))
        assert executable().is_file()
        print("Grok probe: isolated environment and runtime pin passed; no inference.")
        return 0
    if options.action == "status":
        print(json.dumps(account_status(), indent=2))
        return 0
    args = {
        "version": ("version",),
        "inspect": ("inspect", "--json"),
        "login": ("login", "--device-auth"),
    }[options.action]
    # Login is the official account flow. Credentials remain opaque in GROK_HOME.
    # Neither this tool nor CompAI reads, prints, or exports credential contents.
    return subprocess.call(command(*args), cwd=ROOT / "workspace", env=environment())


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError) as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(1)
