"""Offline tests for IPA archive creation and fail-closed validation."""
import hashlib
from pathlib import Path
import plistlib
import tempfile
import unittest
import zipfile

from package_ipa import package


class PackageIpaTests(unittest.TestCase):
    def test_signed_app_archive(self):
        with tempfile.TemporaryDirectory() as root:
            base = Path(root)
            app = base / "StS2.app"
            for directory in ("_CodeSignature", "Managed", "Frameworks"):
                (app / directory).mkdir(parents=True, exist_ok=True)
            (app / "Info.plist").write_bytes(plistlib.dumps({
                "CFBundleIdentifier": "dev.example.sts2",
                "CFBundleExecutable": "StS2",
            }))
            (app / "StS2").write_bytes(b"fake executable")
            (app / "_CodeSignature/CodeResources").write_bytes(b"fake signature")
            (app / "Managed/STS2Jit.dll").write_bytes(b"fake JIT assembly")
            output = base / "dist" / "StS2-JIT.ipa"
            digest = package(app, output, "dev.example.sts2")
            self.assertEqual(hashlib.sha256(output.read_bytes()).hexdigest(), digest)
            self.assertEqual(output.with_suffix(".ipa.sha256").read_text().strip(),
                             f"{digest}  StS2-JIT.ipa")
            with zipfile.ZipFile(output) as archive:
                self.assertIsNone(archive.testzip())
                self.assertIn("Payload/StS2.app/Managed/STS2Jit.dll", archive.namelist())

    def test_refuse_missing_signature_and_wrong_bundle(self):
        with tempfile.TemporaryDirectory() as root:
            base = Path(root)
            app = base / "StS2.app"
            app.mkdir()
            (app / "Info.plist").write_bytes(plistlib.dumps({
                "CFBundleIdentifier": "dev.example.sts2",
                "CFBundleExecutable": "StS2",
            }))
            with self.assertRaisesRegex(ValueError, "code-signature"):
                package(app, base / "out.ipa")
            (app / "_CodeSignature").mkdir()
            (app / "_CodeSignature/CodeResources").write_bytes(b"signature")
            with self.assertRaisesRegex(ValueError, "identifier"):
                package(app, base / "out.ipa", "dev.wrong.bundle")


if __name__ == "__main__":
    unittest.main()
