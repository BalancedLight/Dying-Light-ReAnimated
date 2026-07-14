from __future__ import annotations

import hashlib
import json
import shutil
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any

import numpy as np

from dlanm2_gui.animation_scr import AnimationScrSequence, build_animation_scr_sections
from dlanm2_gui.anm2 import Anm2Header
from dlanm2_gui.anm2_components import decode_file_samples
from dlanm2_gui.anm2_writer import build_payload_from_values
from dlanm2_gui.oracle import binary_fbx_mixamo as fbx_module
from dlanm2_gui.oracle.binary_fbx_mixamo import (
    FBX_TICKS_PER_SECOND,
    _FbxDocument,
    _axis_rotation,
)
from dlanm2_gui.oracle.custom_fbx_smd_retarget_editor_rpack import (
    _manifest,
    _sequence,
    _verify,
    _write_observation_sheet,
)
from dlanm2_gui.oracle.custom_fbx_smd_two_vector_fullbody_editor_rpack import (
    AXIAL_MAP,
    CLAVICLES,
    HELPER_TRACKS,
    LIMBS,
    LimbSpec,
    _continuous_frames,
    _desired_globals_to_local_rotations,
    _frame0_bind_error,
    _frame_from_primary_secondary,
    _hierarchy_sample,
    _limb_hierarchy_sample,
    _mid_bone_frame,
    _moving_tracks,
    _orthogonalize,
    _source_body_frame,
    _target_body_frame,
    _two_bone_frame,
    _unit,
)
from dlanm2_gui.oracle.smd_bind_pose import (
    anm2_cayley_vector_from_quaternion,
    bind_track_values,
    parse_smd_bind_pose,
    quaternion_wxyz_from_matrix,
    smd_global_matrices,
    smd_local_matrices,
)
from dlanm2_gui.rp6l import build_common_anims_multi_probe_rpack

PACK_NAME = "common_anims_sp_pc.rpack"
SCRIPT_RESOURCE_NAME = "anims_man_all_DLC60"
FPS = 30


@dataclass(frozen=True, slots=True)
class CandidateSpec:
    name: str
    role: str
    resource_name: str
    right_arm: bool = False
    left_arm: bool = False
    legs: bool = False
    clavicles: bool = False
    torso: bool = False
    hands_feet: bool = False


CANDIDATES = (
    CandidateSpec(
        "right_arm_absolute_locked",
        "corrected_fbx_intrinsic_euler_absolute_right_arm_with_clavicle_locked",
        "dl_reanimated_fbxfix_rightarm_absolute_locked",
        right_arm=True,
    ),
    CandidateSpec(
        "right_arm_absolute_hand_locked",
        "corrected_fbx_absolute_right_arm_plus_primary_hand_with_clavicle_locked",
        "dl_reanimated_fbxfix_rightarm_absolute_hand",
        right_arm=True,
        hands_feet=True,
    ),
    CandidateSpec(
        "right_arm_absolute_clavicle_hand",
        "corrected_fbx_absolute_right_clavicle_arm_forearm_hand",
        "dl_reanimated_fbxfix_rightarm_absolute_clav_hand",
        right_arm=True,
        clavicles=True,
        hands_feet=True,
    ),
    CandidateSpec(
        "fullbody_absolute_limbs",
        "corrected_fbx_absolute_both_arms_and_legs_with_torso_locked",
        "dl_reanimated_fbxfix_full_limbs_absolute",
        right_arm=True,
        left_arm=True,
        legs=True,
    ),
    CandidateSpec(
        "fullbody_absolute_primary",
        "corrected_fbx_absolute_torso_clavicles_arms_and_legs_without_terminal_roll",
        "dl_reanimated_fbxfix_fullbody_absolute",
        right_arm=True,
        left_arm=True,
        legs=True,
        clavicles=True,
        torso=True,
    ),
    CandidateSpec(
        "fullbody_absolute_hands_feet",
        "corrected_fbx_absolute_full_primary_body_with_hands_and_feet_no_fingers",
        "dl_reanimated_fbxfix_fullbody_hf_absolute",
        right_arm=True,
        left_arm=True,
        legs=True,
        clavicles=True,
        torso=True,
        hands_feet=True,
    ),
)


