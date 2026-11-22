"""Prepare a pinned, local test catalog from the staged private Codex bundle.

No provider request, login, credential read, install, or user-config mutation.
Nulling apply_patch_tool_type is a tested implementation detail, not a stable
security API. Revalidate after any binary/catalog/model change.
"""

import hashlib
import json
from pathlib import Path
import os
import subprocess


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def main():
    root = Path(__file__).resolve().parent
    executable = (root / "runtime" / "win-x64" / "codex.exe").resolve(strict=True)
    output = root / ".isolation-probe"
    output.mkdir(exist_ok=True)
    home = output / "catalog-home"
    home.mkdir(exist_ok=True)
    environment = {k: v for k, v in os.environ.items() if k.upper() in {
        "PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "COMSPEC", "SYSTEMDRIVE",
        "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP",
    }}
    environment["CODEX_HOME"] = str(home)
    version = subprocess.check_output([str(executable), "--version"], text=True, env=environment).strip()
    raw = subprocess.check_output([str(executable), "debug", "models", "--bundled"],
                                  text=True, encoding="utf-8", env=environment)
    catalog = json.loads(raw)
    visible = [model for model in catalog["models"] if model.get("visibility") == "list"]
    matches = [model for model in visible if model.get("slug") == "gpt-6-sol"]
    if (len(matches) != 1 or not visible or
            any(model.get("apply_patch_tool_type") != "freeform" for model in visible)):
        raise RuntimeError("Unexpected bundled model metadata; inspect before changing the profile")
    (output / "bundled-models.json").write_text(raw, encoding="utf-8")
    for model in visible:
        model["apply_patch_tool_type"] = None
    target = output / "no-patch-models.json"
    target.write_text(json.dumps(catalog), encoding="utf-8")
    manifest = {
        "codex_version": version,
        "binary_sha256": digest(executable),
        "runtime_files": {name: digest(executable.parent / name) for name in
                          ("codex.exe", "codex-code-mode-host.exe")},
        "catalog_sha256": digest(target),
        "model": "gpt-6-sol",
        "source": "codex debug models --bundled (no refresh)",
        "change": "All visible models: apply_patch_tool_type freeform -> null; hidden models unchanged",
        "validated_route": "loopback mock only; subscription route pending",
    }
    (output / "runtime-pin.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
