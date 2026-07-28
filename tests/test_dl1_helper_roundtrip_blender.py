from __future__ import annotations

from dataclasses import replace
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

import numpy as np
import pytest

from dlanm2_gui.anm2_fbx import (
    MOTION_HELPER_DESCRIPTOR,
    decode_anm2_animation,
)
from dlanm2_gui.anm2_writer import build_payload_from_values
from dlanm2_gui.blender_fbx import discover_blender, export_anm2_to_fbx
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.fbx_core import FbxDocument
from dlanm2_gui.retarget_engines.exact_rig import build_exact_rig_anm2
from dlanm2_gui.retarget_engines.legacy_exact_rig import (
    _Y_UP_TO_BLENDER,
    _raw_native_limb_globals,
    decompose_local_matrix,
)
from dlanm2_gui.roundtrip_contract import (
    embedded_native_metadata,
    load_roundtrip_sidecar,
    roundtrip_sidecar_path,
)


ROOT = Path(__file__).resolve().parents[1]
BASE_GAME_SOURCE = Path(
    r"F:\DyingLightTools\RP6Dumper\common_anims_PC\Animation"
    r"\m_fpp_unarmed_beginhangl_jump.anm2"
)
BASE_GAME_SHA256 = (
    "cc09048e3a00c80b1fe91c25d328630ee9448a56021e25bb83df43b6b12dee31"
)
REFCAMERA_DESCRIPTOR = 0xC9C05F6E


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _run_blender_edit(
    blender: Path,
    source: Path,
    destination: Path,
    *,
    bone: str,
    custom_properties: bool,
) -> dict[str, object]:
    command = [
        str(blender),
        "--background",
        "--factory-startup",
        "--python",
        str(ROOT / "tools" / "blender_offset_refcamera.py"),
        "--",
        "--input",
        str(source),
        "--output",
        str(destination),
        "--distance",
        "0.1",
        "--bone",
        bone,
    ]
    if custom_properties:
        command.append("--export-custom-props")
    completed = subprocess.run(
        command,
        cwd=ROOT,
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    marker = "DLR_REFCAMERA_EDIT:"
    rows = [
        line[len(marker) :]
        for line in (completed.stdout + completed.stderr).splitlines()
        if line.startswith(marker)
    ]
    assert len(rows) == 1
    return json.loads(rows[0])


def _copy_contract(source_fbx: Path, edited_fbx: Path) -> None:
    shutil.copy2(
        roundtrip_sidecar_path(source_fbx),
        roundtrip_sidecar_path(edited_fbx),
    )


def _expected_native_backward_deltas(
    base_document: FbxDocument,
    rig: ChromeRig,
    metadata: dict[str, object],
) -> np.ndarray:
    corrections = {
        name: np.asarray(values, dtype=float).reshape(4, 4)
        for name, values in metadata["display_basis_corrections"].items()
    }
    refcamera = next(bone for bone in rig.bones if bone.name == "refcamera")
    parent_name = rig.bones[refcamera.parent_index].name
    y_up_to_blender = _Y_UP_TO_BLENDER
    blender_to_y_up = np.linalg.inv(y_up_to_blender)
    local_backward = np.eye(4)
    local_backward[2, 3] = 0.1
    rows = []
    for tick in base_document.frame_ticks(fps=30.0):
        display = _raw_native_limb_globals(
            base_document,
            rig,
            tick=tick,
            use_animation=True,
        )
        parent_game = (
            blender_to_y_up
            @ (
                display[parent_name]
                @ np.linalg.inv(corrections[parent_name])
            )
            @ y_up_to_blender
        )
        original_game = (
            blender_to_y_up
            @ (
                display["refcamera"]
                @ np.linalg.inv(corrections["refcamera"])
            )
            @ y_up_to_blender
        )
        edited_game = (
            blender_to_y_up
            @ (
                display["refcamera"]
                @ local_backward
                @ np.linalg.inv(corrections["refcamera"])
            )
            @ y_up_to_blender
        )
        original_local = np.linalg.inv(parent_game) @ original_game
        edited_local = np.linalg.inv(parent_game) @ edited_game
        rows.append(edited_local[:3, 3] - original_local[:3, 3])
    return np.asarray(rows)


def _write_active_motion_source(path: Path, rig: ChromeRig) -> None:
    by_name = {bone.name: bone for bone in rig.bones}
    descriptors = (
        MOTION_HELPER_DESCRIPTOR,
        by_name["bip01"].descriptor,
        by_name["propsholder1"].descriptor,
        by_name["propsholder2"].descriptor,
    )
    bind = {
        descriptor: list(values)
        for descriptor, values in zip(
            rig.descriptors,
            rig.bind_track_values(),
        )
    }
    values = []
    for frame in range(3):
        rows = [list(bind[descriptor]) for descriptor in descriptors]
        rows[0][3] = 0.2 * frame
        rows[0][4] = 0.05 * frame
        rows[1][5] += 0.01 * frame
        rows[2][3] = 0.03 * frame
        rows[3][4] = -0.02 * frame
        values.append(rows)
    packed_flags = [
        [
            max(frame[track][component] for frame in values)
            - min(frame[track][component] for frame in values)
            > 1.0e-8
            for component in range(9)
        ]
        for track in range(len(descriptors))
    ]
    header = replace(
        rig.make_header(frame_count=3),
        track_count=len(descriptors),
    )
    path.write_bytes(
        build_payload_from_values(
            header,
            descriptors,
            values,
            packed_flags,
        )
    )


def test_base_game_refcamera_helper_roundtrip_acceptance(
    tmp_path: Path,
) -> None:
    blender = discover_blender()
    if blender is None:
        pytest.skip("Blender is not installed")
    if not BASE_GAME_SOURCE.is_file():
        pytest.skip("Private base-game refcamera acceptance fixture is unavailable")
    assert _sha256(BASE_GAME_SOURCE) == BASE_GAME_SHA256

    source = tmp_path / BASE_GAME_SOURCE.name
    shutil.copy2(BASE_GAME_SOURCE, source)
    assert _sha256(source) == BASE_GAME_SHA256
    rig = ChromeRig.load(
        ROOT / "reference" / "dl1" / "player_1_tpp_helpers.crig"
    )
    refcamera = next(bone for bone in rig.bones if bone.name == "refcamera")
    assert refcamera.descriptor == REFCAMERA_DESCRIPTOR
    assert refcamera.helper and not refcamera.deform

    base_fbx = tmp_path / f"{source.stem}_helpers.fbx"
    exported = export_anm2_to_fbx(
        source,
        rig,
        base_fbx,
        anm2_input_fps=30.0,
        fbx_output_fps=30.0,
        start_frame=0,
        end_frame=12,
        unknown_track_policy="helpers",
        blender_executable=blender,
    )
    assert exported.frame_count == 13
    assert exported.bind_pose_bone_count == 87
    assert Path(exported.roundtrip_metadata_path) == (
        roundtrip_sidecar_path(base_fbx).resolve()
    )
    base_document = FbxDocument(base_fbx)
    assert len(base_document.limb_models) == 87
    assert "DLR_OffsetHelper_CCC3CDDF" in base_document.null_models
    assert base_document.bind_diagnostics()["bind_coverage"]["authoritative"] == 87
    native_metadata = load_roundtrip_sidecar(base_fbx)
    assert native_metadata["version"] == 5
    with pytest.raises(ValueError, match="frame count changed"):
        build_exact_rig_anm2(base_fbx, rig, fps=24.0)

    edited_fbx = tmp_path / f"{source.stem}_refcamera_back.fbx"
    edit_report = _run_blender_edit(
        blender,
        base_fbx,
        edited_fbx,
        bone="refcamera",
        custom_properties=False,
    )
    assert edit_report["frame_start"] == 1
    assert edit_report["frame_end"] == 13
    assert edit_report["local_axis"] == "+Z"
    assert edit_report["export_custom_props"] is False
    for frame in ("1", "7", "13"):
        before = np.asarray(edit_report["sample_before"][frame], dtype=float)
        after = np.asarray(edit_report["sample_after"][frame], dtype=float)
        assert float(np.linalg.norm(after - before)) == pytest.approx(
            0.1,
            abs=2.0e-6,
        )
    assert not embedded_native_metadata(FbxDocument(edited_fbx))
    _copy_contract(base_fbx, edited_fbx)

    rebuilt = build_exact_rig_anm2(edited_fbx, rig, fps=30.0)
    rebuilt_anm2 = tmp_path / f"{source.stem}_refcamera_back.anm2"
    rebuilt_anm2.write_bytes(rebuilt.payload)
    assert rebuilt.report["roundtrip_metadata_source"] == "sidecar"
    assert rebuilt.report["appended_edited_descriptors"] == []

    original = decode_anm2_animation(source)
    actual = decode_anm2_animation(rebuilt_anm2)
    assert original.frame_count == actual.frame_count == 13
    assert original.descriptors == actual.descriptors
    assert len(actual.descriptors) == 70
    assert actual.descriptors.index(REFCAMERA_DESCRIPTOR) == 21

    source_index = original.descriptors.index(REFCAMERA_DESCRIPTOR)
    actual_index = actual.descriptors.index(REFCAMERA_DESCRIPTOR)
    translation_delta = (
        actual.values[:, actual_index, 3:6]
        - original.values[:, source_index, 3:6]
    )
    magnitudes = np.linalg.norm(translation_delta, axis=1)
    assert float(np.min(magnitudes)) >= 0.096
    assert float(np.max(magnitudes)) <= 0.104
    expected_direction = _expected_native_backward_deltas(
        base_document,
        rig,
        native_metadata,
    )
    direction_cosines = np.sum(
        translation_delta * expected_direction,
        axis=1,
    ) / (
        np.linalg.norm(translation_delta, axis=1)
        * np.linalg.norm(expected_direction, axis=1)
    )
    assert float(np.min(direction_cosines)) >= 0.995

    assert actual.values[:, actual_index, :3] == pytest.approx(
        original.values[:, source_index, :3],
        abs=4.0e-3,
    )
    assert actual.values[:, actual_index, 6:9] == pytest.approx(
        original.values[:, source_index, 6:9],
        abs=4.0e-3,
    )
    quaternion_dots = np.abs(
        np.sum(
            actual.quaternions_wxyz[:, actual_index]
            * original.quaternions_wxyz[:, source_index],
            axis=1,
        )
    )
    assert float(np.min(quaternion_dots)) >= 1.0 - 2.0e-5

    non_target_maximum = 0.0
    for original_index, descriptor in enumerate(original.descriptors):
        if descriptor == REFCAMERA_DESCRIPTOR:
            continue
        rebuilt_index = actual.descriptors.index(descriptor)
        non_target_maximum = max(
            non_target_maximum,
            float(
                np.max(
                    np.abs(
                        actual.values[:, rebuilt_index]
                        - original.values[:, original_index]
                    )
                )
            ),
        )
    assert non_target_maximum <= 4.0e-3
    bip01 = next(bone for bone in rig.bones if bone.name == "bip01")
    bip01_source = original.descriptors.index(bip01.descriptor)
    bip01_actual = actual.descriptors.index(bip01.descriptor)
    assert actual.values[:, bip01_actual] == pytest.approx(
        original.values[:, bip01_source],
        abs=4.0e-3,
    )
    for holder_name in ("propsholder1", "propsholder2"):
        holder = next(bone for bone in rig.bones if bone.name == holder_name)
        assert holder.descriptor not in actual.descriptors

    edited_document = FbxDocument(edited_fbx)
    base_ticks = base_document.frame_ticks(fps=30.0)
    edited_ticks = edited_document.frame_ticks(fps=30.0)
    for holder_name in ("propsholder1", "propsholder2"):
        for frame in (0, 6, 12):
            base_local = base_document._local_matrix(
                base_document.limb_models[holder_name],
                tick=base_ticks[frame],
                use_animation=True,
            )
            edited_local = edited_document._local_matrix(
                edited_document.limb_models[holder_name],
                tick=edited_ticks[frame],
                use_animation=True,
            )
            assert edited_local == pytest.approx(base_local, abs=4.0e-6)

    second_fbx = tmp_path / f"{source.stem}_refcamera_back_second.fbx"
    second_export = export_anm2_to_fbx(
        rebuilt_anm2,
        rig,
        second_fbx,
        anm2_input_fps=30.0,
        fbx_output_fps=30.0,
        start_frame=0,
        end_frame=12,
        unknown_track_policy="helpers",
        blender_executable=blender,
    )
    assert second_export.frame_count == 13
    second_document = FbxDocument(second_fbx)
    second_ticks = second_document.frame_ticks(fps=30.0)
    for frame in (0, 6, 12):
        original_local = base_document._local_matrix(
            base_document.limb_models["refcamera"],
            tick=base_ticks[frame],
            use_animation=True,
        )
        rebuilt_local = second_document._local_matrix(
            second_document.limb_models["refcamera"],
            tick=second_ticks[frame],
            use_animation=True,
        )
        relative = np.linalg.inv(original_local) @ rebuilt_local
        translation, quaternion, scale = decompose_local_matrix(relative)
        assert float(np.linalg.norm(translation)) == pytest.approx(
            0.1,
            abs=4.0e-3,
        )
        assert float(translation[2] / np.linalg.norm(translation)) >= 0.995
        assert abs(float(quaternion[0])) >= 1.0 - 2.0e-5
        assert scale == pytest.approx((1.0, 1.0, 1.0), abs=4.0e-3)

    custom_props_fbx = tmp_path / f"{source.stem}_refcamera_back_props.fbx"
    _run_blender_edit(
        blender,
        base_fbx,
        custom_props_fbx,
        bone="refcamera",
        custom_properties=True,
    )
    _copy_contract(base_fbx, custom_props_fbx)
    props_document = FbxDocument(custom_props_fbx)
    assert embedded_native_metadata(props_document)
    props_rebuilt = build_exact_rig_anm2(
        custom_props_fbx,
        rig,
        fps=30.0,
    )
    assert props_rebuilt.report["roundtrip_metadata_source"] == (
        "embedded_and_sidecar"
    )
    assert props_rebuilt.report["preserved_source_descriptors"] == [
        f"0x{descriptor:08X}" for descriptor in original.descriptors
    ]

    appended_fbx = tmp_path / f"{source.stem}_l_eye_edit.fbx"
    _run_blender_edit(
        blender,
        base_fbx,
        appended_fbx,
        bone="l_eye",
        custom_properties=False,
    )
    _copy_contract(base_fbx, appended_fbx)
    appended = build_exact_rig_anm2(appended_fbx, rig, fps=30.0)
    l_eye = next(bone for bone in rig.bones if bone.name == "l_eye")
    assert appended.report["track_count"] == 71
    assert appended.report["appended_edited_tracks"] == ["l_eye"]
    appended_path = tmp_path / "l_eye_appended.anm2"
    appended_path.write_bytes(appended.payload)
    appended_animation = decode_anm2_animation(appended_path)
    assert appended_animation.descriptors[:70] == original.descriptors
    assert appended_animation.descriptors[70] == l_eye.descriptor

    assert _sha256(source) == BASE_GAME_SHA256
    assert _sha256(BASE_GAME_SOURCE) == BASE_GAME_SHA256


def test_offset_helper_unbakes_only_recorded_root_and_keeps_current_track(
    tmp_path: Path,
) -> None:
    blender = discover_blender()
    if blender is None:
        pytest.skip("Blender is not installed")
    rig = ChromeRig.load(
        ROOT / "reference" / "dl1" / "player_1_tpp_helpers.crig"
    )
    source = tmp_path / "active_offset_helper.anm2"
    _write_active_motion_source(source, rig)
    original = decode_anm2_animation(source)

    baked_fbx = tmp_path / "active_offset_helper_baked.fbx"
    baked = export_anm2_to_fbx(
        source,
        rig,
        baked_fbx,
        anm2_input_fps=30.0,
        fbx_output_fps=30.0,
        start_frame=0,
        end_frame=2,
        unknown_track_policy="helpers",
        blender_executable=blender,
    )
    assert baked.motion_accumulator_active
    assert baked.motion_accumulator_baked
    assert baked.motion_accumulator_root == "bip01"
    baked_metadata = load_roundtrip_sidecar(baked_fbx)
    motion = baked_metadata["roundtrip_contract"]["motion_accumulator"]
    assert motion["baked"] is True
    assert motion["root_name"] == "bip01"
    assert len(motion["original_bake_samples"]) == 3

    edited_fbx = tmp_path / "active_offset_helper_edited.fbx"
    edit_report = _run_blender_edit(
        blender,
        baked_fbx,
        edited_fbx,
        bone="DLR_OffsetHelper_CCC3CDDF",
        custom_properties=False,
    )
    assert edit_report["node_kind"] == "empty"
    _copy_contract(baked_fbx, edited_fbx)
    rebuilt = build_exact_rig_anm2(edited_fbx, rig, fps=30.0)
    rebuilt_path = tmp_path / "active_offset_helper_edited.anm2"
    rebuilt_path.write_bytes(rebuilt.payload)
    actual = decode_anm2_animation(rebuilt_path)
    assert actual.descriptors == original.descriptors

    helper_index = original.descriptors.index(MOTION_HELPER_DESCRIPTOR)
    rebuilt_helper_index = actual.descriptors.index(MOTION_HELPER_DESCRIPTOR)
    helper_delta = (
        actual.values[:, rebuilt_helper_index, 3:6]
        - original.values[:, helper_index, 3:6]
    )
    assert np.linalg.norm(helper_delta, axis=1) == pytest.approx(
        (0.1, 0.1, 0.1),
        abs=4.0e-3,
    )

    by_name = {bone.name: bone for bone in rig.bones}
    for name in ("bip01", "propsholder1", "propsholder2"):
        descriptor = by_name[name].descriptor
        source_index = original.descriptors.index(descriptor)
        output_index = actual.descriptors.index(descriptor)
        assert actual.values[:, output_index] == pytest.approx(
            original.values[:, source_index],
            abs=4.0e-3,
        )

    unbaked_fbx = tmp_path / "active_offset_helper_unbaked.fbx"
    unbaked = export_anm2_to_fbx(
        source,
        rig,
        unbaked_fbx,
        anm2_input_fps=30.0,
        fbx_output_fps=30.0,
        start_frame=0,
        end_frame=2,
        unknown_track_policy="helpers",
        bake_motion_accumulator=False,
        blender_executable=blender,
    )
    assert unbaked.motion_accumulator_active
    assert not unbaked.motion_accumulator_baked
    unbaked_build = build_exact_rig_anm2(unbaked_fbx, rig, fps=30.0)
    unbaked_path = tmp_path / "active_offset_helper_unbaked.anm2"
    unbaked_path.write_bytes(unbaked_build.payload)
    unbaked_actual = decode_anm2_animation(unbaked_path)
    assert unbaked_actual.descriptors == original.descriptors
    for index, descriptor in enumerate(original.descriptors):
        output_index = unbaked_actual.descriptors.index(descriptor)
        assert unbaked_actual.values[:, output_index] == pytest.approx(
            original.values[:, index],
            abs=4.0e-3,
        )
