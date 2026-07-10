"""Build and verify the Windows one-folder executable distribution."""

from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    tests = parser.add_mutually_exclusive_group()
    tests.add_argument(
        "--with-tests",
        action="store_true",
        help="Run the optional source test suite before building.",
    )
    tests.add_argument(
        "--skip-tests",
        action="store_true",
        help=argparse.SUPPRESS,  # Backward-compatible no-op; skipping is now default.
    )
    parser.add_argument("--skip-smoke", action="store_true")
    args = parser.parse_args()

    if sys.platform != "win32":
        raise SystemExit(
            "Windows EXEs must be built on Windows. Run build_exe.bat from a Windows checkout."
        )

    root = Path(__file__).resolve().parents[1]
    build = root / "build"
    dist = root / "dist"
    if build.exists():
        shutil.rmtree(build)
    if dist.exists():
        shutil.rmtree(dist)

    if args.with_tests:
        subprocess.run(
            [
                sys.executable,
                "-m",
                "pytest",
                "tests/test_release_gui_surface.py",
                "tests/test_exe_build_surface.py",
                "tests/test_project_format.py",
                "tests/test_embedded_bind_project_policy.py",
                "-q",
            ],
            cwd=root,
            check=True,
        )

    if not args.with_tests:
        print("Source tests skipped (default). Use --with-tests for release validation.")

    subprocess.run(
        [sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean", "DL-ReAnimated.spec"],
        cwd=root,
        check=True,
    )

    app_dir = dist / "DL-ReAnimated"
    exe = app_dir / "DL-ReAnimated.exe"
    if not exe.is_file():
        raise FileNotFoundError(exe)

    report = app_dir / "exe_self_test.json"
    if not args.skip_smoke:
        completed = subprocess.run(
            [str(exe), "--self-test", "--report", str(report)],
            cwd=app_dir,
            timeout=180,
        )
        if completed.returncode != 0:
            raise RuntimeError(f"Frozen self-test failed with exit code {completed.returncode}")
        if not report.is_file():
            raise RuntimeError("Frozen self-test did not create exe_self_test.json")

    archive = dist / "DL-ReAnimated-Windows-x64.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for path in sorted(app_dir.rglob("*")):
            if path.is_file():
                zf.write(path, Path("DL-ReAnimated") / path.relative_to(app_dir))

    print(f"EXE: {exe}")
    print(f"Portable folder: {app_dir}")
    print(f"ZIP: {archive}")
    print(f"ZIP SHA-256: {sha256(archive)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
