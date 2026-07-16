from __future__ import annotations

import hashlib
import json
import math
import shutil
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Iterable

import numpy as np

from dlanm2_gui.animation_scr import AnimationScrSequence, build_animation_scr_sections
from dlanm2_gui.anm2 import Anm2Header
from dlanm2_gui.anm2_components import decode_file_samples
from dlanm2_gui.anm2_writer import build_payload_from_values
from dlanm2_gui.fbx_core import FBX_TICKS_PER_SECOND, FbxDocument
from dlanm2_gui.oracle.custom_fbx_smd_retarget_editor_rpack import (
    _manifest,
    _sequence,
    _verify,
    _write_observation_sheet,
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
class LimbSpec:
    name: str
    source_root: str
    source_mid: str
    source_end: str
    target_root: str
    target_mid: str
    target_end: str
    source_terminal: str | None = None
    target_terminal: str | None = None


LIMBS = {
    "right_arm": LimbSpec(
        "right_arm",
        "mixamorig:RightArm",
        "mixamorig:RightForeArm",
        "mixamorig:RightHand",
        "r_upperarm",
        "r_forearm",
        "r_hand",
        "mixamorig:RightHandMiddle1",
        "r_finger21",
    ),
    "left_arm": LimbSpec(
        "left_arm",
        "mixamorig:LeftArm",
        "mixamorig:LeftForeArm",
        "mixamorig:LeftHand",
        "l_upperarm",
        "l_forearm",
        "l_hand",
        "mixamorig:LeftHandMiddle1",
        "l_finger21",
    ),
    "right_leg": LimbSpec(
        "right_leg",
        "mixamorig:RightUpLeg",
        "mixamorig:RightLeg",
        "mixamorig:RightFoot",
        "r_thigh",
        "r_calf",
        "r_foot",
        "mixamorig:RightToeBase",
        "r_toebase",
    ),
    "left_leg": LimbSpec(
        "left_leg",
        "mixamorig:LeftUpLeg",
        "mixamorig:LeftLeg",
        "mixamorig:LeftFoot",
        "l_thigh",
        "l_calf",
        "l_foot",
        "mixamorig:LeftToeBase",
        "l_toebase",
    ),
}

CLAVICLES = {
    "right": ("mixamorig:RightShoulder", "mixamorig:RightArm", "r_clavicle", "r_upperarm"),
    "left": ("mixamorig:LeftShoulder", "mixamorig:LeftArm", "l_clavicle", "l_upperarm"),
}

AXIAL_MAP = (
    ("hspine", "spine1", "mixamorig:Spine", "mixamorig:Spine1"),
    ("spine", "spine1", "mixamorig:Spine", "mixamorig:Spine1"),
    ("spine1", "spine2", "mixamorig:Spine1", "mixamorig:Spine2"),
    ("spine2", "spine3", "mixamorig:Spine1", "mixamorig:Spine2"),
    ("spine3", "hspine1", "mixamorig:Spine2", "mixamorig:Neck"),
    ("hspine1", "neck1", "mixamorig:Spine2", "mixamorig:Neck"),
    ("neck", "neck1", "mixamorig:Neck", "mixamorig:Head"),
    ("neck1", "head", "mixamorig:Neck", "mixamorig:Head"),
    ("head", "headend", "mixamorig:Head", "mixamorig:HeadTop_End"),
)

HELPER_TRACKS = {
    "l_uparmtwist", "r_uparmtwist",
    "l_foretwist", "l_foretwist1", "l_foretwistt",
    "r_foretwist", "r_foretwist1", "r_foretwistt",
    "l_thightwist", "r_thightwist",
    "l_hand1", "r_hand1", "l_handholder", "r_handholder",
}


@dataclass(frozen=True, slots=True)
class CandidateSpec:
    name: str
    role: str
    resource_name: str
    elbow_knee_gain: float
    right_arm: bool = False
    left_arm: bool = False
    legs: bool = False
    clavicles: bool = False
    torso: bool = False
    hands_feet: bool = False


CANDIDATES = (
    CandidateSpec(
        "right_arm_two_vector_gain070_locked",
        "right_arm_coherent_two_vector_frame_with_elbow_plane_and_clavicle_locked",
        "dl_reanimated_smd_frame_rightarm_g070_locked",
        0.70,
        right_arm=True,
    ),
    CandidateSpec(
        "right_arm_two_vector_gain100_locked",
        "right_arm_full_source_elbow_angle_delta_with_clavicle_locked",
        "dl_reanimated_smd_frame_rightarm_g100_locked",
        1.00,
        right_arm=True,
    ),
    CandidateSpec(
        "right_arm_two_vector_gain070_clavicle",
        "right_arm_two_vector_frame_with_anatomical_clavicle_motion",
        "dl_reanimated_smd_frame_rightarm_g070_clav",
        0.70,
        right_arm=True,
        clavicles=True,
    ),
    CandidateSpec(
        "fullbody_limbs_gain070",
        "both_arms_and_legs_two_vector_frames_with_torso_locked",
        "dl_reanimated_smd_frame_full_limbs_g070",
        0.70,
        right_arm=True,
        left_arm=True,
        legs=True,
    ),
    CandidateSpec(
        "fullbody_limbs_gain100",
        "both_arms_and_legs_full_joint_angle_delta_with_torso_locked",
        "dl_reanimated_smd_frame_full_limbs_g100",
        1.00,
        right_arm=True,
        left_arm=True,
        legs=True,
    ),
    CandidateSpec(
        "fullbody_torso_limbs_gain070",
        "full_body_no_hands_or_feet_with_anatomical_torso_frames",
        "dl_reanimated_smd_frame_fullbody_g070",
        0.70,
        right_arm=True,
        left_arm=True,
        legs=True,
        clavicles=True,
        torso=True,
    ),
    CandidateSpec(
        "fullbody_torso_limbs_gain100",
        "full_body_full_joint_angle_delta_no_hands_or_feet",
        "dl_reanimated_smd_frame_fullbody_g100",
        1.00,
        right_arm=True,
        left_arm=True,
        legs=True,
        clavicles=True,
        torso=True,
    ),
    CandidateSpec(
        "fullbody_torso_limbs_handfeet_gain070",
        "full_body_primary_hands_and_feet_without_fingers_or_twist_helpers",
        "dl_reanimated_smd_frame_fullbody_hf_g070",
        0.70,
        right_arm=True,
        left_arm=True,
        legs=True,
        clavicles=True,
        torso=True,
        hands_feet=True,
    ),
)


def build_custom_fbx_smd_two_vector_fullbody_editor_rpack(
    *,
    animation_fbx: str | Path,
    canonical_smd: str | Path,
    target_template_anm2: str | Path,
    stock_writer_control_anm2: str | Path,
    out_dir: str | Path,
) -> dict[str, Any]:
    out = Path(out_dir)
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    animation = FbxDocument(Path(animation_fbx))
    ticks = animation.frame_ticks(fps=FPS)
    frame_count = len(ticks)
    source_positions = _sample_source_positions(animation, ticks)
    source_body_frames = _continuous_frames([_source_body_frame(row) for row in source_positions])

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
    control_resource = "dl_reanimated_smd_frame_stock_rebuilt_control"
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
    bind_resource = "dl_reanimated_smd_frame_target_bind_control"
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
            source_body_frames=source_body_frames,
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
                "elbow_knee_gain": spec.elbow_knee_gain,
                "moving_named_tracks": report["moving_named_tracks"],
                "unintended_moving_named_tracks": report["unintended_moving_named_tracks"],
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
        "canonical_smd": str(canonical_smd),
        "target_template_anm2": str(target_template_anm2),
        "source_frame_count": frame_count,
        "fps": FPS,
        "resource_count": len(packaged),
        "source_neutral_strategy": "Standing Greeting frame 0 relative",
        "target_reference_strategy": "player_1_tpp SMD time-0 bind pose",
        "rotation_encoding": "engine Cayley quaternion-vector",
        "primary_retarget_formula": (
            "source limb frame = [root->mid, projected mid->end, bend normal]; "
            "D = source_relative_frame0^T * source_relative_frameN; "
            "target_frameN = target_bodyN * target_limb_relative_bind * D; "
            "joint angleN = target_bind_angle + gain*(source_angleN-source_angle0)"
        ),
        "full_body_policy": {
            "limbs": "coherent two-vector frames for both arms and both legs",
            "torso": "anatomical child-direction plus shoulder-line frames",
            "hands_feet": "primary palm/toe frames relative to the solved forearm/calf frame",
            "excluded": sorted(HELPER_TRACKS),
            "root_translation": "locked",
            "fingers": "locked at bind",
        },
        "resources": manifests,
        "pack": verification,
    }
    (out / "custom_fbx_smd_two_vector_fullbody_manifest.json").write_text(
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
    (out / "CUSTOM_FBX_SMD_TWO_VECTOR_FULLBODY_TEST_GUIDE.md").write_text(
        _guide(), encoding="utf-8"
    )
    (out / "TWO_VECTOR_FULLBODY_FINDINGS.md").write_text(
        _findings(), encoding="utf-8"
    )
    return summary


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
    source_body_frames: list[np.ndarray],
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

    source_body0 = source_body_frames[0]
    body_delta_frames = [source_body0.T @ frame for frame in source_body_frames]
    target_body_frames = [
        target_body_bind @ delta if spec.torso else target_body_bind
        for delta in body_delta_frames
    ]

    desired_globals_by_frame: list[dict[str, np.ndarray]] = [dict() for _ in range(frame_count)]
    limb_details: dict[str, Any] = {}
    desired_limb_frames: dict[str, list[tuple[np.ndarray, np.ndarray]]] = {}

    if spec.torso:
        _add_torso_globals(
            desired_globals_by_frame,
            source_positions,
            source_body_frames,
            target_body_frames,
            target_body_bind,
            target_global,
        )

    if spec.clavicles:
        _add_clavicle_globals(
            desired_globals_by_frame,
            source_positions,
            source_body_frames,
            target_body_frames,
            target_body_bind,
            target_global,
            sides=("right", "left") if (spec.left_arm or spec.legs) else ("right",),
        )

    limb_names: list[str] = []
    if spec.right_arm:
        limb_names.append("right_arm")
    if spec.left_arm:
        limb_names.append("left_arm")
    if spec.legs:
        limb_names.extend(("right_leg", "left_leg"))

    for limb_name in limb_names:
        detail, frames = _add_limb_globals(
            desired_globals_by_frame,
            limb=LIMBS[limb_name],
            source_positions=source_positions,
            source_body_frames=source_body_frames,
            target_body_frames=target_body_frames,
            target_body_bind=target_body_bind,
            target_global=target_global,
            gain=spec.elbow_knee_gain,
        )
        limb_details[limb_name] = detail
        desired_limb_frames[limb_name] = frames

    if spec.hands_feet:
        for limb_name in limb_names:
            _add_terminal_global(
                desired_globals_by_frame,
                limb=LIMBS[limb_name],
                source_positions=source_positions,
                target_global=target_global,
                solved_frames=desired_limb_frames[limb_name],
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
        "elbow_knee_gain": spec.elbow_knee_gain,
        "drives": {
            "right_arm": spec.right_arm,
            "left_arm": spec.left_arm,
            "legs": spec.legs,
            "clavicles": spec.clavicles,
            "torso": spec.torso,
            "hands_feet": spec.hands_feet,
        },
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


def _add_limb_globals(
    desired_globals_by_frame: list[dict[str, np.ndarray]],
    *,
    limb: LimbSpec,
    source_positions: list[dict[str, np.ndarray]],
    source_body_frames: list[np.ndarray],
    target_body_frames: list[np.ndarray],
    target_body_bind: np.ndarray,
    target_global: dict[str, np.ndarray],
    gain: float,
) -> tuple[dict[str, Any], list[tuple[np.ndarray, np.ndarray]]]:
    source_frames: list[np.ndarray] = []
    source_angles: list[float] = []
    for row in source_positions:
        root = row[limb.source_root]
        mid = row[limb.source_mid]
        end = row[limb.source_end]
        frame, angle = _two_bone_frame(mid - root, end - mid)
        source_frames.append(frame)
        source_angles.append(angle)
    source_frames = _continuous_frames(source_frames)

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

    source_relative0 = source_body_frames[0].T @ source_frames[0]
    target_relative0 = target_body_bind.T @ target_bind_frame
    solved_frames: list[tuple[np.ndarray, np.ndarray]] = []
    output_angles: list[float] = []

    for frame_index, (source_frame, source_angle) in enumerate(zip(source_frames, source_angles)):
        source_relative = source_body_frames[frame_index].T @ source_frame
        relative_delta = source_relative0.T @ source_relative
        target_frame = _orthogonalize(
            target_body_frames[frame_index] @ target_relative0 @ relative_delta
        )
        output_angle = float(np.clip(
            target_bind_angle + gain * (source_angle - source_angles[0]),
            math.radians(2.0),
            math.radians(178.0),
        ))
        output_angles.append(output_angle)
        root_direction = target_frame[:, 0]
        bend_direction = target_frame[:, 1]
        mid_direction = _unit(
            math.cos(output_angle) * root_direction
            + math.sin(output_angle) * bend_direction
        )
        mid_frame = _mid_bone_frame(mid_direction, target_frame[:, 2])
        desired_globals_by_frame[frame_index][limb.target_root] = _orthogonalize(
            target_frame @ root_roll_offset
        )
        desired_globals_by_frame[frame_index][limb.target_mid] = _orthogonalize(
            mid_frame @ mid_roll_offset
        )
        solved_frames.append((target_frame, mid_frame))

    sample_index = min(70, len(source_angles) - 1)
    detail = {
        "source_root": limb.source_root,
        "source_mid": limb.source_mid,
        "source_end": limb.source_end,
        "target_root": limb.target_root,
        "target_mid": limb.target_mid,
        "target_end": limb.target_end,
        "gain": gain,
        "source_frame0_angle_degrees": math.degrees(source_angles[0]),
        "source_frame70_angle_degrees": math.degrees(source_angles[sample_index]),
        "target_bind_angle_degrees": math.degrees(target_bind_angle),
        "target_frame70_angle_degrees": math.degrees(output_angles[sample_index]),
        "source_frame0_plane_normal": source_frames[0][:, 2].tolist(),
        "source_frame70_plane_normal": source_frames[sample_index][:, 2].tolist(),
        "target_bind_plane_normal": target_bind_frame[:, 2].tolist(),
        "target_frame70_plane_normal": solved_frames[sample_index][0][:, 2].tolist(),
    }
    return detail, solved_frames


def _add_clavicle_globals(
    desired_globals_by_frame: list[dict[str, np.ndarray]],
    source_positions: list[dict[str, np.ndarray]],
    source_body_frames: list[np.ndarray],
    target_body_frames: list[np.ndarray],
    target_body_bind: np.ndarray,
    target_global: dict[str, np.ndarray],
    *,
    sides: Iterable[str],
) -> None:
    for side in sides:
        source_root, source_child, target_root, target_child = CLAVICLES[side]
        source_frames = _continuous_frames([
            _frame_from_primary_secondary(
                row[source_child] - row[source_root],
                source_body_frames[index][:, 1],
            )
            for index, row in enumerate(source_positions)
        ])
        target_bind_frame = _frame_from_primary_secondary(
            target_global[target_child][:3, 3] - target_global[target_root][:3, 3],
            target_body_bind[:, 1],
        )
        source_relative0 = source_body_frames[0].T @ source_frames[0]
        target_relative0 = target_body_bind.T @ target_bind_frame
        roll_offset = target_bind_frame.T @ target_global[target_root][:3, :3]
        for frame_index, source_frame in enumerate(source_frames):
            source_relative = source_body_frames[frame_index].T @ source_frame
            delta = source_relative0.T @ source_relative
            desired_frame = _orthogonalize(
                target_body_frames[frame_index] @ target_relative0 @ delta
            )
            desired_globals_by_frame[frame_index][target_root] = _orthogonalize(
                desired_frame @ roll_offset
            )


def _add_torso_globals(
    desired_globals_by_frame: list[dict[str, np.ndarray]],
    source_positions: list[dict[str, np.ndarray]],
    source_body_frames: list[np.ndarray],
    target_body_frames: list[np.ndarray],
    target_body_bind: np.ndarray,
    target_global: dict[str, np.ndarray],
) -> None:
    pelvis_roll_offset = target_body_bind.T @ target_global["pelvis"][:3, :3]
    for frame_index, target_body in enumerate(target_body_frames):
        desired_globals_by_frame[frame_index]["pelvis"] = _orthogonalize(
            target_body @ pelvis_roll_offset
        )

    source_right_vectors = [
        row["mixamorig:RightShoulder"] - row["mixamorig:LeftShoulder"]
        for row in source_positions
    ]
    target_right = target_global["r_clavicle"][:3, 3] - target_global["l_clavicle"][:3, 3]

    for target_bone, target_child, source_bone, source_child in AXIAL_MAP:
        source_frames = _continuous_frames([
            _frame_from_primary_secondary(
                row[source_child] - row[source_bone],
                source_right_vectors[index],
            )
            for index, row in enumerate(source_positions)
        ])
        target_bind_frame = _frame_from_primary_secondary(
            target_global[target_child][:3, 3] - target_global[target_bone][:3, 3],
            target_right,
        )
        source_relative0 = source_body_frames[0].T @ source_frames[0]
        target_relative0 = target_body_bind.T @ target_bind_frame
        roll_offset = target_bind_frame.T @ target_global[target_bone][:3, :3]
        for frame_index, source_frame in enumerate(source_frames):
            source_relative = source_body_frames[frame_index].T @ source_frame
            delta = source_relative0.T @ source_relative
            desired_frame = _orthogonalize(
                target_body_frames[frame_index] @ target_relative0 @ delta
            )
            desired_globals_by_frame[frame_index][target_bone] = _orthogonalize(
                desired_frame @ roll_offset
            )


def _add_terminal_global(
    desired_globals_by_frame: list[dict[str, np.ndarray]],
    *,
    limb: LimbSpec,
    source_positions: list[dict[str, np.ndarray]],
    target_global: dict[str, np.ndarray],
    solved_frames: list[tuple[np.ndarray, np.ndarray]],
) -> None:
    if limb.source_terminal is None or limb.target_terminal is None:
        return

    source_frames: list[np.ndarray] = []
    source_mid_frames: list[np.ndarray] = []
    for row in source_positions:
        root = row[limb.source_root]
        mid = row[limb.source_mid]
        end = row[limb.source_end]
        terminal = row[limb.source_terminal]
        limb_frame, _angle = _two_bone_frame(mid - root, end - mid)
        mid_frame = _mid_bone_frame(end - mid, limb_frame[:, 2])
        terminal_frame = _frame_from_primary_secondary(
            terminal - end,
            limb_frame[:, 2],
        )
        source_mid_frames.append(mid_frame)
        source_frames.append(terminal_frame)
    source_mid_frames = _continuous_frames(source_mid_frames)
    source_frames = _continuous_frames(source_frames)

    target_root = target_global[limb.target_root][:3, 3]
    target_mid = target_global[limb.target_mid][:3, 3]
    target_end = target_global[limb.target_end][:3, 3]
    target_terminal = target_global[limb.target_terminal][:3, 3]
    target_limb_frame, _ = _two_bone_frame(target_mid - target_root, target_end - target_mid)
    target_mid_bind_frame = _mid_bone_frame(target_end - target_mid, target_limb_frame[:, 2])
    target_terminal_bind_frame = _frame_from_primary_secondary(
        target_terminal - target_end,
        target_limb_frame[:, 2],
    )
    source_rel0 = source_mid_frames[0].T @ source_frames[0]
    target_rel0 = target_mid_bind_frame.T @ target_terminal_bind_frame
    roll_offset = target_terminal_bind_frame.T @ target_global[limb.target_end][:3, :3]

    for frame_index, (source_mid, source_terminal_frame) in enumerate(
        zip(source_mid_frames, source_frames)
    ):
        source_rel = source_mid.T @ source_terminal_frame
        delta = source_rel0.T @ source_rel
        target_mid_frame = solved_frames[frame_index][1]
        target_terminal_frame = _orthogonalize(target_mid_frame @ target_rel0 @ delta)
        desired_globals_by_frame[frame_index][limb.target_end] = _orthogonalize(
            target_terminal_frame @ roll_offset
        )


def _desired_globals_to_local_rotations(
    target_pose: Any,
    target_local: dict[str, np.ndarray],
    desired_globals: dict[str, np.ndarray],
) -> dict[str, np.ndarray]:
    actual_globals: dict[str, np.ndarray] = {}
    overrides: dict[str, np.ndarray] = {}
    by_index = target_pose.by_index
    for bone in target_pose.bones:
        parent_rotation = (
            np.eye(3)
            if bone.parent_index < 0
            else actual_globals[by_index[bone.parent_index].name]
        )
        if bone.name in desired_globals:
            local_rotation = _orthogonalize(parent_rotation.T @ desired_globals[bone.name])
            overrides[bone.name] = local_rotation
        else:
            local_rotation = target_local[bone.name][:3, :3]
        actual_globals[bone.name] = _orthogonalize(parent_rotation @ local_rotation)
    return overrides


def _sample_source_positions(
    animation: FbxDocument,
    ticks: list[int],
) -> list[dict[str, np.ndarray]]:
    required = set(animation.limb_models)
    rows: list[dict[str, np.ndarray]] = []
    for tick in ticks:
        globals_by_name = animation.global_matrices(tick=tick, use_animation=True)
        rows.append({
            name: np.asarray(globals_by_name[name][:3, 3], dtype=float)
            for name in required
        })
    return rows


def _source_body_frame(row: dict[str, np.ndarray]) -> np.ndarray:
    right = row["mixamorig:RightShoulder"] - row["mixamorig:LeftShoulder"]
    up = row["mixamorig:Spine2"] - row["mixamorig:Hips"]
    return _frame_from_primary_secondary(right, up)


def _target_body_frame(target_global: dict[str, np.ndarray]) -> np.ndarray:
    right = target_global["r_clavicle"][:3, 3] - target_global["l_clavicle"][:3, 3]
    up = target_global["hspine1"][:3, 3] - target_global["pelvis"][:3, 3]
    return _frame_from_primary_secondary(right, up)


def _two_bone_frame(root_to_mid: np.ndarray, mid_to_end: np.ndarray) -> tuple[np.ndarray, float]:
    root_direction = _unit(root_to_mid)
    end_direction = _unit(mid_to_end)
    bend = end_direction - float(np.dot(end_direction, root_direction)) * root_direction
    if float(np.linalg.norm(bend)) <= 1.0e-8:
        raise ValueError("two-bone chain is too straight to recover a stable bend plane")
    bend_direction = _unit(bend)
    normal = _unit(np.cross(root_direction, bend_direction))
    bend_direction = _unit(np.cross(normal, root_direction))
    angle = math.acos(float(np.clip(np.dot(root_direction, end_direction), -1.0, 1.0)))
    return np.column_stack((root_direction, bend_direction, normal)), angle


def _mid_bone_frame(direction: np.ndarray, plane_normal: np.ndarray) -> np.ndarray:
    primary = _unit(direction)
    normal = plane_normal - float(np.dot(plane_normal, primary)) * primary
    normal = _unit(normal)
    secondary = _unit(np.cross(normal, primary))
    normal = _unit(np.cross(primary, secondary))
    return np.column_stack((primary, secondary, normal))


def _frame_from_primary_secondary(primary: np.ndarray, secondary: np.ndarray) -> np.ndarray:
    x_axis = _unit(primary)
    y_axis = secondary - float(np.dot(secondary, x_axis)) * x_axis
    if float(np.linalg.norm(y_axis)) <= 1.0e-8:
        fallback = np.asarray((0.0, 1.0, 0.0), dtype=float)
        if abs(float(np.dot(fallback, x_axis))) > 0.9:
            fallback = np.asarray((1.0, 0.0, 0.0), dtype=float)
        y_axis = fallback - float(np.dot(fallback, x_axis)) * x_axis
    y_axis = _unit(y_axis)
    z_axis = _unit(np.cross(x_axis, y_axis))
    y_axis = _unit(np.cross(z_axis, x_axis))
    return np.column_stack((x_axis, y_axis, z_axis))


def _continuous_frames(frames: list[np.ndarray]) -> list[np.ndarray]:
    if not frames:
        return []
    result = [_orthogonalize(frames[0])]
    for frame in frames[1:]:
        current = _orthogonalize(frame)
        if float(np.dot(current[:, 2], result[-1][:, 2])) < 0.0:
            current = current.copy()
            current[:, 1] *= -1.0
            current[:, 2] *= -1.0
        result.append(current)
    return result


def _orthogonalize(matrix: np.ndarray) -> np.ndarray:
    u, _singular, vt = np.linalg.svd(np.asarray(matrix, dtype=float))
    result = u @ vt
    if float(np.linalg.det(result)) < 0.0:
        u[:, -1] *= -1.0
        result = u @ vt
    return result


def _unit(vector: np.ndarray) -> np.ndarray:
    value = np.asarray(vector, dtype=float)
    length = float(np.linalg.norm(value))
    if not np.isfinite(length) or length <= 1.0e-12:
        raise ValueError("cannot normalize a zero or non-finite vector")
    return value / length


def _frame0_bind_error(decoded_tracks: Any, bind_tracks: list[list[float]]) -> float:
    return max(
        abs(float(decoded_tracks[track_index][component]) - float(bind_tracks[track_index][component]))
        for track_index in range(len(bind_tracks))
        for component in range(9)
    )


def _moving_tracks(decoded: Any, descriptors: list[int], names_by_descriptor: dict[int, str]) -> list[str]:
    moving: list[str] = []
    for track_index, descriptor in enumerate(descriptors):
        values = [
            float(frame.tracks[track_index][component])
            for frame in decoded.frames
            for component in range(3)
        ]
        first = values[:3]
        if any(abs(value - first[index % 3]) > 1.0e-5 for index, value in enumerate(values)):
            moving.append(names_by_descriptor.get(descriptor, f"0x{descriptor:08X}"))
    return sorted(moving)


def _hierarchy_sample(
    target_pose: Any,
    target_local: dict[str, np.ndarray],
    overrides: dict[str, np.ndarray],
) -> dict[str, Any]:
    globals_by_name: dict[str, np.ndarray] = {}
    by_index = target_pose.by_index
    for bone in target_pose.bones:
        local = target_local[bone.name].copy()
        if bone.name in overrides:
            local[:3, :3] = overrides[bone.name]
        globals_by_name[bone.name] = (
            local
            if bone.parent_index < 0
            else globals_by_name[by_index[bone.parent_index].name] @ local
        )
    core_names = (
        "pelvis", "hspine", "spine", "spine1", "spine2", "spine3", "hspine1",
        "neck", "head",
        "l_clavicle", "l_upperarm", "l_forearm", "l_hand",
        "r_clavicle", "r_upperarm", "r_forearm", "r_hand",
        "l_thigh", "l_calf", "l_foot", "l_toebase",
        "r_thigh", "r_calf", "r_foot", "r_toebase",
    )
    return {
        "positions": {
            name: [float(value) for value in globals_by_name[name][:3, 3]]
            for name in core_names
        }
    }


def _limb_hierarchy_sample(positions: dict[str, list[float]], limb: LimbSpec) -> dict[str, Any]:
    root = np.asarray(positions[limb.target_root], dtype=float)
    mid = np.asarray(positions[limb.target_mid], dtype=float)
    end = np.asarray(positions[limb.target_end], dtype=float)
    root_direction = _unit(mid - root)
    mid_direction = _unit(end - mid)
    angle = math.degrees(math.acos(float(np.clip(np.dot(root_direction, mid_direction), -1.0, 1.0))))
    normal = _unit(np.cross(root_direction, mid_direction))
    return {
        "root": root.tolist(),
        "mid": mid.tolist(),
        "end": end.tolist(),
        "root_direction": root_direction.tolist(),
        "mid_direction": mid_direction.tolist(),
        "joint_angle_degrees": angle,
        "bend_plane_normal": normal.tolist(),
    }


def _guide() -> str:
    return """# Custom FBX SMD Two-Vector + Full-Body Test Guide

This pack replaces independent segment alignment with a coherent two-vector frame for each arm and leg. The frame carries both the segment direction and the elbow/knee bend-plane normal.

## Test order

1. `dl_reanimated_smd_frame_stock_rebuilt_control`
2. `dl_reanimated_smd_frame_target_bind_control`
3. `dl_reanimated_smd_frame_rightarm_g070_locked`
4. `dl_reanimated_smd_frame_rightarm_g100_locked`
5. `dl_reanimated_smd_frame_rightarm_g070_clav`
6. `dl_reanimated_smd_frame_full_limbs_g070`
7. `dl_reanimated_smd_frame_full_limbs_g100`
8. `dl_reanimated_smd_frame_fullbody_g070`
9. `dl_reanimated_smd_frame_fullbody_g100`
10. `dl_reanimated_smd_frame_fullbody_hf_g070`

## What to inspect at frame 70

For the three right-arm candidates, check whether the forearm rises toward the face instead of hanging downward. Compare gain 0.70 against gain 1.00 to separate bend-plane correctness from excessive elbow flexion. The clavicle candidate shows whether source shoulder motion should be included.

For full-body candidates, inspect both arms, both knees, feet, spine, neck, and head. `full_limbs` intentionally holds the torso at bind. `fullbody` adds pelvis/spine/neck/head motion. `fullbody_hf` additionally transfers primary hand and foot orientation while keeping fingers and twist/helper tracks fixed.

## Decision rules

- Right-arm frame is correct but elbow is over-folded: keep the frame solver and reduce the gain.
- Gain 1.00 is correct: use full source joint-angle delta.
- Clavicle-locked is correct but clavicle-driven is displaced: do not directly transfer Mixamo shoulder motion.
- Full limbs are stable but torso full body breaks: the remaining issue is axial-spine distribution.
- Full body is stable but palms/feet are wrong: retain limb solution and refine terminal roll separately.
- Twist/helper tracks must remain fixed in every candidate.
"""


def _findings() -> str:
    return """# Two-Vector Full-Body Retarget Findings

The previous fixed-swing candidate aligned upper-arm and forearm segments independently. That discarded their shared bend-plane frame and allowed the forearm to point downward even while the upper arm looked plausible.

The replacement constructs one coherent frame from the upper segment, projected lower segment, and their cross-product. It transfers that frame relative to the source and target torso frames, then transfers the elbow/knee angle separately. This preserves the bend plane and target bone roll while keeping the proven SMD bind pose and validated ANM2 writer unchanged.

Full-body candidates apply the same solver to both arms and both legs. Torso variants add anatomical child-direction/shoulder-line frames for pelvis, spine, neck, and head. Fingers, twist bones, handholders, and root translation remain excluded.
"""
