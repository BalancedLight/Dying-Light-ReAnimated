from __future__ import annotations

import hashlib
import json
import math
import re
import shutil
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

import numpy as np

from dlanm2_gui.animation_scr import AnimationScrSequence, build_animation_scr_sections
from dlanm2_gui.anm2 import Anm2Header
from dlanm2_gui.anm2_components import decode_file_samples
from dlanm2_gui.anm2_writer import build_payload_from_values
from dlanm2_gui.oracle.binary_fbx_mixamo import FBX_TICKS_PER_SECOND, _FbxDocument
from dlanm2_gui.oracle.custom_fbx_smd_intrinsic_absolute_editor_rpack import (
    _add_absolute_clavicle_globals,
    _add_absolute_limb_globals,
    _add_absolute_terminal_global,
    _add_absolute_torso_globals,
    validate_fbx_intrinsic_euler_against_trusted_rest,
)
from dlanm2_gui.oracle.custom_fbx_smd_retarget_editor_rpack import (
    _manifest,
    _sequence,
)
from dlanm2_gui.oracle.custom_fbx_smd_two_vector_fullbody_editor_rpack import (
    HELPER_TRACKS,
    LIMBS,
    _continuous_frames,
    _desired_globals_to_local_rotations,
    _frame0_bind_error,
    _frame_from_primary_secondary,
    _hierarchy_sample,
    _limb_hierarchy_sample,
    _moving_tracks,
    _orthogonalize,
    _source_body_frame,
    _target_body_frame,
)
from dlanm2_gui.oracle.smd_bind_pose import (
    anm2_cayley_vector_from_quaternion,
    bind_track_values,
    parse_smd_bind_pose,
    quaternion_wxyz_from_matrix,
    smd_global_matrices,
    smd_local_matrices,
)
from dlanm2_gui.rp6l import build_common_anims_multi_probe_rpack, extract_animation_library
from dlanm2_gui.retarget_profiles import apply_canonical_aliases, required_canonical_source_names

PACK_NAME = "common_anims_sp_pc.rpack"
SCRIPT_RESOURCE_NAME = "anims_man_all_DLC60"
FPS = 30
MOTION_DESCRIPTOR = 0xCCC3CDDF


@dataclass(frozen=True, slots=True)
class ClipSpec:
    path: Path
    slug: str
    display_name: str


@dataclass(frozen=True, slots=True)
class CandidateSpec:
    clip: ClipSpec
    root_policy: str

    @property
    def name(self) -> str:
        return f"{self.clip.slug}_{self.root_policy}"

    @property
    def resource_name(self) -> str:
        return f"dl_reanimated_rc_{self.clip.slug}_{self.root_policy}"

    @property
    def role(self) -> str:
        if self.root_policy == "inplace":
            return "editor_confirmed_absolute_fullbody_hands_feet_fingers_with_root_locked"
        if self.root_policy == "bip01":
            return "absolute_fullbody_hands_feet_fingers_with_skeletal_root_translation"
        return "absolute_fullbody_hands_feet_fingers_with_bip01_pose_and_ccc3_motion"


@dataclass(frozen=True, slots=True)
class FingerChainSpec:
    side: str
    digit: str
    source_hand: str
    target_hand: str
    target_root_parent: str
    source_joints: tuple[str, str, str, str]
    target_bones: tuple[str, str, str]


# Mixamo has three deform phalanges plus a terminal "4" node.  Dying Light
# exposes three ANM2 tracks per digit; the terminal Mixamo node is therefore
# intentionally omitted.
FINGER_MAP: dict[str, str] = {
    # left thumb/index/middle/ring/pinky
    "l_finger01": "mixamorig:LeftHandThumb1",
    "l_finger02": "mixamorig:LeftHandThumb2",
    "l_finger03": "mixamorig:LeftHandThumb3",
    "l_finger11": "mixamorig:LeftHandIndex1",
    "l_finger12": "mixamorig:LeftHandIndex2",
    "l_finger13": "mixamorig:LeftHandIndex3",
    "l_finger21": "mixamorig:LeftHandMiddle1",
    "l_finger22": "mixamorig:LeftHandMiddle2",
    "l_finger23": "mixamorig:LeftHandMiddle3",
    "l_finger31": "mixamorig:LeftHandRing1",
    "l_finger32": "mixamorig:LeftHandRing2",
    "l_finger33": "mixamorig:LeftHandRing3",
    "l_finger41": "mixamorig:LeftHandPinky1",
    "l_finger42": "mixamorig:LeftHandPinky2",
    "l_finger43": "mixamorig:LeftHandPinky3",
    # right thumb/index/middle/ring/pinky
    "r_finger01": "mixamorig:RightHandThumb1",
    "r_finger02": "mixamorig:RightHandThumb2",
    "r_finger03": "mixamorig:RightHandThumb3",
    "r_finger11": "mixamorig:RightHandIndex1",
    "r_finger12": "mixamorig:RightHandIndex2",
    "r_finger13": "mixamorig:RightHandIndex3",
    "r_finger21": "mixamorig:RightHandMiddle1",
    "r_finger22": "mixamorig:RightHandMiddle2",
    "r_finger23": "mixamorig:RightHandMiddle3",
    "r_finger31": "mixamorig:RightHandRing1",
    "r_finger32": "mixamorig:RightHandRing2",
    "r_finger33": "mixamorig:RightHandRing3",
    "r_finger41": "mixamorig:RightHandPinky1",
    "r_finger42": "mixamorig:RightHandPinky2",
    "r_finger43": "mixamorig:RightHandPinky3",
}


def _build_finger_chains() -> tuple[FingerChainSpec, ...]:
    rows: list[FingerChainSpec] = []
    for side, source_side, target_prefix in (
        ("left", "Left", "l"),
        ("right", "Right", "r"),
    ):
        for digit, group in (
            ("Thumb", "0"),
            ("Index", "1"),
            ("Middle", "2"),
            ("Ring", "3"),
            ("Pinky", "4"),
        ):
            rows.append(
                FingerChainSpec(
                    side=side,
                    digit=digit.lower(),
                    source_hand=f"mixamorig:{source_side}Hand",
                    target_hand=f"{target_prefix}_hand",
                    target_root_parent=(
                        f"{target_prefix}_hand1" if group in {"3", "4"}
                        else f"{target_prefix}_hand"
                    ),
                    source_joints=(
                        f"mixamorig:{source_side}Hand{digit}1",
                        f"mixamorig:{source_side}Hand{digit}2",
                        f"mixamorig:{source_side}Hand{digit}3",
                        f"mixamorig:{source_side}Hand{digit}4",
                    ),
                    target_bones=(
                        f"{target_prefix}_finger{group}1",
                        f"{target_prefix}_finger{group}2",
                        f"{target_prefix}_finger{group}3",
                    ),
                )
            )
    return tuple(rows)


