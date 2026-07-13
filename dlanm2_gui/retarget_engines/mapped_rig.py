from __future__ import annotations

"""Rest-pose corrected FBX -> arbitrary Chrome Rig retargeting.

This is the missing middle path between strict ``exact`` rigs and the fixed
Dying Light humanoid solver.  A ``GenericBoneMap`` maps each target Chrome-rig
bone to one source FBX bone.  Animated local transforms are transferred as a
rest-relative delta:

    target_local(frame) = target_bind_local
                          * inverse(source_bind_local)
                          * source_local(frame)

Unmapped target bones remain at their target bind transform.  This allows a
Wada/CC-base target rig to consume a Mixamo animation without pretending the
skeletons are byte-identical.
"""

from dataclasses import asdict
from pathlib import Path
from typing import Any, Mapping
import math

import numpy as np

from ..anm2_components import decode_samples
from ..anm2_writer import build_payload_from_values
from ..bone_maps import GenericBoneMap, skeleton_signature
from ..chrome_rig import ChromeRig
from ..chrome_rig_builder import decompose_local_matrix
from ..model_importer.fbx_model import FBX_Y_UP_TO_DYING_LIGHT
from ..oracle.binary_fbx_mixamo import FBX_TICKS_PER_SECOND, _FbxDocument
from ..oracle.smd_bind_pose import anm2_cayley_vector_from_quaternion
from ..root_mapping import RootMappingSelection, choose_hierarchy_root, resolve_source_root
from .base import RetargetBuild

MappedRigBuild = RetargetBuild


