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
from dlanm2_gui.oracle.binary_fbx_mixamo import FBX_TICKS_PER_SECOND, _FbxDocument, _decompose_basis
from dlanm2_gui.oracle.smd_bind_pose import (
    anm2_cayley_vector_from_quaternion,
    bind_track_values,
    build_smd_bind_pose_audit,
    parse_smd_bind_pose,
    quaternion_wxyz_from_anm2_cayley,
    quaternion_wxyz_from_matrix,
    rotation_angle_degrees,
    smd_local_matrices,
)
from dlanm2_gui.rp6l import (
    build_common_anims_multi_probe_rpack,
    extract_animation_library,
)
from dlanm2_gui.trackmap import dl_name_hash

PACK_NAME = "common_anims_sp_pc.rpack"
SCRIPT_RESOURCE_NAME = "anims_man_all_DLC60"
FPS = 30

SOURCE_TO_TARGET = {
    "mixamorig:RightShoulder": "r_clavicle",
    "mixamorig:RightArm": "r_upperarm",
    "mixamorig:RightForeArm": "r_forearm",
    "mixamorig:RightHand": "r_hand",
}


@dataclass(frozen=True, slots=True)
class RetargetCandidate:
    name: str
    role: str
    resource_name: str
    composition: str
    target_bones: tuple[str, ...]


CANDIDATES = (
    RetargetCandidate(
        "greeting_frame0_relative_forearm_local",
        "isolates_forearm_with_target_bind_postmultiplied_source_local_delta",
        "dl_reanimated_smd_greeting_forearm_local_delta",
        "local_delta",
        ("r_forearm",),
    ),
    RetargetCandidate(
        "greeting_frame0_relative_forearm_parent",
        "isolates_forearm_with_source_parent_space_delta_premultiplied_into_target_bind",
        "dl_reanimated_smd_greeting_forearm_parent_delta",
        "parent_delta",
        ("r_forearm",),
    ),
    RetargetCandidate(
        "greeting_frame0_relative_main_chain_local",
        "main_right_arm_chain_without_twist_helpers_using_local_delta",
        "dl_reanimated_smd_greeting_main_chain_local_delta",
        "local_delta",
        ("r_clavicle", "r_upperarm", "r_forearm"),
    ),
    RetargetCandidate(
        "greeting_frame0_relative_main_chain_parent",
        "main_right_arm_chain_without_twist_helpers_using_parent_delta",
        "dl_reanimated_smd_greeting_main_chain_parent_delta",
        "parent_delta",
        ("r_clavicle", "r_upperarm", "r_forearm"),
    ),
    RetargetCandidate(
        "greeting_frame0_relative_main_chain_hand_local",
        "main_right_arm_and_hand_without_twist_or_holder_helpers_using_local_delta",
        "dl_reanimated_smd_greeting_main_chain_hand_local_delta",
        "local_delta",
        ("r_clavicle", "r_upperarm", "r_forearm", "r_hand"),
    ),
    RetargetCandidate(
        "greeting_frame0_relative_main_chain_hand_parent",
        "main_right_arm_and_hand_without_twist_or_holder_helpers_using_parent_delta",
        "dl_reanimated_smd_greeting_main_chain_hand_parent_delta",
        "parent_delta",
        ("r_clavicle", "r_upperarm", "r_forearm", "r_hand"),
    ),
)