FINGER_CHAINS = _build_finger_chains()
ROOT_POLICIES = ("inplace", "bip01", "motion")

_SLUG_OVERRIDES = {
    "standing_greeting": "greeting",
    "hip_hop_dancing": "hiphop",
    "right_turn_binary": "rightturn",
    "walk_strafe_left": "strafeleft",
    "crouch_to_stand": "crouchstand",
}


def build_custom_fbx_release_candidate_editor_rpack(
    *,
    animation_fbxs: Sequence[str | Path],
    source_rest_fbx: str | Path,
    trusted_source_rest_json: str | Path | None = None,
    canonical_smd: str | Path,
    target_template_anm2: str | Path,
    stock_writer_control_anm2: str | Path,
    out_dir: str | Path,
    root_policies: Sequence[str] = ("inplace", "motion"),
    ik_authoring_preset: str = "runtime",
    source_bone_aliases: Mapping[str, str] | None = None,
    animation_script_resource_name: str = SCRIPT_RESOURCE_NAME,
    include_controls: bool = True,
) -> dict[str, Any]:
    """Build the post-greeting validation pack.

    The `inplace` resources use the editor-confirmed absolute retarget with
    hands, feet, and fingers while keeping root translation/motion locked.
    The `motion` resources use the same skeletal pose and additionally split
    Mixamo hip motion between the Dying Light `bip01` pose root and the
    `0xCCC3CDDF` motion-accumulator track.
    """

    selected_root_policies = tuple(dict.fromkeys(str(value).lower() for value in root_policies))
    if not selected_root_policies:
        raise ValueError("at least one root policy is required")
    invalid_root_policies = sorted(set(selected_root_policies) - set(ROOT_POLICIES))
    if invalid_root_policies:
        raise ValueError(f"unsupported root policies: {', '.join(invalid_root_policies)}")
    if ik_authoring_preset not in {"runtime", "off"}:
        raise ValueError("ik_authoring_preset must be 'runtime' or 'off'")

    out = Path(out_dir)
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    clips = [_clip_spec(Path(value)) for value in animation_fbxs]
    if not clips:
        raise ValueError("at least one animation FBX is required")
    if len({clip.slug for clip in clips}) != len(clips):
        raise ValueError("animation FBX slugs are not unique")

    if trusted_source_rest_json:
        euler_validation = validate_fbx_intrinsic_euler_against_trusted_rest(
            source_rest_fbx=source_rest_fbx,
            trusted_source_rest_json=trusted_source_rest_json,
        )
        if euler_validation["fixed_max_abs_matrix_delta"] > 1.0e-3:
            raise ValueError("corrected FBX evaluator does not match the trusted source rest")
    else:
        euler_validation = {
            "status": "skipped",
            "reason": "no trusted source-rest matrix JSON was supplied",
            "fixed_max_abs_matrix_delta": None,
        }

    source_rest = _FbxDocument(Path(source_rest_fbx))
    _validate_source_aliases(source_rest.limb_models, source_bone_aliases)
    source_rest_globals = apply_canonical_aliases(
        source_rest.global_matrices(tick=0, use_animation=False),
        source_bone_aliases,
    )
    source_rest_positions = {
        name: np.asarray(matrix[:3, 3], dtype=float)
        for name, matrix in source_rest_globals.items()
    }
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
    motion_track_index = descriptors.index(MOTION_DESCRIPTOR)
    bip01_track_index = track_index_by_name["bip01"]

    packaged: list[tuple[str, bytes]] = []
    sequences: list[AnimationScrSequence] = []
    manifests: list[dict[str, Any]] = []
    candidate_reports: list[dict[str, Any]] = []

    if include_controls:
        _add_controls(
            packaged=packaged,
            sequences=sequences,
            manifests=manifests,
            template_header=template_header,
            descriptors=descriptors,
            bind_track_rows=bind_track_rows,
            stock_writer_control_anm2=stock_writer_control_anm2,
            canonical_smd=canonical_smd,
            fallback_descriptors=fallback_descriptors,
        )

    inventory: list[dict[str, Any]] = []
    for clip in clips:
        animation = _FbxDocument(clip.path)
        if set(animation.limb_models) != set(source_rest.limb_models):
            raise ValueError(f"{clip.path.name}: animation and source-rest skeletons differ")
        _validate_source_aliases(animation.limb_models, source_bone_aliases)
        frame_count = animation.frame_count(fps=FPS)
        ticks = [round(frame * FBX_TICKS_PER_SECOND / FPS) for frame in range(frame_count)]
        source_globals = [
            apply_canonical_aliases(
                animation.global_matrices(tick=tick, use_animation=True),
                source_bone_aliases,
            )
            for tick in ticks
        ]
        source_positions = [
            {name: np.asarray(matrix[:3, 3], dtype=float) for name, matrix in row.items()}
            for row in source_globals
        ]
        source_body_frames = _continuous_frames([
            _source_body_frame(row) for row in source_positions
        ])
        inventory.append(_clip_inventory(
            clip,
            animation=animation,
            source_positions=source_positions,
            source_body_frames=source_body_frames,
        ))

        for root_policy in selected_root_policies:
            spec = CandidateSpec(clip=clip, root_policy=root_policy)
            report, payload = _build_clip_candidate(
                spec=spec,
                animation=animation,
                frame_count=frame_count,
                template_header=template_header,
                descriptors=descriptors,
                bind_track_rows=bind_track_rows,
                names_by_descriptor=names_by_descriptor,
                track_index_by_name=track_index_by_name,
                motion_track_index=motion_track_index,
                bip01_track_index=bip01_track_index,
                source_globals=source_globals,
                source_positions=source_positions,
                source_body_frames=source_body_frames,
                source_rest_globals=source_rest_globals,
                source_rest_positions=source_rest_positions,
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
                    "source_fbx": str(clip.path),
                    "root_policy": root_policy,
                    "moving_named_tracks": report["moving_named_tracks"],
                    "moving_finger_tracks": report["moving_finger_tracks"],
                    "helper_tracks_animated": report["helper_tracks_animated"],
                    "motion_summary": report["motion_summary"],
                    "retarget_report": str(out / "candidates" / spec.name / "retarget_report.json"),
                },
            ))
            candidate_reports.append(report)

    pack_path = out / PACK_NAME
    if not animation_script_resource_name.strip():
        raise ValueError("animation script resource name cannot be empty")
    pack_path.write_bytes(build_common_anims_multi_probe_rpack(
        animation_resources=packaged,
        animation_script_resource_name=animation_script_resource_name.strip(),
        animation_script_sections=build_animation_scr_sections(sequences),
    ))
    verification = _verify_release_pack(
        pack_path,
        [name for name, _payload in packaged],
        expected_script_resource=animation_script_resource_name.strip(),
    )

    authoring_presets = _authoring_presets(
        selected_root_policies,
        ik_authoring_preset=ik_authoring_preset,
    )
    _write_json(out / "movie_authoring_presets.json", authoring_presets)

    summary = {
        "status": verification["status"],
        "milestone": "editor_confirmed_fullbody_hands_feet_absolute_retarget",
        "editor_confirmation": {
            "resource": "dl_reanimated_fbxfix_fullbody_hf_absolute",
            "result": "matches Mixamo Standing Greeting body pose in editor",
            "remaining_at_confirmation": "fingers were held at bind",
        },
        "source_rest_fbx": str(source_rest_fbx),
        "trusted_source_rest_json": str(trusted_source_rest_json) if trusted_source_rest_json else None,
        "source_bone_aliases": dict(source_bone_aliases or {}),
        "animation_script_resource_name": animation_script_resource_name.strip(),
        "include_controls": bool(include_controls),
        "canonical_smd": str(canonical_smd),
        "target_template_anm2": str(target_template_anm2),
        "fps": FPS,
        "fbx_rotation_evaluation": "intrinsic order; XYZ => Rz @ Ry @ Rx for column vectors",
        "target_reference_strategy": "player_1_tpp SMD bind pose",
        "pose_strategy": "absolute source pose in animated body space; target lengths and bind roll retained",
        "finger_strategy": "absolute source phalanx directions transferred through anatomical palm frames; ring/pinky roots use hand1 hierarchy",
        "root_policies": list(selected_root_policies),
        "ik_authoring_preset": ik_authoring_preset,
        "ik_binary_status": "IK is not stored in ANM2; the preset belongs to the movie/animation-graph authoring layer",
        "motion_strategy": {
            "inplace": "root tracks held at target bind/fallback",
            "bip01": "all source hip displacement is written to bip01; loops reset unless the consumer accumulates root motion",
            "motion": "bip01 receives pose/vertical root offset; 0xCCC3CDDF receives horizontal accumulation and body rotation delta",
        },
        "fbx_intrinsic_euler_validation": euler_validation,
        "clip_inventory": inventory,
        "resource_count": len(packaged),
        "resources": manifests,
        "pack": verification,
    }
    _write_json(out / "release_candidate_test_manifest.json", summary)
    _write_json(out / "clip_inventory.json", inventory)
    _write_json(out / "retarget_candidate_summary.json", candidate_reports)
    _write_json(out / "rpack_verification.json", verification)
    _write_observation_sheet(out, manifests)
    (out / "CUSTOM_FBX_RELEASE_CANDIDATE_TEST_GUIDE.md").write_text(
        _guide(clips, selected_root_policies, ik_authoring_preset), encoding="utf-8"
    )
    (out / "PACKAGING_REPORT.md").write_text(
        _packaging_report(summary), encoding="utf-8"
    )
    return summary


