"""Direct same-skeleton FBX to Chrome Rig ANM2 export."""

from __future__ import annotations

import base64
import json
import math
from pathlib import Path
import re
from typing import Any
import zlib

import numpy as np

from ..anm2_components import decode_samples
from ..anm2_writer import build_payload_from_values
from ..chrome_rig import ChromeRig
from ..chrome_rig_builder import decompose_local_matrix
from ..oracle.binary_fbx_mixamo import (
    FBX_TICKS_PER_SECOND,
    _FbxDocument,
    _properties70,
)
from ..oracle.smd_bind_pose import anm2_cayley_vector_from_quaternion
from .base import RetargetBuild


ExactRigBuild = RetargetBuild


_SYNTHETIC_TRACK = re.compile(r"^DLR_(?:OffsetHelper_|Track_)([0-9A-Fa-f]{8})$")
_MOTION_HELPER_DESCRIPTOR = 0xCCC3CDDF
_Y_UP_TO_BLENDER = np.asarray(
    ((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)),
    dtype=float,
)


def _is_dlr_native_export(document: _FbxDocument) -> bool:
    for object_id in getattr(document, "null_models", {}).values():
        node = document.object_by_id.get(object_id)
        if node is not None and (_properties70(node).get("dlr_native_anm2_export") or [0])[0]:
            return True
    return False


def _dlr_native_metadata(document: _FbxDocument) -> dict[str, Any]:
    for object_id in getattr(document, "null_models", {}).values():
        node = document.object_by_id.get(object_id)
        if node is None:
            continue
        encoded = (_properties70(node).get("dlr_native_metadata_zlib_b64") or [""])[0]
        if encoded:
            return json.loads(zlib.decompress(base64.b64decode(str(encoded))).decode("utf-8"))
    return {}


def _bind_local_matrix(bone) -> np.ndarray:
    w, x, y, z = map(float, bone.bind_rotation_wxyz)
    rotation = np.asarray(
        [
            [1 - 2 * (y*y + z*z), 2 * (x*y - z*w), 2 * (x*z + y*w)],
            [2 * (x*y + z*w), 1 - 2 * (x*x + z*z), 2 * (y*z - x*w)],
            [2 * (x*z - y*w), 2 * (y*z + x*w), 1 - 2 * (x*x + y*y)],
        ],
        dtype=float,
    )
    result = np.eye(4, dtype=float)
    result[:3, :3] = rotation @ np.diag(np.asarray(bone.bind_scale, dtype=float))
    result[:3, 3] = np.asarray(bone.bind_translation, dtype=float)
    return result


def _row_matrix(row: dict[str, Any]) -> np.ndarray:
    class _Row:
        bind_translation = tuple(row["translation"])
        bind_rotation_wxyz = tuple(row["rotation_wxyz"])
        bind_scale = tuple(row["scale"])
    return _bind_local_matrix(_Row())


def _rig_bind_globals(rig: ChromeRig) -> list[np.ndarray]:
    local = [_bind_local_matrix(bone) for bone in rig.bones]
    result: list[np.ndarray | None] = [None] * len(rig.bones)
    def resolve(index: int) -> np.ndarray:
        if result[index] is not None:
            return result[index]  # type: ignore[return-value]
        parent = rig.bones[index].parent_index
        result[index] = resolve(parent) @ local[index] if parent >= 0 else local[index]
        return result[index]  # type: ignore[return-value]
    return [resolve(index) for index in range(len(rig.bones))]


def _synthetic_tracks(document: _FbxDocument) -> dict[int, str]:
    result: dict[int, str] = {}
    model_names = [
        *document.limb_models,
        *getattr(document, "null_models", {}),
    ]
    for name in model_names:
        match = _SYNTHETIC_TRACK.match(name)
        if match:
            result[int(match.group(1), 16)] = name
    return result


def _model_id(document: _FbxDocument, name: str) -> int:
    if name in document.limb_models:
        return document.limb_models[name]
    null_models = getattr(document, "null_models", {})
    if name in null_models:
        return null_models[name]
    raise KeyError(name)


