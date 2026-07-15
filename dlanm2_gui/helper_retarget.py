"""Value-level helper-track retarget overrides.

Helper rules are evaluated after the existing body/root solver and before the
existing packed-flag calculation and ANM2 writer.  Each candidate begins from
its own target bind transform, and component merging leaves unselected values
untouched.
"""

from __future__ import annotations

from collections import Counter
from dataclasses import asdict, dataclass, field
import math
from typing import Any, Iterable, Mapping, Sequence

import numpy as np

from .bone_maps import COMPONENT_POLICIES, TRANSFER_POLICIES, BoneMapPair
from .chrome_rig_builder import decompose_local_matrix
from .oracle.smd_bind_pose import (
    anm2_cayley_vector_from_quaternion,
    quaternion_wxyz_from_anm2_cayley,
)


CAMERA_HELPERS = frozenset({"refcamera", "eyecamera"})


@dataclass(frozen=True, slots=True)
class HelperRetargetRule:
    target_bone: str
    source_bone: str
    transfer_policy: str = "rest_relative"
    component_policy: str = "full_transform"

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "HelperRetargetRule":
        return cls(
            target_bone=str(payload.get("target_bone", "")),
            source_bone=str(payload.get("source_bone", "")),
            transfer_policy=str(
                payload.get("transfer_policy", "rest_relative") or "rest_relative"
            ),
            component_policy=str(
                payload.get("component_policy", "full_transform") or "full_transform"
            ),
        )

    @classmethod
    def from_mapping_pair(cls, row: BoneMapPair) -> "HelperRetargetRule":
        return cls(
            target_bone=row.target_rig_bone,
            source_bone=row.source_fbx_bone,
            transfer_policy=(
                "rest_relative" if row.transfer_policy == "default" else row.transfer_policy
            ),
            component_policy=row.component_policy,
        )

    def to_dict(self) -> dict[str, str]:
        return asdict(self)


@dataclass(frozen=True, slots=True)
class HelperValidation:
    errors: tuple[str, ...] = ()
    warnings: tuple[str, ...] = ()

    def require_valid(self) -> None:
        if self.errors:
            raise ValueError("Invalid helper retarget rules:\n- " + "\n- ".join(self.errors))


@dataclass(slots=True)
class HelperApplyReport:
    helper_override_count: int = 0
    helper_source_fanout_count: int = 0
    helper_targets: list[str] = field(default_factory=list)
    shared_source_bones: dict[str, list[str]] = field(default_factory=dict)
    helper_transfer_policies: dict[str, str] = field(default_factory=dict)
    helper_component_policies: dict[str, str] = field(default_factory=dict)
    helper_movement_ranges: dict[str, float] = field(default_factory=dict)
    maximum_helper_translation_delta_meters: float = 0.0
    skipped_helper_targets: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def helper_rules_from_pairs(rows: Iterable[BoneMapPair]) -> list[HelperRetargetRule]:
    return [
        HelperRetargetRule.from_mapping_pair(row)
        for row in rows
        if row.mapping_kind == "helper_override"
    ]


def helper_rules_from_dicts(rows: Iterable[Mapping[str, Any]]) -> list[HelperRetargetRule]:
    return [HelperRetargetRule.from_dict(row) for row in rows]


def helper_rules_to_dicts(rows: Iterable[HelperRetargetRule]) -> list[dict[str, str]]:
    return [row.to_dict() for row in rows]


def include_base_source_fanout(
    report: HelperApplyReport,
    rules: Sequence[HelperRetargetRule],
    base_targets_by_source: Mapping[str, Iterable[str]],
) -> None:
    """Account for a helper source that already drives an ordinary body track."""

    active_targets = set(report.helper_targets)
    combined: dict[str, list[str]] = {}
    for rule in rules:
        if rule.target_bone not in active_targets:
            continue
        targets = list(dict.fromkeys(
            [*base_targets_by_source.get(rule.source_bone, ()), rule.target_bone]
        ))
        if len(targets) > 1:
            combined[rule.source_bone] = targets
    report.shared_source_bones = combined
    report.helper_source_fanout_count = sum(
        len(targets) - 1 for targets in combined.values()
    )


