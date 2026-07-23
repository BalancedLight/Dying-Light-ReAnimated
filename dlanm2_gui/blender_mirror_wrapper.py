"""Reflected FBX-wrapper geometry and independent bilateral semantics.

Animation sampling removes a reflected common scene wrapper before evaluating
joint globals.  That is a geometric coordinate canonicalization only: neither
the wrapper determinant nor its name says which animation channel owns the
left or right target row.

This module deliberately keeps the two decisions separate.  Wrapper detection
returns diagnostics and a physical-observation basis.  Bilateral row swapping
is controlled by an explicit policy, with ``auto`` requiring a bind-pose
consensus across trusted bilateral pairs.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
from enum import Enum
import re
from typing import Any, Iterable, Mapping

import numpy as np


_BLENDER_ARMATURE_NAME = re.compile(r"^armature(?:[._ -]\d+)?$", re.IGNORECASE)
_TRUSTED_BILATERAL_PAIRS = (
    ("l_clavicle", "r_clavicle"),
    ("l_upperarm", "r_upperarm"),
    ("l_forearm", "r_forearm"),
    ("l_hand", "r_hand"),
    ("l_thigh", "r_thigh"),
    ("l_calf", "r_calf"),
    ("l_foot", "r_foot"),
)


class BilateralSemanticPolicy(str, Enum):
    """How named source-side animation rows are assigned to target rows."""

    AUTO = "auto"
    PRESERVE_SOURCE_NAMES = "preserve_source_names"
    SWAP_BILATERAL_EXPLICIT = "swap_bilateral_explicit"


def coerce_bilateral_semantic_policy(
    value: BilateralSemanticPolicy | str | None,
    *,
    default: BilateralSemanticPolicy = BilateralSemanticPolicy.PRESERVE_SOURCE_NAMES,
) -> BilateralSemanticPolicy:
    if value is None or str(value).strip() == "":
        return default
    if isinstance(value, BilateralSemanticPolicy):
        return value
    try:
        return BilateralSemanticPolicy(str(value).strip().casefold())
    except ValueError as exc:
        allowed = ", ".join(item.value for item in BilateralSemanticPolicy)
        raise ValueError(
            f"bilateral_semantic_policy must be one of: {allowed}"
        ) from exc


@dataclass(frozen=True, slots=True)
class BlenderLateralMirrorContext:
    """Geometric diagnostics for one removed lateral scene reflection."""

    wrapper_name: str
    basis_matrix: tuple[tuple[float, ...], ...]
    removed_wrapper_matrix: tuple[tuple[float, ...], ...] = ()
    reflected_axis_target: tuple[float, float, float] = (1.0, 0.0, 0.0)
    canonicalized_before_sampling: bool = True
    mode: str = "blender_lateral_armature_wrapper_geometry_v2"

    def matrix(self) -> np.ndarray:
        """Return the removed reflection expressed in target space."""

        return np.asarray(self.basis_matrix, dtype=float).copy()

    def physical_observation_basis(self, source_basis_matrix: np.ndarray) -> np.ndarray:
        """Return a basis used only to inspect physical bind-pose side.

        Sampling continues to use canonical globals and ``source_basis_matrix``.
        The removed scene reflection is reintroduced here solely so Auto can
        compare physical bind pivots without changing an animation transform.
        """

        return self.matrix() @ np.asarray(source_basis_matrix, dtype=float)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True, slots=True)
class BilateralSemanticDecision:
    """Resolved semantic assignment, independent of wrapper canonicalization."""

    requested_policy: str
    effective_policy: str
    reason: str
    trusted_pair_count: int = 0
    same_side_votes: int = 0
    opposite_side_votes: int = 0
    ambiguous_pair_count: int = 0
    evaluated_pairs: tuple[dict[str, Any], ...] = ()
    warning: str = ""

    @property
    def swap_applied(self) -> bool:
        return (
            self.effective_policy
            == BilateralSemanticPolicy.SWAP_BILATERAL_EXPLICIT.value
        )

    def to_dict(self) -> dict[str, Any]:
        payload = asdict(self)
        payload["bilateral_swap_applied"] = self.swap_applied
        return payload


def resolve_blender_lateral_mirror(
    document: Any,
    *,
    source_basis_matrix: np.ndarray,
    target_right: Iterable[float] = (1.0, 0.0, 0.0),
    target_up: Iterable[float] = (0.0, 1.0, 0.0),
    target_forward: Iterable[float] = (0.0, 0.0, 1.0),
) -> BlenderLateralMirrorContext | None:
    """Return diagnostics for a safe removed Blender lateral reflection.

    ``source_basis_matrix`` is the proper axis conversion used after wrapper
    removal.  Recognition never authorizes a bilateral name swap.
    """

    contract = getattr(document, "transform_contract", None)
    if contract is None:
        return None
    wrappers = tuple(getattr(contract, "common_wrapper_models", ()) or ())
    if (
        len(wrappers) != 1
        or not _BLENDER_ARMATURE_NAME.fullmatch(str(wrappers[0]))
        or not bool(getattr(contract, "common_wrapper_is_static", False))
        or not bool(getattr(contract, "common_wrapper_is_uniform", False))
        or not bool(getattr(contract, "common_wrapper_is_reflected", False))
        or not bool(getattr(contract, "canonicalized_wrapper_reflection", False))
    ):
        return None
    raw = getattr(contract, "common_wrapper_matrix", None)
    if raw is None:
        return None
    wrapper = np.asarray(raw, dtype=float)
    source_basis = np.asarray(source_basis_matrix, dtype=float)
    if (
        wrapper.shape != (4, 4)
        or source_basis.shape != (4, 4)
        or not np.isfinite(wrapper).all()
        or not np.isfinite(source_basis).all()
    ):
        return None
    linear = wrapper[:3, :3]
    scales = np.linalg.norm(linear, axis=0)
    scale = float(np.mean(scales))
    if (
        not np.isfinite(scales).all()
        or scale <= 1.0e-12
        or max(abs(scales - scale)) > max(1.0e-5, scale * 1.0e-5)
        or np.linalg.norm(wrapper[:3, 3]) > max(1.0e-5, scale * 1.0e-5)
    ):
        return None
    normalized_wrapper = np.eye(4, dtype=float)
    normalized_wrapper[:3, :3] = linear / scale
    try:
        mirror = normalized_wrapper @ np.linalg.inv(source_basis)
    except np.linalg.LinAlgError:
        return None
    mirror_linear = mirror[:3, :3]
    if (
        not np.allclose(mirror[:3, 3], 0.0, atol=1.0e-8, rtol=0.0)
        or not np.allclose(
            mirror[3], (0.0, 0.0, 0.0, 1.0), atol=1.0e-8, rtol=0.0
        )
        or not np.allclose(
            mirror_linear.T @ mirror_linear,
            np.eye(3, dtype=float),
            atol=1.0e-5,
            rtol=1.0e-5,
        )
        or not np.allclose(
            mirror_linear @ mirror_linear,
            np.eye(3),
            atol=1.0e-5,
            rtol=1.0e-5,
        )
        or float(np.linalg.det(mirror_linear)) >= -1.0e-8
    ):
        return None
    right = np.asarray(tuple(target_right), dtype=float)
    up = np.asarray(tuple(target_up), dtype=float)
    forward = np.asarray(tuple(target_forward), dtype=float)
    if (
        right.shape != (3,)
        or up.shape != (3,)
        or forward.shape != (3,)
        or min(
            np.linalg.norm(right),
            np.linalg.norm(up),
            np.linalg.norm(forward),
        )
        <= 1.0e-8
    ):
        return None
    right /= np.linalg.norm(right)
    up /= np.linalg.norm(up)
    forward /= np.linalg.norm(forward)
    if not (
        np.allclose(mirror_linear @ right, -right, atol=1.0e-5, rtol=1.0e-5)
        and np.allclose(mirror_linear @ up, up, atol=1.0e-5, rtol=1.0e-5)
        and np.allclose(
            mirror_linear @ forward, forward, atol=1.0e-5, rtol=1.0e-5
        )
    ):
        return None
    reflected_axis = -mirror_linear @ right
    return BlenderLateralMirrorContext(
        wrapper_name=str(wrappers[0]),
        basis_matrix=tuple(
            tuple(float(value) for value in row) for row in mirror
        ),
        removed_wrapper_matrix=tuple(
            tuple(float(value) for value in row) for row in wrapper
        ),
        reflected_axis_target=tuple(float(value) for value in reflected_axis),
    )


def should_swap_bilateral_rows(
    policy: BilateralSemanticPolicy | str,
    decision: BilateralSemanticDecision | None = None,
) -> bool:
    """Return whether semantic source rows should be exchanged."""

    selected = coerce_bilateral_semantic_policy(policy)
    if selected is BilateralSemanticPolicy.SWAP_BILATERAL_EXPLICIT:
        return True
    if selected is BilateralSemanticPolicy.PRESERVE_SOURCE_NAMES:
        return False
    return bool(decision is not None and decision.swap_applied)


def _quaternion_matrix(value: Iterable[float]) -> np.ndarray:
    quaternion = np.asarray(tuple(value), dtype=float)
    if quaternion.shape != (4,) or not np.isfinite(quaternion).all():
        raise ValueError("Target bind quaternion must be finite length four")
    norm = float(np.linalg.norm(quaternion))
    if norm <= 1.0e-12:
        raise ValueError("Target bind quaternion must be nonzero")
    w, x, y, z = quaternion / norm
    return np.asarray(
        (
            (1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
            (2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
            (2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)),
        ),
        dtype=float,
    )


def _target_bind_globals(target_rig: Any) -> dict[str, np.ndarray]:
    bones = tuple(getattr(target_rig, "bones", ()) or ())
    result: dict[str, np.ndarray] = {}
    for index, bone in enumerate(bones):
        local = np.eye(4, dtype=float)
        local[:3, :3] = _quaternion_matrix(
            getattr(bone, "bind_rotation_wxyz")
        ) @ np.diag(np.asarray(getattr(bone, "bind_scale"), dtype=float))
        local[:3, 3] = np.asarray(
            getattr(bone, "bind_translation"), dtype=float
        )
        parent_index = int(getattr(bone, "parent_index", -1))
        name = str(getattr(bone, "name", ""))
        if parent_index >= 0:
            parent_name = str(getattr(bones[parent_index], "name", ""))
            result[name] = result[parent_name] @ local
        else:
            result[name] = local
    return result


def _casefold_matrices(
    values: Mapping[str, Any],
) -> dict[str, tuple[str, np.ndarray]]:
    return {
        str(name).casefold(): (str(name), np.asarray(matrix, dtype=float))
        for name, matrix in values.items()
    }


def resolve_bilateral_semantic_decision(
    document: Any,
    target_rig: Any,
    policy: BilateralSemanticPolicy | str,
    *,
    source_basis_matrix: np.ndarray,
    wrapper_context: BlenderLateralMirrorContext | None = None,
) -> BilateralSemanticDecision:
    """Resolve Preserve/Swap, using physical bind consensus only for Auto."""

    selected = coerce_bilateral_semantic_policy(policy)
    if selected is BilateralSemanticPolicy.PRESERVE_SOURCE_NAMES:
        return BilateralSemanticDecision(
            selected.value,
            BilateralSemanticPolicy.PRESERVE_SOURCE_NAMES.value,
            "Project policy preserves named source-side ownership.",
        )
    if selected is BilateralSemanticPolicy.SWAP_BILATERAL_EXPLICIT:
        return BilateralSemanticDecision(
            selected.value,
            BilateralSemanticPolicy.SWAP_BILATERAL_EXPLICIT.value,
            "Explicit project policy swaps verified bilateral source rows.",
            warning=(
                "Explicit bilateral source-bone swapping is enabled. This is a "
                "semantic mapping override, not normal FBX axis conversion."
            ),
        )

    source_bind = getattr(document, "bind_global_matrices", None)
    try:
        target_bind = _target_bind_globals(target_rig)
    except (AttributeError, IndexError, KeyError, TypeError, ValueError):
        target_bind = {}
    if not source_bind or not target_bind:
        return BilateralSemanticDecision(
            selected.value,
            BilateralSemanticPolicy.PRESERVE_SOURCE_NAMES.value,
            "Auto had no complete source/target bind-pose evidence; source names were preserved.",
            warning=(
                "Bilateral Auto was ambiguous because bind-pose evidence was "
                "unavailable; preserving source left/right names."
            ),
        )

    source_by_fold = _casefold_matrices(source_bind)
    target_by_fold = _casefold_matrices(target_bind)
    basis = np.asarray(source_basis_matrix, dtype=float)
    if wrapper_context is not None:
        basis = wrapper_context.physical_observation_basis(basis)
    if basis.shape != (4, 4) or not np.isfinite(basis).all():
        raise ValueError("Bilateral Auto source observation basis must be finite 4x4")

    evaluated: list[dict[str, Any]] = []
    same = 0
    opposite = 0
    ambiguous = 0
    for left_name, right_name in _TRUSTED_BILATERAL_PAIRS:
        source_left = source_by_fold.get(left_name.casefold())
        source_right = source_by_fold.get(right_name.casefold())
        target_left = target_by_fold.get(left_name.casefold())
        target_right = target_by_fold.get(right_name.casefold())
        if None in (source_left, source_right, target_left, target_right):
            continue
        source_left_position = (
            basis @ source_left[1]
        )[:3, 3]
        source_right_position = (
            basis @ source_right[1]
        )[:3, 3]
        target_left_position = target_left[1][:3, 3]
        target_right_position = target_right[1][:3, 3]
        source_span = source_right_position - source_left_position
        target_span = target_right_position - target_left_position
        denominator = float(
            np.linalg.norm(source_span) * np.linalg.norm(target_span)
        )
        alignment = (
            float(source_span @ target_span) / denominator
            if denominator > 1.0e-10
            else 0.0
        )
        vote = (
            "same"
            if alignment >= 0.5
            else "opposite"
            if alignment <= -0.5
            else "ambiguous"
        )
        same += vote == "same"
        opposite += vote == "opposite"
        ambiguous += vote == "ambiguous"
        evaluated.append(
            {
                "left": source_left[0],
                "right": source_right[0],
                "alignment": alignment,
                "vote": vote,
            }
        )

    trusted = len(evaluated)
    strong_same = trusted >= 3 and same >= 3 and same >= opposite + 2
    strong_opposite = (
        trusted >= 3 and opposite >= 3 and opposite >= same + 2
    )
    if strong_opposite:
        effective = BilateralSemanticPolicy.SWAP_BILATERAL_EXPLICIT
        reason = (
            "Auto found strong opposite-side bind-pose agreement across "
            f"{opposite}/{trusted} trusted bilateral pairs."
        )
        warning = (
            "Bilateral Auto detected physically reversed source-side names and "
            "will swap verified bilateral source rows. This is a semantic "
            "mapping decision, not FBX axis conversion."
        )
    else:
        effective = BilateralSemanticPolicy.PRESERVE_SOURCE_NAMES
        if strong_same:
            reason = (
                "Auto found strong same-side bind-pose agreement across "
                f"{same}/{trusted} trusted bilateral pairs."
            )
            warning = ""
        else:
            reason = (
                "Auto bind-pose evidence was missing, centered, or inconsistent; "
                "source names were preserved."
            )
            warning = (
                "Bilateral Auto was ambiguous; preserving source left/right "
                "names. Use the advanced explicit Swap setting only for a "
                "verified semantically reversed rig."
            )
    return BilateralSemanticDecision(
        selected.value,
        effective.value,
        reason,
        trusted_pair_count=trusted,
        same_side_votes=same,
        opposite_side_votes=opposite,
        ambiguous_pair_count=ambiguous,
        evaluated_pairs=tuple(evaluated),
        warning=warning,
    )


def swapped_bilateral_source_name(name: str, available: Iterable[str]) -> str:
    """Return a verified left/right counterpart, retaining source spelling."""

    source = str(name or "")
    by_fold = {str(value).casefold(): str(value) for value in available}
    candidates: list[str] = []
    prefix = re.match(r"^(l|r)_", source, flags=re.IGNORECASE)
    if prefix:
        side = "r" if prefix.group(1).casefold() == "l" else "l"
        candidates.append(side + source[1:])
    suffix = re.search(r"_([lr])$", source, flags=re.IGNORECASE)
    if suffix:
        side = "r" if suffix.group(1).casefold() == "l" else "l"
        candidates.append(source[: suffix.start(1)] + side)
    word = re.search(r"left|right", source, flags=re.IGNORECASE)
    if word:
        replacement = (
            "right" if word.group(0).casefold() == "left" else "left"
        )
        candidates.append(
            source[: word.start()] + replacement + source[word.end() :]
        )
    for candidate in candidates:
        matched = by_fold.get(candidate.casefold())
        if matched is not None:
            return matched
    return source


__all__ = [
    "BilateralSemanticDecision",
    "BilateralSemanticPolicy",
    "BlenderLateralMirrorContext",
    "coerce_bilateral_semantic_policy",
    "resolve_bilateral_semantic_decision",
    "resolve_blender_lateral_mirror",
    "should_swap_bilateral_rows",
    "swapped_bilateral_source_name",
]