def build_custom_fbx_smd_intrinsic_absolute_editor_rpack(
    *,
    animation_fbx: str | Path,
    source_rest_fbx: str | Path,
    trusted_source_rest_json: str | Path,
    canonical_smd: str | Path,
    target_template_anm2: str | Path,
    stock_writer_control_anm2: str | Path,
    out_dir: str | Path,
) -> dict[str, Any]:
    out = Path(out_dir)
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    euler_validation = validate_fbx_intrinsic_euler_against_trusted_rest(
        source_rest_fbx=source_rest_fbx,
        trusted_source_rest_json=trusted_source_rest_json,
    )
    if euler_validation["fixed_max_abs_matrix_delta"] > 1.0e-3:
        raise ValueError("corrected FBX evaluator does not match the trusted source rest")
    (out / "fbx_intrinsic_euler_validation.json").write_text(
        json.dumps(euler_validation, indent=2) + "\n",
        encoding="utf-8",
    )

    animation = _FbxDocument(Path(animation_fbx))
    source_rest = _FbxDocument(Path(source_rest_fbx))
    if set(animation.limb_models) != set(source_rest.limb_models):
        raise ValueError("animation FBX and source-rest FBX skeletons differ")

    ticks = animation.frame_ticks(fps=FPS)
    frame_count = len(ticks)
    source_positions = _sample_source_positions(animation, ticks)
    source_rest_positions = _sample_source_positions(source_rest, [0])[0]
    source_body_frames = _continuous_frames([_source_body_frame(row) for row in source_positions])
    source_rest_body = _source_body_frame(source_rest_positions)

    template_path = Path(target_template_anm2)
    template_payload = template_path.read_bytes()
    template_header = Anm2Header.parse(template_payload)
    template_sample = decode_file_samples(template_path, [0.0])
    descriptors = list(template_sample.descriptors)

    target_pose = parse_smd_bind_pose(canonical_smd)
    target_local = smd_local_matrices(target_pose)
    target_global = smd_global_matrices(target_pose)
    bind_track_rows, names_by_descriptor, fallback_descriptors = bind_track_values(
        target_pose,
        descriptors,
        template_sample.frames[0].tracks,
    )
    track_index_by_name = {
        name: descriptors.index(descriptor)
        for descriptor, name in names_by_descriptor.items()
    }
    target_body_bind = _target_body_frame(target_global)

    packaged: list[tuple[str, bytes]] = []
    sequences: list[AnimationScrSequence] = []
    manifests: list[dict[str, Any]] = []

    control_path = Path(stock_writer_control_anm2)
    control_payload = control_path.read_bytes()
    control_header = Anm2Header.parse(control_payload)
    control_resource = "dl_reanimated_fbxfix_stock_rebuilt_control"
    packaged.append((control_resource, control_payload))
    sequences.append(_sequence(control_resource, control_header.frame_count))
    manifests.append(_manifest(
        candidate_name="stock_rebuilt_engine_integration_control",
        role="known_good_writer_and_delivery_regression_control",
        resource_name=control_resource,
        payload=control_payload,
        frame_count=control_header.frame_count,
        extra={"source": str(control_path)},
    ))

    bind_header = replace(template_header, frame_count=2)
    bind_values = [[list(track) for track in bind_track_rows] for _ in range(2)]
    bind_payload = build_payload_from_values(
        bind_header,
        descriptors,
        bind_values,
        [[False] * 9 for _ in descriptors],
    )
    bind_resource = "dl_reanimated_fbxfix_target_bind_control"
    packaged.append((bind_resource, bind_payload))
    sequences.append(_sequence(bind_resource, 2))
    manifests.append(_manifest(
        candidate_name="canonical_smd_target_bind_pose_control",
        role="editor_reset_matching_target_bind_pose",
        resource_name=bind_resource,
        payload=bind_payload,
        frame_count=2,
        extra={
            "canonical_smd": str(canonical_smd),
            "fallback_descriptors": [f"0x{value:08X}" for value in fallback_descriptors],
        },
    ))

    reports: list[dict[str, Any]] = []
    for spec in CANDIDATES:
        report, payload = _build_candidate(
            spec=spec,
            frame_count=frame_count,
            template_header=template_header,
            descriptors=descriptors,
            bind_track_rows=bind_track_rows,
            names_by_descriptor=names_by_descriptor,
            track_index_by_name=track_index_by_name,
            source_positions=source_positions,
            source_rest_positions=source_rest_positions,
            source_body_frames=source_body_frames,
            source_rest_body=source_rest_body,
            target_pose=target_pose,
            target_local=target_local,
            target_global=target_global,
            target_body_bind=target_body_bind,
            out_dir=out / "candidates" / spec.name,
        )
        packaged.append((spec.resource_name, payload))
        sequences.append(_sequence(spec.resource_name, frame_count))
        manifests.append(_manifest(
            candidate_name=spec.name,
            role=spec.role,
            resource_name=spec.resource_name,
            payload=payload,
            frame_count=frame_count,
            extra={
                "moving_named_tracks": report["moving_named_tracks"],
                "unintended_moving_named_tracks": report["unintended_moving_named_tracks"],
                "frame0_max_component_delta_from_smd_bind": report[
                    "frame0_max_component_delta_from_smd_bind"
                ],
                "frame70_right_arm": report["limb_samples"].get("right_arm", {}).get("70"),
                "retarget_report": str(out / "candidates" / spec.name / "retarget_report.json"),
            },
        ))
        reports.append(report)

    pack_path = out / PACK_NAME
    pack_path.write_bytes(build_common_anims_multi_probe_rpack(
        animation_resources=packaged,
        animation_script_resource_name=SCRIPT_RESOURCE_NAME,
        animation_script_sections=build_animation_scr_sections(sequences),
    ))
    verification = _verify(pack_path, [name for name, _payload in packaged])

    summary = {
        "status": verification["status"],
        "animation_fbx": str(animation_fbx),
        "source_rest_fbx": str(source_rest_fbx),
        "trusted_source_rest_json": str(trusted_source_rest_json),
        "canonical_smd": str(canonical_smd),
        "target_template_anm2": str(target_template_anm2),
        "source_frame_count": frame_count,
        "fps": FPS,
        "resource_count": len(packaged),
        "fbx_rotation_evaluation": "intrinsic order; XYZ => Rz @ Ry @ Rx for column vectors",
        "source_reference_strategy": "trusted Mixamo T-Pose only for evaluator validation; animated limb directions are transferred absolutely in body space",
        "target_reference_strategy": "player_1_tpp SMD bind pose for hierarchy, translations, lengths, and target bone roll",
        "first_frame_policy": "source animation frame 0 is preserved; candidates do not collapse frame 0 to target bind",
        "rotation_encoding": "engine Cayley quaternion-vector",
        "root_translation": "locked",
        "fingers_and_helpers": "locked at target bind",
        "resources": manifests,
        "fbx_intrinsic_euler_validation": euler_validation,
        "pack": verification,
    }
    (out / "custom_fbx_smd_intrinsic_absolute_manifest.json").write_text(
        json.dumps(summary, indent=2) + "\n",
        encoding="utf-8",
    )
    (out / "retarget_candidate_summary.json").write_text(
        json.dumps(reports, indent=2) + "\n",
        encoding="utf-8",
    )
    (out / "rpack_verification.json").write_text(
        json.dumps(verification, indent=2) + "\n",
        encoding="utf-8",
    )
    _write_observation_sheet(out, manifests)
    (out / "CUSTOM_FBX_INTRINSIC_ABSOLUTE_TEST_GUIDE.md").write_text(
        _guide(), encoding="utf-8"
    )
    (out / "FBX_EULER_ORDER_FINDINGS.md").write_text(
        _findings(euler_validation), encoding="utf-8"
    )
    return summary


