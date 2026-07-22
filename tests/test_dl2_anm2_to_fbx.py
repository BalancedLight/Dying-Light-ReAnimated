from __future__ import annotations

import json
import hashlib
from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.anm2_fbx import (
    build_decode_report,
    decode_anm2_animation,
    reconstruct_native_scene,
    unknown_track_indices,
    write_unknown_track_sidecar,
)
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.blender_fbx import (
    FbxExportResult,
    discover_blender,
    export_anm2_to_fbx,
)
from dlanm2_gui.oracle.binary_fbx_mixamo import _FbxDocument
from dlanm2_gui.trackmap import dl_name_hash
from dlanm2_gui.workspace_project import DlReanimatedProject


ROOT = Path(__file__).resolve().parents[1]
SAMPLE = ROOT / "reference" / "dl2" / "0_m_fpp_farjump.anm2"
ADVANCED_RIG = ROOT / "reference" / "dl2" / "player_skeleton.crig"
SOURCE_SHA256 = "9368914A4C59521BDD31FED064DF93A5D2D287E793FDC9447BE24ACD4A3FFF6D"


@pytest.fixture(scope="module")
def advanced_rig() -> ChromeRig:
    return ChromeRig.load(ADVANCED_RIG)


@pytest.fixture(scope="module")
def farjump_animation():
    return decode_anm2_animation(SAMPLE)


def test_native_dl2_decode_dispatch_and_report(farjump_animation, advanced_rig: ChromeRig) -> None:
    animation = farjump_animation
    assert animation.values.shape == (229, 189, 9)
    assert animation.quaternions_wxyz.shape == (229, 189, 4)
    assert np.isfinite(animation.values).all()
    assert animation.descriptors[89] == dl_name_hash("pelvis")

    report = build_decode_report(animation, advanced_rig)
    expected = {
        "container": "dl2_header_version_2",
        "signature": 42,
        "header_version": 2,
        "frame_count": 229,
        "track_count": 189,
        "static_stream_count": 1354,
        "packed_stream_count": 347,
        "block_count": 2,
        "block_frame_spans": [120, 108],
        "vfr_words": [1, 228, 1],
        "unknown_descriptor_count": 97,
        "source_anm2_sha256": SOURCE_SHA256,
        "decoded_track_count": 189,
        "unique_packed_slots_decoded": 16,
        "prepared_base_segment_count": 2,
    }
    assert {key: report[key] for key in expected} == expected
    root = report["root_motion_diagnostics"]
    assert root["target_primary_root"] == "pelvis"
    assert root["diagnostic_only_no_curve_mutation"] is True
    assert root["skeletal_root"]["available"] is True
    assert root["motion_accumulator"]["available"] is True
    assert root["skeletal_root"]["finite"] is True


def test_default_dl2_scene_is_advanced_skeleton_with_sidecar_policy(
    farjump_animation,
    advanced_rig: ChromeRig,
) -> None:
    scene = reconstruct_native_scene(farjump_animation, advanced_rig)
    assert len(advanced_rig.bones) == 271
    assert len(scene.bones) == 271
    assert len(unknown_track_indices(farjump_animation, advanced_rig)) == 97
    assert any("deterministic .dlr_unknown_tracks.json sidecar" in row for row in scene.warnings)

    matched = [
        bone for bone in scene.bones
        if bone.descriptor in set(farjump_animation.descriptors)
    ]
    assert len(matched) == 92
    unmatched_indices = [
        index
        for index, bone in enumerate(advanced_rig.bones)
        if bone.descriptor not in set(farjump_animation.descriptors)
    ]
    assert len(unmatched_indices) == 179
    expected_translations = np.asarray(
        [advanced_rig.bones[index].bind_translation for index in unmatched_indices]
    )
    expected_rotations = np.asarray(
        [advanced_rig.bones[index].bind_rotation_wxyz for index in unmatched_indices]
    )
    expected_scales = np.asarray(
        [advanced_rig.bones[index].bind_scale for index in unmatched_indices]
    )
    np.testing.assert_allclose(
        scene.translations[:, unmatched_indices],
        np.broadcast_to(expected_translations, (scene.frame_count, *expected_translations.shape)),
    )
    np.testing.assert_allclose(
        scene.rotations_wxyz[:, unmatched_indices],
        np.broadcast_to(expected_rotations, (scene.frame_count, *expected_rotations.shape)),
    )
    np.testing.assert_allclose(
        scene.scales[:, unmatched_indices],
        np.broadcast_to(expected_scales, (scene.frame_count, *expected_scales.shape)),
    )
    by_name = {bone.name: bone for bone in scene.bones}
    for name in (
        "refcamera",
        "eyecamera",
        "l_leg_secanim_01",
        "l_leg_secanim_02_d",
        "r_leg_secanim_01",
        "r_leg_secanim_02_d",
    ):
        assert by_name[name].descriptor in farjump_animation.descriptors

    job = scene.to_job_dict(ROOT / "build" / "ignored-test-output.fbx")
    assert job["frame_start"] == 0
    assert job["frame_end"] == 228
    assert len(job["bones"]) == 271


