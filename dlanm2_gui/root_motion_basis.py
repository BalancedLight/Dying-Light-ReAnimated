"""Actor-space basis derivation and root displacement mapping.

Model-matrix normalization and semantic actor motion are intentionally separate.
The former may use an FBX/Chrome coordinate conversion; the latter decomposes a
raw source displacement in a source body frame and reconstructs it in the target
body frame.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any, Mapping, Sequence

import numpy as np

from .root_heading import infer_target_up_axis


class ActorFrameAmbiguityError(ValueError):
    """One focused, actionable failure for an underdetermined actor frame."""


def _unit(value: Sequence[float], label: str) -> np.ndarray:
    result = np.asarray(tuple(float(v) for v in value), dtype=float)
    if result.shape != (3,) or not np.isfinite(result).all():
        raise ActorFrameAmbiguityError(f"{label} is not a finite three-vector")
    length = float(np.linalg.norm(result))
    if length <= 1.0e-10:
        raise ActorFrameAmbiguityError(f"{label} is degenerate")
    return result / length


@dataclass(frozen=True, slots=True)
class ActorFrame:
    right: np.ndarray
    up: np.ndarray
    forward: np.ndarray
    method: str
    confidence: float
    evidence: tuple[str, ...] = ()

    def __post_init__(self) -> None:
        right = _unit(self.right, "actor right axis")
        up = _unit(self.up, "actor up axis")
        right = _unit(right - up * float(np.dot(right, up)), "actor lateral axis")
        forward = _unit(np.cross(right, up), "actor forward axis")
        supplied_forward = _unit(self.forward, "actor supplied forward axis")
        if float(np.dot(forward, supplied_forward)) < 0.0:
            right = -right
            forward = -forward
        up = _unit(np.cross(forward, right), "actor reconstructed up axis")
        matrix = np.column_stack((right, up, forward))
        if not np.allclose(matrix.T @ matrix, np.eye(3), atol=1.0e-8, rtol=0.0):
            raise ActorFrameAmbiguityError("actor frame is not orthonormal")
        if float(np.linalg.det(matrix)) <= 0.0:
            raise ActorFrameAmbiguityError("actor frame is not right-handed")
        confidence = float(self.confidence)
        if not np.isfinite(confidence) or not 0.0 <= confidence <= 1.0:
            raise ValueError("actor-frame confidence must be between zero and one")
        object.__setattr__(self, "right", right)
        object.__setattr__(self, "up", up)
        object.__setattr__(self, "forward", forward)
        object.__setattr__(self, "confidence", confidence)

    def to_dict(self) -> dict[str, Any]:
        return {
            "right": self.right.tolist(),
            "up": self.up.tolist(),
            "forward": self.forward.tolist(),
            "method": self.method,
            "confidence": self.confidence,
            "evidence": list(self.evidence),
            "orthonormal": True,
            "right_handed": True,
        }


def _value(value: Any, name: str, default: Any = None) -> Any:
    if isinstance(value, Mapping):
        return value.get(name, default)
    return getattr(value, name, default)


def _candidate_name(analysis: Any, *roles: str) -> str:
    candidates = _value(analysis, "semantic_roles", {}) or {}
    for role in roles:
        candidate = candidates.get(role) if isinstance(candidates, Mapping) else None
        if candidate is None:
            continue
        name = str(
            _value(candidate, "bone_name", "")
            or _value(candidate, "source_bone", "")
            or _value(candidate, "name", "")
        )
        if name:
            return name
    return ""


def _node_positions(analysis: Any) -> dict[str, np.ndarray]:
    result: dict[str, np.ndarray] = {}
    for node in tuple(_value(analysis, "nodes", ()) or ()):
        name = str(_value(node, "name", "") or _value(node, "original_name", ""))
        position = _value(node, "bind_position", None)
        if name and position is not None:
            array = np.asarray(position, dtype=float)
            if array.shape == (3,) and np.isfinite(array).all():
                result[name] = array
    return result


def _declared_axis_frame(analysis: Any, *, method: str) -> ActorFrame:
    settings = dict(_value(analysis, "axis_settings", {}) or {})
    axes = (
        np.asarray((1.0, 0.0, 0.0)),
        np.asarray((0.0, 1.0, 0.0)),
        np.asarray((0.0, 0.0, 1.0)),
    )
    up_index = int(settings.get("UpAxis", 1) or 1)
    coord_index = int(settings.get("CoordAxis", 0) or 0)
    if up_index not in range(3) or coord_index not in range(3) or up_index == coord_index:
        up_index, coord_index = 1, 0
    up = axes[up_index] * float(settings.get("UpAxisSign", 1) or 1)
    right = axes[coord_index] * float(settings.get("CoordAxisSign", 1) or 1)
    forward = np.cross(right, up)
    return ActorFrame(
        right,
        up,
        forward,
        method,
        0.55,
        ("declared coordinate/up axes for a non-humanoid target",),
    )


def build_source_actor_frame(
    source_or_analysis: Any, *, allow_declared_fallback: bool = False
) -> ActorFrame:
    """Build a sign-stable actor frame from universal analysis evidence."""

    analysis = source_or_analysis
    if _value(analysis, "nodes", None) is None or _value(analysis, "semantic_roles", None) is None:
        from .skeleton_analysis import analyze_source_skeleton

        analysis = analyze_source_skeleton(source_or_analysis)
    body = _value(analysis, "body_frame", None)
    if body is not None:
        body_right = np.asarray(_value(body, "right_axis"), dtype=float)
        body_up = _unit(_value(body, "up_axis"), "source structural up axis")
        body_forward = np.asarray(_value(body, "forward_axis"), dtype=float)
        declared = _declared_axis_frame(
            analysis, method="declared_source_coordinate_frame"
        )
        declared_alignment = float(np.dot(body_up, declared.up))
        if abs(declared_alignment) >= 0.8:
            # Actor vertical is a world-space channel. A slightly leaning bind
            # spine must not rotate metres of forward travel into vertical
            # motion. Keep the anatomical hip/shoulder span and forward sign,
            # but snap up to the FBX-declared axis when structure corroborates
            # it. Strong disagreement remains structural rather than guessed.
            declared_up = declared.up if declared_alignment >= 0.0 else -declared.up
            return ActorFrame(
                body_right,
                declared_up,
                body_forward,
                "universal_skeleton_analysis_body_frame_declared_up",
                float(_value(body, "quality", 0.0) or 0.0),
                tuple(str(row) for row in (_value(body, "evidence", ()) or ()))
                + ("FBX declared up agrees with the structural body axis",),
            )
        return ActorFrame(
            body_right,
            body_up,
            body_forward,
            "universal_skeleton_analysis_body_frame",
            float(_value(body, "quality", 0.0) or 0.0),
            tuple(str(row) for row in (_value(body, "evidence", ()) or ())),
        )

    positions = _node_positions(analysis)
    left = _candidate_name(analysis, "left_thigh", "left_clavicle", "left_upper_arm")
    right = _candidate_name(analysis, "right_thigh", "right_clavicle", "right_upper_arm")
    pelvis = _candidate_name(analysis, "pelvis")
    axial = _candidate_name(analysis, "spine_1", "spine_2", "neck_1", "head")
    if not all(name and name in positions for name in (left, right, pelvis, axial)):
        if allow_declared_fallback:
            return _declared_axis_frame(
                analysis, method="declared_non_humanoid_coordinate_frame"
            )
        raise ActorFrameAmbiguityError(
            "Needs attention — actor frame is ambiguous: identify pelvis, one axial bone, "
            "and a left/right hip or shoulder pair."
        )
    up = _unit(positions[axial] - positions[pelvis], "source pelvis-to-spine axis")
    right_axis = positions[right] - positions[left]
    right_axis -= up * float(np.dot(right_axis, up))
    right_axis = _unit(right_axis, "source left/right body span")
    forward = _unit(np.cross(right_axis, up), "source reconstructed forward axis")
    return ActorFrame(
        right_axis,
        up,
        forward,
        "semantic_bind_anchors",
        0.78,
        ("pelvis/axial semantic anchors", "bilateral hip or shoulder span"),
    )


def _target_bind_globals(rig: Any) -> dict[str, np.ndarray]:
    from .retarget_engines.mapped_rig import target_bind_local_matrix

    result: dict[str, np.ndarray] = {}
    for bone in rig.bones:
        local = target_bind_local_matrix(bone)
        result[bone.name] = (
            result[rig.bones[bone.parent_index].name] @ local
            if bone.parent_index >= 0
            else local
        )
    return result


def build_target_actor_frame(
    target_rig: Any,
    target_policy: Any = None,
    *,
    allow_declared_fallback: bool = False,
) -> ActorFrame:
    globals_ = _target_bind_globals(target_rig)
    slots = tuple(_value(target_policy, "direct_slots", ()) or ())
    by_role = {
        str(_value(slot, "semantic_role", "")): str(_value(slot, "target_bone", ""))
        for slot in slots
    }
    left_name = by_role.get("left_thigh") or by_role.get("left_clavicle")
    right_name = by_role.get("right_thigh") or by_role.get("right_clavicle")
    if not left_name or not right_name:
        names = set(globals_)
        left_name = next((name for name in ("l_thigh", "l_clavicle") if name in names), "")
        right_name = next((name for name in ("r_thigh", "r_clavicle") if name in names), "")
    if not left_name or not right_name:
        # Conservative custom-rig fallback: canonical role inference is used
        # only to select a bilateral bind span, never to invent animation maps.
        from .retarget_mapping import canonical_humanoid_role

        by_canonical_role: dict[str, str] = {}
        for name in globals_:
            role = canonical_humanoid_role(name)
            if role:
                by_canonical_role.setdefault(role, name)
        left_name = left_name or by_canonical_role.get("left_thigh") or by_canonical_role.get("left_clavicle")
        right_name = right_name or by_canonical_role.get("right_thigh") or by_canonical_role.get("right_clavicle")
    if not left_name or not right_name or left_name not in globals_ or right_name not in globals_:
        if allow_declared_fallback:
            up = _unit(infer_target_up_axis(target_rig), "target world-up axis")
            candidate = np.asarray((1.0, 0.0, 0.0), dtype=float)
            if abs(float(np.dot(candidate, up))) > 0.95:
                candidate = np.asarray((0.0, 0.0, 1.0), dtype=float)
            right = _unit(
                candidate - up * float(np.dot(candidate, up)),
                "target declared lateral axis",
            )
            return ActorFrame(
                right,
                up,
                np.cross(right, up),
                "declared_non_humanoid_target_frame",
                0.55,
                ("target world-up plus canonical lateral axis",),
            )
        raise ActorFrameAmbiguityError(
            "Needs attention — target actor frame has no bilateral hip/shoulder anchors."
        )
    up = _unit(infer_target_up_axis(target_rig), "target world-up axis")
    right = globals_[right_name][:3, 3] - globals_[left_name][:3, 3]
    right -= up * float(np.dot(right, up))
    right = _unit(right, "target bilateral body span")
    forward = _unit(np.cross(right, up), "target reconstructed forward axis")
    sign = float(dict(getattr(target_rig, "extensions", {}) or {}).get("actor_forward_sign", 1.0) or 1.0)
    if sign < 0.0:
        right = -right
        forward = -forward
    return ActorFrame(
        right,
        up,
        forward,
        "target_bind_policy_anchors",
        1.0,
        (f"bilateral span {left_name} -> {right_name}", "declared target world-up axis"),
    )


def actor_components(delta: Sequence[float], frame: ActorFrame) -> dict[str, float]:
    value = np.asarray(tuple(float(v) for v in delta), dtype=float)
    if value.shape != (3,) or not np.isfinite(value).all():
        raise ValueError("root displacement must be a finite three-vector")
    return {
        "lateral": float(np.dot(value, frame.right)),
        "vertical": float(np.dot(value, frame.up)),
        "forward": float(np.dot(value, frame.forward)),
    }


def map_root_displacement_by_actor_frame(
    source_delta_meters: Sequence[float],
    source_frame: ActorFrame,
    target_frame: ActorFrame,
) -> np.ndarray:
    components = actor_components(source_delta_meters, source_frame)
    result = (
        components["lateral"] * target_frame.right
        + components["vertical"] * target_frame.up
        + components["forward"] * target_frame.forward
    )
    if not np.isfinite(result).all():
        raise ValueError("actor-frame root displacement mapping produced non-finite values")
    return result


def root_motion_basis_report(
    source_frame: ActorFrame,
    target_frame: ActorFrame,
    source_net_delta_meters: Sequence[float],
) -> dict[str, Any]:
    target_delta = map_root_displacement_by_actor_frame(
        source_net_delta_meters, source_frame, target_frame
    )
    source_components = actor_components(source_net_delta_meters, source_frame)
    target_components = actor_components(target_delta, target_frame)
    return {
        "source_frame_method": source_frame.method,
        "source_frame_confidence": source_frame.confidence,
        "source_frame_evidence": list(source_frame.evidence),
        "source_right": source_frame.right.tolist(),
        "source_up": source_frame.up.tolist(),
        "source_forward": source_frame.forward.tolist(),
        "target_frame_method": target_frame.method,
        "target_frame_confidence": target_frame.confidence,
        "target_frame_evidence": list(target_frame.evidence),
        "target_right": target_frame.right.tolist(),
        "target_up": target_frame.up.tolist(),
        "target_forward": target_frame.forward.tolist(),
        "source_net_actor_displacement_m": source_components,
        "target_net_actor_displacement_m": target_components,
        "target_net_vector_m": target_delta.tolist(),
        "model_basis_used_for_root_vector": False,
    }


__all__ = [
    "ActorFrame",
    "ActorFrameAmbiguityError",
    "actor_components",
    "build_source_actor_frame",
    "build_target_actor_frame",
    "map_root_displacement_by_actor_frame",
    "root_motion_basis_report",
]