def _validate_exact_skeleton(
    rig: ChromeRig,
    document: _FbxDocument,
    *,
    meters_per_unit: float,
) -> dict[str, Any]:
    helpers = set(_synthetic_tracks(document).values())
    source_names = set(document.limb_models) - helpers
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
        if actual_parent in helpers:
            actual_parent = None
        display_twist_parent = None
        if bone.parent_index >= 0 and "twist" in bone.name.lower():
            original_parent = by_index[bone.parent_index]
            if original_parent.parent_index >= 0:
                display_twist_parent = by_index[original_parent.parent_index].name
        if actual_parent == display_twist_parent:
            # ANM2 -> FBX moves zero-length twist nodes to the grandparent only
            # for Blender display purposes, preventing the visible upper-arm
            # or thigh bone from being shortened by half on FBX import.
            continue
        if actual_parent != expected_parent:
            errors.append(
                f"parent mismatch for {bone.name!r}: expected {expected_parent!r}, "
                f"found {actual_parent!r}"
            )
    if errors:
        raise ValueError("Exact-rig skeleton mismatch:\n- " + "\n- ".join(errors))
    if _is_dlr_native_export(document):
        # Native ANM2 -> FBX deliberately replaces Chrome's internal bone axes
        # with Blender-friendly display axes. Animation recovery below removes
        # that fixed display basis before rebuilding ANM2, so a direct local
        # bind comparison here would report every bone as a false mismatch.
        return {
            "max_translation_meters": 0.0,
            "max_rotation_degrees": 0.0,
            "max_scale_component": 0.0,
            "default_pose_mismatches": [],
            "default_pose_mismatch_count": 0,
            "status": "compatible",
        }
    maximum_translation = 0.0
    maximum_rotation_degrees = 0.0
    maximum_scale = 0.0
    mismatches: list[dict[str, Any]] = []
    for bone in rig.bones:
        local = document._local_matrix(
            document.limb_models[bone.name], tick=0, use_animation=False
        )
        translation, quaternion, scale = decompose_local_matrix(local)
        translation_delta = float(
            np.linalg.norm(
                translation * meters_per_unit
                - np.asarray(bone.bind_translation, dtype=float)
            )
        )
        quaternion_dot = abs(
            float(np.dot(quaternion, np.asarray(bone.bind_rotation_wxyz, dtype=float)))
        )
        rotation_delta = math.degrees(
            2.0 * math.acos(max(-1.0, min(1.0, quaternion_dot)))
        )
        scale_delta = float(
            np.max(np.abs(scale - np.asarray(bone.bind_scale, dtype=float)))
        )
        maximum_translation = max(maximum_translation, translation_delta)
        maximum_rotation_degrees = max(maximum_rotation_degrees, rotation_delta)
        maximum_scale = max(maximum_scale, scale_delta)
        components = []
        if translation_delta > 1.0e-4:
            components.append("translation")
        if rotation_delta > 0.1:
            components.append("rotation")
        if scale_delta > 1.0e-4:
            components.append("scale")
        if components:
            mismatches.append(
                {
                    "bone": bone.name,
                    "components": components,
                    "translation_delta_meters": translation_delta,
                    "rotation_delta_degrees": rotation_delta,
                    "scale_component_delta": scale_delta,
                }
            )
    return {
        "max_translation_meters": maximum_translation,
        "max_rotation_degrees": maximum_rotation_degrees,
        "max_scale_component": maximum_scale,
        "default_pose_mismatches": mismatches,
        "default_pose_mismatch_count": len(mismatches),
        "status": "warning" if mismatches else "compatible",
    }


def _compatibility_warnings(compatibility: dict[str, Any]) -> list[str]:
    mismatches = list(compatibility.get("default_pose_mismatches", []))
    if not mismatches:
        return []
    worst = max(mismatches, key=lambda row: float(row["rotation_delta_degrees"]))
    return [
        "Exact-rig default pose differs from the .crig for "
        f"{len(mismatches)} bone(s); exporting anyway. Largest rotation mismatch: "
        f"{worst['bone']!r} (translation {worst['translation_delta_meters']:.6g} m, "
        f"rotation {worst['rotation_delta_degrees']:.6g} degrees, "
        f"scale {worst['scale_component_delta']:.6g})."
    ]