def validate_helper_rules(
    rules: Sequence[HelperRetargetRule],
    *,
    target_names: Iterable[str] = (),
    source_names: Iterable[str] = (),
    target_descriptors: Mapping[str, int] | None = None,
    target_bind_local: Mapping[str, np.ndarray] | None = None,
    source_bind_local: Mapping[str, np.ndarray] | None = None,
    target_roots: Iterable[str] = (),
    source_roots: Iterable[str] = (),
    deforming_primary_targets: Iterable[str] = (),
) -> HelperValidation:
    errors: list[str] = []
    warnings: list[str] = []
    targets = set(target_names)
    sources = set(source_names)
    target_root_set = set(target_roots)
    source_root_set = set(source_roots)
    deforming = set(deforming_primary_targets)

    assigned = [rule.target_bone for rule in rules if rule.target_bone]
    duplicates = sorted(name for name, count in Counter(assigned).items() if count > 1)
    if duplicates:
        errors.append("Duplicate helper target assignment(s): " + ", ".join(duplicates))

    descriptor_rows = dict(target_descriptors or {})
    descriptors = [descriptor_rows[name] for name in assigned if name in descriptor_rows]
    if len(descriptors) != len(set(descriptors)):
        errors.append("Helper target descriptors collide.")

    for rule in rules:
        if rule.transfer_policy not in TRANSFER_POLICIES or rule.transfer_policy == "default":
            errors.append(
                f"Helper {rule.target_bone!r} has invalid transfer policy {rule.transfer_policy!r}."
            )
        if rule.component_policy not in COMPONENT_POLICIES:
            errors.append(
                f"Helper {rule.target_bone!r} has invalid component policy {rule.component_policy!r}."
            )
        if targets and rule.target_bone not in targets:
            warnings.append(
                f"Helper target {rule.target_bone!r} is unavailable in the selected target profile; rule skipped."
            )
        if sources and rule.source_bone not in sources:
            warnings.append(
                f"Source helper bone {rule.source_bone!r} is missing; {rule.target_bone!r} remains unchanged."
            )
        if rule.target_bone in target_root_set or rule.source_bone in source_root_set:
            warnings.append(
                f"Helper rule {rule.target_bone!r} <- {rule.source_bone!r} references a skeletal root."
            )
        if rule.target_bone in deforming:
            warnings.append(
                f"Helper override targets deforming primary body bone {rule.target_bone!r}."
            )
        if rule.component_policy == "full_transform":
            warnings.append(
                f"Full-transform helper mapping for {rule.target_bone!r} may change target scale."
            )
            if rule.target_bone.casefold() in CAMERA_HELPERS:
                warnings.append(
                    f"Camera helper {rule.target_bone!r} uses full transform; a component-limited policy is safer."
                )

    for source, count in Counter(rule.source_bone for rule in rules).items():
        if count >= 4:
            warnings.append(f"Helper source {source!r} is shared by {count} target tracks.")

    for label, matrices in (
        ("target", target_bind_local or {}),
        ("source", source_bind_local or {}),
    ):
        for name, matrix in matrices.items():
            value = np.asarray(matrix, dtype=float)
            if value.shape != (4, 4) or not np.isfinite(value).all():
                errors.append(f"{label.title()} bind matrix for {name!r} is non-finite or not 4x4.")
            elif abs(float(np.linalg.det(value[:3, :3]))) <= 1.0e-12:
                errors.append(f"{label.title()} bind matrix for {name!r} is singular.")

    return HelperValidation(
        tuple(dict.fromkeys(errors)), tuple(dict.fromkeys(warnings))
    )


def _finite_matrix(value: np.ndarray, label: str) -> np.ndarray:
    matrix = np.asarray(value, dtype=float)
    if matrix.shape != (4, 4) or not np.isfinite(matrix).all():
        raise ValueError(f"{label} must be a finite 4x4 matrix.")
    if abs(float(np.linalg.det(matrix[:3, :3]))) <= 1.0e-12:
        raise ValueError(f"{label} is singular.")
    return matrix


def _compose_local_matrix(
    translation: Sequence[float], rotation_wxyz: Sequence[float], scale: Sequence[float]
) -> np.ndarray:
    # Imported lazily to avoid an import cycle: mapped_rig consumes this module.
    from .retarget_engines.mapped_rig import compose_local_matrix

    return compose_local_matrix(list(translation), list(rotation_wxyz), list(scale))


