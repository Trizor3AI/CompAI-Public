"""Exact-runtime tool policy; MCP resource declarations never authorize a server.

The resource exception is bound to the reviewed official Windows executable pair
and transformed bundled catalog. Unknown or changed identities keep the previous
closed declaration policy. Runtime bytes and evidence are verified by the caller.
"""
import re


MCP_RESOURCE_TOOLS = frozenset({"list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource"})
_REVIEWED_RUNTIME_FILES = {
    "codex.exe": "edfdd48f8b458af0906e507b4cb05217b326c3d6e515e9f024e05e3b8d650f70",
    "codex-code-mode-host.exe": "5acd42f62966dd4a498ebfe04c5cae4c4a644a342a65a21bb53f8f36235c3d35",
}
_REVIEWED_CATALOG = "4c9cb7c9d56372d65fb05740548aab27abc59aec2ceb439afe7bb45aaeabee2e"
_BASE_CASES = ("shell-control", "text", "snapshot", "deny-command", "deny-patch", "inspect")
MAX_MCP_STATUS_PAGES = 8


def resource_tools(pin):
    """Return the three reviewed declarations only for this exact pair/catalog."""
    if (isinstance(pin, dict) and pin.get("codex_version") == "codex-cli 0.167.0"
            and pin.get("runtime_files") == _REVIEWED_RUNTIME_FILES
            and pin.get("binary_sha256") == _REVIEWED_RUNTIME_FILES["codex.exe"]
            and pin.get("catalog_sha256") == _REVIEWED_CATALOG
            and pin.get("model") == "gpt-6-sol"):
        return MCP_RESOURCE_TOOLS
    return frozenset()


def policy_id(pin):
    return "empty_mcp_resources_v1" if resource_tools(pin) else "no_mcp_resources_v1"


def expected_nested_tools(pin, dynamic=(), web_search=False):
    return sorted({"clock__curr_time", *dynamic, *resource_tools(pin)}
                  | ({"web__run"} if web_search else set()))


def required_isolation_cases(pin):
    return _BASE_CASES + (("mcp-resources", "mcp-inherited-server") if resource_tools(pin) else ())


def known_function_declaration_prefixes(pin, web_search=False):
    """Exact observed static declaration lines, excluding shared type aliases."""
    lines = ["declare const tools: { clock__curr_time(args: {}): Promise<{"]
    lines.extend(f"declare const tools: {{ {name}(args: {{" for name in resource_tools(pin))
    if web_search:
        lines.append("declare const tools: { web__run(args: {")
    return sorted(lines)


def declared_function_names(declarations):
    """Project function declarations without treating shared types as tools."""
    names = []
    for line in declarations:
        if not isinstance(line, str):
            raise ValueError("Invalid tool declaration")
        if line.startswith("declare const tools:"):
            match = re.match(r"declare const tools: \{ ([A-Za-z_]\w*)\(", line)
            if not match:
                raise ValueError("Invalid tool declaration")
            names.append(match.group(1))
    return sorted(names)


def require_no_mcp_servers(call, thread_id):
    """Fail closed before a turn; this inventory check is bounded, not a lock.

    The caller must keep configuration and server registration unchanged after
    this check. No resource declaration grants permission to configure a server.
    """
    if not isinstance(thread_id, str) or not thread_id.strip():
        raise RuntimeError("Invalid thread for MCP isolation check; question not sent")
    cursor = None
    seen = set()
    for _ in range(MAX_MCP_STATUS_PAGES):
        page = call("mcpServerStatus/list", {"threadId": thread_id,
                    "detail": "toolsAndAuthOnly", "limit": 100, "cursor": cursor})
        if not isinstance(page, dict) or not isinstance(page.get("data"), list):
            raise RuntimeError("Malformed MCP server inventory; question not sent")
        if page["data"]:
            raise RuntimeError("Unexpected inherited MCP server; question not sent")
        cursor = page.get("nextCursor")
        if cursor is None:
            return
        if (not isinstance(cursor, str) or not cursor.strip() or len(cursor) > 256
                or cursor in seen):
            raise RuntimeError("Invalid MCP server inventory cursor; question not sent")
        seen.add(cursor)
    raise RuntimeError("MCP server inventory page limit exceeded; question not sent")
