"""Run one selected maintainer tool or Python fixture with checkout-local imports."""

from pathlib import Path
import runpy
import sys


ROOT = Path(__file__).resolve().parents[1]
DIRECTORIES = (ROOT / "tools", ROOT / "tests/python")


def main():
    scripts = {
        path.stem: path
        for directory in DIRECTORIES
        for path in sorted(directory.glob("*.py"))
        if path.name != "run.py"
    }
    if len(sys.argv) < 2 or sys.argv[1] in ("--help", "-h", "--list"):
        print("Usage: python -B tools/run.py <script> [script arguments]")
        print("Runs only the named script; no automatic suite or account checks.")
        for name in sorted(scripts):
            print(f"  {name}")
        return
    name = sys.argv[1].removesuffix(".py")
    if name not in scripts:
        raise SystemExit(f"Unknown script: {name}. Use --list for available names.")
    sys.dont_write_bytecode = True
    sys.path[:0] = [str(ROOT / "bridge"), *(str(directory) for directory in DIRECTORIES)]
    sys.argv = [str(scripts[name]), *sys.argv[2:]]
    runpy.run_path(str(scripts[name]), run_name="__main__")


if __name__ == "__main__":
    main()