def build_exact_rig_anm2(
    animation_fbx: str | Path,
    rig: ChromeRig,
    *,
    fps: int | None = None,
    animation_stack: str | None = None,
    document_factory: Any = _FbxDocument,
) -> ExactRigBuild:
    rig.validate().require_valid()
    sample_fps = int(fps or rig.writer_profile.default_fps)
    if not 1 <= sample_fps <= 240:
        raise ValueError("Exact-rig sample FPS must be between 1 and 240")
    source = Path(animation_fbx)
    document = document_factory(source)
    if animation_stack or len(getattr(document, "animation_stacks", ())) > 1:
        document.select_animation_stack(animation_stack)
    source_meters = float(document.meters_per_unit)
    synthetic_tracks = _synthetic_tracks(document)
    motion_helper_name = synthetic_tracks.get(_MOTION_HELPER_DESCRIPTOR)
    native_dlr_export = _is_dlr_native_export(document)
    native_metadata = _dlr_native_metadata(document) if native_dlr_export else {}
    native_helper_tracks = native_metadata.get("helper_tracks", {})
    bind_compatibility = _validate_exact_skeleton(
        rig, document, meters_per_unit=source_meters
    )
    display_basis_corrections: dict[str, np.ndarray] = {}
    if native_dlr_export:
        stored_corrections = native_metadata.get("display_basis_corrections", {})
        if stored_corrections:
            display_basis_corrections = {
                name: np.asarray(values, dtype=float).reshape(4, 4)
                for name, values in stored_corrections.items()
            }
        else:
            document_bind_global = document.global_matrices(tick=0, use_animation=False)
            for bone, game_bind_global in zip(rig.bones, _rig_bind_globals(rig)):
                blender_bind_global = (
                    _Y_UP_TO_BLENDER @ game_bind_global @ np.linalg.inv(_Y_UP_TO_BLENDER)
                )
                display_basis_corrections[bone.name] = (
                    np.linalg.inv(blender_bind_global) @ document_bind_global[bone.name]
                )
    if hasattr(document, "frame_ticks"):
        ticks = list(document.frame_ticks(fps=sample_fps))
    else:
        ticks = [
            int(round(frame * FBX_TICKS_PER_SECOND / sample_fps))
            for frame in range(max(1, int(document.frame_count(fps=sample_fps))))
        ]
    if len(ticks) == 1:
        ticks.append(ticks[0])
    frame_count = len(ticks)
    values: list[list[list[float]]] = []
    for frame_index, tick in enumerate(ticks):
        rows_by_descriptor: dict[int, list[float]] = {}
        stored_motion_rows = native_helper_tracks.get(
            f"{_MOTION_HELPER_DESCRIPTOR:08X}", ()
        )
        if native_dlr_export and frame_index < len(stored_motion_rows):
            motion_helper_local = _row_matrix(stored_motion_rows[frame_index])
            motion_helper_is_game_space = True
        else:
            motion_helper_local = (
                document._local_matrix(
                    _model_id(document, motion_helper_name),
                    tick=tick,
                    use_animation=True,
                )
                if motion_helper_name is not None
                else None
            )
            motion_helper_is_game_space = False
        native_game_globals: dict[str, np.ndarray] = {}
        if native_dlr_export:
            display_globals = document.global_matrices(tick=tick, use_animation=True)
            for bone in rig.bones:
                blender_game_global = (
                    display_globals[bone.name]
                    @ np.linalg.inv(display_basis_corrections[bone.name])
                )
                native_game_globals[bone.name] = (
                    np.linalg.inv(_Y_UP_TO_BLENDER)
                    @ blender_game_global
                    @ _Y_UP_TO_BLENDER
                )
        for bone in rig.bones:
            if native_dlr_export:
                game_global = native_game_globals[bone.name]
                if bone.parent_index >= 0:
                    parent_global = native_game_globals[rig.bones[bone.parent_index].name]
                    local = np.linalg.inv(parent_global) @ game_global
                elif motion_helper_local is not None:
                    helper_game = motion_helper_local
                    if not motion_helper_is_game_space:
                        helper_game = (
                            np.linalg.inv(_Y_UP_TO_BLENDER)
                            @ motion_helper_local
                            @ _Y_UP_TO_BLENDER
                        )
                    local = np.linalg.inv(helper_game) @ game_global
                else:
                    local = game_global
                translation_factor = 1.0
            else:
                object_id = document.limb_models[bone.name]
                local = document._local_matrix(
                    object_id, tick=tick, use_animation=True
                )
                translation_factor = source_meters
            translation, quaternion, scale = decompose_local_matrix(local)
            rotation = anm2_cayley_vector_from_quaternion(quaternion)
            rows_by_descriptor[bone.descriptor] = [
                *map(float, rotation),
                *(float(v * translation_factor) for v in translation),
                *map(float, scale),
            ]
        for descriptor, name in synthetic_tracks.items():
            if descriptor not in rig.descriptors:
                continue
            stored_rows = native_helper_tracks.get(f"{descriptor:08X}", ())
            if native_dlr_export and frame_index < len(stored_rows):
                stored_row = stored_rows[frame_index]
                quaternion = np.asarray(stored_row["rotation_wxyz"], dtype=float)
                rotation = anm2_cayley_vector_from_quaternion(quaternion)
                rows_by_descriptor[descriptor] = [
                    *map(float, rotation),
                    *map(float, stored_row["translation"]),
                    *map(float, stored_row["scale"]),
                ]
                continue
            local = document._local_matrix(
                _model_id(document, name), tick=tick, use_animation=True
            )
            if native_dlr_export:
                local = (
                    np.linalg.inv(_Y_UP_TO_BLENDER)
                    @ local
                    @ _Y_UP_TO_BLENDER
                )
            translation, quaternion, scale = decompose_local_matrix(local)
            rotation = anm2_cayley_vector_from_quaternion(quaternion)
            rows_by_descriptor[descriptor] = [
                *map(float, rotation),
                *(
                    float(v if native_dlr_export else v * source_meters)
                    for v in translation
                ),
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
            "source_animation_stack": (
                document.selected_animation_stack.name
                if getattr(document, "selected_animation_stack", None)
                else ""
            ),
            "bind_compatibility": bind_compatibility,
            "warnings": _compatibility_warnings(bind_compatibility),
            "root_policy": "exact_local_transforms",
            "candidate_path": None,
        },
    )


__all__ = ["ExactRigBuild", "build_exact_rig_anm2"]
