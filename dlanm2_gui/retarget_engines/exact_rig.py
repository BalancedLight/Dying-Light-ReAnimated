"""Direct same-skeleton FBX to Chrome Rig ANM2 export."""

from __future__ import annotations

import math
from pathlib import Path
from typing import Any

import numpy as np

from ..anm2_components import decode_samples
from ..anm2_writer import build_payload_from_values
from ..chrome_rig import ChromeRig
from ..chrome_rig_builder import decompose_local_matrix
from ..oracle.binary_fbx_mixamo import FBX_TICKS_PER_SECOND, _FbxDocument
from ..oracle.smd_bind_pose import anm2_cayley_vector_from_quaternion
from .base import RetargetBuild


ExactRigBuild = RetargetBuild


def _validate_exact_skeleton(
    rig: ChromeRig,
    document: _FbxDocument,
    *,
    meters_per_unit: float,
) -> dict[str, float]:
    source_names = set(document.limb_models)
    target_names = {bone.name for bone in rig.bones}
    missing = sorted(target_names - source_names)
    extra = sorted(source_names - target_names)
    errors: list[str] = []
    if missing:
        errors.append("missing target bones: " + ", ".join(missing[:12]))
    if extra:
        errors.append("source has extra bones: " + ", ".join(extra[:12]))
    by_index = {bone.index: bone for bone in rig.bones}
    for bone in rig.bones:
        expected_parent = (
            None if bone.parent_index < 0 else by_index[bone.parent_index].name
        )
        actual_parent = document.parent_by_name.get(bone.name)
        if actual_parent != expected_parent:
            errors.append(
                f"parent mismatch for {bone.name!r}: expected {expected_parent!r}, "
                f"found {actual_parent!r}"
            )
    maximum_translation = 0.0
    maximum_rotation_degrees = 0.0
    maximum_scale = 0.0
    if not errors:
        for bone in rig.bones:
            local = document._local_matrix(
                document.limb_models[bone.name], tick=0, use_animation=False
            )
            translation, quaternion, scale = decompose_local_matrix(local)
            target_translation = np.asarray(bone.bind_translation, dtype=float)
            source_translation = translation * meters_per_unit
            translation_delta = float(np.linalg.norm(source_translation - target_translation))
            target_quaternion = np.asarray(bone.bind_rotation_wxyz, dtype=float)
            quaternion_dot = abs(float(np.dot(quaternion, target_quaternion)))
            quaternion_dot = max(-1.0, min(1.0, quaternion_dot))
            rotation_delta = math.degrees(2.0 * math.acos(quaternion_dot))
            scale_delta = float(
                np.max(np.abs(scale - np.asarray(bone.bind_scale, dtype=float)))
            )
            maximum_translation = max(maximum_translation, translation_delta)
            maximum_rotation_degrees = max(maximum_rotation_degrees, rotation_delta)
            maximum_scale = max(maximum_scale, scale_delta)
            if translation_delta > 1.0e-4 or rotation_delta > 0.1 or scale_delta > 1.0e-4:
                errors.append(
                    f"bind mismatch for {bone.name!r}: translation {translation_delta:.6g} m, "
                    f"rotation {rotation_delta:.6g}°, scale {scale_delta:.6g}"
                )
    if errors:
        raise ValueError("Exact-rig skeleton mismatch:\n- " + "\n- ".join(errors))
    return {
        "max_translation_meters": maximum_translation,
        "max_rotation_degrees": maximum_rotation_degrees,
        "max_scale_component": maximum_scale,
    }


def build_exact_rig_anm2(
    animation_fbx: str | Path,
    rig: ChromeRig,
    *,
    fps: int | None = None,
    document_factory: Any = _FbxDocument,
) -> ExactRigBuild:
    rig.validate().require_valid()
    sample_fps = int(fps or rig.writer_profile.default_fps)
    if not 1 <= sample_fps <= 240:
        raise ValueError("Exact-rig sample FPS must be between 1 and 240")
    source = Path(animation_fbx)
    document = document_factory(source)
    source_meters = float(document.meters_per_unit)
    bind_compatibility = _validate_exact_skeleton(
        rig, document, meters_per_unit=source_meters
    )
    frame_count = max(2, int(document.frame_count(fps=sample_fps)))
    ticks = [
        int(round(frame * FBX_TICKS_PER_SECOND / sample_fps))
        for frame in range(frame_count)
    ]
    values: list[list[list[float]]] = []
    for tick in ticks:
        rows_by_descriptor: dict[int, list[float]] = {}
        for bone in rig.bones:
            object_id = document.limb_models[bone.name]
            local = document._local_matrix(object_id, tick=tick, use_animation=True)
            translation, quaternion, scale = decompose_local_matrix(local)
            rotation = anm2_cayley_vector_from_quaternion(quaternion)
            rows_by_descriptor[bone.descriptor] = [
                *map(float, rotation),
                *(float(v * source_meters) for v in translation),
                *map(float, scale),
            ]
        frame = [
            rows_by_descriptor.get(
                descriptor,
                [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0],
            )
            for descriptor in rig.descriptors
        ]
        values.append(frame)
    packed_flags: list[list[bool]] = []
    for track_index in range(len(rig.descriptors)):
        flags = []
        for component_index in range(9):
            curve = [frame[track_index][component_index] for frame in values]
            flags.append(max(curve) - min(curve) > 1.0e-8)
        if any(flags[6:9]):
            flags[6:9] = [True, True, True]
        packed_flags.append(flags)
    header = rig.make_header(frame_count=frame_count)
    payload = build_payload_from_values(header, rig.descriptors, values, packed_flags)
    sample_frames = sorted({0, frame_count // 2, frame_count - 1})
    decoded = decode_samples(payload, [float(value) for value in sample_frames])
    maximum_error = 0.0
    for decoded_frame, frame_index in zip(decoded.frames, sample_frames):
        expected = values[frame_index]
        for actual_track, expected_track in zip(decoded_frame.tracks, expected):
            maximum_error = max(
                maximum_error,
                max(abs(float(a) - float(b)) for a, b in zip(actual_track, expected_track)),
            )
    names_by_descriptor = {bone.descriptor: bone.name for bone in rig.bones}
    moving_tracks = [
        names_by_descriptor.get(rig.descriptors[index], f"extra:{index}")
        for index, flags in enumerate(packed_flags)
        if any(flags)
    ]
    return ExactRigBuild(
        payload=payload,
        frame_count=frame_count,
        report={
            "retarget_mode": "exact",
            "engine": "ExactRigRetargetEngine",
            "source_fbx": str(source),
            "target_rig_id": rig.rig_id,
            "target_rig_name": rig.name,
            "target_skeleton_hash": rig.skeleton_hash,
            "frame_count": frame_count,
            "fps": sample_fps,
            "track_count": len(rig.descriptors),
            "bone_count": len(rig.bones),
            "moving_tracks": moving_tracks,
            "sample_frames": sample_frames,
            "decoded_max_component_error": maximum_error,
            "source_unit_meters": source_meters,
            "bind_compatibility": bind_compatibility,
            "root_policy": "exact_local_transforms",
            "candidate_path": None,
        },
    )


__all__ = ["ExactRigBuild", "build_exact_rig_anm2"]
