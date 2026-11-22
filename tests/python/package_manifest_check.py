"""Synthetic release-manifest checks; no build, runtime launch, account or network call."""
import copy
import importlib.util
import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("compai_package_verifier", ROOT / "CompAI/verify_package.py")
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)


def project(version="0.53.0"):
    return f"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>".encode()


def manifest():
    return {"Author": "Trizor3", "Name": "CompAI", "InternalName": "CompAI", "AssemblyVersion": "0.53.0.0",
            "DalamudApiLevel": 15, "LoadSync": False, "ApplicableVersion": "any", "Tags": ["CompAI"],
            "Description": "Synthetic manifest", "SdkFutureMetadata": {"enabled": True, "count": 1}}


def encoded(value):
    return json.dumps(value).encode()


class PackageManifestChecks(unittest.TestCase):
    def verify(self, packaged=None, generated=None, project_xml=None):
        verifier.verify_manifest(encoded(manifest() if packaged is None else packaged),
                                 encoded(manifest() if generated is None else generated),
                                 project() if project_xml is None else project_xml)

    def test_project_triple_and_quad_normalize_to_four_components(self):
        self.verify()
        self.verify(project_xml=project("0.53.0.0"))
        for version in ("0.0.0", "65534.65534.65534.65534"):
            with self.subTest(version=version):
                value = manifest()
                value["AssemblyVersion"] = verifier.normalized_version(version, "synthetic version")
                self.verify(value, value, project(version))

    def test_json_formatting_and_property_order_do_not_change_agreement(self):
        value = manifest()
        reordered = dict(reversed(list(value.items())))
        reordered["SdkFutureMetadata"] = dict(reversed(list(value["SdkFutureMetadata"].items())))
        verifier.verify_manifest(json.dumps(reordered, indent=4).encode(), encoded(value), project())

    def test_stale_packaged_or_generated_version_rejects(self):
        stale = manifest()
        stale["AssemblyVersion"] = "0.52.3.0"
        for packed, generated in ((stale, manifest()), (manifest(), stale), (stale, stale)):
            with self.subTest(packaged=packed["AssemblyVersion"], generated=generated["AssemblyVersion"]):
                with self.assertRaisesRegex(AssertionError, "AssemblyVersion differs from project Version"):
                    self.verify(packed, generated)

    def test_missing_malformed_and_unsupported_assembly_versions_reject(self):
        for bad in (None, 53, True, [], {}, "", "0.53", "0.53.0.0.0", "0.53.0-beta", "0.53.0+build",
                    "0.53.*", "$(Version)", " 0.53.0", "0.53.0\n", "00.53.0", "0.-1.0", "０.53.0",
                    "65535.0.0", "1" * 100):
            for generated in (False, True):
                with self.subTest(version=bad, generated=generated):
                    value = manifest()
                    value["AssemblyVersion"] = bad
                    with self.assertRaisesRegex(AssertionError, "Unsupported .*AssemblyVersion"):
                        self.verify(generated=value) if generated else self.verify(packaged=value)
        value = manifest()
        del value["AssemblyVersion"]
        with self.assertRaisesRegex(AssertionError, "Unsupported packaged AssemblyVersion"):
            self.verify(packaged=value)

    def test_unsupported_project_version_declarations_reject(self):
        for xml in (b"not xml", b"<Project />", b"<Project><PropertyGroup><Version /></PropertyGroup></Project>",
                    b"<Project><PropertyGroup><Version>1.2.3</Version><Version>1.2.3</Version></PropertyGroup></Project>",
                    b'<Project><PropertyGroup Condition="true"><Version>0.53.0</Version></PropertyGroup></Project>',
                    b'<Project><PropertyGroup><Version Condition="true">0.53.0</Version></PropertyGroup></Project>',
                    b"<Project><PropertyGroup><Version>0.53.0<Other /></Version></PropertyGroup></Project>",
                    project("0.53.0-beta"), project("$(ReleaseVersion)"), project("1.2"), project("65535.0.0")):
            with self.subTest(xml=xml):
                with self.assertRaises(AssertionError):
                    self.verify(project_xml=xml)

    def test_public_identity_checks_remain_mandatory_even_when_manifests_agree(self):
        for key in ("Author", "Name", "InternalName"):
            with self.subTest(field=key):
                value = manifest()
                value[key] = "Unexpected"
                with self.assertRaisesRegex(AssertionError, f"manifest {key} differs"):
                    self.verify(value, value)

    def test_all_generated_metadata_is_compared_with_json_types_preserved(self):
        for key, replacement in (("DalamudApiLevel", 14), ("LoadSync", 0), ("Tags", ["Other"]),
                                 ("Description", "Changed"), ("Extra", "Injected"),
                                 ("SdkFutureMetadata", {"enabled": 1, "count": 1})):
            with self.subTest(field=key):
                value = copy.deepcopy(manifest())
                value[key] = replacement
                with self.assertRaisesRegex(AssertionError, "differs from the generated Release manifest"):
                    self.verify(packaged=value)
        value = manifest()
        del value["LoadSync"]
        with self.assertRaisesRegex(AssertionError, "differs from the generated Release manifest"):
            self.verify(packaged=value)

    def test_nonobject_malformed_duplicate_and_nonstandard_json_reject(self):
        for payload in (b"null", b"[]", b'"text"', b"{", b"\xff", b'{"Name":"CompAI","Name":"CompAI"}',
                        b'{"nested":{"same":1,"same":1}}', b'{"extra":NaN}', b'{"extra":Infinity}'):
            for generated in (False, True):
                with self.subTest(payload=payload, generated=generated):
                    with self.assertRaisesRegex(AssertionError, "Malformed .*plugin manifest"):
                        verifier.verify_manifest(encoded(manifest()) if generated else payload,
                                                 payload if generated else encoded(manifest()), project())


class LicenseManifestChecks(unittest.TestCase):
    def setUp(self):
        self.files = {name.removeprefix('licenses/'): b'Synthetic notice bytes\r\n'
            for name in verifier.ASSETS if name.startswith('licenses/') and name != 'licenses/manifest.json'}
        self.manifest = {'schema': 1, 'files': [{'path': name, 'sha256': verifier.sha256(data)}
            for name, data in self.files.items()]}

    def verify(self):
        verifier.verify_license_manifest(encoded(self.manifest), self.files.__getitem__)

    def test_exact_notice_bytes_and_complete_manifest_pass(self):
        self.verify()

    def test_changed_notice_with_stale_checksum_rejects(self):
        self.files['grok/README.md'] += b'Changed source location\n'
        with self.assertRaisesRegex(AssertionError, 'checksum differs: grok/README.md'):
            self.verify()

    def test_missing_duplicate_or_unexpected_paths_reject(self):
        original = copy.deepcopy(self.manifest)
        for rows in (original['files'][:-1], original['files'] + [original['files'][0]],
                     [{'path': '../outside', 'sha256': '0' * 64}],
                     [{'path': ['not a path'], 'sha256': '0' * 64}]):
            with self.subTest(rows=len(rows)), self.assertRaises(AssertionError):
                self.manifest = {'schema': 1, 'files': rows}
                self.verify()

    def test_malformed_records_and_digests_reject(self):
        for rows in (None, {}, [None], [{'path': 'grok/README.md', 'sha256': None}],
                     [{'path': 'grok/README.md', 'sha256': 'not-a-hash'}]):
            with self.subTest(rows=rows), self.assertRaises(AssertionError):
                self.manifest = {'schema': 1, 'files': rows}
                self.verify()


if __name__ == "__main__":
    unittest.main()