def test_unknown_sidecar_is_complete_and_deterministic(
    tmp_path: Path,
    farjump_animation,
    advanced_rig: ChromeRig,
) -> None:
    output = tmp_path / "0_m_fpp_farjump.fbx"
    first = write_unknown_track_sidecar(farjump_animation, advanced_rig, output)
    assert first == tmp_path / "0_m_fpp_farjump.dlr_unknown_tracks.json"
    first_bytes = first.read_bytes()
    second = write_unknown_track_sidecar(farjump_animation, advanced_rig, output)
    assert second is not None
    assert second.read_bytes() == first_bytes

    payload = json.loads(first_bytes)
    assert payload["source_anm2_sha256"] == SOURCE_SHA256
    assert payload["unknown_descriptor_count"] == 97
    assert len(payload["tracks"]) == 97
    assert [row["track_index"] for row in payload["tracks"]] == sorted(
        row["track_index"] for row in payload["tracks"]
    )
    for row in payload["tracks"]:
        assert row["descriptor"].startswith("0x")
        assert row["semantic"] == "unknown_transform_track"
        assert row["source_anm2_sha256"] == SOURCE_SHA256
        assert len(row["frame_table"]) == 229
        assert all(len(frame) == 10 for frame in row["frame_table"])


def test_helpers_and_drop_are_explicit_policies(farjump_animation, advanced_rig: ChromeRig) -> None:
    helpers = reconstruct_native_scene(
        farjump_animation,
        advanced_rig,
        unknown_track_policy="helpers",
    )
    assert len(helpers.bones) == 271 + 97
    assert any(bone.name == "DLR_OffsetHelper_CCC3CDDF" for bone in helpers.bones)
    assert all(not bone.deform for bone in helpers.bones if bone.helper)

    dropped = reconstruct_native_scene(
        farjump_animation,
        advanced_rig,
        unknown_track_policy="drop",
    )
    assert len(dropped.bones) == 271
    assert any("explicitly dropped" in row for row in dropped.warnings)


def test_partial_dl2_frame_range_uses_container_bounds(advanced_rig: ChromeRig) -> None:
    animation = decode_anm2_animation(SAMPLE, start_frame=120, end_frame=121)
    assert animation.values.shape == (2, 189, 9)
    assert animation.source_frame_start == 120
    assert animation.source_frame_end == 121
    assert animation.container_frame_count == 229
    assert np.isfinite(animation.values).all()
    scene = reconstruct_native_scene(animation, advanced_rig)
    assert scene.to_job_dict(ROOT / "build" / "ignored-test-output.fbx")["frame_end"] == 1


def test_export_service_writes_default_dl2_sidecar_without_interactive_blender(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    advanced_rig: ChromeRig,
) -> None:
    destination = tmp_path / "farjump.fbx"

    def fake_blender_export(scene, output_path, **_kwargs):
        assert len(scene.bones) == 271
        return FbxExportResult(
            str(Path(output_path).resolve()),
            scene.frame_count,
            scene.fps,
            len(scene.bones),
            tuple(scene.warnings),
            "non-interactive test",
        )

    monkeypatch.setattr("dlanm2_gui.blender_fbx.run_blender_export", fake_blender_export)
    result = export_anm2_to_fbx(SAMPLE, advanced_rig, destination)
    assert result.frame_count == 229
    assert result.bone_count == 271
    assert result.unknown_track_policy == "sidecar"
    assert result.unknown_track_count == 97
    assert Path(result.unknown_tracks_sidecar).name == "farjump.dlr_unknown_tracks.json"
    assert Path(result.unknown_tracks_sidecar).is_file()