def _verify_release_pack(
    pack_path: Path,
    expected_resources: list[str],
    *,
    expected_script_resource: str,
) -> dict[str, Any]:
    library = extract_animation_library(pack_path.read_bytes())
    animations = sorted(library.animations)
    scripts = sorted(library.animation_scripts)
    missing = sorted(set(expected_resources) - set(animations))
    if expected_script_resource not in scripts:
        missing.append(expected_script_resource)
    return {
        "status": "ok" if not missing else "missing_resources",
        "pack_name": pack_path.name,
        "path": str(pack_path),
        "size": pack_path.stat().st_size,
        "sha256": _sha256(pack_path).upper(),
        "animation_count": len(animations),
        "animation_scr_count": len(scripts),
        "animations": animations,
        "animation_scripts": scripts,
        "missing_resources": missing,
        "forbidden_common_anims_PC_produced": False,
    }


def _validate_source_aliases(
    source_bones: Mapping[str, int],
    aliases: Mapping[str, str] | None,
) -> None:
    available = set(source_bones)
    if aliases:
        missing_targets = sorted(
            {source_name for source_name in aliases.values() if source_name not in available}
        )
        if missing_targets:
            raise ValueError(
                "source mapping refers to missing FBX bones: "
                + ", ".join(missing_targets)
            )
        canonical_available = available | set(aliases)
    else:
        canonical_available = available
    missing_required = sorted(
        name for name in required_canonical_source_names() if name not in canonical_available
    )
    if missing_required:
        raise ValueError(
            "source mapping is missing required humanoid roles: "
            + ", ".join(missing_required)
        )


def _add_controls(
    *,
    packaged: list[tuple[str, bytes]],
    sequences: list[AnimationScrSequence],
    manifests: list[dict[str, Any]],
    template_header: Anm2Header,
    descriptors: list[int],
    bind_track_rows: list[list[float]],
    stock_writer_control_anm2: str | Path,
    canonical_smd: str | Path,
    fallback_descriptors: list[int],
) -> None:
    control_path = Path(stock_writer_control_anm2)
    control_payload = control_path.read_bytes()
    control_header = Anm2Header.parse(control_payload)
    control_resource = "dl_reanimated_rc_stock_control"
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
    bind_resource = "dl_reanimated_rc_target_bind"
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


