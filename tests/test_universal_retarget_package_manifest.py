from __future__ import annotations

import hashlib
import importlib
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]

EXPECTED_SURFACE = {
    "dlanm2_gui/semantic_roles.py": "python_package",
    "dlanm2_gui/skeleton_archetypes.py": "python_package",
    "dlanm2_gui/skeleton_analysis.py": "python_package",
    "dlanm2_gui/semantic_chain_alignment.py": "python_package",
    "dlanm2_gui/animation_targets.py": "python_package",
    "dlanm2_gui/automatic_retarget.py": "python_package",
    "dlanm2_gui/retarget_profiles.py": "python_package",
    "dlanm2_gui/semantic_retarget.py": "python_package",
    "dlanm2_gui/retarget_recipes.py": "python_package",
    "dlanm2_gui/target_retarget_policy.py": "python_package",
    "dlanm2_gui/retarget_routing.py": "python_package",
    "dlanm2_gui/root_motion.py": "python_package",
    "dlanm2_gui/root_heading.py": "python_package",
    "dlanm2_gui/root_motion_basis.py": "python_package",
    "dlanm2_gui/locomotion.py": "python_package",
    "dlanm2_gui/target_mapping_inventory.py": "python_package",
    "docs/ANM2_TO_FBX.md": "windows_bundle_docs",
    "docs/CHROME_RIGS.md": "windows_bundle_docs",
    "docs/DYING_LIGHT_2.md": "windows_bundle_docs",
    "docs/RETARGETING.md": "windows_bundle_docs",
    "docs/ROOT_MOTION_AND_IK.md": "windows_bundle_docs",
    "docs/FBX_PREFLIGHT.md": "windows_bundle_docs",
    "docs/GUI_GUIDE.md": "windows_bundle_docs",
    "reverse_engineering/pseudocode/UniversalAutomaticRetargetPolicy.c": (
        "source_release_policy_reconstruction"
    ),
    "reverse_engineering/pseudocode/DLR_RootMotionActorBasisAndLocomotion.c": (
        "source_release_policy_reconstruction"
    ),
    "reverse_engineering/pseudocode/dlr_anm2_fbx_native_basis_and_timing.c": (
        "source_release_policy_reconstruction"
    ),
}


def _manifest() -> dict[str, object]:
    return json.loads(
        (ROOT / "PACKAGE_MANIFEST.json").read_text(encoding="utf-8")
    )


def test_manifest_declares_versioned_universal_and_verified_policies() -> None:
    manifest = _manifest()
    assert manifest["universal_source_analyzer"] == (
        "dlr-source-skeleton-analysis-v1"
    )
    assert manifest["universal_automatic_retarget_policy"] == (
        "automatic-retarget-planner-v1"
    )
    assert manifest["verified_dl2_advanced_body_bridge"] == (
        "dl2_advanced_body_bridge_v1"
    )
    assert manifest["verified_dl2_legacy_body_bridge"] == (
        "dl2_legacy_body_bridge_v1"
    )
    assert manifest["bundled_semantic_profile_schema"] == 2
    # This bridge still feeds the explicit format-1 compatibility writer.  Its
    # presence must never imply native Header_Version2 output support.
    assert manifest["native_dl2_format42_write"] is False


def test_manifest_hashes_the_complete_retarget_release_surface() -> None:
    manifest = _manifest()
    rows = list(manifest["universal_retarget_files"])
    by_path = {str(row["path"]): row for row in rows}
    assert len(by_path) == len(rows), "manifest contains duplicate retarget paths"
    assert {path: str(row["install_surface"]) for path, row in by_path.items()} == (
        EXPECTED_SURFACE
    )

    for relative, row in by_path.items():
        path = ROOT / relative
        assert path.is_file(), relative
        assert path.stat().st_size == int(row["size"]), relative
        assert hashlib.sha256(path.read_bytes()).hexdigest().upper() == str(
            row["sha256"]
        ), relative


def test_python_surface_is_discovered_and_selected_docs_ship_with_windows_bundle() -> None:
    for relative, surface in EXPECTED_SURFACE.items():
        if surface != "python_package":
            continue
        module_name = relative.removesuffix(".py").replace("/", ".")
        assert importlib.import_module(module_name).__name__ == module_name

    pyproject = (ROOT / "pyproject.toml").read_text(encoding="utf-8")
    assert 'include = ["dlanm2_gui*"]' in pyproject

    spec = (ROOT / "DL-ReAnimated.spec").read_text(encoding="utf-8")
    assert 'for directory in ("reference", "docs", "examples")' in spec
    for relative, surface in EXPECTED_SURFACE.items():
        if surface == "windows_bundle_docs":
            assert relative.startswith("docs/")


def test_private_regression_fixtures_are_not_release_manifest_entries() -> None:
    paths = {
        str(row["path"])
        for row in _manifest()["universal_retarget_files"]
    }
    assert not any(path.casefold().endswith(".fbx") for path in paths)
    assert not any("dl2test.dlraproj" in path.casefold() for path in paths)