def validate_fbx_intrinsic_euler_against_trusted_rest(
    *,
    source_rest_fbx: str | Path,
    trusted_source_rest_json: str | Path,
) -> dict[str, Any]:
    trusted_payload = json.loads(Path(trusted_source_rest_json).read_text(encoding="utf-8"))
    trusted = {
        str(row["name"]): np.asarray(row["rest_matrix"], dtype=float)
        for row in trusted_payload["bones"]
    }
    fixed_doc = _FbxDocument(Path(source_rest_fbx))
    fixed = fixed_doc.global_matrices(tick=0, use_animation=False)
    shared = sorted(set(trusted).intersection(fixed))
    fixed_rows = _matrix_error_rows(fixed, trusted, shared)

    original_euler = fbx_module._euler_matrix
    try:
        fbx_module._euler_matrix = _legacy_euler_matrix
        legacy_doc = _FbxDocument(Path(source_rest_fbx))
        legacy = legacy_doc.global_matrices(tick=0, use_animation=False)
    finally:
        fbx_module._euler_matrix = original_euler
    legacy_rows = _matrix_error_rows(legacy, trusted, shared)

    critical_names = (
        "mixamorig:RightShoulder",
        "mixamorig:RightArm",
        "mixamorig:RightForeArm",
        "mixamorig:RightHand",
    )
    critical = {}
    for name in critical_names:
        critical[name] = {
            "trusted_position": trusted[name][:3, 3].tolist(),
            "legacy_position": legacy[name][:3, 3].tolist(),
            "fixed_position": fixed[name][:3, 3].tolist(),
            "legacy_position_error": float(np.linalg.norm(legacy[name][:3, 3] - trusted[name][:3, 3])),
            "fixed_position_error": float(np.linalg.norm(fixed[name][:3, 3] - trusted[name][:3, 3])),
        }

    return {
        "status": "ok",
        "bone_count": len(shared),
        "legacy_formula": "postmultiply written order; XYZ => Rx @ Ry @ Rz",
        "fixed_formula": "premultiply written order; XYZ => Rz @ Ry @ Rx",
        "legacy_max_abs_matrix_delta": max(row["max_abs_matrix_delta"] for row in legacy_rows),
        "legacy_max_position_error": max(row["position_error"] for row in legacy_rows),
        "fixed_max_abs_matrix_delta": max(row["max_abs_matrix_delta"] for row in fixed_rows),
        "fixed_max_position_error": max(row["position_error"] for row in fixed_rows),
        "fixed_mean_abs_matrix_delta": float(np.mean([row["max_abs_matrix_delta"] for row in fixed_rows])),
        "worst_fixed_bone": max(fixed_rows, key=lambda row: row["max_abs_matrix_delta"]),
        "worst_legacy_bone": max(legacy_rows, key=lambda row: row["max_abs_matrix_delta"]),
        "critical_right_arm_positions": critical,
    }


