from __future__ import annotations

import json
import math
from pathlib import Path
import subprocess

import numpy as np
import pytest

from dlanm2_gui.anm2_writer import build_payload_from_values
from dlanm2_gui.anm2_fbx import decode_anm2_animation
from dlanm2_gui.blender_fbx import discover_blender, export_anm2_to_fbx
from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.oracle.binary_fbx_mixamo import _FbxDocument
from dlanm2_gui.oracle.smd_bind_pose import anm2_cayley_vector_from_quaternion
from dlanm2_gui.retarget_engines.exact_rig import build_exact_rig_anm2
from dlanm2_gui.retarget_engines.legacy_exact_rig import _dlr_native_metadata
from dlanm2_gui.trackmap import dl_name_hash


def _imported_bone_head(
    blender: Path | str, fbx_path: Path, bone_name: str, frame: int,
) -> tuple[np.ndarray, np.ndarray]:
    """Return an imported bone's rest and displayed pose heads in world space."""
    script = "\n".join((
        "import bpy, json",
        f"bpy.ops.import_scene.fbx(filepath={json.dumps(str(fbx_path))})",
        "armature = next(obj for obj in bpy.context.scene.objects if obj.type == 'ARMATURE')",
        f"bpy.context.scene.frame_set({int(frame)})",
        "bpy.context.view_layer.update()",
        f"bone = armature.data.bones[{json.dumps(bone_name)}]",
        f"pose_bone = armature.pose.bones[{json.dumps(bone_name)}]",
        "snapshot = {",
        "    'rest': list(armature.matrix_world @ bone.head_local),",
        "    'pose': list(armature.matrix_world @ pose_bone.head),",
        "}",
        "print('DLR_BONE_SNAPSHOT:' + json.dumps(snapshot))",
    ))
    completed = subprocess.run(
        [str(blender), "--background", "--factory-startup", "--python-expr", script],
        check=True,
        capture_output=True,
        text=True,
    )
    prefix = "DLR_BONE_SNAPSHOT:"
    for line in completed.stdout.splitlines():
        if line.startswith(prefix):
            snapshot = json.loads(line[len(prefix):])
            return np.asarray(snapshot["rest"], dtype=float), np.asarray(snapshot["pose"], dtype=float)
    raise AssertionError(f"Blender did not report imported bone state:\n{completed.stdout}\n{completed.stderr}")


def test_blender_exports_first_anm2_frame_and_animation(tmp_path: Path) -> None:
    blender = discover_blender()
    if blender is None:
        pytest.skip("Blender is not installed")
    descriptor = dl_name_hash("root")
    rig = ChromeRig(
        "test:blender-root", "Blender root", "Test",
        (ChromeRigBone(
            0, "root", -1, descriptor, (0.0, 1.0, 0.0),
            (1.0, 0.0, 0.0, 0.0), (1.0, 1.0, 1.0),
        ),),
        0,
    )
    values = [rig.bind_track_values() for _ in range(3)]
    values[0][0][3:6] = [0.1, 1.0, 0.2]
    values[1][0][3:6] = [0.2, 1.0, 0.3]
    values[2][0][3:6] = [0.3, 1.0, 0.4]
    quaternion = np.asarray([math.cos(math.pi / 8), 0.0, 0.0, math.sin(math.pi / 8)])
    values[2][0][:3] = anm2_cayley_vector_from_quaternion(quaternion).tolist()
    payload = build_payload_from_values(
        rig.make_header(frame_count=3), [descriptor], values,
        [[True, True, True, True, True, True, False, False, False]],
    )
    source = tmp_path / "root_motion.anm2"
    source.write_bytes(payload)
    output = tmp_path / "root_motion.fbx"
    result = export_anm2_to_fbx(source, rig, output, blender_executable=blender)
    assert result.frame_count == 3
    assert output.is_file() and output.stat().st_size > 0

    document = _FbxDocument(output)
    document.select_animation_stack()
    ticks = document.frame_ticks(fps=30)
    assert len(ticks) == 3
    assert set(document.limb_models) == {"root"}
    y_up_to_blender = np.asarray(((1, 0, 0), (0, 0, -1), (0, 1, 0)), dtype=float)
    for frame, tick in enumerate(ticks):
        global_matrix = document.global_matrices(tick=tick, use_animation=True)["root"]
        expected = y_up_to_blender @ np.asarray(values[frame][0][3:6], dtype=float)
        assert global_matrix[:3, 3] == pytest.approx(expected, abs=2.0e-5)


