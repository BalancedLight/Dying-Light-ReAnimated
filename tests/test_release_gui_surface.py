from __future__ import annotations

from pathlib import Path

from dlanm2_gui import __version__
from dlanm2_gui import gui
from dlanm2_gui.script_targets import ScriptTargetRegistry
from dlanm2_gui.workspace_project import DlReanimatedProject


def test_gui_module_is_importable_without_loading_qt() -> None:
    assert callable(gui.main)
    assert __version__ == "0.4.0a2"


def test_release_docs_launchers_and_examples_exist() -> None:
    root = Path(__file__).resolve().parents[1]
    for relative in (
        "run_gui.bat",
        "setup_gui.bat",
        "build_exe.bat",
        "build_exe.ps1",
        "DL-ReAnimated.spec",
        "docs/BUILDING_WINDOWS_EXE.md",
        "docs/GUI_GUIDE.md",
        "docs/PROJECT_FORMAT.md",
        "docs/schemas/dlraproj.schema.v2.json",
        "docs/schemas/dlraproj.schema.v4.json",
        "docs/schemas/dlraproj.schema.v5.json",
        "docs/ANM2_TO_FBX.md",
        "docs/RETARGETING.md",
        "docs/ANIMATION_SCRIPT_TARGETS.md",
        "docs/RPACK_WORKFLOW.md",
        "docs/TROUBLESHOOTING.md",
        "examples/multi_animation_project.example.dlraproj",
        "examples/mixamo_humanoid.dlrmap.json",
    ):
        assert (root / relative).is_file(), relative


def test_example_project_opens_and_uses_multiple_script_targets() -> None:
    root = Path(__file__).resolve().parents[1]
    project = DlReanimatedProject.load(
        root / "examples/multi_animation_project.example.dlraproj"
    )
    assert len(project.animations) == 3
    assert {row.script_target for row in project.animations} >= {
        "",
        "npc_male_base",
        "player_male",
    }
    assert project.mapping_profiles


def test_builtin_script_targets_include_player_and_female() -> None:
    registry = ScriptTargetRegistry()
    assert registry.resolve_resource_name("player_male") == "anims_player_dlc60"
    assert registry.resolve_resource_name("npc_female") == "anims_woman_all"


def test_alpha3_gui_usability_surface_is_present() -> None:
    root = Path(__file__).resolve().parents[1]
    source = (root / "dlanm2_gui/gui.py").read_text(encoding="utf-8")
    assert "Use imported animation FBX bind pose (recommended)" in source
    assert "class _NoWheelComboBox" in source
    assert "Show advanced settings" in source
    assert "setMaximumHeight(84)" in source
    assert "Include stock writer and bind-pose controls" in source


def test_release_root_is_not_cluttered_with_project_documents() -> None:
    root = Path(__file__).resolve().parents[1]
    for filename in (
        "CHANGELOG.md",
        "CONTRIBUTING.md",
        "SECURITY.md",
        "LICENSE_PENDING.md",
        "THIRD_PARTY_ASSETS.md",
        "RELEASE_NOTES_0.3.0-alpha.1.md",
        "RELEASE_NOTES_0.3.0-alpha.2.md",
        "cli_help.txt",
    ):
        assert not (root / filename).exists(), filename
