"""Advanced source-bone override for humanoid bip01/OffsetHelper translation."""

from __future__ import annotations

from pathlib import Path
from typing import Any, Mapping

import numpy as np

from .anm2 import Anm2Header
from .anm2_components import decode_samples
from .fbx_core import FBX_TICKS_PER_SECOND, FbxDocument
from .oracle.custom_fbx_smd_two_vector_fullbody_editor_rpack import (
    _continuous_frames,
    _orthogonalize,
    _source_body_frame,
    _target_body_frame,
)
from .oracle.smd_bind_pose import parse_smd_bind_pose, smd_global_matrices
from .retarget_profiles import apply_canonical_aliases
from .trackmap import dl_name_hash

try:
    from .anm2_writer import build_payload_from_values
except ImportError:
    from .oracle.turn_duration_candidate_builder import (
        _build_payload_from_values as build_payload_from_values,
    )

MOTION_HELPER_DESCRIPTOR = 0xCCC3CDDF


def _frame_ticks(document: FbxDocument, fps: int, frame_count: int) -> list[int]:
    if hasattr(document, "frame_ticks"):
        ticks = list(document.frame_ticks(fps=fps))
    else:
        ticks = [
            int(round(index * FBX_TICKS_PER_SECOND / fps))
            for index in range(max(1, int(document.frame_count(fps=fps))))
        ]
    if len(ticks) == frame_count:
        return ticks
    start = int(getattr(document, "animation_start_tick", 0))
    stop = int(getattr(document, "animation_stop_tick", start))
    if stop <= start and ticks:
        start, stop = int(ticks[0]), int(ticks[-1])
    if frame_count <= 1:
        return [start]
    return [int(round(start + (stop - start) * index / (frame_count - 1))) for index in range(frame_count)]


def _dynamic_flags(values: list[list[list[float]]]) -> list[list[bool]]:
    track_count = len(values[0])
    flags: list[list[bool]] = []
    for track_index in range(track_count):
        row = []
        for component_index in range(9):
            curve = [frame[track_index][component_index] for frame in values]
            row.append(max(curve) - min(curve) > 1.0e-8)
        if any(row[6:9]):
            row[6:9] = [True, True, True]
        flags.append(row)
    return flags


