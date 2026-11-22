"""Offline checks for staged interpreter integrity and archive rejection."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import prepare_python_runtime as runtime


class PythonBundleChecks(unittest.TestCase):
    def test_current_bundle_matches_pinned_official_files(self):
        runtime.verify_bundle(runtime.DESTINATION, runtime.read_pin())

    def test_added_import_path_or_modified_binary_is_rejected(self):
        with tempfile.TemporaryDirectory(dir=runtime.ROOT / '.test-tmp') as temporary:
            directory = Path(temporary)
            (directory / 'python.exe').write_bytes(b'synthetic')
            pin = {'files': {'python.exe': hashlib.sha256(b'synthetic').hexdigest()}}
            runtime.verify_bundle(directory, pin)
            (directory / 'python._pth').write_text('unexpected')
            with self.assertRaises(ValueError):
                runtime.verify_bundle(directory, pin)
            (directory / 'python._pth').unlink()
            (directory / 'python.exe').write_bytes(b'changed')
            with self.assertRaises(ValueError):
                runtime.verify_bundle(directory, pin)

    def test_invalid_paths_and_ambient_imports_are_rejected(self):
        with tempfile.TemporaryDirectory(dir=runtime.ROOT / '.test-tmp') as temporary:
            path = Path(temporary) / 'pin.json'
            original = runtime.read_pin()
            for mutation in ('parent', 'case', 'site', 'url'):
                pin = json.loads(json.dumps(original))
                if mutation == 'parent': pin['files']['../python.exe'] = '0' * 64
                if mutation == 'case': pin['files']['PYTHON.EXE'] = '0' * 64
                if mutation == 'site': pin['path_file_text'] += 'import site\n'
                if mutation == 'url': pin['source_url'] = 'https://example.org/python.zip'
                path.write_text(json.dumps(pin))
                with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                    runtime.read_pin(path)

    def test_wrong_archive_is_rejected_before_writing(self):
        with tempfile.TemporaryDirectory(dir=runtime.ROOT / '.test-tmp') as temporary:
            directory = Path(temporary)
            archive = directory / 'archive.zip'
            archive.write_bytes(b'not the pinned archive')
            target = directory / 'stage'
            target.mkdir()
            with self.assertRaises(ValueError):
                runtime.unpack_archive(archive, target, runtime.read_pin())
            self.assertEqual(list(target.iterdir()), [])


if __name__ == '__main__':
    (runtime.ROOT / '.test-tmp').mkdir(exist_ok=True)
    unittest.main()
