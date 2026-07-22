"""Reusable animation/model FBX preflight with actionable findings."""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Iterable
import math
import unicodedata

import numpy as np

from .chrome_rig import ChromeRig
from .fbx_core import FbxDocument
from .model_importer.fbx_model import (
    BLENDSHAPE_IDENTITY_NOOP,
    BLENDSHAPE_MALFORMED,
    BLENDSHAPE_REAL_ANIMATED,
    BLENDSHAPE_REAL_STATIC,
    FbxDomainError,
    FbxImportTolerance,
    FbxLoadPurpose,
    FbxScene,
)


ERROR = "error"
WARNING = "warning"
INFO = "informational"
PASS = "pass"
AUTOMATICALLY_REPAIRED = "automatically_repaired"
BLOCK = "block"
EXPORT_FIRST_POLICY = "export_first_v1"

_ANIMATION_BUILD_BLOCKING_CODES = frozenset(
    {
        "animation_stack_unusable",
        "requested_animation_stack_missing",
        "singular_bind_matrix",
        "singular_or_nonfinite_canonical_transform",
    }
)


@dataclass(frozen=True, slots=True)
class FbxPreflightFinding:
    severity: str
    code: str
    detected: str
    why_it_matters: str
    can_continue: bool
    action: str
    outcome: str = PASS
    group: str = "needs_review"


@dataclass(slots=True)
class FbxPreflightReport:
    path: str
    purpose: str
    findings: list[FbxPreflightFinding] = field(default_factory=list)
    inventory: dict[str, Any] = field(default_factory=dict)

    @property
    def blocking(self) -> bool:
        return any(row.severity == ERROR for row in self.findings)

    @property
    def import_blocking(self) -> bool:
        """Whether the file is too broken to add to a project for repair.

        Target-skeleton incompatibilities are build blockers in strict mode,
        but they are intentionally importable because the mapped-.crig editor
        is the place where users repair them.
        """

        return any(
            row.severity == ERROR and not row.can_continue for row in self.findings
        )

    @property
    def repairable_findings(self) -> list[FbxPreflightFinding]:
        return [
            row for row in self.findings if row.severity == ERROR and row.can_continue
        ]

    @property
    def readiness_level(self) -> str:
        if self.import_blocking:
            return "cannot_export"
        if any(
            row.severity == ERROR
            or row.code == "multiple_animation_stacks"
            for row in self.findings
        ):
            return "needs_attention"
        if any(row.severity == WARNING for row in self.findings):
            return "advisory"
        return "ready"

    @property
    def readiness_label(self) -> str:
        level = self.readiness_level
        codes = {row.code for row in self.findings}
        if level == "cannot_export":
            return "Cannot export — invalid or unsampleable FBX data"
        if level == "needs_attention":
            return "Needs attention — review the selected animation or mapping"
        if level == "advisory":
            return "Advisory — export remains available"
        if "common_wrapper_reflection_canonicalized" in codes:
            return "Ready — automatically repaired wrapper transform"
        if "target_only_bones_held_at_bind" in codes:
            return "Ready — partial skeleton; target-only bones held at bind"
        if codes.intersection({"static_bind_pose_clip", "static_rest_pose_stack"}):
            return "Ready — static/bind-pose clip"
        return "Ready"

    def add(
        self,
        severity: str,
        code: str,
        detected: str,
        why: str,
        action: str,
        *,
        can_continue: bool | None = None,
        outcome: str | None = None,
        group: str | None = None,
    ) -> None:
        resolved_outcome = outcome or (
            BLOCK if severity == ERROR else WARNING if severity == WARNING else PASS
        )
        resolved_group = group or (
            "fatal"
            if resolved_outcome == BLOCK
            else "repaired"
            if resolved_outcome == AUTOMATICALLY_REPAIRED
            else "needs_review"
            if resolved_outcome == WARNING
            else "ignored"
        )
        self.findings.append(
            FbxPreflightFinding(
                severity,
                code,
                detected,
                why,
                severity != ERROR if can_continue is None else bool(can_continue),
                action,
                resolved_outcome,
                resolved_group,
            )
        )

    def actionable_message(
        self,
        findings: Iterable[FbxPreflightFinding] | None = None,
    ) -> str:
        rows = list(self.findings if findings is None else findings)
        if not rows:
            return "No FBX preflight issues were found."
        return "\n\n".join(
            f"{row.detected}\nWhy this matters: {row.why_it_matters}\nWhat to do: {row.action}"
            for row in rows
        )

    def require_buildable(self) -> None:
        rows = [
            row
            for row in self.findings
            if row.severity == ERROR
            and (
                self.purpose != "animation"
                or not row.can_continue
                or row.code in _ANIMATION_BUILD_BLOCKING_CODES
            )
        ]
        if rows:
            raise ValueError(
                "FBX preflight blocked the build:\n- "
                + "\n- ".join(f"[{row.code}] {row.detected} {row.action}" for row in rows)
            )

    def to_dict(self) -> dict[str, Any]:
        return {
            "format": "dl-reanimated-fbx-preflight-v1",
            "preflight_policy": EXPORT_FIRST_POLICY,
            "path": self.path,
            "purpose": self.purpose,
            "blocking": self.blocking,
            "import_blocking": self.import_blocking,
            "repairable": bool(self.repairable_findings),
            "readiness_level": self.readiness_level,
            "readiness_label": self.readiness_label,
            "findings": [asdict(row) for row in self.findings],
            "inventory": self.inventory,
        }


