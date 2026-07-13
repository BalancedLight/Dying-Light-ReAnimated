from __future__ import annotations

import json
from pathlib import Path

from dlanm2_gui.model_importer.compiler_support import stage_compiler_bootstrap
from dlanm2_gui.model_importer.compiler_bridge import _validate_compact_result


def test_staged_bootstrap_report_is_json_serializable(tmp_path: Path) -> None:
    devtools_data = tmp_path / "Engine" / "Data"
    engine_defs = devtools_data / "Shaders" / "Common" / "EngineDefs.mth"
    engine_defs.parent.mkdir(parents=True)
    engine_defs.write_text("// test", encoding="ascii")
    (devtools_data / "ResourcePackCfg.scr").write_text("// test", encoding="ascii")

    bootstrap = stage_compiler_bootstrap(
        compiler=tmp_path / "ResPackCompiler.exe",
        data0_pak=tmp_path / "Data0.pak",
        project_dir=tmp_path / "project",
        devtools_data_dir=devtools_data,
    )

    assert bootstrap["files"]
    assert all(isinstance(path, str) for path in bootstrap["files"])
    json.dumps({"bootstrap": bootstrap})


def _compact_report(*, half_extent: float) -> dict:
    return {
        "mesh_resources": [
            {
                "type_counts": {"BONE": 1, "MESH_SKINNED": 1},
                "bone_bounds_global_aggregate": {
                    "contributing_bone_count": 1 if half_extent > 0.0 else 0,
                    "collapsed_bone_count": 0 if half_extent > 0.0 else 1,
                    "invalid_bone_count": 0,
                    "diagonal_length": 1.0 if half_extent > 0.0 else 0.0,
                },
                "entities": [
                    {
                        "name": "bip01",
                        "element_type_name": "BONE",
                        "flags": "0x00004201",
                        "global_reference_identity_max_abs_error": 0.0,
                        "bounds_center_half_extents": [
                            0.0,
                            0.0,
                            0.0,
                            half_extent,
                            half_extent,
                            half_extent,
                        ],
                    },
                    {
                        "name": "body",
                        "element_type_name": "MESH_SKINNED",
                        "flags": "0x00004701",
                        "bounds_center_half_extents": [0.0] * 6,
                    },
                ],
            }
        ]
    }


def test_compiler_contract_rejects_collapsed_bone_bounds() -> None:
    result = _validate_compact_result(
        _compact_report(half_extent=0.0),
        mode="dying_light_humanoid",
        expected_bones=1,
    )

    assert result["ready"] is False
    assert any("collapsed bounds" in error for error in result["errors"])


def test_compiler_contract_accepts_nonzero_bone_bounds() -> None:
    result = _validate_compact_result(
        _compact_report(half_extent=0.01),
        mode="dying_light_humanoid",
        expected_bones=1,
    )

    assert result["ready"] is True


def test_compiler_contract_accepts_stock_position_tracks_on_stock_bind_rig() -> None:
    report = _compact_report(half_extent=0.01)
    report["mesh_resources"][0]["entities"][0]["flags"] = "0x00004301"

    result = _validate_compact_result(
        report,
        mode="dying_light_humanoid",
        expected_bones=1,
    )

    assert result["ready"] is True


def test_compiler_contract_rejects_unknown_humanoid_animation_policy() -> None:
    report = _compact_report(half_extent=0.01)
    report["mesh_resources"][0]["entities"][0]["flags"] = "0x00004101"

    result = _validate_compact_result(
        report,
        mode="dying_light_humanoid",
        expected_bones=1,
    )

    assert result["ready"] is False
    assert any("unsupported animation flags" in error for error in result["errors"])


def test_compiler_contract_rejects_nodes_without_animated_flag() -> None:
    report = _compact_report(half_extent=0.01)
    report["mesh_resources"][0]["entities"][0]["flags"] = "0x00004200"
    report["mesh_resources"][0]["entities"][1]["flags"] = "0x00004704"

    result = _validate_compact_result(
        report,
        mode="dying_light_humanoid",
        expected_bones=1,
    )

    assert result["ready"] is False
    assert any("animated-node flag" in error for error in result["errors"])