def _build_clip_candidate(
    *,
    spec: CandidateSpec,
    animation: _FbxDocument,
    frame_count: int,
    template_header: Anm2Header,
    descriptors: list[int],
    bind_track_rows: list[list[float]],
    names_by_descriptor: dict[int, str],
    track_index_by_name: dict[str, int],
    motion_track_index: int,
    bip01_track_index: int,
    source_globals: list[dict[str, np.ndarray]],
    source_positions: list[dict[str, np.ndarray]],
    source_body_frames: list[np.ndarray],
    source_rest_globals: dict[str, np.ndarray],
    source_rest_positions: dict[str, np.ndarray],
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
        for source_body in source_body_frames
    ]
    desired_globals_by_frame: list[dict[str, np.ndarray]] = [dict() for _ in range(frame_count)]

    _add_absolute_torso_globals(
        desired_globals_by_frame,
        source_positions=source_positions,
        source_body_frames=source_body_frames,
        target_body_frames=target_body_frames,
        target_body_bind=target_body_bind,
        target_global=target_global,
    )
    _add_absolute_clavicle_globals(
        desired_globals_by_frame,
        source_positions=source_positions,
        source_body_frames=source_body_frames,
        target_body_frames=target_body_frames,
        target_body_bind=target_body_bind,
        target_global=target_global,
        sides=("right", "left"),
    )
    limb_details: dict[str, Any] = {}
    for limb_name in ("right_arm", "left_arm", "right_leg", "left_leg"):
        detail, _frames = _add_absolute_limb_globals(
            desired_globals_by_frame,
            limb=LIMBS[limb_name],
            source_positions=source_positions,
            source_body_frames=source_body_frames,
            target_body_frames=target_body_frames,
            target_global=target_global,
        )
        limb_details[limb_name] = detail
        _add_absolute_terminal_global(
            desired_globals_by_frame,
            limb=LIMBS[limb_name],
            source_positions=source_positions,
            source_body_frames=source_body_frames,
            target_body_frames=target_body_frames,
            target_global=target_global,
        )

    finger_details = _add_anatomical_finger_globals(
        desired_globals_by_frame,
        source_globals=source_globals,
        source_positions=source_positions,
        source_rest_globals=source_rest_globals,
        source_rest_positions=source_rest_positions,
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
            track_index = track_index_by_name.get(target_name)
            if track_index is None:
                continue
            vector = anm2_cayley_vector_from_quaternion(quaternion_wxyz_from_matrix(rotation))
            values[frame_index][track_index][0:3] = [float(value) for value in vector]

    for target_name in selected_names:
        packed_flags[track_index_by_name[target_name]][0:3] = [True, True, True]

    finger_sample_frames = sorted(
        {
            0,
            min(70, frame_count - 1),
            min(frame_count // 2, frame_count - 1),
            frame_count - 1,
        }
    )
    finger_direction_parity = _finger_direction_parity_report(
        target_pose=target_pose,
        target_local=target_local,
        local_overrides_by_frame=local_overrides_by_frame,
        source_globals=source_globals,
        source_positions=source_positions,
        source_rest_globals=source_rest_globals,
        source_rest_positions=source_rest_positions,
        target_global=target_global,
        sample_frames=finger_sample_frames,
    )

    motion_summary = _apply_root_policy(
        root_policy=spec.root_policy,
        animation=animation,
        values=values,
        packed_flags=packed_flags,
        bind_track_rows=bind_track_rows,
        bip01_track_index=bip01_track_index,
        motion_track_index=motion_track_index,
        source_positions=source_positions,
        source_body_frames=source_body_frames,
        source_rest_positions=source_rest_positions,
        source_rest_body=source_rest_body,
        target_body_bind=target_body_bind,
    )

    payload = build_payload_from_values(header, descriptors, values, packed_flags)
    candidate_path = out_dir / "candidate.anm2"
    candidate_path.write_bytes(payload)

    sample_frames = sorted({0, min(frame_count // 2, frame_count - 1), frame_count - 1})
    decoded = decode_file_samples(candidate_path, [float(frame) for frame in sample_frames])
    frame0_error = _frame0_bind_error(decoded.frames[0].tracks, bind_track_rows)
    moving = _moving_tracks(decoded, descriptors, names_by_descriptor)
    finger_targets = set(FINGER_MAP)
    moving_fingers = sorted(finger_targets.intersection(moving))
    helper_animated = sorted(HELPER_TRACKS.intersection(moving))

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
        for name in ("right_arm", "left_arm", "right_leg", "left_leg")
    }

    report = {
        "candidate_name": spec.name,
        "resource_name": spec.resource_name,
        "role": spec.role,
        "source_fbx": str(spec.clip.path),
        "frame_count": frame_count,
        "fps": FPS,
        "root_policy": spec.root_policy,
        "source_pose_policy": "absolute corrected-FBX anatomical directions and orientation in animated body space",
        "first_frame_policy": "source animation frame 0 retained, not replaced by target bind",
        "finger_mapping": FINGER_MAP,
        "finger_strategy": "absolute source phalanx directions in anatomical palm space; target lengths/bind roll retained; ring and pinky parented through hand1",
        "finger_details": finger_details,
        "finger_direction_parity": finger_direction_parity,
        "selected_target_tracks": selected_names,
        "frame0_max_component_delta_from_smd_bind": frame0_error,
        "moving_named_tracks": moving,
        "moving_finger_tracks": moving_fingers,
        "helper_tracks_animated": helper_animated,
        "unintended_moving_named_tracks": sorted(
            set(moving)
            - set(selected_names)
            - (
                {"bip01", "0xCCC3CDDF"}
                if spec.root_policy == "motion"
                else ({"bip01"} if spec.root_policy == "bip01" else set())
            )
        ),
        "motion_summary": motion_summary,
        "limb_details": limb_details,
        "hierarchy_samples": hierarchy_samples,
        "limb_samples": limb_samples,
        "candidate_path": str(candidate_path),
        "size": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest().upper(),
    }
    _write_json(out_dir / "retarget_report.json", report)
    return report, payload


def _add_anatomical_finger_globals(
    desired_globals_by_frame: list[dict[str, np.ndarray]],
    *,
    source_globals: list[dict[str, np.ndarray]],
    source_positions: list[dict[str, np.ndarray]],
    source_rest_globals: dict[str, np.ndarray],
    source_rest_positions: dict[str, np.ndarray],
    target_global: dict[str, np.ndarray],
) -> dict[str, Any]:
    """Retarget fingers as an absolute source pose in anatomical palm space.

    The body retarget is absolute: source frame zero remains source frame zero.
    Fingers must follow the same rule.  The two earlier finger experiments both
    biased the result toward the curled Dying Light bind hand:

    * hand-relative orientation transfer mapped the Mixamo T-pose to the target
      bind orientation; and
    * per-segment rest corrections mapped every straight source rest segment to
      a pre-curled target bind segment.

    Both approaches add target-bind curl to the source animation.  On neutral
    locomotion frames this appears as curled or backward fingers, and on strong
    poses it can fold the digits through the palm.

    This implementation constructs an anatomical palm frame from joint
    positions on each side.  Every animated source phalanx direction is
    expressed directly in that palm frame and copied into the retargeted target
    palm frame.  No source-rest -> target-bind direction offset is added.  The
    target skeleton keeps its own root positions, phalanx lengths, and bind
    roll; only the anatomical segment direction is transferred.

    Dying Light's hierarchy is asymmetric inside each hand:

      finger01/11/21 roots -> hand
      finger31/41 roots    -> hand1

    ``_desired_globals_to_local_rotations`` resolves those real parents from
    the SMD hierarchy.  ``hand1`` stays at its bind-local transform, while the
    ring and pinky desired global rotations are converted relative to it.
    """

    if len(source_globals) != len(source_positions):
        raise ValueError("finger source matrix/position frame counts differ")

    del source_globals, source_rest_globals, source_rest_positions  # kept in API for compatibility

    active_chains = _available_finger_chains(source_positions)
    active_sides = sorted({chain.side for chain in active_chains})
    if not active_chains:
        return {
            "strategy": "absolute_anatomical_palm_direction",
            "status": "skipped",
            "reason": "source skeleton does not provide complete finger chains",
            "chain_count": 0,
            "track_count": 0,
            "segments": [],
        }

    source_palm_frames = {
        side: _continuous_frames([
            _source_palm_frame(row, side=side) for row in source_positions
        ])
        for side in active_sides
    }
    target_palm_bind = {
        side: _target_palm_frame(target_global, side=side)
        for side in active_sides
    }
    target_palm_relative_to_hand = {
        side: _orthogonalize(
            target_global[_target_hand_name(side)][:3, :3].T @ target_palm_bind[side]
        )
        for side in active_sides
    }

    target_bind_directions: dict[tuple[str, int], np.ndarray] = {}
    segment_rows: list[dict[str, Any]] = []
    for chain in active_chains:
        for segment_index, target_bone in enumerate(chain.target_bones):
            target_direction_global = _target_finger_bind_direction(
                chain=chain,
                segment_index=segment_index,
                target_global=target_global,
            )
            target_bind_directions[(chain.target_bones[0], segment_index)] = (
                target_direction_global
            )
            segment_rows.append(
                {
                    "side": chain.side,
                    "digit": chain.digit,
                    "segment_index": segment_index,
                    "source_start": chain.source_joints[segment_index],
                    "source_end": chain.source_joints[segment_index + 1],
                    "target_bone": target_bone,
                    "target_root_parent": chain.target_root_parent,
                    "target_bind_direction_palm": (
                        target_palm_bind[chain.side].T @ target_direction_global
                    ).tolist(),
                }
            )

    for frame_index, positions_by_name in enumerate(source_positions):
        for chain in active_chains:
            target_hand = chain.target_hand
            target_hand_rotation = _orthogonalize(
                desired_globals_by_frame[frame_index].get(
                    target_hand,
                    target_global[target_hand][:3, :3],
                )
            )
            target_palm_rotation = _orthogonalize(
                target_hand_rotation @ target_palm_relative_to_hand[chain.side]
            )
            source_palm_rotation = source_palm_frames[chain.side][frame_index]
            chain_key = chain.target_bones[0]

            for segment_index, target_bone in enumerate(chain.target_bones):
                source_start = chain.source_joints[segment_index]
                source_end = chain.source_joints[segment_index + 1]
                source_direction_global = _unit_vector(
                    positions_by_name[source_end] - positions_by_name[source_start]
                )
                source_direction_palm = _unit_vector(
                    source_palm_rotation.T @ source_direction_global
                )
                desired_direction_global = _unit_vector(
                    target_palm_rotation @ source_direction_palm
                )

                target_bind_direction = target_bind_directions[(chain_key, segment_index)]
                swing = _shortest_rotation(target_bind_direction, desired_direction_global)
                desired_globals_by_frame[frame_index][target_bone] = _orthogonalize(
                    swing @ target_global[target_bone][:3, :3]
                )

    return {
        "strategy": "absolute_anatomical_palm_direction",
        "chain_count": len(active_chains),
        "track_count": sum(len(chain.target_bones) for chain in active_chains),
        "source_terminal_nodes_used": True,
        "source_rest_maps_to_target_bind": False,
        "target_hand1_helpers_animated": False,
        "target_root_parents": {
            chain.target_bones[0]: chain.target_root_parent for chain in active_chains
        },
        "segments": segment_rows,
    }


def _available_finger_chains(
    source_positions: Sequence[dict[str, np.ndarray]],
) -> tuple[FingerChainSpec, ...]:
    if not source_positions:
        return ()
    available = set(source_positions[0])
    active_sides: set[str] = set()
    for side, source_side in (("left", "Left"), ("right", "Right")):
        palm_required = {
            f"mixamorig:{source_side}Hand",
            f"mixamorig:{source_side}HandIndex1",
            f"mixamorig:{source_side}HandMiddle1",
            f"mixamorig:{source_side}HandRing1",
            f"mixamorig:{source_side}HandPinky1",
        }
        if palm_required.issubset(available):
            active_sides.add(side)
    return tuple(
        chain
        for chain in FINGER_CHAINS
        if chain.side in active_sides and set(chain.source_joints).issubset(available)
    )


def _target_hand_name(side: str) -> str:
    return "l_hand" if side == "left" else "r_hand"


def _source_palm_frame(
    positions: dict[str, np.ndarray],
    *,
    side: str,
) -> np.ndarray:
    source_side = "Left" if side == "left" else "Right"
    hand = positions[f"mixamorig:{source_side}Hand"]
    roots = [
        positions[f"mixamorig:{source_side}Hand{digit}1"]
        for digit in ("Index", "Middle", "Ring", "Pinky")
    ]
    forward = np.mean(np.asarray(roots, dtype=float), axis=0) - hand
    toward_index = (
        positions[f"mixamorig:{source_side}HandIndex1"]
        - positions[f"mixamorig:{source_side}HandPinky1"]
    )
    return _frame_from_primary_secondary(forward, toward_index)


def _target_palm_frame(
    target_global: dict[str, np.ndarray],
    *,
    side: str,
) -> np.ndarray:
    prefix = "l" if side == "left" else "r"
    hand = target_global[f"{prefix}_hand"][:3, 3]
    roots = [
        target_global[f"{prefix}_finger{group}1"][:3, 3]
        for group in ("1", "2", "3", "4")
    ]
    forward = np.mean(np.asarray(roots, dtype=float), axis=0) - hand
    toward_index = (
        target_global[f"{prefix}_finger11"][:3, 3]
        - target_global[f"{prefix}_finger41"][:3, 3]
    )
    return _frame_from_primary_secondary(forward, toward_index)


def _target_finger_bind_direction(
    *,
    chain: FingerChainSpec,
    segment_index: int,
    target_global: dict[str, np.ndarray],
) -> np.ndarray:
    if segment_index < 2:
        start = chain.target_bones[segment_index]
        end = chain.target_bones[segment_index + 1]
        return _unit_vector(target_global[end][:3, 3] - target_global[start][:3, 3])

    # There is no fourth target fingertip track.  Use the third phalanx's bind
    # anatomical axis, inferred from the incoming 2->3 segment and expressed
    # through the third bone.  The source *4 node supplies the animated outgoing
    # direction for this target segment.
    third = chain.target_bones[2]
    second = chain.target_bones[1]
    return _unit_vector(target_global[third][:3, 3] - target_global[second][:3, 3])


def _unit_vector(vector: np.ndarray) -> np.ndarray:
    value = np.asarray(vector, dtype=float)
    norm = float(np.linalg.norm(value))
    if norm <= 1.0e-10:
        raise ValueError("cannot normalize a zero-length finger segment")
    return value / norm


def _shortest_rotation(source: np.ndarray, target: np.ndarray) -> np.ndarray:
    left = _unit_vector(source)
    right = _unit_vector(target)
    cosine = float(np.clip(np.dot(left, right), -1.0, 1.0))
    if cosine >= 1.0 - 1.0e-10:
        return np.eye(3, dtype=float)
    if cosine <= -1.0 + 1.0e-10:
        axis = np.cross(left, np.asarray((1.0, 0.0, 0.0), dtype=float))
        if float(np.linalg.norm(axis)) <= 1.0e-6:
            axis = np.cross(left, np.asarray((0.0, 1.0, 0.0), dtype=float))
        axis = _unit_vector(axis)
        return _orthogonalize(2.0 * np.outer(axis, axis) - np.eye(3, dtype=float))
    cross = np.cross(left, right)
    sine = float(np.linalg.norm(cross))
    skew = np.asarray(
        (
            (0.0, -cross[2], cross[1]),
            (cross[2], 0.0, -cross[0]),
            (-cross[1], cross[0], 0.0),
        ),
        dtype=float,
    )
    result = np.eye(3, dtype=float) + skew + skew @ skew * ((1.0 - cosine) / (sine * sine))
    return _orthogonalize(result)


def _finger_direction_parity_report(
    *,
    target_pose: Any,
    target_local: dict[str, np.ndarray],
    local_overrides_by_frame: list[dict[str, np.ndarray]],
    source_globals: list[dict[str, np.ndarray]],
    source_positions: list[dict[str, np.ndarray]],
    source_rest_globals: dict[str, np.ndarray],
    source_rest_positions: dict[str, np.ndarray],
    target_global: dict[str, np.ndarray],
    sample_frames: Sequence[int],
) -> dict[str, Any]:
    """Verify source/target phalanx directions in independent palm frames.

    This check deliberately does *not* compare against a rest-corrected target
    expectation.  It compares the absolute source segment coordinates in the
    source anatomical palm frame with the reconstructed target coordinates in
    the target anatomical palm frame.  That catches parent-routing mistakes,
    including the ring/pinky ``hand1`` branch, after global-to-local conversion.
    """

    del source_globals, source_rest_globals, source_rest_positions
    active_chains = _available_finger_chains(source_positions)
    active_sides = sorted({chain.side for chain in active_chains})
    if not active_chains:
        return {
            "status": "skipped",
            "strategy": "absolute_source_vs_reconstructed_target_palm_coordinates",
            "sample_frames": list(sample_frames),
            "compared_segments": 0,
            "max_angular_delta_degrees": 0.0,
            "largest_deltas": [],
        }
    target_palm_bind = {
        side: _target_palm_frame(target_global, side=side)
        for side in active_sides
    }
    target_palm_relative_to_hand = {
        side: _orthogonalize(
            target_global[_target_hand_name(side)][:3, :3].T @ target_palm_bind[side]
        )
        for side in active_sides
    }

    rows: list[dict[str, Any]] = []
    maximum = 0.0
    for frame_index in sample_frames:
        reconstructed = _reconstruct_target_globals(
            target_pose,
            target_local,
            local_overrides_by_frame[frame_index],
        )
        source_palm = {
            side: _source_palm_frame(source_positions[frame_index], side=side)
            for side in active_sides
        }
        target_palm = {
            side: _orthogonalize(
                reconstructed[_target_hand_name(side)][:3, :3]
                @ target_palm_relative_to_hand[side]
            )
            for side in active_sides
        }

        for chain in active_chains:
            for segment_index, target_bone in enumerate(chain.target_bones):
                source_start = chain.source_joints[segment_index]
                source_end = chain.source_joints[segment_index + 1]
                source_direction = _unit_vector(
                    source_positions[frame_index][source_end]
                    - source_positions[frame_index][source_start]
                )
                expected_palm = _unit_vector(
                    source_palm[chain.side].T @ source_direction
                )

                if segment_index < 2:
                    target_start = chain.target_bones[segment_index]
                    target_end = chain.target_bones[segment_index + 1]
                    actual_direction = _unit_vector(
                        reconstructed[target_end][:3, 3]
                        - reconstructed[target_start][:3, 3]
                    )
                else:
                    bind_direction = _target_finger_bind_direction(
                        chain=chain,
                        segment_index=segment_index,
                        target_global=target_global,
                    )
                    local_axis = _unit_vector(
                        target_global[target_bone][:3, :3].T @ bind_direction
                    )
                    actual_direction = _unit_vector(
                        reconstructed[target_bone][:3, :3] @ local_axis
                    )

                actual_palm = _unit_vector(
                    target_palm[chain.side].T @ actual_direction
                )
                angle = math.degrees(
                    math.acos(
                        float(np.clip(np.dot(expected_palm, actual_palm), -1.0, 1.0))
                    )
                )
                maximum = max(maximum, angle)
                rows.append(
                    {
                        "frame": frame_index,
                        "side": chain.side,
                        "digit": chain.digit,
                        "segment_index": segment_index,
                        "target_bone": target_bone,
                        "target_root_parent": chain.target_root_parent,
                        "angular_delta_degrees": angle,
                    }
                )

    return {
        "status": "ok" if maximum <= 1.0e-4 else "mismatch",
        "strategy": "absolute_source_vs_reconstructed_target_palm_coordinates",
        "sample_frames": list(sample_frames),
        "compared_segments": len(rows),
        "max_angular_delta_degrees": maximum,
        "largest_deltas": sorted(
            rows,
            key=lambda row: row["angular_delta_degrees"],
            reverse=True,
        )[:20],
    }


def _reconstruct_target_globals(
    target_pose: Any,
    target_local: dict[str, np.ndarray],
    overrides: dict[str, np.ndarray],
) -> dict[str, np.ndarray]:
    result: dict[str, np.ndarray] = {}
    by_index = target_pose.by_index
    for bone in target_pose.bones:
        local = target_local[bone.name].copy()
        if bone.name in overrides:
            local[:3, :3] = overrides[bone.name]
        result[bone.name] = (
            local
            if bone.parent_index < 0
            else result[by_index[bone.parent_index].name] @ local
        )
    return result


def _apply_root_policy(
    *,
    root_policy: str,
    animation: _FbxDocument,
    values: list[list[list[float]]],
    packed_flags: list[list[bool]],
    bind_track_rows: list[list[float]],
    bip01_track_index: int,
    motion_track_index: int,
    source_positions: list[dict[str, np.ndarray]],
    source_body_frames: list[np.ndarray],
    source_rest_positions: dict[str, np.ndarray],
    source_rest_body: np.ndarray,
    target_body_bind: np.ndarray,
) -> dict[str, Any]:
    if root_policy == "inplace":
        return {
            "policy": "inplace",
            "meters_per_fbx_unit": animation.meters_per_unit,
            "bip01_translation_dynamic": False,
            "ccc3_translation_dynamic": False,
            "ccc3_rotation_dynamic": False,
        }
    if root_policy not in {"bip01", "motion"}:
        raise ValueError(f"unknown root policy: {root_policy}")

    scale = animation.meters_per_unit
    source_to_target = _orthogonalize(target_body_bind @ source_rest_body.T)
    rest_hips = source_rest_positions["mixamorig:Hips"]
    first_hips = source_positions[0]["mixamorig:Hips"]
    first_body = source_body_frames[0]

    bip01_base = np.asarray(bind_track_rows[bip01_track_index][3:6], dtype=float)
    motion_base = np.asarray(bind_track_rows[motion_track_index][3:6], dtype=float)

    if root_policy == "bip01":
        skeletal_rows: list[np.ndarray] = []
        for frame_index, row in enumerate(source_positions):
            offset = source_to_target @ (row["mixamorig:Hips"] - rest_hips) * scale
            values[frame_index][bip01_track_index][3:6] = [
                float(value) for value in bip01_base + offset
            ]
            skeletal_rows.append(offset)
        packed_flags[bip01_track_index][3:6] = [True, True, True]
        skeletal_array = np.asarray(skeletal_rows, dtype=float)
        return {
            "policy": "bip01",
            "meters_per_fbx_unit": scale,
            "bip01_translation_dynamic": True,
            "ccc3_translation_dynamic": False,
            "ccc3_rotation_dynamic": False,
            "mapped_skeletal_root_start": skeletal_array[0].tolist(),
            "mapped_skeletal_root_end": skeletal_array[-1].tolist(),
            "mapped_skeletal_root_net": (
                skeletal_array[-1] - skeletal_array[0]
            ).tolist(),
            "mapped_skeletal_root_range": np.ptp(skeletal_array, axis=0).tolist(),
            "loop_behavior": "raw playback resets to frame 0; cumulative movie/game motion requires an accumulating consumer",
        }

    mapped_motion_rows: list[np.ndarray] = []
    mapped_pose_rows: list[np.ndarray] = []
    motion_rotation_vectors: list[np.ndarray] = []

    for frame_index, row in enumerate(source_positions):
        absolute_offset = source_to_target @ (row["mixamorig:Hips"] - rest_hips) * scale
        accumulated = source_to_target @ (row["mixamorig:Hips"] - first_hips) * scale
        horizontal = accumulated.copy()
        horizontal[1] = 0.0
        pose_offset = absolute_offset - horizontal

        values[frame_index][bip01_track_index][3:6] = [
            float(value) for value in bip01_base + pose_offset
        ]
        values[frame_index][motion_track_index][3:6] = [
            float(value) for value in motion_base + horizontal
        ]

        source_delta_global = _orthogonalize(
            source_body_frames[frame_index] @ first_body.T
        )
        target_delta_global = _orthogonalize(
            source_to_target @ source_delta_global @ source_to_target.T
        )
        rotation_vector = anm2_cayley_vector_from_quaternion(
            quaternion_wxyz_from_matrix(target_delta_global)
        )
        values[frame_index][motion_track_index][0:3] = [
            float(value) for value in rotation_vector
        ]
        mapped_motion_rows.append(horizontal)
        mapped_pose_rows.append(pose_offset)
        motion_rotation_vectors.append(rotation_vector)

    packed_flags[bip01_track_index][3:6] = [True, True, True]
    packed_flags[motion_track_index][0:6] = [True, True, True, True, True, True]

    motion_array = np.asarray(mapped_motion_rows, dtype=float)
    pose_array = np.asarray(mapped_pose_rows, dtype=float)
    rotation_array = np.asarray(motion_rotation_vectors, dtype=float)
    return {
        "policy": "motion",
        "meters_per_fbx_unit": scale,
        "bip01_translation_dynamic": True,
        "ccc3_translation_dynamic": True,
        "ccc3_rotation_dynamic": True,
        "mapped_motion_start": motion_array[0].tolist(),
        "mapped_motion_end": motion_array[-1].tolist(),
        "mapped_motion_net": (motion_array[-1] - motion_array[0]).tolist(),
        "mapped_motion_range": np.ptp(motion_array, axis=0).tolist(),
        "mapped_pose_offset_start": pose_array[0].tolist(),
        "mapped_pose_offset_end": pose_array[-1].tolist(),
        "mapped_pose_offset_range": np.ptp(pose_array, axis=0).tolist(),
        "ccc3_rotation_vector_range": np.ptp(rotation_array, axis=0).tolist(),
        "loop_behavior": "0xCCC3CDDF is authored as a motion accumulator, but continuous looping still requires UseOffsetHelper/AccumulateMotion in the consumer",
    }


def _clip_inventory(
    clip: ClipSpec,
    *,
    animation: _FbxDocument,
    source_positions: list[dict[str, np.ndarray]],
    source_body_frames: list[np.ndarray],
) -> dict[str, Any]:
    hips = np.asarray([row["mixamorig:Hips"] for row in source_positions], dtype=float)
    delta = (hips - hips[0]) * animation.meters_per_unit
    body0 = source_body_frames[0]
    body_end = source_body_frames[-1]
    relative = _orthogonalize(body_end @ body0.T)
    angle = math.degrees(math.acos(float(np.clip((np.trace(relative) - 1.0) * 0.5, -1.0, 1.0))))
    return {
        "slug": clip.slug,
        "display_name": clip.display_name,
        "path": str(clip.path),
        "sha256": _sha256(clip.path),
        "frame_count": len(source_positions),
        "fps": FPS,
        "duration_seconds": (len(source_positions) - 1) / FPS,
        "meters_per_fbx_unit": animation.meters_per_unit,
        "root_delta_meters": delta[-1].tolist(),
        "root_range_meters": np.ptp(delta, axis=0).tolist(),
        "maximum_root_displacement_meters": float(np.max(np.linalg.norm(delta, axis=1))),
        "body_orientation_net_angle_degrees": angle,
    }


def _clip_spec(path: Path) -> ClipSpec:
    if not path.exists():
        raise FileNotFoundError(path)
    normalized = re.sub(r"[^a-z0-9]+", "_", path.stem.lower()).strip("_")
    slug = _SLUG_OVERRIDES.get(normalized, normalized)
    if len(slug) > 20:
        slug = slug[:20].rstrip("_")
    return ClipSpec(path=path, slug=slug, display_name=path.stem)


def _write_observation_sheet(out: Path, manifests: list[dict[str, Any]]) -> None:
    columns = [
        "resource_name",
        "role",
        "appears_in_editor",
        "pose_matches_source",
        "fingers_match_source",
        "hands_feet_correct",
        "root_moves",
        "root_direction_correct",
        "world_yaw_correct",
        "feet_slide",
        "limbs_break",
        "notes",
        "verdict",
    ]
    rows: list[dict[str, str]] = []
    lines = ["\t".join(columns)]
    for manifest in manifests:
        row = {column: "" for column in columns}
        row["resource_name"] = str(manifest["resource_name"])
        row["role"] = str(manifest["role"])
        rows.append(row)
        lines.append("\t".join(row[column] for column in columns))
    (out / "release_candidate_observation_sheet.tsv").write_text(
        "\n".join(lines) + "\n", encoding="utf-8"
    )
    _write_json(out / "release_candidate_observation_sheet.json", rows)


def _authoring_presets(
    root_policies: Sequence[str],
    *,
    ik_authoring_preset: str,
) -> dict[str, Any]:
    presets: dict[str, Any] = {
        "schema_version": 1,
        "ik_binary_status": "ANM2 contains sampled bone transforms, not an IK enable bit",
        "ik_authoring_preset": ik_authoring_preset,
        "ik_note": (
            "Keep the movie/game animation system's runtime IK enabled"
            if ik_authoring_preset == "runtime"
            else "Disable runtime IK in the movie/graph consumer when comparing raw authored transforms"
        ),
        "root_motion_presets": {},
    }
    policy_rows = presets["root_motion_presets"]
    for policy in root_policies:
        if policy == "inplace":
            policy_rows[policy] = {
                "anm2_tracks": "bip01 and 0xCCC3CDDF remain fixed",
                "movie_use_offset_helper": False,
                "expected_loop": "repeats in place",
            }
        elif policy == "bip01":
            policy_rows[policy] = {
                "anm2_tracks": "source hip displacement is written to bip01",
                "movie_use_offset_helper": False,
                "expected_loop": "raw sequence resets to frame 0 unless the consumer accumulates skeletal root motion",
            }
        else:
            policy_rows[policy] = {
                "anm2_tracks": "vertical/pose offset on bip01; horizontal displacement and body delta on 0xCCC3CDDF",
                "movie_use_offset_helper": True,
                "expected_loop": "continuous only when CKeyAnimation.UseOffsetHelper or an equivalent graph motion accumulator is enabled",
                "verification_status": "track authoring implemented; exact editor/game accumulator binding still requires manual validation",
            }
    return presets


def _guide(
    clips: Sequence[ClipSpec],
    root_policies: Sequence[str],
    ik_authoring_preset: str,
) -> str:
    clip_lines = "\n".join(
        "- `{}:` {}".format(
            clip.display_name,
            ", ".join(
                f"`dl_reanimated_rc_{clip.slug}_{policy}`"
                for policy in root_policies
            ),
        )
        for clip in clips
    )
    policy_text = "\n".join(f"- `{policy}`" for policy in root_policies)
    return f"""# Custom FBX release-candidate movement/finger test

## Milestone protected by this pack

`dl_reanimated_fbxfix_fullbody_hf_absolute` was visually confirmed in the editor to match the Mixamo `Standing Greeting` body pose. The source FBX Euler evaluator, target SMD bind pose, hierarchy, ANM2 Cayley rotation encoding, packed writer, and RPack delivery route are therefore protected known-good systems.

This pack changes only two still-unverified areas:

1. Dying Light finger-chain mapping.
2. Root/body translation and the `0xCCC3CDDF` motion accumulator.

## Controls

1. `dl_reanimated_rc_stock_control`
2. `dl_reanimated_rc_target_bind`

## Clips

{clip_lines}

Every resource uses the editor-confirmed absolute full-body retarget. Finger rotations now transfer the actual 1→2, 2→3, and 3→4 source phalanx directions **absolutely in anatomical palm space**. No target-bind curl is added. The Dying Light hierarchy is respected: thumb/index/middle roots use `hand`, while ring/pinky roots use `hand1`.

Root policies included:

{policy_text}

`inplace` locks root motion. `bip01` writes source hip displacement to the skeletal root and is expected to reset when a raw sequence loops. `motion` splits pose/vertical offset onto `bip01` and horizontal/yaw accumulation onto `0xCCC3CDDF`.

The `motion` resource additionally applies:

```
bip01:
  source hip pose/vertical offset relative to the Mixamo T-pose

0xCCC3CDDF:
  horizontal displacement from source frame 0
  body-orientation delta from source frame 0
```

FBX units are converted using `UnitScaleFactor`; the supplied Mixamo files use 1 centimeter per unit, or 0.01 meters per unit.

## Root motion and IK authoring

The selected IK authoring preset is `{ik_authoring_preset}`. IK is not an ANM2 field; it is controlled by the movie/animation-graph consumer. The generated `movie_authoring_presets.json` records the intended settings.

For looping locomotion or turns, enable `CKeyAnimation.m_UseOffsetHelper` (or the equivalent graph motion accumulator) when testing a `motion` resource. Without an accumulating consumer, the animation correctly returns to frame 0 and therefore restarts its displacement/yaw.

## Recommended order

1. Test `greeting_inplace` first and compare the fingers to Mixamo.
2. Test `taunt_inplace` and `hiphop_inplace` for broad full-body/finger motion.
3. Test `crouchstand_inplace`, then `crouchstand_motion`, to isolate vertical root placement.
4. Test `strafeleft_inplace`, then `strafeleft_motion`, to isolate horizontal accumulation.
5. Test `rightturn_inplace`, then `rightturn_motion`, to isolate actor/world yaw ownership.

Do not reject `rightturn_motion` solely because raw ANM2 playback lacks final actor yaw. Stock evidence still indicates that gameplay, IK, SeqTrack, OffsetHelper, or actor-orientation layers may own part of the visible turn.
"""


def _packaging_report(summary: dict[str, Any]) -> str:
    pack = summary["pack"]
    return f"""# Packaging report

## Saved known-good milestone

The Dying Light editor visually confirmed that `dl_reanimated_fbxfix_fullbody_hf_absolute` matches the Mixamo `Standing Greeting` body pose. This closes the core full-body rotation-retarget problem for the tested standard Mixamo 65-bone skeleton and the standard `player_1_tpp` Dying Light skeleton.

## Frozen systems

- FBX intrinsic Euler evaluation (`XYZ` -> `Rz @ Ry @ Rx` for column vectors)
- 65/65 T-pose matrix validation
- `player_1_tpp.smd` target bind/reference hierarchy
- 69/70 ANM2 descriptor-to-bone mapping, with `0xCCC3CDDF` treated separately
- Absolute animated-pose transfer in body space
- Dying Light target lengths and bind roll
- ANM2 Cayley/quaternion-vector rotation encoding
- Engine-equivalent packed integration
- `common_anims_sp_pc.rpack` / `anims_man_all_DLC60` delivery

## New test surface

- 30 finger tracks mapped from Mixamo thumb/index/middle/ring/pinky chains
- absolute anatomical palm-space direction transfer with no bind-curl bias
- `finger31` / `finger41` root routing through `hand1` on both sides
- `bip01` root-pose translation
- `0xCCC3CDDF` horizontal motion and orientation delta

## Pack

```
file:      {pack['path']}
size:      {pack['size']} bytes
sha256:    {pack['sha256']}
animations:{pack['animation_count']}
scripts:   {pack['animation_scr_count']}
status:    {pack['status']}
```
"""


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def _write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