def normalized_bone_name(value: str) -> str:
    return unicodedata.normalize("NFKC", str(value)).casefold()


def _helper_like(name: str) -> bool:
    value = normalized_bone_name(name).replace("_", "")
    return any(token in value for token in ("iktarget", "helper", "shadowcaster", "camera", "platform"))


def _nearest_selected_ancestor(name: str, parents: dict[str, str | None], selected: set[str]) -> str | None:
    seen: set[str] = set()
    cursor = parents.get(name)
    while cursor is not None and cursor not in seen:
        if cursor in selected:
            return cursor
        seen.add(cursor)
        cursor = parents.get(cursor)
    return None


def classify_target_compatibility(document: Any, rig: ChromeRig) -> dict[str, Any]:
    source_names = set(str(name) for name in document.limb_models)
    target_names = {str(bone.name) for bone in rig.bones}
    required = {bone.name for bone in rig.bones if bone.deform and not bone.helper}
    optional = target_names - required
    source_by_normalized: dict[str, list[str]] = {}
    target_by_normalized: dict[str, list[str]] = {}
    for name in source_names:
        source_by_normalized.setdefault(normalized_bone_name(name), []).append(name)
    for name in target_names:
        target_by_normalized.setdefault(normalized_bone_name(name), []).append(name)
    matched: dict[str, str] = {}
    for key in sorted(set(source_by_normalized).intersection(target_by_normalized)):
        sources = source_by_normalized[key]
        targets = target_by_normalized[key]
        if len(sources) == len(targets) == 1:
            matched[targets[0]] = sources[0]

    by_name = {bone.name: bone for bone in rig.bones}

    def hierarchy_mismatches(rows: dict[str, str]) -> list[dict[str, str | None]]:
        selected_targets = set(rows)
        selected_sources = set(rows.values())
        source_target_by_normalized = {
            normalized_bone_name(source_name): target_name
            for target_name, source_name in rows.items()
        }
        mismatches: list[dict[str, str | None]] = []
        for name in sorted(selected_targets, key=str.casefold):
            bone = by_name[name]
            expected_index = bone.parent_index
            expected = None
            while expected_index >= 0:
                candidate = rig.bones[expected_index].name
                if candidate in selected_targets:
                    expected = candidate
                    break
                expected_index = rig.bones[expected_index].parent_index
            source_name = rows[name]
            actual_source = _nearest_selected_ancestor(
                source_name,
                document.parent_by_name,
                selected_sources,
            )
            actual = (
                source_target_by_normalized.get(normalized_bone_name(actual_source))
                if actual_source
                else None
            )
            if actual != expected:
                mismatches.append(
                    {
                        "bone": name,
                        "expected_target_parent": expected,
                        "source_target_ancestor": actual,
                    }
                )
        return mismatches

    # A disconnected optional/helper endpoint is safer at target bind than as
    # a direct row in the wrong parent basis.  It should not turn an otherwise
    # usable animation into a mapping error: its mapped parent will carry the
    # bind-held target row naturally.  Required/deform ancestry mismatches
    # remain action-required.
    initial_mismatches = hierarchy_mismatches(matched)
    optional_mismatches = [
        row for row in initial_mismatches if str(row["bone"]) in optional
    ]
    for row in optional_mismatches:
        matched.pop(str(row["bone"]), None)

    matched_targets = set(matched)
    matched_sources = set(matched.values())
    missing_required = sorted(required - matched_targets, key=str.casefold)
    missing_optional = sorted(optional - matched_targets, key=str.casefold)
    extra = sorted(source_names - matched_sources, key=str.casefold)
    mismatches = hierarchy_mismatches(matched)
    if mismatches:
        classification = "incompatible"
    elif matched_sources == source_names and len(matched_targets) < len(target_names):
        classification = "exact_target_subset"
    elif missing_required:
        classification = "incompatible"
    elif extra or missing_optional:
        classification = "target_compatible_source_superset"
    else:
        classification = "exact_identity"
    return {
        "classification": classification,
        "matched_target_bones": sorted(matched_targets, key=str.casefold),
        "exact_target_subset_mapping": {
            target: matched[target]
            for target in sorted(matched, key=str.casefold)
        },
        "exact_target_subset_rows": len(matched),
        "target_bind_bones": sorted(target_names - matched_targets, key=str.casefold),
        "target_bind_rows": len(target_names - matched_targets),
        "required_missing_bones": missing_required,
        "optional_helper_missing_bones": missing_optional,
        "extra_source_bones": extra,
        "hierarchy_mismatches": mismatches,
        "optional_hierarchy_mismatches_held_at_bind": optional_mismatches,
    }