def test_blender_fbx_rest_pose_is_anchored_at_first_sample(tmp_path: Path) -> None:
    blender = discover_blender()
    if blender is None:
        pytest.skip("Blender is not installed")
    root_descriptor = dl_name_hash("root")
    child_descriptor = dl_name_hash("child")
    rig = ChromeRig(
        "test:first-sample-rest-anchor", "First sample rest anchor", "Test",
        (
            ChromeRigBone(
                0, "root", -1, root_descriptor, (0.0, 0.0, 0.0),
                (1.0, 0.0, 0.0, 0.0), (1.0, 1.0, 1.0),
            ),
            ChromeRigBone(
                1, "child", 0, child_descriptor, (0.0, 0.8, 0.0),
                (1.0, 0.0, 0.0, 0.0), (1.0, 1.0, 1.0),
            ),
        ),
        0,
    )
    values = [rig.bind_track_values() for _ in range(3)]
    for frame, degrees in enumerate((30.0, 0.0, -60.0)):
        rotation = np.asarray((
            math.cos(math.radians(degrees) / 2.0),
            0.0,
            0.0,
            math.sin(math.radians(degrees) / 2.0),
        ))
        values[frame][0][:3] = anm2_cayley_vector_from_quaternion(rotation).tolist()
    payload = build_payload_from_values(
        rig.make_header(frame_count=3),
        [root_descriptor, child_descriptor],
        values,
        [[True, True, True, False, False, False, False, False, False], [False] * 9],
    )
    source = tmp_path / "first_sample_rest_anchor.anm2"
    source.write_bytes(payload)
    output = tmp_path / "first_sample_rest_anchor.fbx"
    export_anm2_to_fbx(source, rig, output, fps=30.0, blender_executable=blender)

    rest_at_start, displayed_at_start = _imported_bone_head(
        blender, output, "child", frame=0,
    )
    _rest_at_end, displayed_at_end = _imported_bone_head(
        blender, output, "child", frame=2,
    )
    # Static data-bone basis must be the starting pose, while the action still
    # visibly moves the child by the final sample. Version 0.5.0 left Blender
    # on the final audited frame, so its rest head matched displayed_at_end.
    assert rest_at_start == pytest.approx(displayed_at_start, abs=2.0e-5)
    assert float(np.linalg.norm(displayed_at_end - displayed_at_start)) > 0.2
    assert float(np.linalg.norm(rest_at_start - displayed_at_end)) > 0.2

    rebuilt = build_exact_rig_anm2(output, rig, fps=30)
    rebuilt_path = tmp_path / "first_sample_rest_anchor_roundtrip.anm2"
    rebuilt_path.write_bytes(rebuilt.payload)
    expected = decode_anm2_animation(source)
    actual = decode_anm2_animation(rebuilt_path)
    for descriptor in expected.descriptors:
        expected_index = expected.descriptors.index(descriptor)
        actual_index = actual.descriptors.index(descriptor)
        assert actual.values[:, actual_index, 3:] == pytest.approx(
            expected.values[:, expected_index, 3:], abs=2.0e-4
        )
        quaternion_dots = np.abs(np.sum(
            actual.quaternions_wxyz[:, actual_index]
            * expected.quaternions_wxyz[:, expected_index],
            axis=1,
        ))
        assert float(np.min(quaternion_dots)) >= 1.0 - 2.0e-5


def test_bundled_native_fbx_uses_readable_helpers_and_roundtrips(tmp_path: Path) -> None:
    blender = discover_blender()
    if blender is None:
        pytest.skip("Blender is not installed")
    root = Path(__file__).resolve().parents[1]
    rig = ChromeRig.load(root / "reference" / "male_npc_infected.crig")
    source = root / "reference" / "infected_turn_90r.template.anm2"
    output = tmp_path / "infected_turn_90r.fbx"
    export_anm2_to_fbx(source, rig, output, blender_executable=blender)

    document = _FbxDocument(output)
    assert len(document.animation_stacks) == 1
    assert len(document.limb_models) == len(rig.bones)
    assert "DLR_OffsetHelper_CCC3CDDF" not in document.limb_models
    assert "DLR_OffsetHelper_CCC3CDDF" in document.null_models
    # Display tails never rewrite the authored CRIG hierarchy.
    assert document.parent_by_name["r_uparmtwist"] == "r_upperarm"
    assert document.parent_by_name["r_thightwist"] == "r_thigh"

    rebuilt = build_exact_rig_anm2(output, rig, fps=30)
    rebuilt_path = tmp_path / "roundtrip.anm2"
    rebuilt_path.write_bytes(rebuilt.payload)
    expected = decode_anm2_animation(source)
    actual = decode_anm2_animation(rebuilt_path)
    assert actual.frame_count == expected.frame_count
    for descriptor in expected.descriptors:
        expected_index = expected.descriptors.index(descriptor)
        actual_index = actual.descriptors.index(descriptor)
        assert actual.values[:, actual_index, 3:] == pytest.approx(
            expected.values[:, expected_index, 3:], abs=2.0e-4
        )
        quaternion_dots = np.abs(np.sum(
            actual.quaternions_wxyz[:, actual_index]
            * expected.quaternions_wxyz[:, expected_index],
            axis=1,
        ))
        assert float(np.min(quaternion_dots)) >= 1.0 - 2.0e-5