def evaluate_helper_target_local(
    target_bind_local: np.ndarray,
    source_bind_local: np.ndarray,
    source_animated_local: np.ndarray,
    transfer_policy: str = "rest_relative",
    *,
    target_bind_global: np.ndarray | None = None,
    source_bind_global: np.ndarray | None = None,
    source_animated_global: np.ndarray | None = None,
    target_parent_animated_global: np.ndarray | None = None,
) -> np.ndarray:
    """Evaluate one helper target while retaining its own bind basis."""

    target_bind = _finite_matrix(target_bind_local, "target helper bind matrix")
    source_bind = _finite_matrix(source_bind_local, "source helper bind matrix")
    source_animated = _finite_matrix(
        source_animated_local, "source helper animated matrix"
    )
    if transfer_policy == "default":
        transfer_policy = "rest_relative"
    if transfer_policy not in TRANSFER_POLICIES:
        raise ValueError(f"Unsupported helper transfer policy {transfer_policy!r}.")

    if transfer_policy == "rest_relative":
        result = target_bind @ np.linalg.inv(source_bind) @ source_animated
    elif transfer_policy == "rotation_delta":
        target_t, target_q, target_s = decompose_local_matrix(target_bind)
        _source_t, source_q, _source_s = decompose_local_matrix(source_bind)
        _animated_t, animated_q, _animated_s = decompose_local_matrix(source_animated)
        target_r = _compose_local_matrix((0, 0, 0), target_q, (1, 1, 1))[:3, :3]
        source_r = _compose_local_matrix((0, 0, 0), source_q, (1, 1, 1))[:3, :3]
        animated_r = _compose_local_matrix((0, 0, 0), animated_q, (1, 1, 1))[:3, :3]
        rotation = target_r @ np.linalg.inv(source_r) @ animated_r
        from .oracle.smd_bind_pose import quaternion_wxyz_from_matrix

        result = _compose_local_matrix(
            target_t,
            quaternion_wxyz_from_matrix(rotation),
            target_s,
        )
    elif transfer_policy == "copy_local":
        result = source_animated.copy()
    else:
        required = {
            "target_bind_global": target_bind_global,
            "source_bind_global": source_bind_global,
            "source_animated_global": source_animated_global,
        }
        missing = [name for name, value in required.items() if value is None]
        if missing:
            raise ValueError(
                "Global-bind helper transfer is missing " + ", ".join(missing) + "."
            )
        target_global = _finite_matrix(
            np.asarray(target_bind_global), "target helper global bind matrix"
        )
        source_global = _finite_matrix(
            np.asarray(source_bind_global), "source helper global bind matrix"
        )
        animated_global = _finite_matrix(
            np.asarray(source_animated_global), "source helper animated global matrix"
        )
        candidate_global = (
            animated_global @ np.linalg.inv(source_global) @ target_global
        )
        result = (
            candidate_global
            if target_parent_animated_global is None
            else np.linalg.inv(
                _finite_matrix(
                    np.asarray(target_parent_animated_global),
                    "target helper animated parent matrix",
                )
            )
            @ candidate_global
        )

    if not np.isfinite(result).all():
        raise ValueError("Helper retarget produced a non-finite local matrix.")
    return result


def local_matrix_to_anm2_values(matrix: np.ndarray) -> list[float]:
    translation, quaternion, scale = decompose_local_matrix(matrix)
    cayley = anm2_cayley_vector_from_quaternion(quaternion)
    return [
        *map(float, cayley),
        *map(float, translation),
        *map(float, scale),
    ]


def anm2_values_to_local_matrix(values: Sequence[float]) -> np.ndarray:
    if len(values) != 9 or not all(math.isfinite(float(value)) for value in values):
        raise ValueError("ANM2 local values must contain nine finite components.")
    quaternion = quaternion_wxyz_from_anm2_cayley(values[0:3])
    return _compose_local_matrix(values[3:6], quaternion, values[6:9])


def merge_helper_components(
    existing_values: Sequence[float],
    candidate_values: Sequence[float],
    component_policy: str,
) -> list[float]:
    """Merge selected components without rewriting untouched float values."""

    if len(existing_values) != 9 or len(candidate_values) != 9:
        raise ValueError("Helper component rows must contain nine values.")
    if component_policy not in COMPONENT_POLICIES:
        raise ValueError(f"Unsupported helper component policy {component_policy!r}.")
    result = list(existing_values)
    slices = {
        "rotation": (0, 3),
        "translation": (3, 6),
        "rotation_translation": (0, 6),
        "scale": (6, 9),
        "full_transform": (0, 9),
    }
    start, stop = slices[component_policy]
    result[start:stop] = [float(value) for value in candidate_values[start:stop]]
    if not all(math.isfinite(float(value)) for value in result):
        raise ValueError("Helper component merge produced a non-finite value.")
    return result


