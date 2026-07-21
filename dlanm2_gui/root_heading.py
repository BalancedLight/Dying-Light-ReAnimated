"""Target-global root heading extraction and root-motion policies.

ANM2 rotations are stored as Cayley vectors, but heading is a geometric
property of the target-global quaternion.  This module therefore performs no
Euler conversion: it reconstructs globals, extracts the twist about the target
profile's up axis, corrects the global root, and converts the result back to a
local transform when the selected skeletal root has a parent.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
import math
from typing import Any, Mapping, Sequence

import numpy as np

from .helper_retarget import (
    anm2_values_to_local_matrix,
    local_matrix_to_anm2_values,
)
from .oracle.smd_bind_pose import (
    anm2_cayley_vector_from_quaternion,
    quaternion_wxyz_from_anm2_cayley,
)
from .root_motion import (
    RootHeadingMode,
    RootMotionMode,
    RootMotionSelection,
    resolve_root_motion_selection,
)


MOTION_ACCUMULATOR_DESCRIPTOR = 0xCCC3CDDF
_IDENTITY_ROW = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0]


@dataclass(frozen=True, slots=True)
class RootHeadingReport:
    policy: str
    up_axis: tuple[float, float, float]
    source_heading_degrees: float
    skeletal_root_heading_degrees: float
    motion_heading_degrees: float
    source_planar_displacement: tuple[float, float, float]
    skeletal_root_planar_displacement: tuple[float, float, float]
    motion_planar_displacement: tuple[float, float, float]
    legacy_serialized_policy: str = ""
    resolved_motion_mode: str = ""
    resolved_heading_mode: str = ""
    resolved_source_root: str = ""
    resolved_target_root: str = ""
    translation_owner: str = "skeletal_root"
    heading_owner: str = "skeletal_root"

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def _unit_vector(value: Sequence[float], *, label: str) -> np.ndarray:
    result = np.asarray(tuple(float(v) for v in value), dtype=float)
    if result.shape != (3,) or not np.isfinite(result).all():
        raise ValueError(f"{label} must be a finite three-vector")
    length = float(np.linalg.norm(result))
    if length <= 1.0e-12:
        raise ValueError(f"{label} must be non-zero")
    return result / length


def infer_target_up_axis(rig: Any) -> tuple[float, float, float]:
    """Return the target-space world-up declared by or inferred for a CRIG."""

    extensions = dict(getattr(rig, "extensions", {}) or {})
    raw = extensions.get("world_up_axis") or extensions.get("target_up_axis")
    if isinstance(raw, str):
        signs = -1.0 if raw.strip().startswith("-") else 1.0
        axis = raw.strip().lstrip("+-").casefold()
        raw = {
            "x": (signs, 0.0, 0.0),
            "y": (0.0, signs, 0.0),
            "z": (0.0, 0.0, signs),
        }.get(axis)
    if raw is None:
        # Chrome/SMD bind translations and both bundled target packages are
        # authored Y-up.  Custom rigs may override this explicitly above.
        raw = (0.0, 1.0, 0.0)
    up = _unit_vector(raw, label="target world-up axis")
    return tuple(float(value) for value in up)


def quaternion_multiply(left: Sequence[float], right: Sequence[float]) -> np.ndarray:
    lw, lx, ly, lz = (float(value) for value in left)
    rw, rx, ry, rz = (float(value) for value in right)
    result = np.asarray(
        (
            lw * rw - lx * rx - ly * ry - lz * rz,
            lw * rx + lx * rw + ly * rz - lz * ry,
            lw * ry - lx * rz + ly * rw + lz * rx,
            lw * rz + lx * ry - ly * rx + lz * rw,
        ),
        dtype=float,
    )
    length = float(np.linalg.norm(result))
    if not math.isfinite(length) or length <= 1.0e-12:
        raise ValueError("quaternion product is not finite or normalizable")
    return result / length


def quaternion_inverse(value: Sequence[float]) -> np.ndarray:
    q = np.asarray(tuple(float(v) for v in value), dtype=float)
    if q.shape != (4,) or not np.isfinite(q).all():
        raise ValueError("quaternion must contain four finite values")
    norm_sq = float(np.dot(q, q))
    if norm_sq <= 1.0e-24:
        raise ValueError("quaternion is not invertible")
    return np.asarray((q[0], -q[1], -q[2], -q[3]), dtype=float) / norm_sq


def extract_heading_twist(
    world_delta_wxyz: Sequence[float], up_axis: Sequence[float]
) -> np.ndarray:
    """Swing–twist decomposition: return only rotation about ``up_axis``."""

    q = np.asarray(tuple(float(v) for v in world_delta_wxyz), dtype=float)
    if q.shape != (4,) or not np.isfinite(q).all():
        raise ValueError("world rotation delta must be a finite quaternion")
    q_length = float(np.linalg.norm(q))
    if q_length <= 1.0e-12:
        raise ValueError("world rotation delta is not normalizable")
    q /= q_length
    up = _unit_vector(up_axis, label="heading up axis")
    projected = up * float(np.dot(q[1:4], up))
    twist = np.asarray((q[0], *projected), dtype=float)
    length = float(np.linalg.norm(twist))
    if length <= 1.0e-10:
        # A pure 180-degree swing has no uniquely defined twist.  Choosing the
        # identity is deterministic and preserves the entire pose as swing.
        return np.asarray((1.0, 0.0, 0.0, 0.0), dtype=float)
    return twist / length


def _rotation_from_matrix(matrix: np.ndarray) -> np.ndarray:
    linear = np.asarray(matrix, dtype=float)[:3, :3]
    if linear.shape != (3, 3) or not np.isfinite(linear).all():
        raise ValueError("transform has a non-finite linear component")
    left, _singular, right = np.linalg.svd(linear)
    rotation = left @ right
    if np.linalg.det(rotation) < 0.0:
        left[:, -1] *= -1.0
        rotation = left @ right
    return rotation


def _quaternion_from_rotation(rotation: np.ndarray) -> np.ndarray:
    m = np.asarray(rotation, dtype=float)
    trace = float(np.trace(m))
    if trace > 0.0:
        s = math.sqrt(trace + 1.0) * 2.0
        q = np.asarray(
            (0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s,
             (m[1, 0] - m[0, 1]) / s),
            dtype=float,
        )
    else:
        index = int(np.argmax(np.diag(m)))
        if index == 0:
            s = math.sqrt(max(0.0, 1.0 + m[0, 0] - m[1, 1] - m[2, 2])) * 2.0
            q = np.asarray(((m[2, 1] - m[1, 2]) / s, 0.25 * s,
                            (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s))
        elif index == 1:
            s = math.sqrt(max(0.0, 1.0 + m[1, 1] - m[0, 0] - m[2, 2])) * 2.0
            q = np.asarray(((m[0, 2] - m[2, 0]) / s,
                            (m[0, 1] + m[1, 0]) / s, 0.25 * s,
                            (m[1, 2] + m[2, 1]) / s))
        else:
            s = math.sqrt(max(0.0, 1.0 + m[2, 2] - m[0, 0] - m[1, 1])) * 2.0
            q = np.asarray(((m[1, 0] - m[0, 1]) / s,
                            (m[0, 2] + m[2, 0]) / s,
                            (m[1, 2] + m[2, 1]) / s, 0.25 * s))
    length = float(np.linalg.norm(q))
    if not math.isfinite(length) or length <= 1.0e-12:
        raise ValueError("rotation matrix did not produce a finite quaternion")
    return q / length


def _rotation_matrix_from_quaternion(value: Sequence[float]) -> np.ndarray:
    w, x, y, z = _unit_quaternion(value)
    return np.asarray(
        (
            (1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y - z * w), 2.0 * (x * z + y * w)),
            (2.0 * (x * y + z * w), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z - x * w)),
            (2.0 * (x * z - y * w), 2.0 * (y * z + x * w), 1.0 - 2.0 * (x * x + y * y)),
        ),
        dtype=float,
    )


def _unit_quaternion(value: Sequence[float]) -> np.ndarray:
    q = np.asarray(tuple(float(v) for v in value), dtype=float)
    length = float(np.linalg.norm(q))
    if q.shape != (4,) or not math.isfinite(length) or length <= 1.0e-12:
        raise ValueError("quaternion must be finite and normalizable")
    return q / length


def _ancestor_indices(rig: Any, bone_index: int) -> list[int]:
    result: list[int] = []
    visiting: set[int] = set()
    current = int(bone_index)
    while current >= 0:
        if current in visiting:
            raise ValueError("target rig contains a parent cycle")
        visiting.add(current)
        result.append(current)
        current = int(rig.bones[current].parent_index)
    result.reverse()
    return result


def _global_for_chain(
    frame: Sequence[Sequence[float]], rig: Any, indices: Sequence[int]
) -> tuple[np.ndarray, np.ndarray | None]:
    current = np.eye(4, dtype=float)
    parent_global: np.ndarray | None = None
    for offset, bone_index in enumerate(indices):
        bone = rig.bones[bone_index]
        try:
            track = rig.descriptors.index(bone.descriptor)
        except ValueError as exc:
            raise ValueError(
                f"target bone {bone.name!r} has no animation descriptor"
            ) from exc
        if offset == len(indices) - 1:
            parent_global = None if offset == 0 else current.copy()
        current = current @ anm2_values_to_local_matrix(frame[track])
    if not np.isfinite(current).all():
        raise ValueError("root global transform is non-finite")
    return current, parent_global


def _bind_frame(rig: Any, template: Sequence[Sequence[float]]) -> list[list[float]]:
    rows = [list(map(float, row)) for row in template]
    for bone in rig.bones:
        try:
            track = rig.descriptors.index(bone.descriptor)
        except ValueError:
            continue
        rows[track] = local_matrix_to_anm2_values(
            _compose_matrix(
                bone.bind_translation,
                bone.bind_rotation_wxyz,
                bone.bind_scale,
            )
        )
    return rows


def _compose_matrix(
    translation: Sequence[float], rotation: Sequence[float], scale: Sequence[float]
) -> np.ndarray:
    result = np.eye(4, dtype=float)
    result[:3, :3] = _rotation_matrix_from_quaternion(rotation) @ np.diag(
        np.asarray(tuple(float(v) for v in scale), dtype=float)
    )
    result[:3, 3] = np.asarray(tuple(float(v) for v in translation), dtype=float)
    return result


def _heading_series(globals_: Sequence[np.ndarray], up_axis: np.ndarray) -> list[np.ndarray]:
    first = _quaternion_from_rotation(_rotation_from_matrix(globals_[0]))
    headings: list[np.ndarray] = []
    previous: np.ndarray | None = None
    for matrix in globals_:
        current = _quaternion_from_rotation(_rotation_from_matrix(matrix))
        world_delta = quaternion_multiply(current, quaternion_inverse(first))
        heading = extract_heading_twist(world_delta, up_axis)
        if previous is not None and float(np.dot(previous, heading)) < 0.0:
            heading *= -1.0
        headings.append(heading)
        previous = heading
    return headings


def accumulated_heading_degrees(
    globals_: Sequence[np.ndarray], up_axis: Sequence[float]
) -> float:
    if len(globals_) < 2:
        return 0.0
    up = _unit_vector(up_axis, label="heading up axis")
    headings = _heading_series(globals_, up)
    angles = np.asarray(
        [
            2.0
            * math.atan2(
                float(np.dot(heading[1:4], up)), float(heading[0])
            )
            for heading in headings
        ],
        dtype=float,
    )
    unwrapped = np.unwrap(angles)
    return math.degrees(float(unwrapped[-1] - unwrapped[0]))


def _planar_displacement(
    globals_: Sequence[np.ndarray], up_axis: np.ndarray
) -> np.ndarray:
    displacement = globals_[-1][:3, 3] - globals_[0][:3, 3]
    return displacement - up_axis * float(np.dot(displacement, up_axis))


def apply_target_root_policy(
    values: list[list[list[float]]],
    rig: Any,
    target_root_name: str,
    policy: RootMotionSelection | Mapping[str, Any] | str,
    *,
    up_axis: Sequence[float] | None = None,
    source_root_name: str = "",
    heading_mode: str | None = None,
) -> RootHeadingReport:
    """Apply target-neutral root translation and heading ownership.

    ``inplace``, ``bip01`` and ``motion`` remain accepted as serialized legacy
    adapters.  The selected target bone is never inferred from the word
    ``bip01``; it is supplied independently by ``target_root_name``.
    """

    selection = resolve_root_motion_selection(
        policy,
        source_root_bone=source_root_name,
        target_root_bone=target_root_name,
        heading_mode=heading_mode,
    )
    if selection.target_root_bone:
        target_root_name = selection.target_root_bone
    motion_mode = RootMotionMode(selection.motion_mode)
    resolved_heading = RootHeadingMode(selection.heading_mode)
    legacy_policy = selection.legacy_serialized_policy
    if len(values) == 0:
        raise ValueError("root policy requires at least one animation frame")
    if any(len(frame) != len(rig.descriptors) for frame in values):
        raise ValueError("root policy frame width does not match the target descriptor table")
    up = _unit_vector(
        up_axis if up_axis is not None else infer_target_up_axis(rig),
        label="target world-up axis",
    )
    try:
        root_index, root_bone = next(
            (index, bone)
            for index, bone in enumerate(rig.bones)
            if bone.name == target_root_name
        )
    except StopIteration as exc:
        raise ValueError(f"Unknown target root bone {target_root_name!r}") from exc
    root_track = rig.descriptors.index(root_bone.descriptor)
    chain = _ancestor_indices(rig, root_index)
    globals_and_parents = [_global_for_chain(frame, rig, chain) for frame in values]
    original_globals = [row[0] for row in globals_and_parents]
    parent_globals = [row[1] for row in globals_and_parents]
    headings = _heading_series(original_globals, up)
    bind_rows = _bind_frame(rig, values[0])
    bind_global, _bind_parent = _global_for_chain(bind_rows, rig, chain)
    first_position = original_globals[0][:3, 3].copy()

    motion_track: int | None = None
    if motion_mode in {
        RootMotionMode.IN_PLACE,
        RootMotionMode.MOTION_ACCUMULATOR,
    }:
        if MOTION_ACCUMULATOR_DESCRIPTOR not in rig.descriptors:
            if motion_mode == RootMotionMode.MOTION_ACCUMULATOR:
                raise ValueError(
                    "Motion-accumulator root policy requires descriptor "
                    "0xCCC3CDDF in the target .crig."
                )
        else:
            motion_track = rig.descriptors.index(MOTION_ACCUMULATOR_DESCRIPTOR)

    corrected_globals: list[np.ndarray] = []
    motion_globals: list[np.ndarray] = []
    for frame_index, (frame, current, parent, heading) in enumerate(
        zip(values, original_globals, parent_globals, headings)
    ):
        corrected = current.copy()
        changes_heading = resolved_heading != RootHeadingMode.PRESERVE
        changes_translation = motion_mode != RootMotionMode.SKELETAL_ROOT
        if changes_heading or changes_translation:
            current_rotation = _quaternion_from_rotation(_rotation_from_matrix(current))
            if changes_heading:
                locked_rotation = quaternion_multiply(
                    quaternion_inverse(heading), current_rotation
                )
                stretch = _rotation_from_matrix(current).T @ current[:3, :3]
                corrected[:3, :3] = (
                    _rotation_matrix_from_quaternion(locked_rotation) @ stretch
                )
            displacement = current[:3, 3] - first_position
            planar = displacement - up * float(np.dot(displacement, up))
            if motion_mode == RootMotionMode.IN_PLACE:
                corrected[:3, 3] = bind_global[:3, 3]
            elif motion_mode == RootMotionMode.MOTION_ACCUMULATOR:
                corrected[:3, 3] = current[:3, 3] - planar
            local = corrected if parent is None else np.linalg.inv(parent) @ corrected
            if not np.isfinite(local).all():
                raise ValueError(
                    f"corrected root local transform is non-finite at frame {frame_index}"
                )
            frame[root_track] = local_matrix_to_anm2_values(local)

            if motion_track is not None:
                if motion_mode == RootMotionMode.IN_PLACE:
                    frame[motion_track] = list(_IDENTITY_ROW)
                    motion_globals.append(np.eye(4, dtype=float))
                else:
                    helper = np.eye(4, dtype=float)
                    helper[:3, :3] = _rotation_matrix_from_quaternion(heading)
                    helper[:3, 3] = planar
                    frame[motion_track] = [
                        *map(
                            float,
                            anm2_cayley_vector_from_quaternion(heading),
                        ),
                        *map(float, planar),
                        1.0,
                        1.0,
                        1.0,
                    ]
                    motion_globals.append(helper)
        corrected_globals.append(corrected)

    source_heading = accumulated_heading_degrees(original_globals, up)
    output_heading = accumulated_heading_degrees(corrected_globals, up)
    motion_heading = (
        accumulated_heading_degrees(motion_globals, up) if motion_globals else 0.0
    )
    source_planar = _planar_displacement(original_globals, up)
    output_planar = _planar_displacement(corrected_globals, up)
    motion_planar = (
        _planar_displacement(motion_globals, up)
        if motion_globals
        else np.zeros(3, dtype=float)
    )
    return RootHeadingReport(
        policy=legacy_policy,
        up_axis=tuple(float(v) for v in up),
        source_heading_degrees=float(source_heading),
        skeletal_root_heading_degrees=float(output_heading),
        motion_heading_degrees=float(motion_heading),
        source_planar_displacement=tuple(float(v) for v in source_planar),
        skeletal_root_planar_displacement=tuple(float(v) for v in output_planar),
        motion_planar_displacement=tuple(float(v) for v in motion_planar),
        legacy_serialized_policy=legacy_policy,
        resolved_motion_mode=motion_mode.value,
        resolved_heading_mode=resolved_heading.value,
        resolved_source_root=selection.source_root_bone or source_root_name,
        resolved_target_root=target_root_name,
        translation_owner=(
            "none"
            if motion_mode == RootMotionMode.IN_PLACE
            else "motion_accumulator"
            if motion_mode == RootMotionMode.MOTION_ACCUMULATOR
            else "skeletal_root"
        ),
        heading_owner=(
            "motion_accumulator"
            if resolved_heading == RootHeadingMode.TO_MOTION_ACCUMULATOR
            else "skeletal_root"
            if resolved_heading == RootHeadingMode.PRESERVE
            else "none"
        ),
    )


__all__ = [
    "MOTION_ACCUMULATOR_DESCRIPTOR",
    "RootHeadingReport",
    "accumulated_heading_degrees",
    "apply_target_root_policy",
    "extract_heading_twist",
    "infer_target_up_axis",
    "quaternion_inverse",
    "quaternion_multiply",
]
