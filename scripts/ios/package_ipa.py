#!/usr/bin/env python3
"""Package an already signed, built StS2.app as an installable iOS IPA.

Does not embed Steam game assets beyond those already present in the signed app.
An IPA is a ZIP containing Payload/<App>.app, not the temporary Godot export.
"""
import argparse
import hashlib
from pathlib import Path
import plistlib
import shutil
import tempfile
import zipfile


def package(app: Path, output: Path, bundle_id: str | None = None) -> str:
    app = app.resolve()
    if not app.is_dir() or app.suffix != ".app":
        raise ValueError("Expected an existing signed .app directory")
    info = app / "Info.plist"
    if not info.is_file() or not (app / "_CodeSignature/CodeResources").is_file():
        raise ValueError("App has no Info.plist or code-signature resource seal")
    metadata = plistlib.loads(info.read_bytes())
    identifier = metadata.get("CFBundleIdentifier")
    if not identifier or (bundle_id is not None and identifier != bundle_id):
        raise ValueError("App bundle identifier does not match requested identifier")
    executable = metadata.get("CFBundleExecutable")
    if not executable or not (app / executable).is_file():
        raise ValueError("App executable is missing")
    if not (app / "Managed/STS2Jit.dll").is_file():
        raise ValueError("App is missing the JIT managed entrypoint")
    if not (app / "Frameworks").is_dir():
        raise ValueError("App is missing runtime frameworks")

    output = output.resolve()
    if output.is_relative_to(app):
        raise ValueError("IPA output cannot be inside the application bundle")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as temp:
        staged = Path(temp) / "Payload" / app.name
        staged.parent.mkdir()
        shutil.copytree(app, staged, symlinks=True)
        temporary = Path(temp) / "package.ipa"
        with zipfile.ZipFile(temporary, "w", allowZip64=True) as archive:
            for item in sorted((Path(temp) / "Payload").rglob("*")):
                if item.is_symlink():
                    # Zip archives cannot safely preserve an iOS framework symlink
                    # with the Python zipfile defaults. Resolve links within the app.
                    target = item.resolve()
                    if not target.is_relative_to(staged.resolve()):
                        raise ValueError("App contains a symlink escaping its bundle")
                    archive.write(target, item.relative_to(temp))
                elif item.is_file():
                    archive.write(item, item.relative_to(temp))
        with zipfile.ZipFile(temporary) as archive:
            names = set(archive.namelist())
            required = [
                f"Payload/{app.name}/Info.plist",
                f"Payload/{app.name}/{executable}",
                f"Payload/{app.name}/Managed/STS2Jit.dll",
                f"Payload/{app.name}/_CodeSignature/CodeResources",
            ]
            if not all(name in names for name in required) or archive.testzip() is not None:
                raise ValueError("IPA integrity check failed")
        temporary.replace(output)
    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    output.with_suffix(output.suffix + ".sha256").write_text(f"{digest}  {output.name}\n")
    return digest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--bundle-id")
    args = parser.parse_args()
    digest = package(args.app, args.output, args.bundle_id)
    print(f"Created {args.output} (SHA-256 {digest})")


if __name__ == "__main__":
    main()
