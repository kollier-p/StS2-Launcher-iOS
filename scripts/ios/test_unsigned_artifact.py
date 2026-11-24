"""Offline unsigned IPA packaging and no-game-file scan checks."""
import plistlib
from pathlib import Path
import tempfile
import unittest

from package_ipa import package
from scan_unsigned_ipa import inspect_app, inspect_ipa


class UnsignedArtifactTests(unittest.TestCase):
    def make_app(self, base: Path) -> Path:
        app = base / "StS2.app"
        (app / "Managed").mkdir(parents=True)
        (app / "Frameworks").mkdir()
        (app / "Info.plist").write_bytes(plistlib.dumps({
            "CFBundleIdentifier": "io.github.example.sts2jit",
            "CFBundleExecutable": "StS2",
        }))
        (app / "StS2").write_bytes(b"unsigned host executable")
        (app / "Frameworks/sts2.framework").mkdir()
        (app / "Frameworks/sts2.framework/sts2").write_bytes(b"patched JIT")
        for name in ("STS2Jit", "STS2JitSupport", "STS2SteamBootstrap",
                     "STS2SteamDownloads", "SteamKit2", "GodotSharp",
                     "System.Private.CoreLib"):
            (app / "Managed" / (name + ".dll")).write_bytes(b"open-source runtime")
        return app

    def test_unsigned_without_credentials_or_game(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            app = self.make_app(base)
            inspect_app(app)
            ipa = base / "unsigned.ipa"
            package(app, ipa, unsigned=True)
            inspect_ipa(ipa)

    def test_game_binary_refused_before_and_after_packaging(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            app = self.make_app(base)
            (app / "Managed/sts2.dll").write_bytes(b"private game assembly")
            with self.assertRaisesRegex(ValueError, "Commercial game"):
                inspect_app(app)
            with self.assertRaisesRegex(ValueError, "must not bundle"):
                package(app, base / "unsigned.ipa", unsigned=True)
            (app / "Managed/sts2.dll").unlink()
            (app / "release_info.json").write_text("{}")
            with self.assertRaisesRegex(ValueError, "Commercial game"):
                inspect_app(app)

    def test_missing_bootstrap_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            app = self.make_app(Path(directory))
            (app / "Managed/STS2SteamBootstrap.dll").unlink()
            with self.assertRaisesRegex(ValueError, "missing"):
                inspect_app(app)


if __name__ == "__main__":
    unittest.main()