def _legacy_euler_matrix(value: np.ndarray, order: str) -> np.ndarray:
    result = np.eye(4, dtype=float)
    axis_values = {"X": value[0], "Y": value[1], "Z": value[2]}
    for axis in order:
        result = result @ _axis_rotation(axis, axis_values[axis])
    return result


def _matrix_error_rows(
    observed: dict[str, np.ndarray],
    trusted: dict[str, np.ndarray],
    names: list[str],
) -> list[dict[str, Any]]:
    return [
        {
            "bone": name,
            "max_abs_matrix_delta": float(np.max(np.abs(observed[name] - trusted[name]))),
            "position_error": float(np.linalg.norm(observed[name][:3, 3] - trusted[name][:3, 3])),
            "rotation_max_abs_delta": float(np.max(np.abs(observed[name][:3, :3] - trusted[name][:3, :3]))),
        }
        for name in names
    ]


def _build_candidate(
    *,
    spec: CandidateSpec,
    frame_count: int,
    template_header: Anm2Header,
    descriptors: list[int],
    bind_track_rows: list[list[float]],
    names_by_descriptor: dict[int, str],
    track_index_by_name: dict[str, int],
    source_positions: list[dict[str, np.ndarray]],
    source_rest_positions: dict[str, np.ndarray],
    source_body_frames: list[np.ndarray],
    source_rest_body: np.ndarray,
    target_pose: Any,
    target_local: dict[str, np.ndarray],
    target_global: dict[str, np.ndarray],
    target_body_bind: np.ndarray,
    out_dir: Path,
) -> tuple[dict[str, Any], bytes]:
    out_dir.mkdir(parents=True, exist_ok=True)
    header = replace(template_header, frame_count=frame_count)
    values = [[list(track) for track in bind_track_rows] for _ in range(frame_count)]
    packed_flags = [[False] * 9 for _ in descriptors]

    target_body_frames = [
        _orthogonalize(target_body_bind @ source_rest_body.T @ source_body)
        if spec.torso
        else target_body_bind
        for source_body in source_body_frames
    ]
    desired_globals_by_frame: list[dict[str, np.ndarray]] = [dict() for _ in range(frame_count)]
    limb_details: dict[str, Any] = {}
    solved_limb_frames: dict[str, list[tuple[np.ndarray, np.ndarray]]] = {}

    if spec.torso:
        _add_absolute_torso_globals(
            desired_globals_by_frame,
            source_positions=source_positions,
            source_body_frames=source_body_frames,
            target_body_frames=target_body_frames,
            target_body_bind=target_body_bind,
            target_global=target_global,
        )

    if spec.clavicles:
        sides = ("right", "left") if spec.left_arm else ("right",)
        _add_absolute_clavicle_globals(
            desired_globals_by_frame,
            source_positions=source_positions,
            source_body_frames=source_body_frames,
            target_body_frames=target_body_frames,
            target_body_bind=target_body_bind,
            target_global=target_global,
            sides=sides,
        )

    limb_names: list[str] = []
    if spec.right_arm:
        limb_names.append("right_arm")
    if spec.left_arm:
        limb_names.append("left_arm")
    if spec.legs:
        limb_names.extend(("right_leg", "left_leg"))

    for limb_name in limb_names:
        detail, frames = _add_absolute_limb_globals(
            desired_globals_by_frame,
            limb=LIMBS[limb_name],
            source_positions=source_positions,
            source_body_frames=source_body_frames,
            target_body_frames=target_body_frames,
            target_global=target_global,
        )
        limb_details[limb_name] = detail
        solved_limb_frames[limb_name] = frames

    if spec.hands_feet:
        for limb_name in limb_names:
            _add_absolute_terminal_global(
                desired_globals_by_frame,
                limb=LIMBS[limb_name],
                source_positions=source_positions,
                source_body_frames=source_body_frames,
                target_body_frames=target_body_frames,
                target_global=target_global,
            )

    selected_names = sorted({name for frame in desired_globals_by_frame for name in frame})
    selected_names = [name for name in selected_names if name in track_index_by_name]
    local_overrides_by_frame: list[dict[str, np.ndarray]] = []
    for frame_index in range(frame_count):
        overrides = _desired_globals_to_local_rotations(
            target_pose,
            target_local,
            desired_globals_by_frame[frame_index],
        )
        local_overrides_by_frame.append(overrides)
        for target_name, rotation in overrides.items():
            if target_name not in track_index_by_name:
                continue
            track_index = track_index_by_name[target_name]
            vector = anm2_cayley_vector_from_quaternion(quaternion_wxyz_from_matrix(rotation))
            values[frame_index][track_index][0:3] = [float(value) for value in vector]

    for target_name in selected_names:
        packed_flags[track_index_by_name[target_name]][0:3] = [True, True, True]

    payload = build_payload_from_values(header, descriptors, values, packed_flags)
    candidate_path = out_dir / "candidate.anm2"
    candidate_path.write_bytes(payload)

    sample_frames = sorted({0, min(70, frame_count - 1), frame_count - 1})
    decoded = decode_file_samples(candidate_path, [float(frame) for frame in sample_frames])
    frame0_error = _frame0_bind_error(decoded.frames[0].tracks, bind_track_rows)
    moving = _moving_tracks(decoded, descriptors, names_by_descriptor)
    unintended = sorted(set(moving) - set(selected_names))
    hierarchy_samples = {
        str(frame): _hierarchy_sample(target_pose, target_local, local_overrides_by_frame[frame])
        for frame in sample_frames
    }
    limb_samples = {
        name: {
            str(frame): _limb_hierarchy_sample(
                hierarchy_samples[str(frame)]["positions"], LIMBS[name]
            )
            for frame in sample_frames
        }
        for name in limb_names
    }

    report = {
        "candidate_name": spec.name,
        "role": spec.role,
        "resource_name": spec.resource_name,
        "frame_count": frame_count,
        "drives": {
            "right_arm": spec.right_arm,
            "left_arm": spec.left_arm,
            "legs": spec.legs,
            "clavicles": spec.clavicles,
            "torso": spec.torso,
            "hands_feet": spec.hands_feet,
        },
        "source_pose_policy": "absolute corrected-FBX anatomical directions in the animated source body frame",
        "first_frame_policy": "source animation frame 0 retained, not replaced by target bind",
        "selected_target_tracks": selected_names,
        "frame0_max_component_delta_from_smd_bind": frame0_error,
        "moving_named_tracks": moving,
        "unintended_moving_named_tracks": unintended,
        "helper_tracks_animated": sorted(HELPER_TRACKS.intersection(moving)),
        "limb_details": limb_details,
        "hierarchy_samples": hierarchy_samples,
        "limb_samples": limb_samples,
        "candidate_path": str(candidate_path),
        "size": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest().upper(),
    }
    (out_dir / "retarget_report.json").write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )
    return report, payload


