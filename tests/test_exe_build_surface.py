from __future__ import annotations

from pathlib import Path
import subprocess
import sys
import tomllib

from dlanm2_gui.runtime_paths import application_root, resource_root


def root() -> Path:
    return Path(__file__).resolve().parents[1]


def test_runtime_dependencies_are_minimal() -> None:
    data = tomllib.loads((root() / "pyproject.toml").read_text(encoding="utf-8"))
    dependencies = data["project"]["dependencies"]
    assert any(value.lower().startswith("numpy") for value in dependencies)
    assert not any(value.lower().startswith("pillow") for value in dependencies)


def test_importing_gui_surface_does_not_eagerly_import_retarget_pipeline() -> None:
    code = """
import sys
import dlanm2_gui.gui
assert 'dlanm2_gui.oracle.custom_fbx_release_candidate_editor_rpack' not in sys.modules
"""
    subprocess.run([sys.executable, "-c", code], cwd=root(), check=True)


def test_source_runtime_paths_resolve_to_repository() -> None:
    assert application_root() == root()
    assert resource_root() == root()


def test_windows_build_surface_exists_and_mentions_frozen_self_test() -> None:
    for relative in (
        "build_exe.bat",
        "build_exe.ps1",
        "DL-ReAnimated.spec",
        "tools/build_windows_exe.py",
        "docs/BUILDING_WINDOWS_EXE.md",
        "dlanm2_gui/environment_check.py",
        "dlanm2_gui/runtime_paths.py",
    ):
        assert (root() / relative).is_file(), relative
    script = (root() / "tools/build_windows_exe.py").read_text(encoding="utf-8")
    assert "--self-test" in script
    assert "DL-ReAnimated-Windows-x64.zip" in script


def test_setup_only_mode_exits_before_gui_launch() -> None:
    text = (root() / "run_gui.bat").read_text(encoding="utf-8").lower()
    setup_block = text.split('if /i "%~1"=="--setup"', 1)[1].split(')', 1)[0]
    assert "exit /b" in setup_block


def test_environment_check_succeeds_without_qt_for_core_assets() -> None:
    completed = subprocess.run(
        [sys.executable, "-m", "dlanm2_gui.environment_check"],
        cwd=root(),
        check=False,
        capture_output=True,
        text=True,
    )
    assert completed.returncode == 0, completed.stdout + completed.stderr