def preflight_fbx(
    path: str | Path,
    *,
    purpose: str = "animation",
    animation_stack: str | None = None,
    target_rig: ChromeRig | None = None,
    game_id: str | None = None,
    document_factory: Any = FbxDocument,
    document: Any | None = None,
    scene: FbxScene | None = None,
    model_morph_support_enabled: bool = False,
    tolerance: FbxImportTolerance | str = FbxImportTolerance.RECOMMENDED,
) -> FbxPreflightReport:
    source = Path(path)
    report = FbxPreflightReport(str(source), purpose)
    try:
        if document is None:
            document = (
                FbxDocument.from_scene(
                    scene,
                    purpose=(
                        FbxLoadPurpose.MODEL
                        if purpose == "model"
                        else FbxLoadPurpose.ANIMATION
                    ),
                    tolerance=tolerance,
                )
                if scene is not None
                else FbxDocument(
                    source,
                    purpose=(
                        FbxLoadPurpose.MODEL
                        if purpose == "model"
                        else FbxLoadPurpose.ANIMATION
                    ),
                    tolerance=tolerance,
                )
                if document_factory is FbxDocument
                else document_factory(source)
            )
    except FbxDomainError as exc:
        report.add(
            ERROR,
            exc.code,
            f"The requested FBX {exc.domain} domain is unusable: {exc}",
            "Other parsed FBX domains are not evidence that this requested output can be built.",
            "Repair the named requested-domain data and retry; unrelated model geometry does "
            "not need to be changed for animation import.",
        )
        return report
    except Exception as exc:
        report.add(ERROR, "fbx_unreadable", f"The FBX could not be read: {exc}", "No reliable scene data is available.", "Export a supported binary FBX and try again.")
        return report
    scene = getattr(document, "scene", scene)
    has_stack_inventory = hasattr(document, "animation_stacks")
    stacks = list(getattr(document, "animation_stacks", ()) or ())
    stack_activity = (
        list(document.animation_stack_activity())
        if hasattr(document, "animation_stack_activity")
        else []
    )
    report.inventory.update(
        {
            "skeleton_bone_count": len(document.limb_models),
            "animation_stacks": [row.name for row in stacks],
            "animation_stack_activity": [
                row.to_dict() if hasattr(row, "to_dict") else str(row)
                for row in stack_activity
            ],
            "meters_per_unit": float(getattr(document, "meters_per_unit", 0.01)),
            "declared_timebase": (
                document.declared_timebase.to_dict()
                if hasattr(getattr(document, "declared_timebase", None), "to_dict")
                else None
            ),
            "game_id": game_id,
            "requested_purpose": purpose,
            "import_tolerance": FbxImportTolerance.coerce(tolerance).value,
            "loaded_domains": list(getattr(scene, "loaded_domains", ()) or ()),
            "raw_geometry_inventory": list(
                getattr(scene, "raw_geometry_inventory", ()) or ()
            ),
            "model_geometry_findings": list(
                getattr(scene, "geometry_findings", ()) or ()
            ),
        }
    )
    raw_geometry = tuple(getattr(scene, "raw_geometry_inventory", ()) or ())
    if purpose == "animation" and raw_geometry:
        polygon_count = sum(int(row.get("polygon_count", 0)) for row in raw_geometry)
        quad_count = sum(
            int((row.get("polygon_size_counts", {}) or {}).get("4", 0))
            for row in raw_geometry
        )
        ngon_count = sum(
            int(count)
            for row in raw_geometry
            for size, count in (row.get("polygon_size_counts", {}) or {}).items()
            if int(size) > 4
        )
        report.add(
            INFO,
            "model_geometry_ignored_for_animation",
            f"The FBX contains {len(raw_geometry)} model mesh object(s) with "
            f"{polygon_count} polygons ({quad_count} quads, {ngon_count} n-gons). "
            "Model topology was not loaded or validated for animation import.",
            "Skeletal ANM2 sampling uses the hierarchy, bind pose, selected animation "
            "stack, and curves; display-mesh triangulation, tangents, normals, materials, "
            "and skin topology are irrelevant to that output.",
            "No model-mesh repair is required for animation import. Use Model or Full "
            "Diagnostic purpose only when that geometry itself must be built or reviewed.",
            outcome=PASS,
            group="ignored",
        )
        inventory_errors = [
            (str(row.get("name", "<unnamed>")), str(row.get("inventory_error", "")))
            for row in raw_geometry
            if str(row.get("inventory_error", ""))
        ]
        if inventory_errors:
            report.add(
                INFO,
                "model_geometry_error_ignored_for_animation",
                "Some unrequested model geometry could not even be inventoried: "
                + "; ".join(
                    f"{name}: {message}" for name, message in inventory_errors[:8]
                ),
                "The skeletal animation domain parsed independently and does not consume model "
                "control-point or polygon-index arrays.",
                "No action is required for ANM2 animation output. Repair the mesh only before "
                "requesting a model build.",
                outcome=PASS,
                group="ignored",
            )
    if purpose == "model":
        for finding in list(getattr(scene, "geometry_findings", ()) or ()):
            code = str(finding.get("code", "model_polygon_repaired"))
            geometry = str(finding.get("geometry", "<unnamed>"))
            count = max(1, int(finding.get("count", 1)))
            indexes = [int(value) for value in finding.get("polygon_indexes", ())]
            polygon_note = (
                " Representative polygon indexes: "
                + ", ".join(str(value) for value in indexes)
                + ("." if count <= len(indexes) else "; additional rows are aggregated.")
                if indexes
                else ""
            )
            report.add(
                INFO,
                code,
                f"Automatically repaired {count} model geometry item(s) in {geometry!r} "
                f"using {finding.get('method', 'deterministic recovery')}: "
                f"{finding.get('reason', 'safe source-data recovery')}.{polygon_note}",
                "The requested model uses the recovered triangles or reconstructed layer data; "
                "polygon, material, and source-corner provenance remain attached to emitted faces.",
                "Review the model build report if the source surface was intentionally unusual. "
                "Use Strict diagnostics to make selected recovery warnings block before output.",
                outcome=AUTOMATICALLY_REPAIRED,
                group="repaired",
            )
    scalar_mimic_domain = bool(
        tuple(getattr(scene, "blend_shapes", ()) or ())
        or tuple(getattr(scene, "blend_shape_names", ()) or ())
    )
    report.inventory["supported_scalar_mimic_domain"] = scalar_mimic_domain
    if not document.limb_models:
        if purpose == "model":
            report.add(
                INFO,
                "static_model_without_armature",
                "No FBX LimbNode skeleton was found; this model is a static prop.",
                "Static model geometry does not require an armature or skin clusters.",
                "Continue in Auto or Static prop mode. Choose Exact Rig only after adding and skinning an armature.",
            )
        elif scalar_mimic_domain:
            report.add(
                INFO,
                "scalar_mimic_animation_domain",
                "No LimbNode skeleton was found, but a supported scalar/mimic animation domain is available.",
                "BlendShapeChannel animation can be exported without a body skeleton.",
                "Continue with the facial/mimic content mode.",
                outcome=PASS,
                group="ignored",
            )
        else:
            report.add(ERROR, "no_usable_skeleton", "No FBX LimbNode skeleton was found.", "Animation builds require a skeleton.", "Export bones as an FBX armature/LimbNode hierarchy.")
            return report
    transform_contract = getattr(document, "transform_contract", None)
    if transform_contract is not None and hasattr(transform_contract, "to_dict"):
        contract_payload = transform_contract.to_dict()
        report.inventory["transform_contract"] = contract_payload
        for error in contract_payload.get("errors", ()):
            report.add(
                ERROR,
                (
                    "singular_or_nonfinite_canonical_transform"
                    if purpose == "animation"
                    else "unsupported_fbx_transform"
                ),
                str(error),
                (
                    "A required canonical skeletal sample cannot be represented as finite ANM2 data."
                    if purpose == "animation"
                    else "The shared FBX transform contract cannot evaluate this node safely for model output."
                ),
                "Repair the singular/non-finite source transform and retry the build.",
                can_continue=(purpose == "animation"),
            )
        wrapper_rows = dict(
            contract_payload.get("wrapper_scale_normalization", {}) or {}
        )
        non_uniform_wrappers = [
            (str(name), row.get("scale_xyz", ()))
            for name, row in wrapper_rows.items()
            if not bool(row.get("uniform", False))
        ]
        if non_uniform_wrappers:
            detected = ", ".join(
                f"{name} scale={list(scale)}"
                for name, scale in non_uniform_wrappers[:12]
            )
            report.add(
                INFO if purpose == "animation" else WARNING,
                "non_uniform_scene_wrapper",
                "Non-uniform armature wrapper scale was found: " + detected,
                (
                    "Animation sampling removes the evaluated common wrapper before deriving "
                    "bind-relative motion; finite residual shear is projected to a proper rotation."
                    if purpose == "animation"
                    else "Target model/CRIG authoring must retain structurally coherent bind and skin data."
                ),
                (
                    "No action is required for animation export unless canonical sampling reports a singular value."
                    if purpose == "animation"
                    else "Apply/freeze the named wrapper scale before creating a target CRIG."
                ),
                outcome=(AUTOMATICALLY_REPAIRED if purpose == "animation" else WARNING),
                group=("repaired" if purpose == "animation" else "needs_review"),
            )
        contract_v2 = str(contract_payload.get("format", "")).endswith("-v2")
        local_reflected = list(contract_payload.get("local_reflected_bones", ()))
        sign_changes = list(
            contract_payload.get("animated_determinant_sign_change_bones", ())
        )
        if bool(contract_payload.get("canonicalized_wrapper_reflection", False)):
            wrappers = list(contract_payload.get("common_wrapper_models", ()))
            report.add(
                INFO,
                "common_wrapper_reflection_canonicalized",
                "Automatically removed a common reflected animation wrapper"
                + (f": {', '.join(str(value) for value in wrappers)}" if wrappers else "."),
                "The reflected determinant belonged to the shared scene/armature wrapper, not to descendant bone-local motion.",
                "No action is required. Advanced diagnostics retain the wrapper matrix and canonical sample audit.",
                outcome=AUTOMATICALLY_REPAIRED,
                group="repaired",
            )
        if local_reflected:
            if purpose == "animation":
                report.add(
                    WARNING,
                    "local_bone_reflection_projected",
                    "A local reflected animation basis remains after wrapper removal: "
                    + ", ".join(str(value) for value in local_reflected[:12]),
                    "Bind-relative motion will be projected to the nearest proper rotation while preserving target bind scale.",
                    "Review the exported motion if the reflection was intentionally animated; export remains available.",
                    can_continue=True,
                    group="needs_review",
                )
            else:
                report.add(
                    ERROR,
                    "reflected_or_negative_bone_scale",
                    "Irreducibly reflected target bind bones affect: "
                    + ", ".join(str(value) for value in local_reflected[:12]),
                    "A target CRIG and its skin bind data must remain structurally self-consistent.",
                    "Apply/freeze the named target-bone scale before creating the model/CRIG.",
                )
        if sign_changes:
            report.add(
                WARNING,
                "animated_scale_sign_change",
                "Animated determinant sign changes affect: "
                + ", ".join(str(value) for value in sign_changes[:12]),
                "The discontinuous scale sign cannot be encoded directly in ANM2.",
                "The exporter will use the nearest proper rotation and target bind scale; review the result.",
                can_continue=True,
                group="needs_review",
            )
        if not contract_v2:
            reflected = list(
                contract_payload.get("reflected_or_negative_scale_nodes", ())
            )
            reflected_bones = [
                str(value) for value in reflected if str(value) in document.limb_models
            ]
            if reflected_bones:
                report.add(
                    ERROR,
                    "reflected_or_negative_bone_scale",
                    "Legacy transform diagnostics found reflected bones: "
                    + ", ".join(reflected_bones[:12]),
                    "Contract v1 cannot distinguish inherited wrapper reflection from a local bone reflection.",
                    "Reload through the production FBX evaluator to generate transform contract v2.",
                    can_continue=(purpose == "animation"),
                )
    collisions = list(getattr(document, "normalized_name_collisions", ()))
    if collisions:
        report.add(
            WARNING if purpose == "animation" else ERROR,
            "duplicate_normalized_bone_names",
            f"Bone names collide after Unicode NFKC/casefold normalization: {collisions}",
            "Automatic normalized-name mapping is ambiguous, but FBX object IDs and curve ownership remain distinct.",
            "Resolve the affected mapping row manually; import and sampling remain available.",
            can_continue=(purpose == "animation"),
        )
    non_ascii = sorted(name for name in document.limb_models if not name.isascii())
    if non_ascii:
        report.add(WARNING, "non_ascii_bone_names", f"Non-ASCII bone names were found: {', '.join(non_ascii[:12])}", "Chrome's implicit descriptor hash is ASCII-oriented.", "Use a .crig with explicit descriptors for every non-ASCII target bone.")
    if purpose == "animation":
        if has_stack_inventory and not stacks:
            report.add(
                INFO,
                "static_bind_pose_clip",
                "The FBX contains no animation stack; it will export as a two-frame static bind-pose clip.",
                "A static skeletal pose is mathematically sampleable and does not need changing channels.",
                "No action is required unless moving animation was expected.",
                outcome=PASS,
                group="ignored",
            )
        try:
            if stacks:
                selected_stack = getattr(document, "selected_animation_stack", None)
                selected_name = str(getattr(selected_stack, "name", "") or "")
                if animation_stack and selected_name != animation_stack:
                    document.select_animation_stack(animation_stack)
                elif not animation_stack and selected_stack is None and hasattr(
                    document, "select_preferred_animation_stack"
                ):
                    document.select_preferred_animation_stack()
        except ValueError as exc:
            missing_requested = bool(
                animation_stack and "was not found" in str(exc)
            )
            report.add(
                ERROR,
                (
                    "requested_animation_stack_missing"
                    if missing_requested
                    else "animation_stack_unusable"
                ),
                str(exc),
                "The selected stack cannot currently be sampled.",
                "Choose an available stack or bake/flatten the intended animation before building.",
                can_continue=not missing_requested,
            )
        selected_stack = getattr(document, "selected_animation_stack", None)
        selected_name = str(getattr(selected_stack, "name", "") or "")
        if len(stacks) > 1 and selected_name:
            report.add(
                INFO,
                "animation_stack_automatically_selected",
                f"The FBX contains {len(stacks)} animation stacks; selected "
                f"{selected_name!r} automatically from skeletal curve activity.",
                "Static or unrelated stacks do not need to prevent import when exactly one "
                "stack contains the useful skeletal clip.",
                "No action is required. Manual stack selection remains available in the "
                "animation row.",
                outcome=AUTOMATICALLY_REPAIRED,
                group="repaired",
            )
        elif len(stacks) > 1 and not selected_name:
            report.add(
                WARNING,
                "multiple_animation_stacks",
                f"The FBX contains {len(stacks)} animation stacks with no unique activity winner.",
                "Choosing between multiple equally useful skeletal stacks changes the output clip.",
                "Select the intended stack manually; static peer stacks do not need to be removed.",
            )
        unusable_activity = [row for row in stack_activity if not row.usable]
        if stacks and stack_activity and len(unusable_activity) == len(stack_activity):
            report.add(
                ERROR,
                "animation_stack_unusable",
                "No animation stack has one usable baked layer with valid curve data: "
                + "; ".join(
                    f"{row.name}: {row.reason or 'unusable'}"
                    for row in unusable_activity
                ),
                "There is no stack that can be sampled safely for skeletal output.",
                "Bake or repair at least one named stack, ensuring finite ordered keys and "
                "one animation layer, then re-export.",
                can_continue=True,
            )
        selected_unusable = [
            row
            for row in unusable_activity
            if row.name == (animation_stack or selected_name)
        ]
        for row in selected_unusable:
            report.add(
                ERROR,
                "animation_stack_unusable",
                f"Animation stack {row.name!r} is unusable: {row.reason}",
                "Malformed curve data or layered animation cannot be sampled reliably.",
                "Bake/repair the named stack or choose another usable stack.",
                can_continue=True,
            )
        curves = dict(getattr(document, "curves", {}) or {})
        changing = []
        facial = []
        for (object_id, prop, axis), (_times, values) in curves.items():
            if len(values) < 2 or max(values) - min(values) <= 1.0e-8:
                continue
            name = scene.model_names.get(object_id, "") if scene is not None else ""
            if object_id in document.limb_models.values():
                changing.append((name, prop, axis))
                if any(token in normalized_bone_name(name) for token in ("brow", "lip", "mouth", "eye", "cheek", "jaw", "nose")):
                    facial.append(name)
        report.inventory["changing_skeletal_channel_count"] = len(changing)
        if stacks and not changing:
            selected_activity = next(
                (row for row in stack_activity if row.name == selected_name),
                None,
            )
            if (
                selected_activity is not None
                and selected_activity.skeletal_channel_count > 0
            ):
                report.add(
                    INFO,
                    "static_rest_pose_stack",
                    f"Animation stack {selected_name!r} contains "
                    f"{selected_activity.skeletal_channel_count} constant skeletal channels "
                    "and is importable as a rest-pose clip.",
                    "A deliberately static T-pose or bind-pose source is useful even though "
                    "no channel changes over time.",
                    "No action is required unless a moving clip was expected.",
                    outcome=PASS,
                    group="ignored",
                )
            else:
                report.add(
                    INFO,
                    "static_bind_pose_clip",
                    "Ready — static/bind-pose clip; the selected stack has no changing skeletal TRS channels.",
                    "Two identical finite frames are a valid ANM2 animation.",
                    "No action is required unless moving animation was expected.",
                    outcome=PASS,
                    group="ignored",
                )
        elif changing and set(facial) == {row[0] for row in changing}:
            report.add(WARNING, "facial_only_curves", "Only facial-like bones have changing curves.", "A body ANM2 export may be static.", "Use the facial/mimic workflow or select a body animation stack.")
    diagnostics = document.bind_diagnostics() if hasattr(document, "bind_diagnostics") else {}
    report.inventory["bind"] = diagnostics
    coverage = diagnostics.get("bind_coverage", {})
    if coverage and coverage.get("authoritative", 0) < coverage.get("total", 0):
        report.add(WARNING, "bind_pose_partial", f"Authoritative bind coverage is {coverage.get('authoritative')}/{coverage.get('total')} bones.", "Fallback Model transforms may not equal the skinned bind pose.", "Re-export with a complete BindPose or skin TransformLink matrices.")
    bind_conflicts = [
        *diagnostics.get("conflicting_transform_links", []),
        *diagnostics.get("conflicting_pose_transform_links", []),
    ]
    if bind_conflicts:
        report.add(WARNING, "conflicting_bind_matrices", f"Pose/TransformLink bind matrices disagree for: {sorted(set(bind_conflicts))}", "Different authoritative bind sources disagree about the bone basis.", "Consolidate skin modifiers or verify the intended bind pose.")
    singular = []
    for name, matrix in getattr(document, "bind_global_matrices", {}).items():
        if not np.isfinite(matrix).all() or abs(float(np.linalg.det(matrix[:3, :3]))) <= 1.0e-12:
            singular.append(name)
    if singular:
        report.add(
            ERROR,
            "singular_bind_matrix",
            f"Non-finite or singular bind matrices affect: {', '.join(singular[:12])}",
            "The bind basis cannot be inverted safely at build time.",
            "Remove zero scale/non-finite transforms and re-export.",
            can_continue=(purpose == "animation"),
        )
    roots = [name for name in document.limb_models if document.parent_by_name.get(name) is None]
    report.inventory["skeletal_roots"] = roots
    if len(roots) > 1:
        report.add(INFO, "multiple_roots", f"The skeleton has {len(roots)} independent roots: {', '.join(roots)}", "Independent helpers must not be parented under the primary skeletal root.", "Review the resolved primary and independent roots in the build report.")
    helper_roots = [name for name in roots if _helper_like(name)]
    if helper_roots:
        report.add(INFO, "helper_like_roots", f"Helper-like roots were detected: {', '.join(helper_roots)}", "They normally remain independent of pelvis/bip01.", "Keep them independent unless the target hierarchy explicitly differs.")
    significant_ancestors = []
    scene = getattr(document, "scene", None)
    limb_ids = set(scene.limb_ids) if scene is not None else set()
    ancestors: set[int] = set()
    for object_id in limb_ids:
        parent = scene.model_parent_id(object_id)
        while parent in scene.model_names and parent not in limb_ids:
            ancestors.add(parent)
            parent = scene.model_parent_id(parent)
    for object_id in ancestors:
        matrix = scene.model_local_matrix(object_id)
        if not np.allclose(matrix, np.eye(4), atol=1.0e-5, rtol=1.0e-5):
            significant_ancestors.append(scene.model_names[object_id])
    if significant_ancestors:
        report.add(INFO, "transformed_non_bone_ancestor", f"Non-bone Model ancestors carry significant transforms: {', '.join(significant_ancestors)}", "They may contain exporter axis and scene-scale conversion and are included in global evaluation.", "No change is required unless the reported transform is unintended.")
    meters = float(document.meters_per_unit)
    if not math.isclose(meters, 0.01, rel_tol=0.05) and not math.isclose(meters, 1.0, rel_tol=0.05):
        report.add(WARNING, "unusual_scene_scale", f"The FBX unit scale is {meters:g} meters per unit.", "Unexpected scene units can magnify root translation.", "Confirm FBX unit settings; exporter wrapper scale will be normalized through bind correction.")
    blend_shape_names = tuple(
        str(value)
        for value in (getattr(scene, "blend_shape_names", ()) or ())
    )
    blend_shapes = tuple(getattr(scene, "blend_shapes", ()) or ())
    report.inventory["blend_shape_names"] = list(blend_shape_names)
    report.inventory["blend_shapes"] = [
        row.diagnostic_summary()
        if hasattr(row, "diagnostic_summary")
        else row.to_dict()
        if hasattr(row, "to_dict")
        else str(row)
        for row in blend_shapes
    ]
    identity_shapes = tuple(
        row
        for row in blend_shapes
        if getattr(row, "classification", "") == BLENDSHAPE_IDENTITY_NOOP
    )
    malformed_shapes = tuple(
        row
        for row in blend_shapes
        if getattr(row, "classification", "") == BLENDSHAPE_MALFORMED
    )
    real_shapes = tuple(
        row
        for row in blend_shapes
        if getattr(row, "classification", "")
        in {BLENDSHAPE_REAL_STATIC, BLENDSHAPE_REAL_ANIMATED}
    )
    report.inventory["ignored_identity_blendshapes"] = [
        row.ignored_identity_report()
        for row in identity_shapes
        if hasattr(row, "ignored_identity_report")
    ]
    report.inventory["model_morph_support_enabled"] = bool(
        model_morph_support_enabled
    )
    if purpose == "model":
        for row in identity_shapes:
            name = str(getattr(row, "name", "UnnamedShape"))
            geometry = str(getattr(row, "base_geometry_name", "<unresolved>"))
            report.add(
                INFO,
                "ignored_identity_model_blend_shape",
                f"Ignored identity blendshape {name}: the target contains no position "
                "deformation and its weight remains zero.",
                "Its sparse Shape payload is an identity/no-op, so omitting it leaves the "
                f"base geometry {geometry!r} unchanged.",
                "No action is required. The non-morph model build will skip this target and "
                "continue through skin, palette, MSH, CRIG, compiler, and artifact validation.",
            )
        for row in malformed_shapes:
            shape_name = str(getattr(row, "shape_name", "") or "<unnamed>")
            channel_name = str(getattr(row, "channel_name", "") or "<unresolved>")
            geometry_name = str(
                getattr(row, "base_geometry_name", "") or "<unresolved>"
            )
            shape_id = getattr(row, "shape_object_id", None)
            channel_id = getattr(row, "channel_object_id", None)
            geometry_id = getattr(row, "base_geometry_id", None)
            fields = tuple(getattr(row, "malformed_fields", ()) or ())
            report.add(
                ERROR,
                "malformed_model_blend_shape",
                "Malformed blendshape "
                f"shape {shape_name!r} ({shape_id}), channel {channel_name!r} "
                f"({channel_id}), geometry {geometry_name!r} ({geometry_id}): "
                + "; ".join(str(value) for value in fields),
                "The sparse target cannot be matched safely to one channel and one base "
                "control-point array, so its deformation cannot be classified or emitted.",
                "Repair the named connection or malformed field in the DCC and re-export "
                "the FBX. No model output is safe until this exact target validates.",
            )
        if real_shapes and model_morph_support_enabled:
            report.add(
                INFO,
                "supported_model_blend_shapes",
                "Model FBX contains real morph targets supported by the selected model "
                "importer: "
                + ", ".join(
                    f"{getattr(row, 'name', 'UnnamedShape')} "
                    f"({getattr(row, 'classification', '')})"
                    for row in real_shapes[:12]
                ),
                "The targets contain meaningful geometry or active channel weights and must "
                "be retained rather than treated as identity placeholders.",
                "No action is required; keep model morph import enabled for this build.",
            )
        elif real_shapes:
            report.add(
                ERROR,
                "unsupported_model_blend_shapes",
                "Model FBX contains authored real blendshape targets that the current "
                "non-morph source-MSH importer cannot emit: "
                + ", ".join(
                    f"{getattr(row, 'name', 'UnnamedShape')} "
                    f"({getattr(row, 'classification', '')})"
                    for row in real_shapes[:12]
                ),
                "Silently writing only the base mesh would discard authored vertex deltas or "
                "active morph weights and can change the intended model.",
                "Enable model morph support, or bake the intended shape into the base mesh or "
                "remove/export real blendshapes separately, then re-export the model FBX. "
                "Exact Rig preserves the skeleton bind but is not an alternative for "
                "unsupported morph geometry.",
            )
        elif blend_shape_names and not blend_shapes:
            # Compatibility for callers constructing an older in-memory scene
            # which exposes only channel names.  Without Shape payloads there
            # is no evidence that those channels are identity targets.
            report.add(
                ERROR,
                "unsupported_model_blend_shapes",
                "Model FBX contains blendshape channels without inspectable Shape records: "
                + ", ".join(blend_shape_names[:12]),
                "The importer cannot prove that these legacy/incomplete in-memory channel "
                "records are identity targets, so dropping them could discard deformation.",
                "Reload the FBX through the production parser. Bake/remove the blendshapes "
                "and re-export if no Shape payload is available. Exact Rig is not an "
                "alternative for unknown morph geometry.",
            )
    cluster_count = sum(len(geometry.clusters) for geometry in scene.geometries) if scene is not None else 0
    report.inventory["skin_cluster_count"] = cluster_count
    mesh_names = [
        str(getattr(geometry, "model_name", "") or getattr(geometry, "name", ""))
        for geometry in (scene.geometries if scene is not None else ())
    ]
    report.inventory["mesh_names"] = mesh_names
    normalized_meshes = " ".join(normalized_bone_name(name) for name in mesh_names)
    likely_fpp = (
        "fpp" in normalized_bone_name(source.name)
        or "fpp" in normalized_meshes
        or (
            "head" in {normalized_bone_name(name) for name in document.limb_models}
            and mesh_names
            and any(token in normalized_meshes for token in ("hand", "forearm", "sleeve"))
            and not any(token in normalized_meshes for token in ("head", "face", "torso", "body"))
        )
    )
    if likely_fpp:
        report.add(WARNING, "likely_fpp_headless_mesh", "The file appears to contain an FPP/arms-only or headless mesh set.", "The skeleton can still animate, but a model build may not contain a complete third-person body.", "Continue for animation-only/FPP use, or export the intended complete mesh set for a TPP model build.")
    if purpose == "model" and document.limb_models and not cluster_count:
        report.add(WARNING, "unskinned_skeleton", "A skeleton exists but no skin clusters were found.", "A retained skeleton will not deform the mesh.", "Skin the mesh to the intended bones or import it as a static model.")
    if target_rig is not None:
        compatibility = classify_target_compatibility(document, target_rig)
        report.inventory["target_compatibility"] = compatibility
        if compatibility["required_missing_bones"]:
            report.add(
                INFO,
                "target_only_bones_held_at_bind",
                f"The source is partial relative to the target; {compatibility['target_bind_rows']} target-only bones will remain at bind.",
                "Target-only face, secondary, twist, helper, IK, and deform rows do not make finite source animation unsampleable.",
                "No action is required unless one of those target rows must be animated for this clip.",
                outcome=PASS,
                group="ignored",
            )
        if compatibility["optional_helper_missing_bones"]:
            report.add(
                INFO,
                "optional_target_bones_missing",
                f"Optional/helper target bones are absent and will remain at bind: {', '.join(compatibility['optional_helper_missing_bones'][:20])}",
                "Optional target rows inherit or hold the target bind safely.",
                "No action is required unless an optional helper must animate.",
                outcome=PASS,
                group="ignored",
            )
        if compatibility["extra_source_bones"]:
            report.add(INFO, "source_has_extra_bones", f"The source contains {len(compatibility['extra_source_bones'])} extra bones.", "Facial, cloth, weapon and secondary chains are safe in a target-compatible superset.", "No change is required unless a required target bone is missing.")
        if compatibility.get("optional_hierarchy_mismatches_held_at_bind"):
            rows = compatibility["optional_hierarchy_mismatches_held_at_bind"]
            report.add(
                INFO,
                "optional_hierarchy_mismatch_held_at_bind",
                f"{len(rows)} optional/helper exact-name row(s) used a different parent basis and will remain at target bind.",
                "An optional endpoint or helper does not need a direct source row to follow its mapped target parent.",
                "No action is required unless that helper must animate independently.",
                outcome=PASS,
                group="ignored",
            )
        if compatibility["hierarchy_mismatches"]:
            report.add(
                ERROR,
                "target_hierarchy_mismatch",
                f"Target ancestry differs for {len(compatibility['hierarchy_mismatches'])} bones.",
                "Strict name-based transfer would use a different parent basis and can produce incorrect local transforms.",
                "Add the clip, then review the generated map in Root & .crig Mapping, or use an animation exported from the exact target hierarchy.",
                can_continue=True,
            )
    return report


__all__ = [
    "ERROR", "INFO", "WARNING", "FbxPreflightFinding", "FbxPreflightReport",
    "classify_target_compatibility", "normalized_bone_name", "preflight_fbx",
]