def test_optional_blender_exports_the_advanced_dl2_scene(
    tmp_path: Path,
    advanced_rig: ChromeRig,
) -> None:
    blender = discover_blender()
    if blender is None:
        pytest.skip("Blender is not installed")
    destination = tmp_path / "0_m_fpp_farjump.fbx"
    result = export_anm2_to_fbx(
        SAMPLE,
        advanced_rig,
        destination,
        blender_executable=blender,
    )
    assert result.frame_count == 229
    assert result.bone_count == 271
    assert result.unknown_track_count == 97
    assert destination.is_file() and destination.stat().st_size > 0
    assert Path(result.unknown_tracks_sidecar).is_file()

    document = _FbxDocument(destination)
    document.select_animation_stack()
    assert len(document.frame_ticks(fps=30)) == 229
    exported_nodes = set(document.limb_models) | set(document.null_models)
    rig_names = {bone.name for bone in advanced_rig.bones}
    assert len(rig_names & exported_nodes) == 271
    assert not rig_names - exported_nodes
    assert {
        "pelvis",
        "refcamera",
        "eyecamera",
        "l_leg_secanim_01",
        "r_leg_secanim_02_d",
    } <= exported_nodes
    # Every authored CRIG row, including camera/non-deform helper rows, remains
    # in the complete 271-bone armature. Only explicit unknown-track helpers
    # are exported as EMPTY objects.
    assert {"refcamera", "eyecamera"} <= set(document.limb_models)
    assert {"pelvis", "l_leg_secanim_01", "r_leg_secanim_02_d"} <= set(
        document.limb_models
    )
    assert not any(name.startswith("DLR_Track_") for name in exported_nodes)


def test_cli_resolves_advanced_builtin_and_routes_policy_noninteractively(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from dlanm2_gui.tools import anm2_to_fbx as cli

    rig = cli.load_source_rig("builtin:dl2_player_advanced")
    assert rig.rig_id == "builtin:dl2_player_advanced"
    assert len(rig.bones) == 271
    calls: list[tuple[Path, str, str | None, float | None, float | None]] = []

    def fake_export(source, source_rig, output, **kwargs):
        calls.append(
            (
                Path(source), source_rig.rig_id,
                kwargs.get("unknown_track_policy"),
                kwargs.get("anm2_input_fps"), kwargs.get("fbx_output_fps"),
            )
        )
        return FbxExportResult(
            str(Path(output).resolve()),
            229,
            30,
            271,
            (),
            "non-interactive CLI test",
            "sidecar",
            97,
            str(Path(output).with_suffix(".dlr_unknown_tracks.json").resolve()),
            anm2_input_fps=30.0,
            fbx_output_fps=24.0,
        )

    monkeypatch.setattr(cli, "export_anm2_to_fbx", fake_export)
    result = cli.main(
        [
            str(SAMPLE),
            "--source-rig",
            "builtin:dl2_player_advanced",
            "--unknown-track-policy",
            "sidecar",
            "--anm2-fps",
            "30",
            "--fbx-fps",
            "24",
            "--output-directory",
            str(tmp_path),
        ]
    )
    assert result == 0
    assert calls == [
        (SAMPLE, "builtin:dl2_player_advanced", "sidecar", 30.0, 24.0)
    ]


def test_advanced_read_export_example_and_manifest_are_coherent() -> None:
    project = DlReanimatedProject.load(
        ROOT / "examples" / "dl2_player_advanced.example.dlraproj"
    )
    assert project.rig.target_rig_ref == "builtin:dl2_player_advanced"
    assert project.anm2_to_fbx.extensions["unknown_track_policy"] == "sidecar"
    assert project.anm2_to_fbx.items[0].end_frame == 228

    manifest = json.loads((ROOT / "PACKAGE_MANIFEST.json").read_text(encoding="utf-8"))
    assert manifest["native_dl2_header_v2_curve_decode"] is True
    assert manifest["native_dl2_anm2_to_fbx"] is True
    assert manifest["native_dl2_format42_write"] is False
    assert manifest["dl2_default_rig"] == "builtin:dl2_player_advanced"
    assert manifest["dl2_legacy_rig"] == "builtin:dl2_player_shadow_caster"
    for row in manifest["key_files"]:
        path = ROOT / row["path"]
        assert path.stat().st_size == row["size"]
        assert hashlib.sha256(path.read_bytes()).hexdigest().upper() == row["sha256"]
