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
CC-base target rig to consume a Mixamo animation without pretending the
skeletons are byte-identical.
"""

from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Mapping
import math

import numpy as np

from ..anm2_components import decode_samples
from ..anm2_writer import build_payload_from_values
from ..bone_maps import BoneMapPair, GenericBoneMap, skeleton_signature
from ..chrome_rig import ChromeRig
from ..chrome_rig_builder import decompose_local_matrix
from ..helper_retarget import (
    HelperApplyReport,
    anm2_values_to_local_matrix,
    apply_helper_retarget_overrides,
    helper_rules_from_pairs,
    include_base_source_fanout,
    local_matrix_to_anm2_values,
    merge_helper_components,
)
from ..model_importer.fbx_model import FBX_Y_UP_TO_DYING_LIGHT
from ..fbx_core import (
    FBX_TICKS_PER_SECOND,
    FbxDocument,
    normalize_matrix_to_target_space,
)
from ..fbx_preflight import preflight_fbx
from ..oracle.smd_bind_pose import (
    anm2_cayley_vector_from_quaternion,
    quaternion_wxyz_from_anm2_cayley,
)
from ..root_mapping import RootMappingSelection, choose_hierarchy_root, resolve_source_root
from ..root_heading import (
    apply_target_root_policy,
)
from ..root_motion import RootMotionSelection, resolve_root_motion_selection
from ..root_motion_basis import (
    build_source_actor_frame,
    build_target_actor_frame,
    map_root_displacement_by_actor_frame,
    root_motion_basis_report,
)
from ..skeleton_analysis import analyze_source_skeleton
from ..target_retarget_policy import build_target_retarget_policy
from .base import RetargetBuild
from .output_validation import (
    DECODED_COMPONENT_ERROR_LIMIT,
    validate_decoded_component_error,
)

MappedRigBuild = RetargetBuild

_DL2_PARITY_FRAMES = frozenset(
    (0, 1, 10, 100, 300, 500, 1000, 1500, 2000, 2200, 2500, 3000, 3342)
)


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
    basis_matrix: np.ndarray | None = None,
) -> np.ndarray:
    """Compatibility wrapper over the canonical FBX matrix normalizer."""

    basis = basis_matrix
    if basis is None and convert_y_up_to_dying_light:
        basis = FBX_Y_UP_TO_DYING_LIGHT
    return normalize_matrix_to_target_space(
        matrix,
        meters_per_unit=meters_per_unit,
        basis_matrix=basis,
    )


def source_global_to_target_basis(
    matrix: np.ndarray,
    *,
    meters_per_unit: float,
    convert_y_up_to_dying_light: bool,
    basis_matrix: np.ndarray | None = None,
) -> np.ndarray:
    """Normalize an FBX global matrix before bind-basis correction.

    FBX global bind/animation matrices are expressed in the document's native
    linear unit.  Chrome animation tracks are expressed in metres.  The old
    global-correction path skipped this conversion (and, for imported model
    rigs, also skipped the FBX Y-up -> Chrome basis change).  Rest pose happened
    to cancel algebraically, but the first animated rotation turned centimetre
    bind offsets into translations hundreds of metres long.

    A homogeneous global transform uses the same unit/basis conversion as a
    local transform, so keep one implementation and a separately named entry
    point to make this required normalization hard to omit at call sites.
    """

    return source_local_to_target_basis(
        matrix,
        meters_per_unit=meters_per_unit,
        convert_y_up_to_dying_light=convert_y_up_to_dying_light,
        basis_matrix=basis_matrix,
    )


@dataclass(frozen=True, slots=True)
class SourceGlobalNormalization:
    """One immutable normalization contract shared by bind and animation globals."""

    meters_per_unit: float
    convert_y_up_to_dying_light: bool
    wrapper_scale_normalization_factor: float = 1.0
    wrapper_axis_conversion: bool = False
    basis_matrix: Any | None = None
    basis_label: str = ""
    unit_conversion_count: int = 1
    axis_conversion_count: int = -1
    wrapper_policy: str = "retained_and_scale_normalized"

    def __post_init__(self) -> None:
        if not math.isfinite(self.meters_per_unit) or self.meters_per_unit <= 0.0:
            raise ValueError("FBX meters_per_unit must be finite and positive")
        if (
            not math.isfinite(self.wrapper_scale_normalization_factor)
            or self.wrapper_scale_normalization_factor <= 0.0
        ):
            raise ValueError("Wrapper scale normalization factor must be finite and positive")
        if self.unit_conversion_count != 1:
            raise ValueError("Source global unit conversion must be applied exactly once")
        basis = (
            FBX_Y_UP_TO_DYING_LIGHT.copy()
            if self.basis_matrix is None and self.convert_y_up_to_dying_light
            else np.eye(4, dtype=float)
            if self.basis_matrix is None
            else np.asarray(self.basis_matrix, dtype=float).copy()
        )
        if basis.shape != (4, 4) or not np.isfinite(basis).all():
            raise ValueError("Source global basis matrix must be a finite 4x4 matrix")
        try:
            np.linalg.inv(basis)
        except np.linalg.LinAlgError as exc:
            raise ValueError("Source global basis matrix is singular") from exc
        object.__setattr__(self, "basis_matrix", basis)
        explicit_axis_conversion = not np.allclose(
            basis,
            np.eye(4, dtype=float),
            rtol=0.0,
            atol=1.0e-12,
        )
        expected_axis_count = (
            1
            if explicit_axis_conversion or self.wrapper_axis_conversion
            else 0
        )
        if self.axis_conversion_count == -1:
            object.__setattr__(self, "axis_conversion_count", expected_axis_count)
        elif self.axis_conversion_count != expected_axis_count:
            raise ValueError("Source global axis conversion must be applied exactly once")

    def apply(self, matrix: np.ndarray) -> np.ndarray:
        return normalize_matrix_to_target_space(
            matrix,
            meters_per_unit=(
                self.meters_per_unit / self.wrapper_scale_normalization_factor
            ),
            basis_matrix=np.asarray(self.basis_matrix, dtype=float),
        )

    def apply_local(self, matrix: np.ndarray) -> np.ndarray:
        return self.apply(matrix)

    def to_report(self) -> dict[str, Any]:
        return {
            "meters_per_unit": self.meters_per_unit,
            "unit_conversion_count": self.unit_conversion_count,
            "wrapper_scale_normalization_factor": self.wrapper_scale_normalization_factor,
            "effective_post_wrapper_translation_scale": (
                self.meters_per_unit / self.wrapper_scale_normalization_factor
            ),
            "axis_conversion": (
                self.basis_label
                if self.basis_label
                else "fbx_y_up_to_dying_light"
                if self.convert_y_up_to_dying_light
                else "fbx_y_up_to_dying_light"
                if self.wrapper_axis_conversion
                else "none"
            ),
            "axis_conversion_count": self.axis_conversion_count,
            "axis_conversion_matrix": np.asarray(
                self.basis_matrix, dtype=float
            ).tolist(),
            "axis_conversion_source": (
                "retained_wrapper"
                if self.wrapper_axis_conversion
                else "canonical_document_basis"
                if self.axis_conversion_count
                else "none"
            ),
            "wrapper_policy": self.wrapper_policy,
            "bind_and_animation_share_normalizer": True,
            "target_crig_bind_conversion_count": 0,
        }


def _joint_pivot_extent(globals_by_name: Mapping[str, np.ndarray]) -> float:
    if not globals_by_name:
        return 0.0
    points = np.asarray(
        [np.asarray(matrix, dtype=float)[:3, 3] for matrix in globals_by_name.values()],
        dtype=float,
    )
    if not np.isfinite(points).all():
        raise ValueError("animated joint hierarchy contains non-finite pivots")
    return float(np.linalg.norm(points.max(axis=0) - points.min(axis=0)))


def _frame_local_matrices(
    rig: ChromeRig, frame: list[list[float]]
) -> dict[str, np.ndarray]:
    result: dict[str, np.ndarray] = {}
    for bone in rig.bones:
        row = np.asarray(frame[rig.descriptors.index(bone.descriptor)], dtype=float)
        if row.shape != (9,) or not np.isfinite(row).all():
            raise ValueError(f"Bone {bone.name!r} has non-finite or malformed track values")
        result[bone.name] = compose_local_matrix(
            row[3:6], quaternion_wxyz_from_anm2_cayley(row[:3]), row[6:9]
        )
    return result


def _globals_from_locals(
    rig: ChromeRig, locals_by_name: Mapping[str, np.ndarray]
) -> dict[str, np.ndarray]:
    result: dict[str, np.ndarray] = {}
    visiting: set[int] = set()

    def calculate(index: int) -> np.ndarray:
        bone = rig.bones[index]
        if bone.name in result:
            return result[bone.name]
        if index in visiting:
            raise ValueError(f"Target hierarchy contains a parent cycle at {bone.name!r}")
        visiting.add(index)
        local = np.asarray(locals_by_name[bone.name], dtype=float)
        value = (
            calculate(bone.parent_index) @ local
            if bone.parent_index >= 0
            else local.copy()
        )
        visiting.remove(index)
        if value.shape != (4, 4) or not np.isfinite(value).all():
            raise ValueError(f"Target global matrix for {bone.name!r} is non-finite")
        result[bone.name] = value
        return value

    for bone in rig.bones:
        calculate(bone.index)
    return result


def reconstruct_target_globals(
    rig: ChromeRig, frame: list[list[float]]
) -> dict[str, np.ndarray]:
    """Reconstruct target globals from one unpacked ANM2 track frame."""

    return _globals_from_locals(rig, _frame_local_matrices(rig, frame))


def validate_hierarchy_safety(
    rig: ChromeRig,
    values: list[list[list[float]]],
    *,
    preserve_non_root_translations: bool,
    allowed_non_root_translation_bones: set[str] | None = None,
) -> dict[str, Any]:
    """Reconstruct output globals and reject detached/stretched hierarchies."""

    if not values:
        raise ValueError("Hierarchy safety validation requires at least one frame")
    rig_validation = rig.validate(test_writer_capacity=False)
    rig_validation.require_valid()
    bind_locals = {bone.name: target_bind_local_matrix(bone) for bone in rig.bones}
    bind_globals = _globals_from_locals(rig, bind_locals)
    bind_extent = _joint_pivot_extent(bind_globals)
    bind_lengths = {
        bone.name: float(
            np.linalg.norm(
                bind_globals[bone.name][:3, 3]
                - bind_globals[rig.bones[bone.parent_index].name][:3, 3]
            )
        )
        for bone in rig.bones
        if bone.parent_index >= 0
    }

    maximum_extent = 0.0
    maximum_extent_frame = 0
    maximum_translation_delta = 0.0
    worst_translation_bone = ""
    worst_translation_frame = 0
    maximum_length_ratio = 1.0
    minimum_length_ratio = 1.0
    worst_length_bone = ""
    worst_length_frame = 0
    maximum_scale = 0.0
    minimum_scale = float("inf")
    violations: list[str] = []
    allowed_translation_bones = set(allowed_non_root_translation_bones or ())

    for frame_index, frame in enumerate(values):
        globals_by_name = reconstruct_target_globals(rig, frame)
        extent = _joint_pivot_extent(globals_by_name)
        if extent > maximum_extent:
            maximum_extent = extent
            maximum_extent_frame = frame_index
        for bone in rig.bones:
            track = np.asarray(
                frame[rig.descriptors.index(bone.descriptor)], dtype=float
            )
            scale = np.abs(track[6:9])
            if not np.isfinite(scale).all() or np.any(scale <= 1.0e-5):
                violations.append(
                    f"{bone.name!r} has singular/non-finite scale at frame {frame_index}."
                )
                continue
            maximum_scale = max(maximum_scale, float(np.max(scale)))
            minimum_scale = min(minimum_scale, float(np.min(scale)))
            bind_scale = np.asarray(bone.bind_scale, dtype=float)
            scale_ratio = scale / bind_scale
            if np.any(scale_ratio > 4.0) or np.any(scale_ratio < 0.25):
                violations.append(
                    f"{bone.name!r} scale changed by more than 4x at frame {frame_index}."
                )
            if bone.parent_index < 0:
                continue
            translation_delta = float(
                np.linalg.norm(track[3:6] - np.asarray(bone.bind_translation))
            )
            if bone.name not in allowed_translation_bones:
                if translation_delta > maximum_translation_delta:
                    maximum_translation_delta = translation_delta
                    worst_translation_bone = bone.name
                    worst_translation_frame = frame_index
            parent_name = rig.bones[bone.parent_index].name
            animated_length = float(
                np.linalg.norm(
                    globals_by_name[bone.name][:3, 3]
                    - globals_by_name[parent_name][:3, 3]
                )
            )
            bind_length = bind_lengths[bone.name]
            if bind_length > 1.0e-5:
                ratio = animated_length / bind_length
                if ratio > maximum_length_ratio:
                    maximum_length_ratio = ratio
                    worst_length_bone = bone.name
                    worst_length_frame = frame_index
                minimum_length_ratio = min(minimum_length_ratio, ratio)
                if ratio > 2.0 or ratio < 0.5:
                    violations.append(
                        f"{bone.name!r} parent-child length changed to {ratio:.3f}x bind "
                        f"at frame {frame_index}."
                    )
            elif animated_length > 0.02:
                violations.append(
                    f"Zero-length bind joint {bone.name!r} separated by "
                    f"{animated_length:.6f} m at frame {frame_index}."
                )

    extent_limit = max(bind_extent * 4.0, bind_extent + 5.0, 1.0)
    if maximum_extent > extent_limit:
        violations.append(
            f"Hierarchy extent reached {maximum_extent:.3f} m at frame "
            f"{maximum_extent_frame}; bind extent is {bind_extent:.3f} m and the "
            f"limit is {extent_limit:.3f} m."
        )
    translation_limit = 1.0e-5 if preserve_non_root_translations else 0.05
    if maximum_translation_delta > translation_limit:
        violations.append(
            f"Non-root local translation changed by {maximum_translation_delta:.6f} m "
            f"on {worst_translation_bone!r} at frame {worst_translation_frame}; "
            f"the limit is {translation_limit:.6f} m."
        )
    if violations:
        raise ValueError(
            "Retargeted hierarchy failed safety validation before ANM2/RPack output:\n- "
            + "\n- ".join(dict.fromkeys(violations))
        )
    return {
        "status": "pass",
        "validated_frame_count": len(values),
        "bind_hierarchy_extent_meters": bind_extent,
        "maximum_animated_hierarchy_extent_meters": maximum_extent,
        "maximum_animated_hierarchy_extent_frame": maximum_extent_frame,
        "extent_limit_meters": extent_limit,
        "maximum_non_root_translation_delta_meters": maximum_translation_delta,
        "worst_non_root_translation_bone": worst_translation_bone,
        "worst_non_root_translation_frame": worst_translation_frame,
        "non_root_translation_limit_meters": translation_limit,
        "maximum_parent_child_length_ratio": maximum_length_ratio,
        "minimum_parent_child_length_ratio": minimum_length_ratio,
        "worst_parent_child_length_bone": worst_length_bone,
        "worst_parent_child_length_frame": worst_length_frame,
        "maximum_scale": maximum_scale,
        "minimum_scale": minimum_scale,
        "root_inventory": [bone.name for bone in rig.bones if bone.parent_index < 0],
    }


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


def _orthonormal_rotation(matrix: np.ndarray, label: str) -> np.ndarray:
    linear = np.asarray(matrix, dtype=float)[:3, :3]
    if linear.shape != (3, 3) or not np.isfinite(linear).all():
        raise ValueError(f"{label} rotation basis must be finite")
    u, _singular, vt = np.linalg.svd(linear)
    rotation = u @ vt
    if float(np.linalg.det(rotation)) < 0.0:
        u[:, -1] *= -1.0
        rotation = u @ vt
    return rotation


def mapped_local_from_rotation_delta(
    target_bind_local: np.ndarray,
    source_bind_local: np.ndarray,
    source_animated_local: np.ndarray,
) -> np.ndarray:
    """Transfer source local rotation while preserving target bone geometry.

    Reviewed cross-skeleton maps connect anatomical roles, not identical bind
    pivots.  Importing source translations/scales changes target bone lengths
    and can tear a skinned mesh even when all units are correct.  Apply the
    source rest-relative rotation to the target bind rotation, while keeping
    the target's authored local translation and scale exactly unchanged.  Root
    displacement is handled independently by ``apply_global_root_policy``.
    """

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
    target_rotation = _orthonormal_rotation(target, "target bind")
    source_bind_rotation = _orthonormal_rotation(source_bind, "source bind")
    source_anim_rotation = _orthonormal_rotation(source_anim, "source animation")
    target_scale = np.linalg.norm(target[:3, :3], axis=0)
    if np.any(target_scale <= 1.0e-12):
        raise ValueError("target bind matrix has singular scale")
    result = np.eye(4, dtype=float)
    result[:3, :3] = (
        target_rotation
        @ source_bind_rotation.T
        @ source_anim_rotation
        @ np.diag(target_scale)
    )
    result[:3, 3] = target[:3, 3]
    return result


def mapped_local_from_composed_rotation_deltas(
    target_bind_local: np.ndarray,
    source_bind_locals: tuple[np.ndarray, ...] | list[np.ndarray],
    source_animated_locals: tuple[np.ndarray, ...] | list[np.ndarray],
) -> np.ndarray:
    """Compose ordered source-segment rotation deltas onto one target bone."""

    if not source_bind_locals or len(source_bind_locals) != len(source_animated_locals):
        raise ValueError(
            "Composed mapping requires equal non-empty bind and animation source lists"
        )
    target = np.asarray(target_bind_local, dtype=float)
    if target.shape != (4, 4) or not np.isfinite(target).all():
        raise ValueError("target bind matrix must be finite 4x4")
    target_rotation = _orthonormal_rotation(target, "target bind")
    target_scale = np.linalg.norm(target[:3, :3], axis=0)
    if np.any(target_scale <= 1.0e-12):
        raise ValueError("target bind matrix has singular scale")
    composed_delta = np.eye(3, dtype=float)
    for index, (source_bind, source_animation) in enumerate(
        zip(source_bind_locals, source_animated_locals)
    ):
        bind_rotation = _orthonormal_rotation(
            np.asarray(source_bind, dtype=float),
            f"composed source bind {index}",
        )
        animated_rotation = _orthonormal_rotation(
            np.asarray(source_animation, dtype=float),
            f"composed source animation {index}",
        )
        composed_delta = composed_delta @ bind_rotation.T @ animated_rotation
    result = np.eye(4, dtype=float)
    result[:3, :3] = target_rotation @ composed_delta @ np.diag(target_scale)
    result[:3, 3] = target[:3, 3]
    return result


def _fractional_rotation(rotation: np.ndarray, weight: float) -> np.ndarray:
    if not math.isfinite(weight) or not 0.0 < weight <= 1.0:
        raise ValueError("Distributed rotation weight must be finite in (0, 1]")
    payload = np.eye(4, dtype=float)
    payload[:3, :3] = np.asarray(rotation, dtype=float)
    _translation, quaternion, _scale = decompose_local_matrix(payload)
    quaternion = np.asarray(quaternion, dtype=float)
    if quaternion[0] < 0.0:
        quaternion *= -1.0
    half_angle = math.acos(max(-1.0, min(1.0, float(quaternion[0]))))
    vector = quaternion[1:]
    vector_norm = float(np.linalg.norm(vector))
    if vector_norm <= 1.0e-12 or half_angle <= 1.0e-12:
        return np.eye(3, dtype=float)
    axis = vector / vector_norm
    weighted_half_angle = half_angle * weight
    weighted_quaternion = np.asarray(
        (
            math.cos(weighted_half_angle),
            *(axis * math.sin(weighted_half_angle)),
        ),
        dtype=float,
    )
    return quaternion_wxyz_to_matrix(weighted_quaternion)


def mapped_local_from_distributed_rotation_delta(
    target_bind_local: np.ndarray,
    source_bind_local: np.ndarray,
    source_animated_local: np.ndarray,
    weight: float,
) -> np.ndarray:
    """Apply a fractional source rotation so chained target rows sum to one delta."""

    target = np.asarray(target_bind_local, dtype=float)
    source_bind = np.asarray(source_bind_local, dtype=float)
    source_animation = np.asarray(source_animated_local, dtype=float)
    target_rotation = _orthonormal_rotation(target, "target bind")
    source_bind_rotation = _orthonormal_rotation(source_bind, "source bind")
    source_animated_rotation = _orthonormal_rotation(
        source_animation, "source animation"
    )
    target_scale = np.linalg.norm(target[:3, :3], axis=0)
    if np.any(target_scale <= 1.0e-12):
        raise ValueError("target bind matrix has singular scale")
    delta = source_bind_rotation.T @ source_animated_rotation
    result = np.eye(4, dtype=float)
    result[:3, :3] = (
        target_rotation @ _fractional_rotation(delta, float(weight)) @ np.diag(target_scale)
    )
    result[:3, 3] = target[:3, 3]
    return result


def global_bind_basis_correction(source_bind_global: np.ndarray, target_bind_global: np.ndarray) -> np.ndarray:
    source = np.asarray(source_bind_global, dtype=float)
    target = np.asarray(target_bind_global, dtype=float)
    if source.shape != (4, 4) or target.shape != (4, 4) or not np.isfinite(source).all() or not np.isfinite(target).all():
        raise ValueError("source and target global bind matrices must be finite 4x4 matrices")
    try:
        correction = np.linalg.inv(source) @ target
    except np.linalg.LinAlgError as exc:
        raise ValueError("source global bind matrix is singular") from exc
    return correction


def corrected_target_global(source_animated_global: np.ndarray, correction: np.ndarray) -> np.ndarray:
    value = np.asarray(source_animated_global, dtype=float) @ np.asarray(correction, dtype=float)
    if value.shape != (4, 4) or not np.isfinite(value).all():
        raise ValueError("corrected target global matrix is non-finite")
    return value


def _rotation_error_degrees(left: np.ndarray, right: np.ndarray) -> float:
    left_rotation = _orthonormal_rotation(left, "parity actual target global")
    right_rotation = _orthonormal_rotation(right, "parity expected target global")
    relative = left_rotation.T @ right_rotation
    cosine = max(-1.0, min(1.0, (float(np.trace(relative)) - 1.0) * 0.5))
    return math.degrees(math.acos(cosine))


def apply_global_root_policy(
    values: list[list[list[float]]],
    rig: ChromeRig,
    target_root_name: str,
    policy: RootMotionSelection | Mapping[str, Any] | str,
) -> Any:
    """Backward-compatible entry point for the production global policy."""

    return apply_target_root_policy(values, rig, target_root_name, policy)


def _target_uses_dying_light_basis(rig: ChromeRig) -> bool:
    convention = str(getattr(rig.writer_profile, "coordinate_convention", "")).lower()
    extensions = dict(getattr(rig, "extensions", {}) or {})
    resolved = extensions.get("resolved_model_axis_conversion")
    if resolved is not None:
        return str(resolved).strip().lower() == "fbx_y_up_to_dying_light"
    orientation = str(extensions.get("model_axis_conversion", "")).lower()
    builder = str(extensions.get("builder", "")).lower()
    return (
        "dying_light_model" in convention
        or orientation in {"auto", "fbx_y_up_to_dying_light"}
        or builder.endswith("binary_fbx_v2")
    )


_EXECUTABLE_AUTOMATIC_MAPPING_MODES = frozenset(
    {"direct", "composed", "distributed", "inherit_bind", "static_bind"}
)


def _row_automatic_mapping_mode(row: BoneMapPair | None) -> str:
    if row is None:
        return "static_bind"
    extensions = dict(row.extensions or {})
    decision = extensions.get("automatic_retarget_decision", {}) or {}
    mode = str(
        extensions.get("execution_mapping_mode")
        or extensions.get("mapping_mode")
        or (decision.get("mode", "") if isinstance(decision, Mapping) else "")
        or ("direct" if row.source_fbx_bone else "inherit_bind")
    )
    return mode if mode in _EXECUTABLE_AUTOMATIC_MAPPING_MODES else ""


def _row_execution_source_bones(row: BoneMapPair) -> tuple[str, ...]:
    extensions = dict(row.extensions or {})
    decision = extensions.get("automatic_retarget_decision", {}) or {}
    raw = extensions.get("source_bones")
    if raw is None and isinstance(decision, Mapping):
        raw = decision.get("source_bones")
    if raw is None:
        raw = (row.source_fbx_bone,) if row.source_fbx_bone else ()
    if isinstance(raw, str):
        raw = (raw,)
    return tuple(str(value) for value in raw if str(value))


def _row_distribution_weight(row: BoneMapPair) -> float:
    extensions = dict(row.extensions or {})
    if "distribution_weight" in extensions:
        return float(extensions["distribution_weight"] or 0.0)
    decision = extensions.get("automatic_retarget_decision", {}) or {}
    if isinstance(decision, Mapping):
        for evidence in decision.get("evidence", ()) or ():
            if (
                isinstance(evidence, Mapping)
                and str(evidence.get("kind", "")).casefold()
                == "semantic_chain_distribution"
            ):
                return float(evidence.get("score", 0.0) or 0.0)
    return 0.0


def _mapped_pairs_by_target_rig_bone(
    rig: ChromeRig,
    bone_map: GenericBoneMap,
    document: FbxDocument,
) -> tuple[dict[str, BoneMapPair], dict[str, str], list[str]]:
    rig_by_name = {bone.name: bone for bone in rig.bones}
    source_names = set(document.limb_models)
    rows_by_target: dict[str, BoneMapPair] = {}
    result: dict[str, str] = {}
    warnings: list[str] = []
    errors: list[str] = []
    # Helper fan-out rows are evaluated only after the main mapped-rig/root
    # solver.  They must not participate in its topology or policy selection.
    for row in bone_map.base_pairs:
        target_rig_bone = str(row.target_rig_bone)
        source_fbx_bone = str(row.source_fbx_bone)
        target = rig_by_name.get(target_rig_bone)
        if target is None:
            errors.append(
                f"Mapping row targets unknown .crig bone {target_rig_bone!r}. "
                "Choose a bone from the selected target rig or recreate the map."
            )
            continue
        if int(row.target_rig_descriptor) != int(target.descriptor):
            errors.append(
                f"Mapping row for target {target_rig_bone!r} records descriptor "
                f"0x{int(row.target_rig_descriptor):08X}, but the selected .crig uses "
                f"0x{int(target.descriptor):08X}. Recreate the map for this target rig."
            )
            continue
        rows_by_target[target_rig_bone] = row
        mapping_mode = _row_automatic_mapping_mode(row)
        if not mapping_mode:
            errors.append(
                f"Mapping row for target {target_rig_bone!r} declares an unsupported "
                "automatic execution mode. Regenerate or review the map."
            )
            continue
        execution_sources = _row_execution_source_bones(row)
        if mapping_mode == "composed":
            if len(execution_sources) < 2:
                errors.append(
                    f"Composed mapping for target {target_rig_bone!r} requires at least "
                    "two ordered source bones."
                )
            if row.transfer_policy != "rotation_delta" or row.component_policy != "rotation":
                errors.append(
                    f"Composed mapping for target {target_rig_bone!r} must use "
                    "rotation_delta with rotation-only ownership."
                )
        elif mapping_mode == "distributed":
            weight = _row_distribution_weight(row)
            if len(execution_sources) != 1:
                errors.append(
                    f"Distributed mapping for target {target_rig_bone!r} requires "
                    "exactly one source bone."
                )
            if not math.isfinite(weight) or not 0.0 < weight <= 1.0:
                errors.append(
                    f"Distributed mapping for target {target_rig_bone!r} has invalid "
                    "fractional rotation weight."
                )
            if row.transfer_policy != "rotation_delta" or row.component_policy != "rotation":
                errors.append(
                    f"Distributed mapping for target {target_rig_bone!r} must use "
                    "rotation_delta with rotation-only ownership."
                )
        if execution_sources and execution_sources[0] != source_fbx_bone:
            errors.append(
                f"Mapping row for target {target_rig_bone!r} has a stale primary source "
                "relative to its executable source inventory."
            )
        missing_execution_sources = [
            name for name in execution_sources if name not in source_names
        ]
        if missing_execution_sources:
            errors.append(
                f"Mapping row for target {target_rig_bone!r} references missing "
                "executable source bone(s): " + ", ".join(missing_execution_sources)
            )
        if row.transfer_policy == "bind" and row.review_state != "intentionally_unmapped":
            errors.append(
                f"Mapping row for target {target_rig_bone!r} uses bind transfer but is not "
                "marked intentionally_unmapped. Explicitly review that target and mark it "
                "intentionally unmapped so an accidental missing body track cannot build."
            )
            continue
        if row.review_state == "intentionally_unmapped":
            continue
        if row.review_state == "automatic_unreviewed":
            errors.append(
                f"Mapping row {target_rig_bone!r} <- {source_fbx_bone!r} is an "
                "unreviewed automatic suggestion. Review or intentionally unmap this row "
                "before building the cross-rig clip."
            )
            continue
        if not source_fbx_bone:
            errors.append(
                f"Mapping row for target {target_rig_bone!r} has no source FBX bone. "
                "Choose a source bone or mark the target intentionally unmapped at bind."
            )
            continue
        if source_fbx_bone not in source_names:
            errors.append(
                f"Mapping row {target_rig_bone!r} references missing source FBX bone "
                f"{source_fbx_bone!r}. Available source bones do not contain that name; "
                "review the map for this animation FBX."
            )
            continue
        result[target_rig_bone] = source_fbx_bone
    reviewed_helper_targets = {
        str(row.target_rig_bone)
        for row in bone_map.helper_pairs
        if row.review_state != "automatic_unreviewed"
        and str(row.source_fbx_bone) in source_names
        and str(row.target_rig_bone) in rig_by_name
        and int(row.target_rig_descriptor)
        == int(rig_by_name[str(row.target_rig_bone)].descriptor)
    }
    missing_required = sorted(
        (
            bone.name
            for bone in rig.bones
            if bone.deform
            and not bone.helper
            and bone.name not in rows_by_target
            and bone.name not in reviewed_helper_targets
        ),
        key=str.casefold,
    )
    if missing_required:
        errors.append(
            "Required target deform bones have no reviewed mapping row: "
            + ", ".join(missing_required[:30])
            + (" ..." if len(missing_required) > 30 else "")
            + ". Map each named target to a source FBX bone, or add an explicit "
            "intentionally_unmapped bind row after reviewing why it should remain at bind."
        )
    if errors:
        raise ValueError("Mapped-rig profile cannot be applied:\n- " + "\n- ".join(errors))
    return rows_by_target, result, warnings


def _validate_mapping_identity(
    rig: ChromeRig,
    bone_map: GenericBoneMap,
    document: FbxDocument,
) -> list[str]:
    warnings: list[str] = []
    expected_bind_hash = bone_map.target_bind_hash or bone_map.source_skeleton_hash
    if expected_bind_hash and expected_bind_hash != rig.skeleton_hash:
        raise ValueError(
            "Mapped-rig profile was created for a different target full bind: "
            f"map={expected_bind_hash}, selected .crig={rig.skeleton_hash}. Recreate or "
            "explicitly migrate the map for the selected model CRIG before building."
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


def _default_row_transfer_policy(transfer_policy: str) -> str:
    aliases = {
        "mapped_local_rest_delta": "rest_relative",
        "mapped_local_rotation_delta": "rotation_delta",
        "global_bind_basis_correction": "global_bind_basis",
    }
    try:
        return aliases[transfer_policy]
    except KeyError as exc:
        raise ValueError(f"Unsupported retarget transfer policy {transfer_policy!r}") from exc


def _resolved_row_transfer_policy(
    row: BoneMapPair | None,
    default_policy: str,
) -> str:
    if row is None:
        return "bind"
    policy = str(row.transfer_policy or "default")
    return default_policy if policy == "default" else policy


def _component_owns_translation(component_policy: str) -> bool:
    return component_policy in {"translation", "rotation_translation", "full_transform"}


def _component_owns_scale(component_policy: str) -> bool:
    return component_policy in {"scale", "full_transform"}


def _row_can_change_translation(row: BoneMapPair, resolved_policy: str) -> bool:
    return (
        _component_owns_translation(row.component_policy)
        and resolved_policy not in {"bind", "rotation_delta"}
    )


def _row_can_change_scale(row: BoneMapPair, resolved_policy: str) -> bool:
    return (
        _component_owns_scale(row.component_policy)
        and resolved_policy not in {"bind", "rotation_delta"}
    )


def build_mapped_rig_anm2(
    animation_fbx: str | Path,
    rig: ChromeRig,
    bone_map: GenericBoneMap,
    *,
    fps: float | None = None,
    animation_stack: str | None = None,
    document_factory: Any = FbxDocument,
    document: Any | None = None,
    root_mapping: RootMappingSelection | Mapping[str, Any] | None = None,
    transfer_policy: str = "mapped_local_rest_delta",
    root_policy: str = "bip01",
    root_motion: RootMotionSelection | Mapping[str, Any] | None = None,
) -> MappedRigBuild:
    """Retarget an arbitrary mapped FBX skeleton onto a Chrome Rig."""

    rig.validate().require_valid()
    errors = bone_map.validate()
    if errors:
        raise ValueError("Invalid mapped-rig profile:\n- " + "\n- ".join(errors))

    sample_fps = float(
        rig.writer_profile.default_fps if fps is None else fps
    )
    if not math.isfinite(sample_fps) or sample_fps <= 0.0:
        raise ValueError("sample FPS must be finite and positive")
    if sample_fps > 1000.0:
        raise ValueError("Mapped-rig sample FPS must not exceed 1000")

    source = Path(animation_fbx)
    document = document if document is not None else document_factory(source)
    selected_stack = getattr(document, "selected_animation_stack", None)
    selected_stack_name = str(getattr(selected_stack, "name", "") or "")
    if (
        animation_stack
        and selected_stack_name != animation_stack
    ) or (
        not animation_stack
        and len(getattr(document, "animation_stacks", ())) > 1
        and selected_stack is None
    ):
        document.select_animation_stack(animation_stack)
    if not document.limb_models:
        raise ValueError("Mapped-rig retarget requires an FBX LimbNode skeleton")
    mapped_preflight = preflight_fbx(
        source,
        purpose="animation",
        animation_stack=animation_stack,
        document_factory=document_factory,
        document=document,
    )
    mapped_preflight.require_buildable()

    warnings = _validate_mapping_identity(rig, bone_map, document)
    base_rows, mapped, pair_warnings = _mapped_pairs_by_target_rig_bone(
        rig, bone_map, document
    )
    warnings.extend(pair_warnings)
    helper_rules = helper_rules_from_pairs(bone_map.helper_pairs)

    if isinstance(root_mapping, RootMappingSelection):
        root_selection = root_mapping
    else:
        payload = dict(root_mapping or {})
        root_selection = RootMappingSelection(
            source_bone=str(payload.get("source_bone", "") or ""),
            target_bone=str(payload.get("target_bone", "") or ""),
        )
    requested_root_motion = resolve_root_motion_selection(
        root_motion if root_motion is not None else root_policy,
        source_root_bone=root_selection.source_bone,
        target_root_bone=root_selection.target_bone,
    )

    rig_names = [bone.name for bone in rig.bones]
    rig_parents = {
        bone.name: (rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None)
        for bone in rig.bones
    }
    requested_target_root = (
        requested_root_motion.target_root_bone or root_selection.target_bone
    )
    if requested_target_root:
        if requested_target_root not in set(rig_names):
            raise ValueError(
                f"Selected target skeletal root {requested_target_root!r} is not present "
                f"in .crig target {rig.name!r}."
            )
        target_root_name = requested_target_root
        target_root_method = "manual"
    else:
        target_root_name = choose_hierarchy_root(rig_names, rig_parents)
        target_root_method = "automatic"

    source_root_name, source_root_method = resolve_source_root(
        document.limb_models.keys(),
        document.parent_by_name,
        requested_bone=(
            requested_root_motion.source_root_bone or root_selection.source_bone
        ),
    )
    resolved_root_motion = RootMotionSelection(
        source_root_name,
        target_root_name,
        requested_root_motion.motion_mode,
        requested_root_motion.heading_mode,
    )
    target_root_bone = next(bone for bone in rig.bones if bone.name == target_root_name)

    # Repair maps created by the early unified GUI, which consumed Hips as the
    # pose of a non-deforming wrapper root.  Hips rotation belongs on the pelvis
    # pose; its displacement is sampled separately below for the selected root
    # policy.  Explicit/manual wrapper mappings remain respected.
    legacy_root_pair = next(
        (
            row
            for row in bone_map.base_pairs
            if row.target_rig_bone == target_root_name
            and row.source_fbx_bone == source_root_name
            and row.method == "hierarchy_root"
        ),
        None,
    )
    if legacy_root_pair is not None and target_root_bone.helper and not target_root_bone.deform:
        mapped.pop(target_root_name, None)
        base_rows.pop(target_root_name, None)
        if source_root_name not in mapped.values():
            from ..retarget_mapping import canonical_humanoid_role

            pelvis_candidates = [
                bone
                for bone in rig.bones
                if bone.name not in mapped
                and canonical_humanoid_role(bone.name) == "pelvis"
            ]
            pelvis_candidates.sort(
                key=lambda bone: (
                    0 if bone.deform and not bone.helper else 1,
                    bone.index,
                )
            )
            if pelvis_candidates:
                pelvis = pelvis_candidates[0]
                mapped[pelvis.name] = source_root_name
                base_rows[pelvis.name] = BoneMapPair(
                    pelvis.descriptor,
                    pelvis.name,
                    source_root_name,
                    legacy_root_pair.confidence,
                    "legacy_root_repair:pelvis_pose",
                    legacy_root_pair.transfer_policy,
                    legacy_root_pair.component_policy,
                    "bone",
                    legacy_root_pair.review_state,
                    legacy_root_pair.notes,
                    legacy_root_pair.extensions,
                )
                warnings.append(
                    f"Migrated legacy wrapper mapping {target_root_name!r} <- "
                    f"{source_root_name!r} to pelvis pose {pelvis.name!r}; "
                    "root displacement remains controlled separately."
                )
        warnings.append(
            f"Removed legacy pose rotation from helper root {target_root_name!r}; "
            "the selected root policy now controls displacement without duplicating pelvis pose."
        )
    if not mapped and not base_rows:
        raise ValueError(
            "Mapped-rig retarget has no valid bone rows. Use Auto-map or assign bones manually."
        )

    default_row_policy = _default_row_transfer_policy(transfer_policy)
    resolved_policy_by_target = {
        target_name: _resolved_row_transfer_policy(row, default_row_policy)
        for target_name, row in base_rows.items()
    }
    global_correction_targets = {
        target_name
        for target_name, policy in resolved_policy_by_target.items()
        if policy == "global_bind_basis"
        and target_name in mapped
        and _row_automatic_mapping_mode(base_rows[target_name])
        not in {"composed", "distributed"}
    }
    # Unit/axis normalization is one contract shared by model bind and animation.
    # New model-generated CRIGs select the source document's full signed
    # GlobalSettings basis. Legacy CRIGs retain their established Y-up fallback.
    target_extensions = dict(getattr(rig, "extensions", {}) or {})
    target_builder = str(target_extensions.get("builder", "") or "").lower()
    uses_canonical_document_basis = (
        target_builder.startswith("dl_reanimated")
        or bool(target_extensions.get("authored_rig_contract_id"))
        or str(target_extensions.get("model_msh_reference_policy", ""))
        == "inverse_global_bind"
    )
    target_requires_dying_light_basis = _target_uses_dying_light_basis(rig)
    meters_per_unit = float(document.meters_per_unit)
    wrapper_axis_conversion = False
    if not uses_canonical_document_basis and hasattr(
        document, "_scene_scale_normalizer"
    ):
        scene = getattr(document, "scene", None)
        if scene is not None:
            limb_ids = set(getattr(scene, "limb_ids", ()))
            parent_id = scene.model_parent_id(
                document.limb_models[source_root_name]
            )
            wrapper_id = None
            while parent_id in limb_ids:
                parent_id = scene.model_parent_id(parent_id)
            while parent_id in getattr(scene, "model_names", {}) and parent_id not in limb_ids:
                wrapper_id = parent_id
                parent_id = scene.model_parent_id(parent_id)
            if wrapper_id is not None:
                wrapper_matrix = np.asarray(
                    scene.model_global_matrix(wrapper_id), dtype=float
                )
                wrapper_linear = wrapper_matrix[:3, :3]
                wrapper_linear_scales = np.linalg.norm(wrapper_linear, axis=0)
                rotation_retained = True
                policy = getattr(
                    document, "wrapper_axis_conversion_is_retained", None
                )
                if callable(policy):
                    rotation_retained = bool(policy(source_root_name))
                if rotation_retained and np.all(wrapper_linear_scales > 1.0e-12):
                    wrapper_rotation = wrapper_linear / wrapper_linear_scales
                    wrapper_axis_conversion = bool(
                        np.allclose(
                            wrapper_rotation,
                            FBX_Y_UP_TO_DYING_LIGHT[:3, :3],
                            atol=1.0e-5,
                            rtol=1.0e-5,
                        )
                    )
    if uses_canonical_document_basis:
        requested_target_basis = str(
            target_extensions.get("requested_model_axis_conversion", "auto")
            or "auto"
        ).strip().lower()
        stored_target_basis = target_extensions.get("model_axis_basis_matrix")
        if requested_target_basis != "auto" and stored_target_basis is not None:
            source_basis_matrix = np.asarray(stored_target_basis, dtype=float)
        elif hasattr(document, "target_basis_matrix"):
            source_basis_matrix = np.asarray(
                document.target_basis_matrix(), dtype=float
            )
        else:
            scene = getattr(document, "scene", None)
            if scene is not None and hasattr(scene, "coordinate_conversion_matrix"):
                source_basis_matrix = np.asarray(
                    scene.coordinate_conversion_matrix(
                        getattr(document, "requested_orientation_policy", "auto")
                    ),
                    dtype=float,
                )
            else:
                source_basis_matrix = np.eye(4, dtype=float)
        source_basis_label = (
            str(
                target_extensions.get("resolved_model_axis_conversion", "none")
                or "none"
            )
            if requested_target_basis != "auto" and stored_target_basis is not None
            else str(
                getattr(document, "resolved_orientation_policy", "none") or "none"
            )
        )
        convert_basis = False
    else:
        convert_basis = bool(
            target_requires_dying_light_basis and not wrapper_axis_conversion
        )
        source_basis_matrix = (
            FBX_Y_UP_TO_DYING_LIGHT.copy()
            if convert_basis
            else np.eye(4, dtype=float)
        )
        source_basis_label = (
            "fbx_y_up_to_dying_light"
            if convert_basis or wrapper_axis_conversion
            else "none"
        )

    source_normalizers: dict[str, SourceGlobalNormalization] = {}

    def source_normalizer(source_name: str) -> SourceGlobalNormalization:
        normalizer = source_normalizers.get(source_name)
        if normalizer is not None:
            return normalizer
        wrapper_factor = 1.0
        if hasattr(document, "wrapper_scale_normalization_factor"):
            wrapper_factor = float(
                document.wrapper_scale_normalization_factor(source_name)
            )
        normalizer = SourceGlobalNormalization(
            meters_per_unit=meters_per_unit,
            convert_y_up_to_dying_light=convert_basis,
            wrapper_scale_normalization_factor=wrapper_factor,
            wrapper_axis_conversion=wrapper_axis_conversion,
            basis_matrix=source_basis_matrix,
            basis_label=source_basis_label,
        )
        source_normalizers[source_name] = normalizer
        return normalizer

    global_normalization = source_normalizer(source_root_name)
    source_analysis = analyze_source_skeleton(document)
    # Semantic body axes are required only when actor displacement will be
    # emitted on a humanoid target.  In-place clips discard that displacement;
    # generic/object rigs have no anatomical bilateral frame to infer.  Those
    # two cases use the document/target declared coordinate frame explicitly,
    # while moving humanoids retain the focused ambiguity failure.
    actor_frame_is_advisory = (
        resolved_root_motion.motion_mode == "inplace"
        or str(source_analysis.archetype).casefold() != "humanoid"
        or str(rig.category).casefold() != "humanoid"
    )
    source_actor_frame = build_source_actor_frame(
        source_analysis,
        allow_declared_fallback=actor_frame_is_advisory,
    )
    target_actor_frame = build_target_actor_frame(
        rig,
        build_target_retarget_policy(rig),
        allow_declared_fallback=actor_frame_is_advisory,
    )
    helper_source_names = {
        rule.source_bone
        for rule in helper_rules
        if rule.source_bone in document.limb_models
    }
    source_bind_local: dict[str, np.ndarray] = {}
    canonical_bind_locals = dict(
        getattr(document, "bind_local_matrices", {}) or {}
    )
    execution_source_names = {
        source_name
        for row in base_rows.values()
        for source_name in _row_execution_source_bones(row)
    }
    for source_name in sorted(
        execution_source_names.union(
            set(mapped.values()), {source_root_name}, helper_source_names
        )
    ):
        raw_bind_local = canonical_bind_locals.get(source_name)
        if raw_bind_local is None:
            raw_bind_local = document._local_matrix(
                document.limb_models[source_name], tick=0, use_animation=False
            )
        source_bind_local[source_name] = source_normalizer(source_name).apply_local(
            raw_bind_local
        )

    target_bind = {bone.name: target_bind_local_matrix(bone) for bone in rig.bones}
    target_bind_global: dict[str, np.ndarray] = {}
    for bone in rig.bones:
        parent_name = rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None
        target_bind_global[bone.name] = (
            target_bind_global[parent_name] @ target_bind[bone.name]
            if parent_name is not None else target_bind[bone.name].copy()
        )
    for target_name, source_name in mapped.items():
        if resolved_policy_by_target[target_name] != "copy_local":
            continue
        if not np.allclose(
            source_bind_local[source_name],
            target_bind[target_name],
            atol=1.0e-5,
            rtol=1.0e-5,
        ):
            raise ValueError(
                f"copy_local is not safe for {target_name!r} <- {source_name!r}: "
                "their normalized local bind bases differ. Use global_bind_basis for an "
                "exact/name-equivalent row or rotation_delta for a cross-rig deform row."
            )
    basis_corrections: dict[str, np.ndarray] = {}
    if global_correction_targets:
        source_globals = getattr(document, "bind_global_matrices", None)
        if not source_globals:
            raise ValueError(
                "Global bind-basis correction requires authoritative/fallback FBX global bind matrices."
            )
        for target_name in sorted(global_correction_targets):
            source_name = mapped[target_name]
            if source_name not in source_globals:
                raise ValueError(
                    f"Global bind-basis row {target_name!r} <- {source_name!r} has no "
                    "source global bind matrix. Re-export the FBX with a complete BindPose "
                    "or skin TransformLink inventory."
                )
            source_global = source_normalizer(source_name).apply(
                source_globals[source_name]
            )
            if not np.isfinite(source_global).all() or abs(float(np.linalg.det(source_global[:3, :3]))) <= 1.0e-12:
                raise ValueError(f"Source bind matrix for {source_name!r} is singular or non-finite.")
            basis_corrections[target_name] = global_bind_basis_correction(
                source_global, target_bind_global[target_name]
            )
    source_bind_globals = getattr(document, "bind_global_matrices", None)
    helper_source_bind_global: dict[str, np.ndarray] = {}
    if source_bind_globals:
        helper_source_bind_global = {
            source_name: source_normalizer(source_name).apply(
                source_bind_globals[source_name]
            )
            for source_name in helper_source_names
            if source_name in source_bind_globals
        }
    if source_bind_globals and source_root_name in source_bind_globals:
        source_root_bind_raw_global = np.asarray(
            source_bind_globals[source_root_name], dtype=float
        )
        source_root_bind_global = global_normalization.apply(
            source_bind_globals[source_root_name]
        )
    elif hasattr(document, "global_matrices"):
        source_root_bind_raw_global = np.asarray(
            document.global_matrices(tick=0, use_animation=False)[source_root_name],
            dtype=float,
        )
        source_root_bind_global = global_normalization.apply(
            source_root_bind_raw_global
        )
    else:
        raw_bind_local = canonical_bind_locals.get(source_root_name)
        if raw_bind_local is None:
            raw_bind_local = document._local_matrix(
                document.limb_models[source_root_name], tick=0, use_animation=False
            )
        source_root_bind_raw_global = np.asarray(raw_bind_local, dtype=float)
        source_root_bind_global = source_bind_local[source_root_name]
    source_root_unit_scale = (
        global_normalization.meters_per_unit
        / global_normalization.wrapper_scale_normalization_factor
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

    values: list[list[list[float]]] = []
    bind_track_values = rig.bind_track_values()
    bind_row_by_descriptor = {
        descriptor: bind_track_values[index]
        for index, descriptor in enumerate(rig.descriptors)
    }
    movement_ranges: dict[str, float] = {bone.name: 0.0 for bone in rig.bones}
    bind_deltas: list[dict[str, Any]] = []
    bind_joint_extent = _joint_pivot_extent(target_bind_global)
    maximum_animated_joint_extent = 0.0
    maximum_animated_joint_extent_frame = 0
    source_root_displacements: list[np.ndarray] = []
    source_root_raw_displacements_m: list[np.ndarray] = []
    source_root_animated_globals: list[np.ndarray] = []
    helper_source_local_frames: list[dict[str, np.ndarray]] = []
    helper_source_global_frames: list[dict[str, np.ndarray]] = []
    representative_rotation_errors: dict[int, float] = {}
    representative_direct_row_counts: dict[int, int] = {}

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
        target_animated_globals: dict[str, np.ndarray] = {}
        raw_source_animated_globals = (
            document.global_matrices(tick=tick, use_animation=True)
            if hasattr(document, "global_matrices")
            else {}
        )
        raw_source_animated_locals = (
            document.skeletal_local_matrices(
                tick=tick,
                use_animation=True,
                globals_by_name=raw_source_animated_globals,
            )
            if hasattr(document, "skeletal_local_matrices")
            else {}
        )
        helper_source_local_frames.append(
            {
                source_name: source_normalizer(source_name).apply_local(
                    raw_source_animated_locals.get(source_name)
                    if source_name in raw_source_animated_locals
                    else document._local_matrix(
                        document.limb_models[source_name],
                        tick=tick,
                        use_animation=True,
                    )
                )
                for source_name in helper_source_names
            }
        )
        helper_source_global_frames.append(
            {
                source_name: source_normalizer(source_name).apply(
                    raw_source_animated_globals[source_name]
                )
                for source_name in helper_source_names
                if source_name in raw_source_animated_globals
            }
        )
        source_animated_globals: dict[str, np.ndarray] = {}
        for target_name in global_correction_targets:
            source_name = mapped[target_name]
            if source_name not in raw_source_animated_globals:
                raise ValueError(
                    f"Global bind-basis row {target_name!r} <- {source_name!r} cannot "
                    "evaluate an animated global matrix. Use a canonical FBX evaluator "
                    "with global animation support or choose another transfer policy."
                )
            source_animated_globals[source_name] = source_normalizer(source_name).apply(
                raw_source_animated_globals[source_name]
            )
        if source_root_name in raw_source_animated_globals:
            root_animated_raw_global = np.asarray(
                raw_source_animated_globals[source_root_name], dtype=float
            )
            root_animated_global = global_normalization.apply(
                root_animated_raw_global
            )
        else:
            root_animated_raw_global = np.asarray(
                document._local_matrix(
                    document.limb_models[source_root_name],
                    tick=tick,
                    use_animation=True,
                ),
                dtype=float,
            )
            root_animated_global = global_normalization.apply_local(
                root_animated_raw_global
            )
        root_displacement_source_m = (
            root_animated_raw_global[:3, 3]
            - source_root_bind_raw_global[:3, 3]
        ) * source_root_unit_scale
        root_displacement_global = map_root_displacement_by_actor_frame(
            root_displacement_source_m,
            source_actor_frame,
            target_actor_frame,
        )
        source_root_raw_displacements_m.append(root_displacement_source_m)
        source_root_animated_globals.append(root_animated_global.copy())
        if target_root_bone.parent_index >= 0:
            parent_name = rig.bones[target_root_bone.parent_index].name
            root_displacement_local = (
                np.linalg.inv(target_bind_global[parent_name][:3, :3])
                @ root_displacement_global
            )
        else:
            root_displacement_local = root_displacement_global
        source_root_displacements.append(root_displacement_local)
        normalized_source_animated_locals: dict[str, np.ndarray] = {}

        def animated_source_local(source_bone: str) -> np.ndarray:
            cached = normalized_source_animated_locals.get(source_bone)
            if cached is not None:
                return cached
            raw = (
                raw_source_animated_locals[source_bone]
                if source_bone in raw_source_animated_locals
                else document._local_matrix(
                    document.limb_models[source_bone],
                    tick=tick,
                    use_animation=True,
                )
            )
            normalized = source_normalizer(source_bone).apply_local(raw)
            normalized_source_animated_locals[source_bone] = normalized
            return normalized

        for bone in rig.bones:
            target_bind_local = target_bind[bone.name]
            pair = base_rows.get(bone.name)
            source_name = mapped.get(bone.name)
            parent_name = rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None
            resolved_policy = _resolved_row_transfer_policy(pair, default_row_policy)
            mapping_mode = _row_automatic_mapping_mode(pair)
            candidate_local = target_bind_local
            if source_name is not None and mapping_mode == "composed":
                execution_sources = _row_execution_source_bones(pair)
                candidate_local = mapped_local_from_composed_rotation_deltas(
                    target_bind_local,
                    [source_bind_local[name] for name in execution_sources],
                    [animated_source_local(name) for name in execution_sources],
                )
            elif source_name is not None and mapping_mode == "distributed":
                candidate_local = mapped_local_from_distributed_rotation_delta(
                    target_bind_local,
                    source_bind_local[source_name],
                    animated_source_local(source_name),
                    _row_distribution_weight(pair),
                )
            elif source_name is not None and resolved_policy == "global_bind_basis":
                desired_global = corrected_target_global(
                    source_animated_globals[source_name], basis_corrections[bone.name]
                )
                if parent_name is None:
                    candidate_local = desired_global
                else:
                    try:
                        candidate_local = (
                            np.linalg.inv(target_animated_globals[parent_name])
                            @ desired_global
                        )
                    except np.linalg.LinAlgError as exc:
                        raise ValueError(
                            f"Animated target parent {parent_name!r} is singular while "
                            f"evaluating global-bind row {bone.name!r}."
                        ) from exc
            elif source_name is not None:
                source_anim_local = animated_source_local(source_name)
                if resolved_policy == "rotation_delta":
                    candidate_local = mapped_local_from_rotation_delta(
                        target_bind_local,
                        source_bind_local[source_name],
                        source_anim_local,
                    )
                elif resolved_policy == "rest_relative":
                    candidate_local = mapped_local_from_rest_delta(
                        target_bind_local,
                        source_bind_local[source_name],
                        source_anim_local,
                    )
                elif resolved_policy == "copy_local":
                    candidate_local = source_anim_local
                elif resolved_policy != "bind":
                    raise ValueError(
                        f"Unsupported transfer policy {resolved_policy!r} on target "
                        f"bone {bone.name!r}."
                    )
            bind_row = bind_row_by_descriptor[bone.descriptor]
            candidate_row = local_matrix_to_anm2_values(candidate_local)
            component_policy = pair.component_policy if pair is not None else "full_transform"
            row = merge_helper_components(
                bind_row,
                candidate_row,
                component_policy,
            )
            local = anm2_values_to_local_matrix(row)
            target_animated_globals[bone.name] = (
                target_animated_globals[parent_name] @ local
                if parent_name is not None
                else local.copy()
            )
            rows_by_descriptor[bone.descriptor] = row
            movement_ranges[bone.name] = max(
                movement_ranges[bone.name],
                max(abs(float(a) - float(b)) for a, b in zip(row, bind_row)),
            )
        frame_index = len(values)
        if frame_index in _DL2_PARITY_FRAMES:
            maximum_rotation_error = 0.0
            compared_rows = 0
            for target_name, pair in base_rows.items():
                source_name = mapped.get(target_name)
                if (
                    target_name == target_root_name
                    or source_name is None
                    or _row_automatic_mapping_mode(pair) != "direct"
                    or resolved_policy_by_target[target_name]
                    != "global_bind_basis"
                    or target_name not in basis_corrections
                ):
                    continue
                expected_global = corrected_target_global(
                    source_animated_globals[source_name],
                    basis_corrections[target_name],
                )
                maximum_rotation_error = max(
                    maximum_rotation_error,
                    _rotation_error_degrees(
                        target_animated_globals[target_name], expected_global
                    ),
                )
                compared_rows += 1
            representative_rotation_errors[frame_index] = maximum_rotation_error
            representative_direct_row_counts[frame_index] = compared_rows

        frame_extent = _joint_pivot_extent(target_animated_globals)
        if frame_extent > maximum_animated_joint_extent:
            maximum_animated_joint_extent = frame_extent
            maximum_animated_joint_extent_frame = len(values)
        frame = [
            rows_by_descriptor.get(
                descriptor,
                [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0],
            )
            for descriptor in rig.descriptors
        ]
        values.append(frame)

    root_row = base_rows.get(target_root_name)
    root_row_policy = _resolved_row_transfer_policy(root_row, default_row_policy)
    root_mapping_owns_translation = bool(
        root_row is not None
        and target_root_name in mapped
        and _row_can_change_translation(root_row, root_row_policy)
    )
    if not root_mapping_owns_translation:
        root_track = rig.descriptors.index(target_root_bone.descriptor)
        root_bind_translation = np.asarray(target_root_bone.bind_translation, dtype=float)
        for frame, displacement in zip(values, source_root_displacements):
            frame[root_track][3:6] = [
                float(value) for value in root_bind_translation + displacement
            ]

    # Root policies are target-track policies and apply to both the global and
    # local-delta solvers.  Previously mapped cross-rig clips silently ignored
    # the user's in-place/motion selection.
    root_heading_report = apply_global_root_policy(
        values, rig, target_root_name, resolved_root_motion
    )
    source_root_heading_degrees = root_heading_report.source_heading_degrees
    source_frame_zero_displacement_m = source_root_raw_displacements_m[0]
    source_last_displacement_m = source_root_raw_displacements_m[-1]
    source_net_displacement_m = (
        source_last_displacement_m - source_frame_zero_displacement_m
    )
    basis_report = root_motion_basis_report(
        source_actor_frame,
        target_actor_frame,
        source_net_displacement_m,
    )
    basis_report.update(
        {
            "net_reference": "source_frame_zero_to_last_frame",
            "source_bind_translation_native": (
                source_root_bind_raw_global[:3, 3].tolist()
            ),
            "source_frame_zero_displacement_from_bind_m": (
                source_frame_zero_displacement_m.tolist()
            ),
            "source_last_displacement_from_bind_m": (
                source_last_displacement_m.tolist()
            ),
        }
    )

    helper_report = HelperApplyReport()
    if helper_rules:
        helper_report = apply_helper_retarget_overrides(
            values,
            helper_rules,
            target_bind_local=target_bind,
            target_track_indices={
                bone.name: rig.descriptors.index(bone.descriptor) for bone in rig.bones
            },
            target_parents=rig_parents,
            source_bind_local={
                name: source_bind_local[name]
                for name in helper_source_names
                if name in source_bind_local
            },
            source_animated_local_frames=helper_source_local_frames,
            target_descriptors={bone.name: bone.descriptor for bone in rig.bones},
            source_bind_global=helper_source_bind_global,
            source_animated_global_frames=helper_source_global_frames,
            target_roots=(bone.name for bone in rig.bones if bone.parent_index < 0),
            source_roots=(
                name
                for name in document.limb_models
                if document.parent_by_name.get(name) is None
            ),
            deforming_primary_targets=(
                bone.name for bone in rig.bones if bone.deform and not bone.helper
            ),
        )
        base_targets_by_source: dict[str, list[str]] = {}
        for target_name, source_name in mapped.items():
            base_targets_by_source.setdefault(source_name, []).append(target_name)
        include_base_source_fanout(
            helper_report, helper_rules, base_targets_by_source
        )
        warnings.extend(helper_report.warnings)

    active_helper_targets = set(helper_report.helper_targets)
    authorized_translation_targets = {
        target_name
        for target_name, row in base_rows.items()
        if target_name in mapped
        and _row_can_change_translation(
            row, resolved_policy_by_target[target_name]
        )
    }
    authorized_translation_targets.update(
        rule.target_bone
        for rule in helper_rules
        if rule.target_bone in active_helper_targets
        if _component_owns_translation(rule.component_policy)
        and rule.transfer_policy not in {"bind", "rotation_delta"}
    )
    hierarchy_safety = validate_hierarchy_safety(
        rig,
        values,
        preserve_non_root_translations=True,
        allowed_non_root_translation_bones=authorized_translation_targets,
    )

    for bone in rig.bones:
        track_index = rig.descriptors.index(bone.descriptor)
        bind_row = bind_row_by_descriptor[bone.descriptor]
        movement_ranges[bone.name] = max(
            max(abs(float(a) - float(b)) for a, b in zip(frame[track_index], bind_row))
            for frame in values
        )

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
    maximum_error = validate_decoded_component_error(
        decoded,
        values,
        sample_frames,
        engine_name="MappedRigRetargetEngine",
    )

    intentionally_unmapped = {
        target_name
        for target_name, row in base_rows.items()
        if row.review_state == "intentionally_unmapped"
    }
    bind_only_targets = {
        target_name
        for target_name, row in base_rows.items()
        if _resolved_row_transfer_policy(row, default_row_policy) == "bind"
    }
    mapped_target_names = set(mapped).union(helper_report.helper_targets)
    unmapped = [
        bone.name
        for bone in rig.bones
        if bone.name not in mapped_target_names
        and bone.name not in bind_only_targets
    ]
    if unmapped:
        warnings.append(
            f"{len(unmapped)} target .crig bone(s) use their bind-local tracks and inherit "
            "mapped parent motion. Review unmapped twist, face, or accessory rows in Root & "
            ".crig Mapping if those tracks need independent motion."
        )
    moving = [name for name, delta in movement_ranges.items() if delta > 1.0e-8]
    if not moving:
        warnings.append(
            "No mapped target track changes over the selected clip. Check the animation stack and mapping."
        )

    helper_rules_by_target = {
        rule.target_bone: rule
        for rule in helper_rules
        if rule.target_bone in active_helper_targets
    }
    preserves_non_root_translation = not any(
        bone.name != target_root_name
        and (
            (
                bone.name in mapped
                and _row_can_change_translation(
                    base_rows[bone.name], resolved_policy_by_target[bone.name]
                )
            )
            or (
                bone.name in helper_rules_by_target
                and _component_owns_translation(
                    helper_rules_by_target[bone.name].component_policy
                )
                and helper_rules_by_target[bone.name].transfer_policy
                not in {"bind", "rotation_delta"}
            )
        )
        for bone in rig.bones
    )
    preserves_non_root_scale = not any(
        bone.name != target_root_name
        and (
            (
                bone.name in mapped
                and _row_can_change_scale(
                    base_rows[bone.name], resolved_policy_by_target[bone.name]
                )
            )
            or (
                bone.name in helper_rules_by_target
                and _component_owns_scale(
                    helper_rules_by_target[bone.name].component_policy
                )
                and helper_rules_by_target[bone.name].transfer_policy
                not in {"bind", "rotation_delta"}
            )
        )
        for bone in rig.bones
    )
    automatic_certificate = dict(
        bone_map.extensions.get("automatic_retarget_certificate", {}) or {}
    )
    automatic_plan = dict(
        bone_map.extensions.get("automatic_retarget_plan", {}) or {}
    )
    certificate_pass = bool(
        automatic_certificate.get("status") == "pass"
        and automatic_certificate.get("live_revalidated") is True
    )
    runtime_certificate_status = (
        "pass"
        if certificate_pass
        else "failed"
        if automatic_certificate
        else "not_applicable"
    )
    certificate_mapped_body = int(
        automatic_certificate.get(
            "mapped_body_row_count",
            automatic_certificate.get("direct_mapping_count", len(mapped)),
        )
        or 0
    )
    certificate_bind_rows = int(
        automatic_certificate.get(
            "bind_row_count",
            automatic_certificate.get("held_at_bind_row_count", len(bind_only_targets)),
        )
        or 0
    )
    transform_contract = getattr(document, "transform_contract", None)
    transform_contract_payload = (
        transform_contract.to_dict()
        if transform_contract is not None
        and hasattr(transform_contract, "to_dict")
        else {}
    )
    wrapper_models = list(
        transform_contract_payload.get("common_wrapper_models", ()) or ()
    )
    wrapper_canonicalization = {
        "applied": bool(
            transform_contract_payload.get(
                "canonicalized_wrapper_reflection", False
            )
        ),
        "wrapper": (
            wrapper_models[0]
            if len(wrapper_models) == 1
            else wrapper_models
        ),
        "matrix": transform_contract_payload.get("common_wrapper_matrix"),
        "uniform": bool(
            transform_contract_payload.get("common_wrapper_is_uniform", False)
        ),
        "static": bool(
            transform_contract_payload.get("common_wrapper_is_static", False)
        ),
        "reflected": bool(
            transform_contract_payload.get("common_wrapper_is_reflected", False)
        ),
    }
    exact_subset_rows = int(
        automatic_certificate.get("exact_target_subset_rows", 0) or 0
    ) if certificate_pass else sum(
        str(row.method or "").casefold() == "exact_or_subset"
        or any(
            str(item.get("kind", ""))
            in {"exact_identity", "exact_target_subset"}
            for item in (
                dict(row.extensions or {})
                .get("automatic_retarget_decision", {})
                .get("evidence", ())
            )
        )
        for row in base_rows.values()
    )
    manual_target_overrides = int(
        automatic_certificate.get("manual_override_rows", 0) or 0
    ) if certificate_pass else sum(
        str(row.method or "").casefold().startswith("manual:target_override:")
        for row in base_rows.values()
    )
    semantic_rows = int(
        automatic_certificate.get("semantic_rows", 0) or 0
    ) if certificate_pass else max(
        0,
        len(mapped) - exact_subset_rows - manual_target_overrides,
    )
    spatial_only_rows = int(
        automatic_certificate.get("spatial_only_row_count", 0) or 0
    ) if certificate_pass else sum(
        str(row.method or "").casefold().startswith("spatial")
        for row in base_rows.values()
    )
    mapping_summary = {
        "exact_target_subset_rows": exact_subset_rows,
        "semantic_rows": semantic_rows,
        "manual_target_overrides": manual_target_overrides,
        "target_bind_rows": (
            certificate_bind_rows if certificate_pass else len(bind_only_targets)
        ),
        "spatial_only_rows": spatial_only_rows,
    }

    return MappedRigBuild(
        payload=payload,
        frame_count=len(values),
        report={
            "preflight_policy": "export_first_v1",
            "wrapper_canonicalization": wrapper_canonicalization,
            "canonical_transform_validation": dict(
                transform_contract_payload.get(
                    "canonical_transform_validation", {}
                )
                or {}
            ),
            "mapping": mapping_summary,
            "provenance": {
                "raw_hash_mismatches": [],
                "semantic_hash_matches": None,
                "scope": "target_package_not_supplied_to_low_level_builder",
            },
            "retarget_mode": "mapped_crig",
            "root_heading_policy": root_heading_report.to_dict(),
            "fixture_heading_audit": {
                "source_root_heading_degrees": source_root_heading_degrees,
                "pre_policy_target_root_heading_degrees": (
                    root_heading_report.source_heading_degrees
                ),
                "post_policy_target_root_heading_degrees": (
                    root_heading_report.skeletal_root_heading_degrees
                ),
                "motion_accumulator_heading_degrees": (
                    root_heading_report.motion_heading_degrees
                ),
            },
            "representative_target_global_rotation_parity": {
                "frames": sorted(representative_rotation_errors),
                "maximum_error_degrees": max(
                    representative_rotation_errors.values(), default=0.0
                ),
                "maximum_error_by_frame_degrees": {
                    str(frame): error
                    for frame, error in sorted(
                        representative_rotation_errors.items()
                    )
                },
                "direct_non_root_rows_by_frame": {
                    str(frame): count
                    for frame, count in sorted(
                        representative_direct_row_counts.items()
                    )
                },
                "tolerance_degrees": 0.05,
                "status": (
                    "pass"
                    if max(representative_rotation_errors.values(), default=0.0)
                    < 0.05
                    else "failed"
                ),
            },
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
            "automatic_retarget_certificate": automatic_certificate or None,
            "automatic_retarget_plan": automatic_plan or None,
            "automatic_mapping_certificate_status": runtime_certificate_status,
            "mapping_certificate_status": runtime_certificate_status,
            "automatic_mapping_certificate_format": str(
                automatic_certificate.get("format", "")
            ),
            "automatic_mapping_mode_counts": dict(
                automatic_certificate.get("mapping_mode_counts", {}) or {}
            ),
            "target_row_count": len(rig.bones),
            "mapped_body_row_count": (
                certificate_mapped_body if certificate_pass else len(mapped)
            ),
            "verified_bind_default_row_count": (
                certificate_bind_rows if certificate_pass else 0
            ),
            "bind_default_row_count": len(bind_only_targets),
            "manual_intentionally_unmapped_row_count": (
                0 if certificate_pass else len(intentionally_unmapped)
            ),
            "truly_missing_row_count": len(unmapped),
            "spatial_only_mapping_count": int(
                automatic_certificate.get("spatial_only_row_count", 0) or 0
            ) if certificate_pass else sum(
                str(row.method or "").casefold().startswith("spatial")
                for row in base_rows.values()
            ),
            "mapped_non_body_target_count": len(
                automatic_certificate.get("mapped_non_body_targets", ()) or ()
            ) if certificate_pass else 0,
            "base_mapped_bone_count": len(mapped),
            "helper_override_count": helper_report.helper_override_count,
            "helper_source_fanout_count": helper_report.helper_source_fanout_count,
            "helper_targets": helper_report.helper_targets,
            "shared_source_bones": helper_report.shared_source_bones,
            "main_transfer_policy": transfer_policy,
            "base_transfer_policies": {
                target_name: resolved_policy_by_target[target_name]
                for target_name in sorted(base_rows)
            },
            "base_component_policies": {
                target_name: base_rows[target_name].component_policy
                for target_name in sorted(base_rows)
            },
            "base_execution_mapping_modes": {
                target_name: _row_automatic_mapping_mode(base_rows[target_name])
                for target_name in sorted(base_rows)
            },
            "base_review_states": {
                target_name: base_rows[target_name].review_state
                for target_name in sorted(base_rows)
            },
            "helper_transfer_policies": helper_report.helper_transfer_policies,
            "helper_component_policies": helper_report.helper_component_policies,
            "helper_movement_ranges": helper_report.helper_movement_ranges,
            "maximum_helper_translation_delta_meters": (
                helper_report.maximum_helper_translation_delta_meters
            ),
            "skipped_helper_targets": helper_report.skipped_helper_targets,
            "root_mapping": {
                "source_bone": source_root_name,
                "source_method": source_root_method,
                "target_bone": target_root_name,
                "target_method": target_root_method,
                "always_retained": True,
                "pose_target_bone": next(
                    (
                        target_name
                        for target_name, source_name in mapped.items()
                        if source_name == source_root_name
                    ),
                    "",
                ),
                "translation_target_bone": target_root_name,
                "pose_and_root_motion_separated": True,
                "motion_mode": resolved_root_motion.motion_mode,
                "heading_mode": resolved_root_motion.heading_mode,
            },
            "root_motion_basis": basis_report,
            "root_heading": root_heading_report.to_dict(),
            "mapped_bone_count": len(mapped) + helper_report.helper_override_count,
            "intentionally_unmapped_bone_count": len(intentionally_unmapped),
            "intentionally_unmapped_target_bones": sorted(intentionally_unmapped),
            "bind_target_bones": sorted(bind_only_targets),
            "unmapped_bone_count": len(unmapped),
            "unmapped_target_bones": unmapped,
            "moving_target_bones": moving,
            "static_target_bones": [bone.name for bone in rig.bones if bone.name not in moving],
            "bind_delta_summary": bind_deltas,
            "maximum_bind_position_discrepancy": max(
                (float(row["translation_delta_meters"]) for row in bind_deltas), default=0.0
            ),
            "maximum_bind_rotation_discrepancy_degrees": max(
                (float(row["rotation_delta_degrees"]) for row in bind_deltas), default=0.0
            ),
            "frame_count": len(values),
            "fps": sample_fps,
            "track_count": len(rig.descriptors),
            "bone_count": len(rig.bones),
            "sample_frames": sample_frames,
            "decoded_max_component_error": maximum_error,
            "decoded_component_error_tolerance": DECODED_COMPONENT_ERROR_LIMIT,
            "source_unit_meters": meters_per_unit,
            "source_global_normalization": global_normalization.to_report(),
            "source_normalization_by_bone": {
                name: source_normalizers[name].to_report()
                for name in sorted(source_normalizers, key=str.casefold)
            },
            "canonical_document_basis": uses_canonical_document_basis,
            "source_basis_conversion": (
                f"FBX native units * {meters_per_unit:g} m/unit; "
                + (
                    f"{global_normalization.to_report()['axis_conversion']} via "
                    + global_normalization.to_report()["axis_conversion_source"]
                    if global_normalization.axis_conversion_count
                    else "source basis preserved"
                )
            ),
            "bind_source": getattr(document, "bind_source", "unanimated Model transforms"),
            "bind_coverage": dict(getattr(document, "bind_coverage", {}) or {}),
            "basis_correction_policy": transfer_policy,
            "preserves_target_non_root_translation": preserves_non_root_translation,
            "preserves_target_non_root_scale": preserves_non_root_scale,
            "preserves_target_translation": preserves_non_root_translation,
            "preserves_target_scale": preserves_non_root_scale,
            "preserve_target_translation": preserves_non_root_translation,
            "preserve_target_scale": preserves_non_root_scale,
            "preserves_target_non_root_translation_and_scale": (
                preserves_non_root_translation and preserves_non_root_scale
            ),
            "authorized_non_root_translation_bones": sorted(
                name
                for name in authorized_translation_targets
                if name != target_root_name
            ),
            "hierarchy_safety": hierarchy_safety,
            "bind_joint_extent_meters": hierarchy_safety[
                "bind_hierarchy_extent_meters"
            ],
            "maximum_animated_joint_extent_meters": hierarchy_safety[
                "maximum_animated_hierarchy_extent_meters"
            ],
            "maximum_animated_joint_extent_frame": hierarchy_safety[
                "maximum_animated_hierarchy_extent_frame"
            ],
            "maximum_non_root_translation_delta_meters": hierarchy_safety[
                "maximum_non_root_translation_delta_meters"
            ],
            "maximum_scale": hierarchy_safety["maximum_scale"],
            "minimum_scale": hierarchy_safety["minimum_scale"],
            "multiple_root_inventory": [
                bone.name for bone in rig.bones if bone.parent_index < 0
            ],
            "warnings": list(dict.fromkeys(warnings)),
            "root_policy": resolved_root_motion.legacy_serialized_policy,
            # Keep the historical string fields stable for downstream report
            # readers and expose the independent v2 choices alongside them.
            "root_motion_policy_requested": requested_root_motion.legacy_serialized_policy,
            "root_motion_policy_applied": resolved_root_motion.legacy_serialized_policy,
            "root_motion_selection_requested": requested_root_motion.to_dict(),
            "root_motion_selection_applied": resolved_root_motion.to_dict(),
            "candidate_path": None,
        },
    )


__all__ = [
    "MappedRigBuild",
    "SourceGlobalNormalization",
    "apply_global_root_policy",
    "build_mapped_rig_anm2",
    "compose_local_matrix",
    "corrected_target_global",
    "global_bind_basis_correction",
    "mapped_local_from_composed_rotation_deltas",
    "mapped_local_from_distributed_rotation_delta",
    "mapped_local_from_rest_delta",
    "mapped_local_from_rotation_delta",
    "quaternion_wxyz_to_matrix",
    "reconstruct_target_globals",
    "source_local_to_target_basis",
    "source_global_to_target_basis",
    "target_bind_local_matrix",
    "validate_hierarchy_safety",
]