def _target_globals_from_values(
    frame: Sequence[Sequence[float]],
    *,
    target_bind_local: Mapping[str, np.ndarray],
    target_track_indices: Mapping[str, int],
    target_parents: Mapping[str, str | None],
) -> dict[str, np.ndarray]:
    cache: dict[str, np.ndarray] = {}
    visiting: set[str] = set()

    def resolve(name: str) -> np.ndarray:
        if name in cache:
            return cache[name]
        if name in visiting:
            raise ValueError(f"Target helper hierarchy contains a cycle at {name!r}.")
        visiting.add(name)
        track_index = target_track_indices.get(name)
        local = (
            anm2_values_to_local_matrix(frame[track_index])
            if track_index is not None
            else np.asarray(target_bind_local[name], dtype=float)
        )
        parent = target_parents.get(name)
        cache[name] = (
            resolve(parent) @ local
            if parent in target_bind_local
            else local.copy()
        )
        visiting.remove(name)
        return cache[name]

    for name in target_bind_local:
        resolve(name)
    return cache


def _model_relative_translation_limit(
    target_bind_local: Mapping[str, np.ndarray],
    target_parents: Mapping[str, str | None],
) -> float:
    if not target_bind_local:
        return 1.0
    dummy_indices: dict[str, int] = {}
    globals_by_name = _target_globals_from_values(
        [],
        target_bind_local=target_bind_local,
        target_track_indices=dummy_indices,
        target_parents=target_parents,
    )
    points = np.asarray([matrix[:3, 3] for matrix in globals_by_name.values()], dtype=float)
    extent = float(np.linalg.norm(np.max(points, axis=0) - np.min(points, axis=0)))
    return max(1.0, extent * 4.0)


