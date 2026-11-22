"""Read-only check: does a fresh CODEX_HOME see the same keyring login?"""
from pathlib import Path
import tempfile
import companion


def main():
    original = companion.PROFILE
    client = None
    with tempfile.TemporaryDirectory(prefix="keyring-scope-", dir=companion.CATALOG.parent) as directory:
        try:
            companion.PROFILE = Path(directory)
            client = companion.Companion()
            print("fresh_profile_account_type=" + str(client.account()["type"]))
        finally:
            if client:
                client.close()
            companion.PROFILE = original


if __name__ == "__main__":
    main()