def _add_absolute_limb_globals(
    desired_globals_by_frame: list[dict[str, np.ndarray]],
    *,
    limb: LimbSpec,
    source_positions: list[dict[str, np.ndarray]],
    source_body_frames: list[np.ndarray],
    target_body_frames: list[np.ndarray],
    target_global: dict[str, np.ndarray],
) -> tuple[dict[str, Any], list[tuple[np.ndarray, np.ndarray]]]:
    target_root = target_global[limb.target_root][:3, 3]
    target_mid = target_global[limb.target_mid][:3, 3]
    target_end = target_global[limb.target_end][:3, 3]
    target_bind_frame, target_bind_angle = _two_bone_frame(
        target_mid - target_root,
        target_end - target_mid,
    )
    target_mid_bind_frame = _mid_bone_frame(
        target_end - target_mid,
        target_bind_frame[:, 2],
    )
    root_roll_offset = target_bind_frame.T @ target_global[limb.target_root][:3, :3]
    mid_roll_offset = target_mid_bind_frame.T @ target_global[limb.target_mid][:3, :3]

    solved_frames: list[tuple[np.ndarray, np.ndarray]] = []
    source_angles: list[float] = []
    source_root_body: list[np.ndarray] = []
    source_mid_body: list[np.ndarray] = []
    target_root_body: list[np.ndarray] = []
    target_mid_body: list[np.ndarray] = []

    for frame_index, row in enumerate(source_positions):
        root_to_mid = row[limb.source_mid] - row[limb.source_root]
        mid_to_end = row[limb.source_end] - row[limb.source_mid]
        source_frame, source_angle = _two_bone_frame(root_to_mid, mid_to_end)
        source_angles.append(source_angle)
        source_root_relative = source_body_frames[frame_index].T @ source_frame[:, 0]
        source_mid_relative = source_body_frames[frame_index].T @ _unit(mid_to_end)
        source_normal_relative = source_body_frames[frame_index].T @ source_frame[:, 2]

        target_root_direction = _unit(target_body_frames[frame_index] @ source_root_relative)
        target_mid_direction = _unit(target_body_frames[frame_index] @ source_mid_relative)
        target_normal = _unit(target_body_frames[frame_index] @ source_normal_relative)
        target_frame = _frame_from_primary_secondary(
            target_root_direction,
            target_mid_direction,
        )
        if float(np.dot(target_frame[:, 2], target_normal)) < 0.0:
            target_frame = target_frame.copy()
            target_frame[:, 1] *= -1.0
            target_frame[:, 2] *= -1.0
        target_mid_frame = _mid_bone_frame(target_mid_direction, target_frame[:, 2])

        desired_globals_by_frame[frame_index][limb.target_root] = _orthogonalize(
            target_frame @ root_roll_offset
        )
        desired_globals_by_frame[frame_index][limb.target_mid] = _orthogonalize(
            target_mid_frame @ mid_roll_offset
        )
        solved_frames.append((target_frame, target_mid_frame))
        source_root_body.append(source_root_relative)
        source_mid_body.append(source_mid_relative)
        target_root_body.append(target_body_frames[frame_index].T @ target_root_direction)
        target_mid_body.append(target_body_frames[frame_index].T @ target_mid_direction)

    sample_index = min(70, len(source_angles) - 1)
    detail = {
        "source_root": limb.source_root,
        "source_mid": limb.source_mid,
        "source_end": limb.source_end,
        "target_root": limb.target_root,
        "target_mid": limb.target_mid,
        "target_end": limb.target_end,
        "target_bind_angle_degrees": float(np.degrees(target_bind_angle)),
        "source_frame0_angle_degrees": float(np.degrees(source_angles[0])),
        "source_frame70_angle_degrees": float(np.degrees(source_angles[sample_index])),
        "source_frame0_root_direction_body": source_root_body[0].tolist(),
        "source_frame0_mid_direction_body": source_mid_body[0].tolist(),
        "source_frame70_root_direction_body": source_root_body[sample_index].tolist(),
        "source_frame70_mid_direction_body": source_mid_body[sample_index].tolist(),
        "target_frame70_root_direction_body": target_root_body[sample_index].tolist(),
        "target_frame70_mid_direction_body": target_mid_body[sample_index].tolist(),
        "frame70_root_direction_parity_error": float(
            np.max(np.abs(source_root_body[sample_index] - target_root_body[sample_index]))
        ),
        "frame70_mid_direction_parity_error": float(
            np.max(np.abs(source_mid_body[sample_index] - target_mid_body[sample_index]))
        ),
    }
    return detail, solved_frames