def apply_root_motion_source_override(
    payload: bytes,
    *,
    animation_fbx: str | Path,
    source_rest_fbx: str | Path,
    canonical_smd: str | Path,
    source_bone: str,
    root_policy: str,
    fps: int,
    animation_stack: str | None = None,
    source_bone_aliases: Mapping[str, str] | None = None,
) -> tuple[bytes, dict[str, Any]]:
    """Replace only the source position used to author bip01/root translation.

    The target track remains ``bip01``. In motion-accumulator mode, the selected
    source bone drives the horizontal OffsetHelper translation and bip01 pose
    offset. Actor/body yaw continues to come from the established source body
    frame; using an arbitrary bone's roll as actor yaw would be unstable.
    """

    if root_policy not in {"bip01", "motion"}:
        raise ValueError("A custom motion source bone is used only by bip01 or motion policies")
    header = Anm2Header.parse(payload)
    sample = decode_samples(payload, [float(index) for index in range(header.frame_count)])
    descriptors = list(sample.descriptors)
    bip_descriptor = dl_name_hash("bip01")
    if bip_descriptor not in descriptors:
        raise ValueError("Generated ANM2 does not contain the target bip01 track")
    if root_policy == "motion" and MOTION_HELPER_DESCRIPTOR not in descriptors:
        raise ValueError("Generated ANM2 does not contain the 0xCCC3CDDF motion-helper track")
    bip_index = descriptors.index(bip_descriptor)
    motion_index = descriptors.index(MOTION_HELPER_DESCRIPTOR) if MOTION_HELPER_DESCRIPTOR in descriptors else -1
    values = [[list(track) for track in frame.tracks] for frame in sample.frames]

    animation = FbxDocument(Path(animation_fbx))
    if animation_stack:
        animation.select_animation_stack(animation_stack)
    elif len(getattr(animation, "animation_stacks", ())) == 1 and getattr(animation, "selected_animation_stack", None) is None:
        animation.select_animation_stack(animation.animation_stacks[0].name)
    rest = FbxDocument(Path(source_rest_fbx))
    ticks = _frame_ticks(animation, fps, header.frame_count)

    rest_globals = apply_canonical_aliases(
        rest.global_matrices(tick=0, use_animation=False),
        source_bone_aliases,
    )
    animated_globals = [
        apply_canonical_aliases(
            animation.global_matrices(tick=tick, use_animation=True),
            source_bone_aliases,
        )
        for tick in ticks
    ]
    missing = [
        name for name in ("mixamorig:Hips", source_bone)
        if name not in rest_globals or any(name not in frame for frame in animated_globals)
    ]
    if missing:
        raise ValueError(
            "Motion source bone is not available in both source bind and animation poses: "
            + ", ".join(sorted(set(missing)))
        )

    rest_positions = {
        name: np.asarray(matrix[:3, 3], dtype=float)
        for name, matrix in rest_globals.items()
    }
    positions = [
        {name: np.asarray(matrix[:3, 3], dtype=float) for name, matrix in frame.items()}
        for frame in animated_globals
    ]
    rest_body = _source_body_frame(rest_positions)
    body_frames = _continuous_frames([_source_body_frame(frame) for frame in positions])
    target_global = smd_global_matrices(parse_smd_bind_pose(canonical_smd))
    target_body = _target_body_frame(target_global)
    source_to_target = _orthogonalize(target_body @ rest_body.T)
    scale = float(animation.meters_per_unit)

    old_rest = rest_positions["mixamorig:Hips"]
    old_first = positions[0]["mixamorig:Hips"]
    selected_rest = rest_positions[source_bone]
    selected_first = positions[0][source_bone]
    old_absolute0 = source_to_target @ (old_first - old_rest) * scale
    bip_base = np.asarray(values[0][bip_index][3:6], dtype=float) - old_absolute0

    selected_offsets: list[np.ndarray] = []
    selected_motion: list[np.ndarray] = []
    selected_pose: list[np.ndarray] = []
    if root_policy == "bip01":
        for frame_index, frame in enumerate(positions):
            absolute = source_to_target @ (frame[source_bone] - selected_rest) * scale
            values[frame_index][bip_index][3:6] = [float(value) for value in bip_base + absolute]
            selected_offsets.append(absolute)
    else:
        motion_base = np.asarray(values[0][motion_index][3:6], dtype=float)
        for frame_index, frame in enumerate(positions):
            absolute = source_to_target @ (frame[source_bone] - selected_rest) * scale
            accumulated = source_to_target @ (frame[source_bone] - selected_first) * scale
            horizontal = accumulated.copy()
            horizontal[1] = 0.0
            pose_offset = absolute - horizontal
            values[frame_index][bip_index][3:6] = [float(value) for value in bip_base + pose_offset]
            values[frame_index][motion_index][3:6] = [float(value) for value in motion_base + horizontal]
            selected_offsets.append(absolute)
            selected_motion.append(horizontal)
            selected_pose.append(pose_offset)
        # Rotation 0..2 on 0xCCC3CDDF is deliberately preserved from the validated body-frame path.

    rebuilt_header = Anm2Header(
        format_version=header.format_version,
        unknown06=header.unknown06,
        frame_count=header.frame_count,
        track_count=header.track_count,
        unknown12=0,
        unknown14=0,
        declared_length=0,
        unknown20=header.unknown20,
        unknown24=header.unknown24,
        unknown28=header.unknown28,
    )
    rebuilt = build_payload_from_values(
        rebuilt_header,
        descriptors,
        values,
        _dynamic_flags(values),
    )
    offset_array = np.asarray(selected_offsets, dtype=float)
    report: dict[str, Any] = {
        "status": "ok",
        "source_bone": source_bone,
        "target_root_track": "bip01",
        "root_policy": root_policy,
        "orientation_policy": "preserve validated source-body-frame accumulator rotation",
        "meters_per_fbx_unit": scale,
        "mapped_absolute_start": offset_array[0].tolist(),
        "mapped_absolute_end": offset_array[-1].tolist(),
        "mapped_absolute_range": np.ptp(offset_array, axis=0).tolist(),
    }
    if selected_motion:
        motion_array = np.asarray(selected_motion, dtype=float)
        pose_array = np.asarray(selected_pose, dtype=float)
        report.update({
            "mapped_motion_start": motion_array[0].tolist(),
            "mapped_motion_end": motion_array[-1].tolist(),
            "mapped_motion_net": (motion_array[-1] - motion_array[0]).tolist(),
            "mapped_motion_range": np.ptp(motion_array, axis=0).tolist(),
            "mapped_pose_offset_range": np.ptp(pose_array, axis=0).tolist(),
        })
    return rebuilt, report


__all__ = ["MOTION_HELPER_DESCRIPTOR", "apply_root_motion_source_override"]
