"""Deterministic exact/subset versus explicitly reviewed cross-rig routing."""

from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any, Mapping

from .automatic_retarget import (
    AUTOMATIC_RETARGET_VALIDATION_FORMAT,
    DL2_BUNDLED_BODY_CERTIFICATE_FORMATS,
    AutomaticRetargetValidation,
)
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
    automatic_verification_status: str = "not_applicable"
    certificate_format: str = ""

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def _is_trusted_live_dl2_verification(value: Any) -> bool:
    """Accept only the typed result produced by live map revalidation."""

    if not isinstance(value, AutomaticRetargetValidation):
        return False
    certificate = dict(value.certificate or {})
    return bool(
        value.format == AUTOMATIC_RETARGET_VALIDATION_FORMAT
        and value.ok
        and value.live_revalidated
        and value.certificate_format in DL2_BUNDLED_BODY_CERTIFICATE_FORMATS
        and certificate.get("policy") == value.certificate_format
        and certificate.get("certificate_status") == "pass"
        and certificate.get("live_revalidated") is True
        and value.plan_hash
        and certificate.get("plan_hash") == value.plan_hash
        and certificate.get("decision_fingerprint")
    )


def select_exact_solver(
    compatibility: Mapping[str, Any],
    mapping_profile: GenericBoneMap | None,
    *,
    automatic_verification: Mapping[str, Any] | Any | None = None,
) -> SolverSelection:
    """Return the only permitted engine for the source/target relationship.

    ``automatic_verification`` must be the typed result of freshly recomputing
    the map against the live source and target. Serialized mappings/dicts are
    deliberately never accepted at this authorization boundary. This keeps the
    legacy ``automatic_repair`` denial intact and prevents an origin string or
    copied certificate payload from becoming an authorization bypass.
    """

    origin = mapping_profile_origin(mapping_profile)
    classification = str(compatibility.get("classification", "incompatible"))
    compatible = bool(
        not compatibility.get("hierarchy_mismatches")
        and (
            not compatibility.get("required_missing_bones")
            or classification == "exact_target_subset"
        )
    )
    if compatible:
        semantic_manual_overrides = int(
            (
                mapping_profile.extensions.get("semantic_manual_override_count", 0)
                if mapping_profile is not None
                else 0
            )
            or 0
        )
        if semantic_manual_overrides:
            if not _is_trusted_live_dl2_verification(automatic_verification):
                return SolverSelection(
                    requested_mode="exact",
                    selected_engine="",
                    selected_policy="",
                    mapping_profile_origin=origin,
                    mapping_profile_changed_solver=False,
                    selection_reason="semantic override requires live validation",
                    build_allowed=False,
                    blocking_error=(
                        "The semantic role override has not passed live source/target "
                        "revalidation. Re-open Retargeting and rebuild the plan."
                    ),
                    automatic_verification_status="failed",
                )
            return SolverSelection(
                requested_mode="exact",
                selected_engine="MappedRigRetargetEngine",
                selected_policy="global_bind_basis_correction",
                mapping_profile_origin=origin,
                mapping_profile_changed_solver=True,
                selection_reason=(
                    f"{semantic_manual_overrides} live-validated semantic role override(s)"
                ),
                automatic_verification_status="pass",
                certificate_format=automatic_verification.certificate_format,
            )
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
            else "exact normalized target subset; target-only rows held at bind"
            if classification == "exact_target_subset"
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
    elif origin == "automatic_verified":
        if _is_trusted_live_dl2_verification(automatic_verification):
            assert isinstance(automatic_verification, AutomaticRetargetValidation)
            return SolverSelection(
                requested_mode="exact",
                selected_engine="MappedRigRetargetEngine",
                selected_policy="global_bind_basis_correction",
                mapping_profile_origin=origin,
                mapping_profile_changed_solver=True,
                selection_reason=(
                    "revalidated built-in DL2 advanced humanoid body bridge"
                ),
                automatic_verification_status="pass",
                certificate_format=automatic_verification.certificate_format,
            )
        error = (
            "The automatically verified map no longer passes live source/target "
            "revalidation. Open Retargeting and regenerate the semantic plan."
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
            automatic_verification_status="not_applicable",
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
        automatic_verification_status=(
            "failed" if origin == "automatic_verified" else "not_applicable"
        ),
    )


__all__ = ["SolverSelection", "select_exact_solver"]