def _add_absolute_clavicle_globals(
    desired_globals_by_frame: list[dict[str, np.ndarray]],
    *,
    source_positions: list[dict[str, np.ndarray]],
    source_body_frames: list[np.ndarray],
    target_body_frames: list[np.ndarray],
    target_body_bind: np.ndarray,
    target_global: dict[str, np.ndarray],
    sides: tuple[str, ...],
) -> None:
    for side in sides:
        source_root, source_child, target_root, target_child = CLAVICLES[side]
        target_bind_frame = _frame_from_primary_secondary(
            target_global[target_child][:3, 3] - target_global[target_root][:3, 3],
            target_body_bind[:, 1],
        )
        roll_offset = target_bind_frame.T @ target_global[target_root][:3, :3]
        for frame_index, row in enumerate(source_positions):
            source_frame = _frame_from_primary_secondary(
                row[source_child] - row[source_root],
                source_body_frames[frame_index][:, 1],
            )
            source_relative = source_body_frames[frame_index].T @ source_frame
            target_frame = _orthogonalize(target_body_frames[frame_index] @ source_relative)
            desired_globals_by_frame[frame_index][target_root] = _orthogonalize(
                target_frame @ roll_offset
            )


def _add_absolute_torso_globals(
    desired_globals_by_frame: list[dict[str, np.ndarray]],
    *,
    source_positions: list[dict[str, np.ndarray]],
    source_body_frames: list[np.ndarray],
    target_body_frames: list[np.ndarray],
    target_body_bind: np.ndarray,
    target_global: dict[str, np.ndarray],
    source_globals: list[dict[str, np.ndarray]] | None = None,
    source_rest_globals: dict[str, np.ndarray] | None = None,
    source_rest_positions: dict[str, np.ndarray] | None = None,
) -> dict[str, Any]:
    """Add torso rotations without requiring optional source end markers.

    ``mixamorig:HeadTop_End`` is useful for recovering the source head's
    anatomical axis, but many otherwise complete humanoid rigs do not contain
    that leaf node. When it is absent, recover the same axis from the animated
    Head transform and its rest-pose incoming Neck -> Head direction. This
    preserves head animation instead of either crashing or silently freezing
    the target head at bind pose.
    """

    pelvis_roll_offset = target_body_bind.T @ target_global["pelvis"][:3, :3]
    for frame_index, target_body in enumerate(target_body_frames):
        desired_globals_by_frame[frame_index]["pelvis"] = _orthogonalize(
            target_body @ pelvis_roll_offset
        )

    source_right_vectors = [
        row["mixamorig:RightShoulder"] - row["mixamorig:LeftShoulder"]
        for row in source_positions
    ]
    direction_strategy_by_target: dict[str, str] = {}
    target_right = target_global["r_clavicle"][:3, 3] - target_global["l_clavicle"][:3, 3]
    for target_bone, target_child, source_bone, source_child in AXIAL_MAP:
        source_directions: list[np.ndarray] | None = None
        if source_positions and all(
            source_bone in row and source_child in row for row in source_positions
        ):
            source_directions = [
                row[source_child] - row[source_bone] for row in source_positions
            ]
            direction_strategy_by_target[target_bone] = "source_child_position"
        elif (
            source_bone == "mixamorig:Head"
            and source_globals is not None
            and source_rest_globals is not None
            and source_rest_positions is not None
            and len(source_globals) == len(source_positions)
            and source_bone in source_rest_globals
            and source_bone in source_rest_positions
            and "mixamorig:Neck" in source_rest_positions
            and all(source_bone in row for row in source_globals)
        ):
            rest_head_rotation = _orthogonalize(
                source_rest_globals[source_bone][:3, :3]
            )
            rest_incoming_direction = _unit(
                source_rest_positions[source_bone]
                - source_rest_positions["mixamorig:Neck"]
            )
            head_axis_in_local_space = rest_head_rotation.T @ rest_incoming_direction
            source_directions = [
                _orthogonalize(row[source_bone][:3, :3]) @ head_axis_in_local_space
                for row in source_globals
            ]
            direction_strategy_by_target[target_bone] = (
                "animated_head_rotation_from_rest_incoming_axis"
            )
        else:
            # All non-head AXIAL_MAP children are required humanoid roles. A
            # missing optional helper must not make an otherwise usable clip
            # unexportable; leave this one target track at bind instead.
            direction_strategy_by_target[target_bone] = (
                f"held_at_bind_missing_source_helper:{source_child}"
            )
            continue

        target_bind_frame = _frame_from_primary_secondary(
            target_global[target_child][:3, 3] - target_global[target_bone][:3, 3],
            target_right,
        )
        roll_offset = target_bind_frame.T @ target_global[target_bone][:3, :3]
        for frame_index, row in enumerate(source_positions):
            source_frame = _frame_from_primary_secondary(
                source_directions[frame_index],
                source_right_vectors[frame_index],
            )
            source_relative = source_body_frames[frame_index].T @ source_frame
            target_frame = _orthogonalize(target_body_frames[frame_index] @ source_relative)
            desired_globals_by_frame[frame_index][target_bone] = _orthogonalize(
                target_frame @ roll_offset
            )

    return {"direction_strategy_by_target": direction_strategy_by_target}