def apply_helper_retarget_overrides(
    values: list[list[list[float]]],
    rules: Sequence[HelperRetargetRule],
    *,
    target_bind_local: Mapping[str, np.ndarray],
    target_track_indices: Mapping[str, int],
    target_parents: Mapping[str, str | None],
    source_bind_local: Mapping[str, np.ndarray],
    source_animated_local_frames: Sequence[Mapping[str, np.ndarray]],
    target_descriptors: Mapping[str, int] | None = None,
    source_bind_global: Mapping[str, np.ndarray] | None = None,
    source_animated_global_frames: Sequence[Mapping[str, np.ndarray]] | None = None,
    target_roots: Iterable[str] = (),
    source_roots: Iterable[str] = (),
    deforming_primary_targets: Iterable[str] = (),
) -> HelperApplyReport:
    """Apply helper rules to decoded values and return an auditable report."""

    if not rules:
        return HelperApplyReport()
    if len(values) != len(source_animated_local_frames):
        raise ValueError("Helper source local frame count does not match ANM2 values.")
    if source_animated_global_frames is not None and len(values) != len(
        source_animated_global_frames
    ):
        raise ValueError("Helper source global frame count does not match ANM2 values.")

    validation = validate_helper_rules(
        rules,
        target_names=target_track_indices,
        source_names=source_bind_local,
        target_descriptors=target_descriptors,
        target_bind_local={
            name: target_bind_local[name]
            for name in target_track_indices
            if name in target_bind_local
        },
        source_bind_local=source_bind_local,
        target_roots=target_roots,
        source_roots=source_roots,
        deforming_primary_targets=deforming_primary_targets,
    )
    validation.require_valid()
    active = [
        rule
        for rule in rules
        if rule.target_bone in target_track_indices
        and rule.target_bone in target_bind_local
        and rule.source_bone in source_bind_local
    ]
    skipped = [rule.target_bone for rule in rules if rule not in active]
    by_source: dict[str, list[str]] = {}
    for rule in active:
        by_source.setdefault(rule.source_bone, []).append(rule.target_bone)

    report = HelperApplyReport(
        helper_override_count=len(active),
        helper_source_fanout_count=sum(
            len(targets) - 1 for targets in by_source.values() if len(targets) > 1
        ),
        helper_targets=[rule.target_bone for rule in active],
        shared_source_bones={
            source: targets for source, targets in by_source.items() if len(targets) > 1
        },
        helper_transfer_policies={
            rule.target_bone: rule.transfer_policy for rule in active
        },
        helper_component_policies={
            rule.target_bone: rule.component_policy for rule in active
        },
        helper_movement_ranges={rule.target_bone: 0.0 for rule in active},
        skipped_helper_targets=skipped,
        warnings=list(validation.warnings),
    )
    translation_limit = _model_relative_translation_limit(
        target_bind_local, target_parents
    )
    target_bind_globals = _target_globals_from_values(
        [],
        target_bind_local=target_bind_local,
        target_track_indices={},
        target_parents=target_parents,
    )

    for frame_index, frame in enumerate(values):
        for rule in active:
            target_index = target_track_indices[rule.target_bone]
            existing = list(frame[target_index])
            target_globals = _target_globals_from_values(
                frame,
                target_bind_local=target_bind_local,
                target_track_indices=target_track_indices,
                target_parents=target_parents,
            )
            parent_name = target_parents.get(rule.target_bone)
            source_global_frame = (
                source_animated_global_frames[frame_index]
                if source_animated_global_frames is not None
                else {}
            )
            candidate_local = evaluate_helper_target_local(
                target_bind_local[rule.target_bone],
                source_bind_local[rule.source_bone],
                source_animated_local_frames[frame_index][rule.source_bone],
                rule.transfer_policy,
                target_bind_global=target_bind_globals.get(rule.target_bone),
                source_bind_global=(source_bind_global or {}).get(rule.source_bone),
                source_animated_global=source_global_frame.get(rule.source_bone),
                target_parent_animated_global=(
                    target_globals.get(parent_name) if parent_name else None
                ),
            )
            candidate = local_matrix_to_anm2_values(candidate_local)
            merged = merge_helper_components(
                existing, candidate, rule.component_policy
            )
            frame[target_index] = merged
            bind_values = local_matrix_to_anm2_values(
                target_bind_local[rule.target_bone]
            )
            movement = max(
                abs(float(actual) - float(bind))
                for actual, bind in zip(merged, bind_values)
            )
            report.helper_movement_ranges[rule.target_bone] = max(
                report.helper_movement_ranges[rule.target_bone], movement
            )
            translation_delta = float(
                np.linalg.norm(
                    np.asarray(merged[3:6], dtype=float)
                    - np.asarray(bind_values[3:6], dtype=float)
                )
            )
            report.maximum_helper_translation_delta_meters = max(
                report.maximum_helper_translation_delta_meters, translation_delta
            )

    if report.maximum_helper_translation_delta_meters > translation_limit:
        report.warnings.append(
            "Helper translation exceeds the model-relative limit: "
            f"{report.maximum_helper_translation_delta_meters:.3f} m > "
            f"{translation_limit:.3f} m."
        )
    report.warnings = list(dict.fromkeys(report.warnings))
    return report


def update_helper_packed_flags(
    packed_flags: list[list[bool]],
    values: Sequence[Sequence[Sequence[float]]],
    target_track_indices: Mapping[str, int],
    helper_targets: Iterable[str],
) -> None:
    """Recalculate only helper rows, preserving no-helper/base solver output."""

    for target_name in helper_targets:
        track_index = target_track_indices.get(target_name)
        if track_index is None:
            continue
        flags = [
            max(float(frame[track_index][component]) for frame in values)
            - min(float(frame[track_index][component]) for frame in values)
            > 1.0e-8
            for component in range(9)
        ]
        if any(flags[6:9]):
            flags[6:9] = [True, True, True]
        packed_flags[track_index] = flags


__all__ = [
    "CAMERA_HELPERS",
    "HelperApplyReport",
    "HelperRetargetRule",
    "HelperValidation",
    "anm2_values_to_local_matrix",
    "apply_helper_retarget_overrides",
    "evaluate_helper_target_local",
    "helper_rules_from_dicts",
    "helper_rules_from_pairs",
    "helper_rules_to_dicts",
    "include_base_source_fanout",
    "local_matrix_to_anm2_values",
    "merge_helper_components",
    "update_helper_packed_flags",
    "validate_helper_rules",
]
