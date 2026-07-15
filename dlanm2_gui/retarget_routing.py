"""Deterministic exact/subset versus explicitly reviewed cross-rig routing."""

from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any, Mapping

from .bone_maps import GenericBoneMap, mapping_profile_origin


@dataclass(frozen=True, slots=True)
class SolverSelection:
    requested_mode: str
    selected_engine: str
    selected_policy: str
    mapping_profile_origin: str
    mapping_profile_changed_solver: bool
    selection_reason: str
    build_allowed: bool = True
    blocking_error: str = ""

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def select_exact_solver(
    compatibility: Mapping[str, Any],
    mapping_profile: GenericBoneMap | None,
) -> SolverSelection:
    """Return the only permitted engine for the source/target relationship."""

    origin = mapping_profile_origin(mapping_profile)
    classification = str(compatibility.get("classification", "incompatible"))
    compatible = not (
        compatibility.get("required_missing_bones")
        or compatibility.get("hierarchy_mismatches")
    )
    if compatible:
        if mapping_profile is not None and mapping_profile.helper_pairs:
            return SolverSelection(
                requested_mode="exact",
                selected_engine="MappedRigRetargetEngine",
                selected_policy="global_bind_basis_correction",
                mapping_profile_origin=origin,
                mapping_profile_changed_solver=True,
                selection_reason=(
                    "target-compatible base mapping with value-level helper overrides"
                ),
            )
        reason = (
            "exact target identity"
            if classification == "exact_identity"
            else "target-compatible source superset"
        )
        return SolverSelection(
            requested_mode="exact",
            selected_engine="ExactRigRetargetEngine",
            selected_policy="global_bind_basis_correction",
            mapping_profile_origin=origin,
            mapping_profile_changed_solver=False,
            selection_reason=reason,
        )

    if mapping_profile is None:
        error = (
            "The source skeleton is incompatible with the selected .crig. Create a map, "
            "review every required body row, and explicitly approve it before building."
        )
    elif origin not in {"manually_reviewed", "imported_profile"}:
        error = (
            f"The source skeleton is incompatible and its map is {origin!r}. Automatic "
            "suggestions cannot select the mapped solver until the map is explicitly reviewed."
        )
    else:
        return SolverSelection(
            requested_mode="exact",
            selected_engine="MappedRigRetargetEngine",
            selected_policy="mapped_local_rotation_delta",
            mapping_profile_origin=origin,
            mapping_profile_changed_solver=True,
            selection_reason="explicitly reviewed incompatible cross-rig map",
        )
    return SolverSelection(
        requested_mode="exact",
        selected_engine="",
        selected_policy="",
        mapping_profile_origin=origin,
        mapping_profile_changed_solver=False,
        selection_reason="incompatible hierarchy requires explicit mapping review",
        build_allowed=False,
        blocking_error=error,
    )


__all__ = ["SolverSelection", "select_exact_solver"]