def _add_absolute_terminal_global(
    desired_globals_by_frame: list[dict[str, np.ndarray]],
    *,
    limb: LimbSpec,
    source_positions: list[dict[str, np.ndarray]],
    source_body_frames: list[np.ndarray],
    target_body_frames: list[np.ndarray],
    target_global: dict[str, np.ndarray],
) -> bool:
    if limb.source_terminal is None or limb.target_terminal is None:
        return False
    if not source_positions or not all(
        limb.source_terminal in row for row in source_positions
    ):
        # Toe bases and finger roots are optional humanoid mapping roles. The
        # limb itself remains valid; only its terminal hand/foot orientation is
        # held at bind when the direction helper is unavailable.
        return False
    target_root = target_global[limb.target_root][:3, 3]
    target_mid = target_global[limb.target_mid][:3, 3]
    target_end = target_global[limb.target_end][:3, 3]
    target_terminal = target_global[limb.target_terminal][:3, 3]
    target_limb_frame, _ = _two_bone_frame(target_mid - target_root, target_end - target_mid)
    target_terminal_bind_frame = _frame_from_primary_secondary(
        target_terminal - target_end,
        target_limb_frame[:, 2],
    )
    roll_offset = target_terminal_bind_frame.T @ target_global[limb.target_end][:3, :3]

    for frame_index, row in enumerate(source_positions):
        root_to_mid = row[limb.source_mid] - row[limb.source_root]
        mid_to_end = row[limb.source_end] - row[limb.source_mid]
        source_limb_frame, _ = _two_bone_frame(root_to_mid, mid_to_end)
        source_terminal_frame = _frame_from_primary_secondary(
            row[limb.source_terminal] - row[limb.source_end],
            source_limb_frame[:, 2],
        )
        source_relative = source_body_frames[frame_index].T @ source_terminal_frame
        target_terminal_frame = _orthogonalize(
            target_body_frames[frame_index] @ source_relative
        )
        desired_globals_by_frame[frame_index][limb.target_end] = _orthogonalize(
            target_terminal_frame @ roll_offset
        )
    return True


def _sample_source_positions(
    document: _FbxDocument,
    ticks: list[int],
) -> list[dict[str, np.ndarray]]:
    required = set(document.limb_models)
    rows: list[dict[str, np.ndarray]] = []
    for tick in ticks:
        globals_by_name = document.global_matrices(tick=tick, use_animation=True)
        rows.append({
            name: np.asarray(globals_by_name[name][:3, 3], dtype=float)
            for name in required
        })
    return rows


def _guide() -> str:
    return 

def _findings(validation: dict[str, Any]) -> str:
    return