def test_native_edit_bones_follow_child_pivots_and_roundtrip(
    tmp_path: Path,
) -> None:
    blender = discover_blender()
    if blender is None:
        pytest.skip("Blender is not installed")
    root_descriptor = dl_name_hash("root")
    child_descriptor = dl_name_hash("child")
    root_bind = (
        math.cos(math.radians(35.0) / 2.0),
        0.0,
        0.0,
        math.sin(math.radians(35.0) / 2.0),
    )
    rig = ChromeRig(
        "test:native-off-axis",
        "Native off-axis",
        "Test",
        (
            ChromeRigBone(
                0, "root", -1, root_descriptor, (0.0, 0.0, 0.0),
                root_bind, (1.0, 1.0, 1.0),
            ),
            # The visible child pivot is along local X, deliberately 90° from
            # the root's native local Y axis.
            ChromeRigBone(
                1, "child", 0, child_descriptor, (1.0, 0.0, 0.0),
                (1.0, 0.0, 0.0, 0.0), (1.0, 1.0, 1.0),
            ),
        ),
        0,
    )
    values = [rig.bind_track_values() for _ in range(2)]
    swing = np.asarray(
        (math.cos(math.radians(84.0) / 2.0), math.sin(math.radians(84.0) / 2.0), 0.0, 0.0)
    )
    values[1][0][:3] = anm2_cayley_vector_from_quaternion(swing).tolist()
    payload = build_payload_from_values(
        rig.make_header(frame_count=2),
        list(rig.descriptors),
        values,
        [[True, True, True, False, False, False, False, False, False], [False] * 9],
    )
    source = tmp_path / "native_off_axis.anm2"
    source.write_bytes(payload)
    output = tmp_path / "native_off_axis.fbx"
    result = export_anm2_to_fbx(
        source, rig, output, fps=30.0, blender_executable=blender
    )
    assert result.root_parity_max_angular_degrees <= 0.05
    assert result.root_parity_max_heading_degrees <= 0.05
    assert result.root_parity_max_translation_m <= 1.0e-5
    assert result.native_rest_basis_max_rotation_degrees > 10.0

    document = _FbxDocument(output)
    assert document.parent_by_name["child"] == "root"
    rest_globals = document.global_matrices(tick=0, use_animation=False)
    root_head = rest_globals["root"][:3, 3]
    child_head = rest_globals["child"][:3, 3]
    child_direction = child_head - root_head
    child_direction /= np.linalg.norm(child_direction)
    root_y_axis = rest_globals["root"][:3, 1]
    root_y_axis /= np.linalg.norm(root_y_axis)
    assert float(np.dot(root_y_axis, child_direction)) >= 0.999

    metadata = _dlr_native_metadata(document)
    assert metadata["basis_mode"] == "child_pivot_display_v1"
    correction = np.asarray(
        metadata["display_basis_corrections"]["root"], dtype=float
    ).reshape(4, 4)
    assert correction != pytest.approx(np.eye(4), abs=1.0e-8)
    assert metadata["native_rest_basis_errors"]["root"]["status"] == "display_delta"

    rebuilt = build_exact_rig_anm2(output, rig, fps=30)
    rebuilt_path = tmp_path / "native_off_axis_roundtrip.anm2"
    rebuilt_path.write_bytes(rebuilt.payload)
    expected = decode_anm2_animation(source)
    actual = decode_anm2_animation(rebuilt_path)
    for descriptor in expected.descriptors:
        expected_index = expected.descriptors.index(descriptor)
        actual_index = actual.descriptors.index(descriptor)
        assert actual.values[:, actual_index, 3:] == pytest.approx(
            expected.values[:, expected_index, 3:], abs=2.0e-4
        )
        quaternion_dots = np.abs(np.sum(
            actual.quaternions_wxyz[:, actual_index]
            * expected.quaternions_wxyz[:, expected_index],
            axis=1,
        ))
        assert float(np.min(quaternion_dots)) >= 1.0 - 2.0e-5