def build_custom_fbx_smd_retarget_editor_rpack(
    *,
    animation_fbx: str | Path,
    models_dir: str | Path,
    canonical_smd: str | Path,
    target_template_anm2: str | Path,
    stock_writer_control_anm2: str | Path,
    out_dir: str | Path,
) -> dict[str, Any]:
    out = Path(out_dir)
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    audit = build_smd_bind_pose_audit(
        models_dir=models_dir,
        canonical_smd=canonical_smd,
        stock_anm2=target_template_anm2,
        out_dir=out / "smd_bind_pose_audit",
    )
    if audit["canonical_descriptor_match_count"] != 69:
        raise ValueError("canonical SMD did not map the expected 69 of 70 ANM2 tracks")

    template_path = Path(target_template_anm2)
    template_payload = template_path.read_bytes()
    template_header = Anm2Header.parse(template_payload)
    template_sample = decode_file_samples(template_path, [0.0])
    descriptors = list(template_sample.descriptors)
    bind_pose = parse_smd_bind_pose(canonical_smd)
    bind_local = smd_local_matrices(bind_pose)
    bind_track_rows, names_by_descriptor, fallback_descriptors = bind_track_values(
        bind_pose,
        descriptors,
        template_sample.frames[0].tracks,
    )
    track_index_by_name = {
        name: descriptors.index(descriptor)
        for descriptor, name in names_by_descriptor.items()
    }

    animation = _FbxDocument(Path(animation_fbx))
    ticks = animation.frame_ticks(fps=FPS)
    frame_count = len(ticks)
    source_rotation_by_bone = _source_rotation_matrices(animation, ticks)

    packaged: list[tuple[str, bytes]] = []
    sequences: list[AnimationScrSequence] = []
    manifests: list[dict[str, Any]] = []

    control_path = Path(stock_writer_control_anm2)
    control_payload = control_path.read_bytes()
    control_header = Anm2Header.parse(control_payload)
    control_resource = "dl_reanimated_smd_stock_rebuilt_control"
    packaged.append((control_resource, control_payload))
    sequences.append(_sequence(control_resource, control_header.frame_count))
    manifests.append(_manifest(
        candidate_name="stock_rebuilt_engine_integration_control",
        role="known_good_writer_packaging_regression_control",
        resource_name=control_resource,
        payload=control_payload,
        frame_count=control_header.frame_count,
        extra={"source": str(control_path), "expected_editor_result": "stock animation remains visually correct"},
    ))

    bind_header = replace(template_header, frame_count=2)
    bind_values = [[list(track) for track in bind_track_rows] for _frame in range(bind_header.frame_count)]
    bind_flags = [[False] * 9 for _track in descriptors]
    bind_payload = build_payload_from_values(bind_header, descriptors, bind_values, bind_flags)
    bind_resource = "dl_reanimated_smd_target_bind_pose_control"
    packaged.append((bind_resource, bind_payload))
    sequences.append(_sequence(bind_resource, bind_header.frame_count))
    manifests.append(_manifest(
        candidate_name="canonical_smd_target_bind_pose_control",
        role="true_extracted_target_bind_pose_not_a_tpose_or_idle_frame",
        resource_name=bind_resource,
        payload=bind_payload,
        frame_count=bind_header.frame_count,
        extra={
            "canonical_smd": str(canonical_smd),
            "smd_track_count": 69,
            "fallback_descriptors": [f"0x{value:08X}" for value in fallback_descriptors],
            "expected_editor_result": "shows the mesh's actual extracted bind/reference pose; it is not expected to be a Mixamo T-pose",
        },
    ))

    candidate_reports: list[dict[str, Any]] = []
    for spec in CANDIDATES:
        candidate_dir = out / "candidates" / spec.name
        candidate_dir.mkdir(parents=True, exist_ok=True)
        header = replace(template_header, frame_count=frame_count)
        desired = [[list(track) for track in bind_track_rows] for _frame in range(frame_count)]
        packed_flags = [[False] * 9 for _track in descriptors]
        per_bone_report: list[dict[str, Any]] = []
        for target_name in spec.target_bones:
            source_name = _source_name_for_target(target_name)
            source_frames = source_rotation_by_bone[source_name]
            source_frame0 = source_frames[0]
            target_bind = bind_local[target_name][:3, :3]
            track_index = track_index_by_name[target_name]
            maximum_source_delta = 0.0
            maximum_target_delta = 0.0
            for frame_index, source_frame in enumerate(source_frames):
                target_rotation = _compose_target_rotation(
                    source_frame=source_frame,
                    source_frame0=source_frame0,
                    target_bind=target_bind,
                    mode=spec.composition,
                )
                vector = anm2_cayley_vector_from_quaternion(quaternion_wxyz_from_matrix(target_rotation))
                desired[frame_index][track_index][0:3] = [float(value) for value in vector]
                maximum_source_delta = max(maximum_source_delta, rotation_angle_degrees(source_frame0, source_frame))
                maximum_target_delta = max(maximum_target_delta, rotation_angle_degrees(target_bind, target_rotation))
            packed_flags[track_index][0:3] = [True, True, True]
            first_quaternion = quaternion_wxyz_from_anm2_cayley(desired[0][track_index][0:3])
            first_rotation = _matrix_from_quaternion_wxyz(first_quaternion)
            per_bone_report.append({
                "source_bone": source_name,
                "target_bone": target_name,
                "target_track_index": track_index,
                "target_descriptor": f"0x{descriptors[track_index]:08X}",
                "source_frame0_rotation": source_frame0.tolist(),
                "target_bind_rotation": target_bind.tolist(),
                "frame0_target_rotation": first_rotation.tolist(),
                "frame0_bind_angle_error_degrees": rotation_angle_degrees(target_bind, first_rotation),
                "max_source_frame0_relative_angle_degrees": maximum_source_delta,
                "max_target_bind_relative_angle_degrees": maximum_target_delta,
            })

        payload = build_payload_from_values(header, descriptors, desired, packed_flags)
        candidate_path = candidate_dir / "candidate.anm2"
        candidate_path.write_bytes(payload)
        decoded = decode_file_samples(candidate_path, [0.0, float(frame_count // 3), float(2 * frame_count // 3), float(frame_count - 1)])
        frame0_bind_error = _frame0_bind_error(decoded.frames[0].tracks, bind_track_rows)
        moving_tracks = _moving_tracks(decoded, descriptors, names_by_descriptor)
        unintended = sorted(set(moving_tracks) - set(spec.target_bones))
        report = {
            "candidate_name": spec.name,
            "role": spec.role,
            "resource_name": spec.resource_name,
            "composition": spec.composition,
            "frame_count": frame_count,
            "fps": FPS,
            "target_bones": list(spec.target_bones),
            "source_bones": [_source_name_for_target(name) for name in spec.target_bones],
            "frame0_max_component_delta_from_smd_bind": frame0_bind_error,
            "moving_named_tracks": moving_tracks,
            "unintended_moving_named_tracks": unintended,
            "twist_helper_tracks_intentionally_static": [
                "r_uparmtwist", "r_foretwist", "r_foretwist1", "r_foretwistt", "r_hand1", "r_handholder"
            ],
            "per_bone": per_bone_report,
            "root_translation_animated": False,
            "finger_tracks_animated": False,
            "rotation_encoding": "engine_cayley_quaternion_vector",
            "source_neutral_strategy": "animation_frame0_relative",
            "target_reference_strategy": "player_1_tpp_smd_time0_bind_pose",
            "candidate_path": str(candidate_path),
            "size": len(payload),
            "sha256": hashlib.sha256(payload).hexdigest().upper(),
        }
        (candidate_dir / "retarget_report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        manifests.append(_manifest(
            candidate_name=spec.name,
            role=spec.role,
            resource_name=spec.resource_name,
            payload=payload,
            frame_count=frame_count,
            extra={
                "composition": spec.composition,
                "target_bones": list(spec.target_bones),
                "frame0_max_component_delta_from_smd_bind": frame0_bind_error,
                "unintended_moving_named_tracks": unintended,
                "retarget_report": str(candidate_dir / "retarget_report.json"),
            },
        ))
        candidate_reports.append(report)
        packaged.append((spec.resource_name, payload))
        sequences.append(_sequence(spec.resource_name, frame_count))

    sections = build_animation_scr_sections(sequences)
    pack_path = out / PACK_NAME
    pack_path.write_bytes(build_common_anims_multi_probe_rpack(
        animation_resources=packaged,
        animation_script_resource_name=SCRIPT_RESOURCE_NAME,
        animation_script_sections=sections,
    ))
    verification = _verify(pack_path, [name for name, _payload in packaged])
    summary = {
        "status": verification["status"],
        "animation_fbx": str(animation_fbx),
        "animation_sha256": _sha256(Path(animation_fbx)),
        "canonical_smd": str(canonical_smd),
        "canonical_smd_sha256": _sha256(Path(canonical_smd)),
        "target_template_anm2": str(target_template_anm2),
        "source_frame_count": frame_count,
        "fps": FPS,
        "canonical_descriptor_match_count": audit["canonical_descriptor_match_count"],
        "canonical_descriptor_unmatched": audit["canonical_descriptor_unmatched"],
        "resource_count": len(packaged),
        "resources": manifests,
        "pack": verification,
        "corrections_from_previous_pack": [
            "replaced mislabeled idle-frame T-pose control with a true extracted SMD bind-pose control",
            "removed the source T-pose-to-standing offset by making all motion relative to Standing Greeting frame 0",
            "stopped copying arm motion onto twist/helper/handholder tracks",
            "replaced implicit quaternion-XYZ handling with the engine-backed ANM2 Cayley quaternion-vector conversion",
        ],
        "test_order": [manifest["resource_name"] for manifest in manifests],
    }
    (out / "custom_fbx_smd_retarget_manifest.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    (out / "rpack_verification.json").write_text(json.dumps(verification, indent=2) + "\n", encoding="utf-8")
    (out / "retarget_candidate_summary.json").write_text(json.dumps(candidate_reports, indent=2) + "\n", encoding="utf-8")
    _write_observation_sheet(out, manifests)
    (out / "CUSTOM_FBX_SMD_RETARGET_TEST_GUIDE.md").write_text(_guide(), encoding="utf-8")
    (out / "MODELS_ZIP_FINDINGS.md").write_text(_models_findings(audit), encoding="utf-8")
    return summary


def _source_rotation_matrices(animation: _FbxDocument, ticks: list[int]) -> dict[str, list[np.ndarray]]:
    result: dict[str, list[np.ndarray]] = {}
    for source_name in SOURCE_TO_TARGET:
        object_id = animation.limb_models.get(source_name)
        if object_id is None:
            raise ValueError(f"animation FBX is missing {source_name}")
        frames: list[np.ndarray] = []
        for tick in ticks:
            local = animation._local_matrix(object_id, tick=tick, use_animation=True)
            _translation, quaternion, _scale = _decompose_basis(local)
            frames.append(_matrix_from_quaternion_wxyz(quaternion))
        result[source_name] = frames
    return result


def _compose_target_rotation(*, source_frame: np.ndarray, source_frame0: np.ndarray, target_bind: np.ndarray, mode: str) -> np.ndarray:
    inverse_frame0 = source_frame0.T
    if mode == "local_delta":
        result = target_bind @ inverse_frame0 @ source_frame
    elif mode == "parent_delta":
        result = source_frame @ inverse_frame0 @ target_bind
    else:
        raise ValueError(f"unknown composition mode {mode!r}")
    u, _singular, vt = np.linalg.svd(result)
    orthogonal = u @ vt
    if np.linalg.det(orthogonal) < 0.0:
        u[:, -1] *= -1.0
        orthogonal = u @ vt
    return orthogonal


def _matrix_from_quaternion_wxyz(quaternion: np.ndarray) -> np.ndarray:
    w, x, y, z = (float(value) for value in quaternion)
    return np.asarray((
        (1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y - z * w), 2.0 * (x * z + y * w)),
        (2.0 * (x * y + z * w), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z - x * w)),
        (2.0 * (x * z - y * w), 2.0 * (y * z + x * w), 1.0 - 2.0 * (x * x + y * y)),
    ), dtype=float)


def _source_name_for_target(target_name: str) -> str:
    for source, target in SOURCE_TO_TARGET.items():
        if target == target_name:
            return source
    raise KeyError(target_name)


def _frame0_bind_error(decoded_tracks: Any, bind_tracks: list[list[float]]) -> float:
    return max(
        abs(float(decoded_tracks[track_index][component]) - float(bind_tracks[track_index][component]))
        for track_index in range(len(bind_tracks))
        for component in range(9)
    )


def _moving_tracks(decoded: Any, descriptors: list[int], names_by_descriptor: dict[int, str], threshold: float = 1.0e-5) -> list[str]:
    moving: list[str] = []
    for track_index, descriptor in enumerate(descriptors):
        name = names_by_descriptor.get(descriptor)
        if name is None:
            continue
        first = decoded.frames[0].tracks[track_index]
        if any(
            abs(float(frame.tracks[track_index][component]) - float(first[component])) > threshold
            for frame in decoded.frames[1:]
            for component in range(9)
        ):
            moving.append(name)
    return sorted(moving)


def _sequence(name: str, frame_count: int) -> AnimationScrSequence:
    return AnimationScrSequence(
        name=name,
        anm2_name=f"{name}.anm2",
        start_frame=0.0,
        end_frame=float(max(0, frame_count - 1)),
        fps=float(FPS),
        enabled=1,
        blend=0.5,
    )


def _manifest(*, candidate_name: str, role: str, resource_name: str, payload: bytes, frame_count: int, extra: dict[str, Any]) -> dict[str, Any]:
    return {
        "candidate_name": candidate_name,
        "role": role,
        "resource_name": resource_name,
        "frame_count": frame_count,
        "size": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest().upper(),
        **extra,
    }


def _verify(pack_path: Path, expected_resources: list[str]) -> dict[str, Any]:
    library = extract_animation_library(pack_path.read_bytes())
    animations = sorted(library.animations)
    scripts = sorted(library.animation_scripts)
    missing = sorted(set(expected_resources) - set(animations))
    if SCRIPT_RESOURCE_NAME not in scripts:
        missing.append(SCRIPT_RESOURCE_NAME)
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


def _write_observation_sheet(out: Path, manifests: list[dict[str, Any]]) -> None:
    columns = [
        "resource_name", "role", "appears_in_editor", "frame0_matches_bind_control", "only_intended_chain_moves",
        "elbow_bends_correctly", "arm_detaches_or_folds", "twist_helpers_stable", "hand_orientation_correct",
        "root_and_legs_stable", "better_than_previous_greeting", "notes", "verdict",
    ]
    lines = ["\t".join(columns)]
    json_rows = []
    for manifest in manifests:
        row = {column: "" for column in columns}
        row["resource_name"] = manifest["resource_name"]
        row["role"] = manifest["role"]
        lines.append("\t".join(str(row[column]) for column in columns))
        json_rows.append(row)
    (out / "custom_fbx_smd_retarget_observation_sheet.tsv").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (out / "custom_fbx_smd_retarget_observation_sheet.json").write_text(json.dumps(json_rows, indent=2) + "\n", encoding="utf-8")


def _guide() -> str:
    return """# Custom FBX → extracted SMD bind-pose retarget test

## What changed

The previous `tpose_reference_control` was mislabeled: it contained a static frame from `infected_idle_01`, not the uploaded Mixamo T-pose or the mesh bind pose. This pack replaces it with the actual time-0 bind skeleton extracted from `player_1_tpp.smd`.

The greeting is now made relative to **Standing Greeting frame 0**. The Mixamo T-pose-to-standing offset is no longer added on top of a standing Dying Light pose. Only primary deform tracks are animated; `r_uparmtwist`, forearm twist tracks, `r_hand1`, and `r_handholder` remain at bind.

## Test order

1. `dl_reanimated_smd_stock_rebuilt_control`
   - Must remain correct. This protects the known-good writer and RPack route.

2. `dl_reanimated_smd_target_bind_pose_control`
   - This is the extracted mesh bind/reference pose. It is **not expected to be a Mixamo T-pose or a stock idle**.
   - It must be stable and connected, without exploding limbs.

3. Forearm-only comparison:
   - `dl_reanimated_smd_greeting_forearm_local_delta`
   - `dl_reanimated_smd_greeting_forearm_parent_delta`

4. Main-chain comparison:
   - `dl_reanimated_smd_greeting_main_chain_local_delta`
   - `dl_reanimated_smd_greeting_main_chain_parent_delta`

5. Add the hand only after one main-chain version behaves:
   - `dl_reanimated_smd_greeting_main_chain_hand_local_delta`
   - `dl_reanimated_smd_greeting_main_chain_hand_parent_delta`

## Decision rules

- Bind control malformed: SMD → ANM2 absolute bind conversion still has a target-space problem.
- Bind control stable, both forearm variants malformed: target track or ANM2 rotation semantic mapping is wrong.
- One forearm variant works: that multiplication order is the correct first-order retarget composition.
- Forearm works but main chain fails: clavicle/upper-arm parent-space basis must be handled per hierarchy.
- Main chain works but hand variant fails: hand basis/finger/helper ownership is the next target.
- Do not judge this pass by root motion, world yaw, fingers, or twist deformation; those are deliberately excluded.
"""


def _models_findings(audit: dict[str, Any]) -> str:
    return f"""# `models.zip` skeleton findings

- Canonical target: `{audit['canonical_smd']}`
- Stock ANM2 tracks: {audit['stock_track_count']}
- Exact SMD-name hash matches: {audit['canonical_descriptor_match_count']} / {audit['stock_track_count']}
- Unmatched track: {', '.join(audit['canonical_descriptor_unmatched'])}
- Standard TPP models sharing the canonical core: {audit['standard_tpp_descriptor_core_count']}
- SMD → ASCII maximum global-position error: {audit['canonical_ascii_validation']['max_position_delta']:.9g}
- Proven SMD Euler convention: extrinsic XYZ radians (`Rz @ Ry @ Rx` for column vectors)

`player_zombie_tpp` is a distinct player-zombie/Night-Hunter rig and is not used as the standard target skeleton. The extracted TPP SMD is now the authoritative target bind-pose source for the 69 named ANM2 tracks. The remaining `0xCCC3CDDF` track has no mesh bone and retains its stock donor value.
"""


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()