def quaternion_wxyz_to_matrix(value: tuple[float, float, float, float] | list[float]) -> np.ndarray:
    w, x, y, z = map(float, value)
    norm = math.sqrt(w * w + x * x + y * y + z * z)
    if not math.isfinite(norm) or norm <= 1.0e-12:
        raise ValueError("rotation quaternion is not finite or normalizable")
    w, x, y, z = w / norm, x / norm, y / norm, z / norm
    return np.asarray(
        (
            (1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
            (2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
            (2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)),
        ),
        dtype=float,
    )


def compose_local_matrix(
    translation: tuple[float, float, float] | list[float],
    rotation_wxyz: tuple[float, float, float, float] | list[float],
    scale: tuple[float, float, float] | list[float],
) -> np.ndarray:
    result = np.eye(4, dtype=float)
    result[:3, :3] = quaternion_wxyz_to_matrix(rotation_wxyz) @ np.diag(
        np.asarray(scale, dtype=float)
    )
    result[:3, 3] = np.asarray(translation, dtype=float)
    return result


def target_bind_local_matrix(bone: Any) -> np.ndarray:
    return compose_local_matrix(
        bone.bind_translation,
        bone.bind_rotation_wxyz,
        bone.bind_scale,
    )


def source_local_to_target_basis(
    matrix: np.ndarray,
    *,
    meters_per_unit: float,
    convert_y_up_to_dying_light: bool,
) -> np.ndarray:
    """Scale FBX translation and optionally conjugate into Chrome model space."""

    value = np.asarray(matrix, dtype=float).copy()
    if value.shape != (4, 4) or not np.isfinite(value).all():
        raise ValueError("source local matrix must be a finite 4x4 matrix")
    value[:3, 3] *= float(meters_per_unit)
    if convert_y_up_to_dying_light:
        basis = FBX_Y_UP_TO_DYING_LIGHT
        value = basis @ value @ np.linalg.inv(basis)
    return value


def mapped_local_from_rest_delta(
    target_bind_local: np.ndarray,
    source_bind_local: np.ndarray,
    source_animated_local: np.ndarray,
) -> np.ndarray:
    """Transfer one source-local rest delta onto a target bind transform."""

    target = np.asarray(target_bind_local, dtype=float)
    source_bind = np.asarray(source_bind_local, dtype=float)
    source_anim = np.asarray(source_animated_local, dtype=float)
    for label, matrix in (
        ("target bind", target),
        ("source bind", source_bind),
        ("source animation", source_anim),
    ):
        if matrix.shape != (4, 4) or not np.isfinite(matrix).all():
            raise ValueError(f"{label} matrix must be finite 4x4")
    try:
        source_delta = np.linalg.inv(source_bind) @ source_anim
    except np.linalg.LinAlgError as exc:
        raise ValueError("source bind matrix is singular") from exc
    result = target @ source_delta
    if not np.isfinite(result).all():
        raise ValueError("mapped local transform became non-finite")
    return result


def _target_uses_dying_light_basis(rig: ChromeRig) -> bool:
    convention = str(getattr(rig.writer_profile, "coordinate_convention", "")).lower()
    extensions = dict(getattr(rig, "extensions", {}) or {})
    orientation = str(extensions.get("model_axis_conversion", "")).lower()
    builder = str(extensions.get("builder", "")).lower()
    return (
        "dying_light_model" in convention
        or orientation in {"auto", "fbx_y_up_to_dying_light"}
        or builder.endswith("binary_fbx_v2")
    )


def _mapped_pairs_by_target_rig_bone(
    rig: ChromeRig,
    bone_map: GenericBoneMap,
    document: _FbxDocument,
) -> tuple[dict[str, str], list[str]]:
    rig_names = {bone.name for bone in rig.bones}
    source_names = set(document.limb_models)
    result: dict[str, str] = {}
    warnings: list[str] = []
    for row in bone_map.pairs:
        target_rig_bone = str(row.source_bone)
        source_fbx_bone = str(row.target_bone)
        if target_rig_bone not in rig_names:
            warnings.append(
                f"Mapping row targets unknown .crig bone {target_rig_bone!r}; row ignored."
            )
            continue
        if source_fbx_bone not in source_names:
            warnings.append(
                f"Mapping row references missing source FBX bone {source_fbx_bone!r}; row ignored."
            )
            continue
        if target_rig_bone in result:
            warnings.append(
                f"Duplicate mapping for target .crig bone {target_rig_bone!r}; last row wins."
            )
        result[target_rig_bone] = source_fbx_bone
    return result, warnings


def _validate_mapping_identity(
    rig: ChromeRig,
    bone_map: GenericBoneMap,
    document: _FbxDocument,
) -> list[str]:
    warnings: list[str] = []
    if bone_map.source_skeleton_hash and bone_map.source_skeleton_hash != rig.skeleton_hash:
        raise ValueError(
            "Mapped-rig profile was created for a different .crig target skeleton."
        )
    current_source_hash = skeleton_signature(
        (name, document.parent_by_name.get(name)) for name in sorted(document.limb_models)
    )
    if bone_map.target_skeleton_hash and bone_map.target_skeleton_hash != current_source_hash:
        warnings.append(
            "Source FBX skeleton hash differs from the map's recorded source skeleton; "
            "bone-name rows were still validated individually."
        )
    return warnings


def build_mapped_rig_anm2(
    animation_fbx: str | Path,
    rig: ChromeRig,
    bone_map: GenericBoneMap,
    *,
    fps: int | None = None,
    animation_stack: str | None = None,
    document_factory: Any = _FbxDocument,
    root_mapping: RootMappingSelection | Mapping[str, Any] | None = None,
) -> MappedRigBuild:
    """Retarget an arbitrary mapped FBX skeleton onto a Chrome Rig."""

    rig.validate().require_valid()
    errors = bone_map.validate()
    if errors:
        raise ValueError("Invalid mapped-rig profile:\n- " + "\n- ".join(errors))

    sample_fps = int(fps or rig.writer_profile.default_fps)
    if not 1 <= sample_fps <= 240:
        raise ValueError("Mapped-rig sample FPS must be between 1 and 240")

    source = Path(animation_fbx)
    document = document_factory(source)
    if animation_stack or len(getattr(document, "animation_stacks", ())) > 1:
        document.select_animation_stack(animation_stack)
    if not document.limb_models:
        raise ValueError("Mapped-rig retarget requires an FBX LimbNode skeleton")

    warnings = _validate_mapping_identity(rig, bone_map, document)
    mapped, pair_warnings = _mapped_pairs_by_target_rig_bone(rig, bone_map, document)
    warnings.extend(pair_warnings)

    if isinstance(root_mapping, RootMappingSelection):
        root_selection = root_mapping
    else:
        payload = dict(root_mapping or {})
        root_selection = RootMappingSelection(
            source_bone=str(payload.get("source_bone", "") or ""),
            target_bone=str(payload.get("target_bone", "") or ""),
        )

    rig_names = [bone.name for bone in rig.bones]
    rig_parents = {
        bone.name: (rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None)
        for bone in rig.bones
    }
    if root_selection.target_bone:
        if root_selection.target_bone not in set(rig_names):
            raise ValueError(
                f"Selected Bip01/root target bone {root_selection.target_bone!r} is not present "
                f"in .crig target {rig.name!r}."
            )
        target_root_name = root_selection.target_bone
        target_root_method = "manual"
    else:
        target_root_name = choose_hierarchy_root(rig_names, rig_parents)
        target_root_method = "automatic"

    source_root_name, source_root_method = resolve_source_root(
        document.limb_models.keys(),
        document.parent_by_name,
        requested_bone=root_selection.source_bone,
    )
    previous_root_source = mapped.get(target_root_name)
    mapped[target_root_name] = source_root_name
    if previous_root_source and previous_root_source != source_root_name:
        warnings.append(
            f"Bip01/root mapping overrides {target_root_name!r}: "
            f"{previous_root_source!r} -> {source_root_name!r}."
        )
    if not mapped:
        raise ValueError(
            "Mapped-rig retarget has no valid bone rows. Use Auto-map or assign bones manually."
        )

    convert_basis = _target_uses_dying_light_basis(rig)
    meters_per_unit = float(document.meters_per_unit)
    source_bind_local: dict[str, np.ndarray] = {}
    for source_name in sorted(set(mapped.values())):
        source_bind_local[source_name] = source_local_to_target_basis(
            document._local_matrix(
                document.limb_models[source_name], tick=0, use_animation=False
            ),
            meters_per_unit=meters_per_unit,
            convert_y_up_to_dying_light=convert_basis,
        )

    target_bind = {bone.name: target_bind_local_matrix(bone) for bone in rig.bones}
    if hasattr(document, "frame_ticks"):
        ticks = list(document.frame_ticks(fps=sample_fps))
    else:
        ticks = [
            int(round(frame * FBX_TICKS_PER_SECOND / sample_fps))
            for frame in range(max(1, int(document.frame_count(fps=sample_fps))))
        ]
    if len(ticks) == 1:
        ticks.append(ticks[0])

    values: list[list[list[float]]] = []
    bind_track_values = rig.bind_track_values()
    movement_ranges: dict[str, float] = {bone.name: 0.0 for bone in rig.bones}
    bind_deltas: list[dict[str, Any]] = []

    # Record rest-pose differences for the UI/report, but they are not a build blocker.
    for bone in rig.bones:
        source_name = mapped.get(bone.name)
        if not source_name:
            continue
        source_bind = source_bind_local[source_name]
        source_t, source_q, source_s = decompose_local_matrix(source_bind)
        target_t = np.asarray(bone.bind_translation, dtype=float)
        target_q = np.asarray(bone.bind_rotation_wxyz, dtype=float)
        rotation_dot = abs(float(np.dot(source_q, target_q)))
        rotation_delta = math.degrees(
            2.0 * math.acos(max(-1.0, min(1.0, rotation_dot)))
        )
        bind_deltas.append(
            {
                "target_bone": bone.name,
                "source_bone": source_name,
                "translation_delta_meters": float(np.linalg.norm(source_t - target_t)),
                "rotation_delta_degrees": rotation_delta,
                "scale_delta": float(
                    np.max(np.abs(source_s - np.asarray(bone.bind_scale, dtype=float)))
                ),
            }
        )

    for tick in ticks:
        rows_by_descriptor: dict[int, list[float]] = {}
        for bone in rig.bones:
            target_bind_local = target_bind[bone.name]
            source_name = mapped.get(bone.name)
            if source_name is None:
                local = target_bind_local
            else:
                source_anim_local = source_local_to_target_basis(
                    document._local_matrix(
                        document.limb_models[source_name], tick=tick, use_animation=True
                    ),
                    meters_per_unit=meters_per_unit,
                    convert_y_up_to_dying_light=convert_basis,
                )
                local = mapped_local_from_rest_delta(
                    target_bind_local,
                    source_bind_local[source_name],
                    source_anim_local,
                )
            translation, quaternion, scale = decompose_local_matrix(local)
            cayley = anm2_cayley_vector_from_quaternion(quaternion)
            row = [
                *map(float, cayley),
                *map(float, translation),
                *map(float, scale),
            ]
            rows_by_descriptor[bone.descriptor] = row
            bind_row = bind_track_values[bone.index]
            movement_ranges[bone.name] = max(
                movement_ranges[bone.name],
                max(abs(float(a) - float(b)) for a, b in zip(row, bind_row)),
            )
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
        flags: list[bool] = []
        for component_index in range(9):
            curve = [frame[track_index][component_index] for frame in values]
            flags.append(max(curve) - min(curve) > 1.0e-8)
        if any(flags[6:9]):
            flags[6:9] = [True, True, True]
        packed_flags.append(flags)

    header = rig.make_header(frame_count=len(values))
    payload = build_payload_from_values(header, rig.descriptors, values, packed_flags)
    sample_frames = sorted({0, len(values) // 2, len(values) - 1})
    decoded = decode_samples(payload, [float(value) for value in sample_frames])
    maximum_error = 0.0
    for decoded_frame, frame_index in zip(decoded.frames, sample_frames):
        for actual_track, expected_track in zip(decoded_frame.tracks, values[frame_index]):
            maximum_error = max(
                maximum_error,
                max(
                    abs(float(actual) - float(expected))
                    for actual, expected in zip(actual_track, expected_track)
                ),
            )

    mapped_target_names = set(mapped)
    unmapped = [bone.name for bone in rig.bones if bone.name not in mapped_target_names]
    if unmapped:
        warnings.append(
            f"{len(unmapped)} target .crig bone(s) are unmapped and remain at bind pose."
        )
    moving = [name for name, delta in movement_ranges.items() if delta > 1.0e-8]
    if not moving:
        warnings.append(
            "No mapped target track changes over the selected clip. Check the animation stack and mapping."
        )

    return MappedRigBuild(
        payload=payload,
        frame_count=len(values),
        report={
            "retarget_mode": "mapped_crig",
            "engine": "MappedRigRetargetEngine",
            "source_fbx": str(source),
            "source_animation_stack": (
                document.selected_animation_stack.name
                if getattr(document, "selected_animation_stack", None)
                else ""
            ),
            "target_rig_id": rig.rig_id,
            "target_rig_name": rig.name,
            "target_skeleton_hash": rig.skeleton_hash,
            "mapping_profile": bone_map.to_dict(),
            "root_mapping": {
                "source_bone": source_root_name,
                "source_method": source_root_method,
                "target_bone": target_root_name,
                "target_method": target_root_method,
                "always_retained": True,
            },
            "mapped_bone_count": len(mapped),
            "unmapped_bone_count": len(unmapped),
            "unmapped_target_bones": unmapped,
            "moving_target_bones": moving,
            "bind_delta_summary": bind_deltas,
            "frame_count": len(values),
            "fps": sample_fps,
            "track_count": len(rig.descriptors),
            "bone_count": len(rig.bones),
            "sample_frames": sample_frames,
            "decoded_max_component_error": maximum_error,
            "source_unit_meters": meters_per_unit,
            "source_basis_conversion": (
                "FBX (x,y,z) -> Dying Light (x,z,-y)"
                if convert_basis
                else "none (target .crig uses FBX-local convention)"
            ),
            "warnings": list(dict.fromkeys(warnings)),
            "root_policy": "mapped_rest_delta",
            "candidate_path": None,
        },
    )


__all__ = [
    "MappedRigBuild",
    "build_mapped_rig_anm2",
    "compose_local_matrix",
    "mapped_local_from_rest_delta",
    "quaternion_wxyz_to_matrix",
    "source_local_to_target_basis",
    "target_bind_local_matrix",
]
