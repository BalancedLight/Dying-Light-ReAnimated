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
    FbxScene,
)


ERROR = "error"
WARNING = "warning"
INFO = "informational"


@dataclass(frozen=True, slots=True)
class FbxPreflightFinding:
    severity: str
    code: str
    detected: str
    why_it_matters: str
    can_continue: bool
    action: str


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

    def add(
        self,
        severity: str,
        code: str,
        detected: str,
        why: str,
        action: str,
        *,
        can_continue: bool | None = None,
    ) -> None:
        self.findings.append(
            FbxPreflightFinding(
                severity,
                code,
                detected,
                why,
                severity != ERROR if can_continue is None else bool(can_continue),
                action,
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
        rows = [row for row in self.findings if row.severity == ERROR]
        if rows:
            raise ValueError(
                "FBX preflight blocked the build:\n- "
                + "\n- ".join(f"[{row.code}] {row.detected} {row.action}" for row in rows)
            )

    def to_dict(self) -> dict[str, Any]:
        return {
            "format": "dl-reanimated-fbx-preflight-v1",
            "path": self.path,
            "purpose": self.purpose,
            "blocking": self.blocking,
            "import_blocking": self.import_blocking,
            "repairable": bool(self.repairable_findings),
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
    source_names = set(document.limb_models)
    target_names = {bone.name for bone in rig.bones}
    required = {bone.name for bone in rig.bones if bone.deform and not bone.helper}
    optional = target_names - required
    missing_required = sorted(required - source_names, key=str.casefold)
    missing_optional = sorted(optional - source_names, key=str.casefold)
    extra = sorted(source_names - target_names, key=str.casefold)
    mismatches: list[dict[str, str | None]] = []
    by_name = {bone.name: bone for bone in rig.bones}
    for name in sorted(target_names & source_names, key=str.casefold):
        bone = by_name[name]
        expected = rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None
        actual = _nearest_selected_ancestor(name, document.parent_by_name, target_names)
        if actual != expected:
            mismatches.append({"bone": name, "expected_target_parent": expected, "source_target_ancestor": actual})
    if missing_required or mismatches:
        classification = "incompatible"
    elif extra or missing_optional:
        classification = "target_compatible_source_superset"
    else:
        classification = "exact_identity"
    return {
        "classification": classification,
        "matched_target_bones": sorted(target_names & source_names, key=str.casefold),
        "required_missing_bones": missing_required,
        "optional_helper_missing_bones": missing_optional,
        "extra_source_bones": extra,
        "hierarchy_mismatches": mismatches,
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
) -> FbxPreflightReport:
    source = Path(path)
    report = FbxPreflightReport(str(source), purpose)
    try:
        if document is None:
            document = (
                FbxDocument.from_scene(scene)
                if scene is not None
                else document_factory(source)
            )
    except Exception as exc:
        report.add(ERROR, "fbx_unreadable", f"The FBX could not be read: {exc}", "No reliable scene data is available.", "Export a supported binary FBX and try again.")
        return report
    has_stack_inventory = hasattr(document, "animation_stacks")
    stacks = list(getattr(document, "animation_stacks", ()) or ())
    report.inventory.update(
        {
            "skeleton_bone_count": len(document.limb_models),
            "animation_stacks": [row.name for row in stacks],
            "meters_per_unit": float(getattr(document, "meters_per_unit", 0.01)),
            "game_id": game_id,
        }
    )
    if not document.limb_models:
        if purpose == "model":
            report.add(
                INFO,
                "static_model_without_armature",
                "No FBX LimbNode skeleton was found; this model is a static prop.",
                "Static model geometry does not require an armature or skin clusters.",
                "Continue in Auto or Static prop mode. Choose Exact Rig only after adding and skinning an armature.",
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
                "unsupported_fbx_transform",
                str(error),
                "The shared FBX transform contract cannot evaluate this node safely for model or animation output.",
                "Remove shear, singular transforms, or invalid scale from the named node and re-export the FBX.",
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
                WARNING,
                "non_uniform_scene_wrapper",
                "Non-uniform armature wrapper scale was found: " + detected,
                "Only uniform scene/unit wrapper scale can be factored out without "
                "changing joint directions. Any resulting joint shear is a build-blocking "
                "unsupported_fbx_transform finding.",
                "Apply/freeze the named wrapper scale in the DCC, preserve the authored "
                "joint bind, and re-export; review this warning before using the generated CRIG.",
            )
        reflected = list(
            contract_payload.get("reflected_or_negative_scale_nodes", ())
        )
        if reflected:
            reflected_bones = [
                str(value) for value in reflected if str(value) in document.limb_models
            ]
            reflected_non_bones = [
                str(value) for value in reflected if str(value) not in document.limb_models
            ]
            if reflected_bones:
                report.add(
                    ERROR,
                    "reflected_or_negative_bone_scale",
                    "Reflected or negative-scale bone transforms affect: "
                    + ", ".join(reflected_bones[:12]),
                    "Chrome rig TRS and inverse-global bind matrices cannot represent an "
                    "irreducibly reflected joint basis safely.",
                    "Apply/freeze the named armature scale in the DCC and re-export. Exact Rig "
                    "is viable only after the bone bases are non-reflected.",
                )
            if reflected_non_bones:
                report.add(
                    WARNING,
                    "reflected_mesh_or_wrapper_scale",
                    "Reflected non-bone Model transforms affect: "
                    + ", ".join(reflected_non_bones[:12]),
                    "The model importer will reverse emitted winding, but the reflection must "
                    "not enter an authored animation-bone basis.",
                    "Apply transforms in the DCC if the reflected surface or normals are not "
                    "intentional; review the model build report before compiling.",
                )
    collisions = list(getattr(document, "normalized_name_collisions", ()))
    if collisions:
        report.add(ERROR, "duplicate_normalized_bone_names", f"Bone names collide after Unicode NFKC/casefold normalization: {collisions}", "Normalized lookup would be ambiguous.", "Rename the colliding bones so their normalized names are unique.")
    non_ascii = sorted(name for name in document.limb_models if not name.isascii())
    if non_ascii:
        report.add(WARNING, "non_ascii_bone_names", f"Non-ASCII bone names were found: {', '.join(non_ascii[:12])}", "Chrome's implicit descriptor hash is ASCII-oriented.", "Use a .crig with explicit descriptors for every non-ASCII target bone.")
    if purpose == "animation":
        if has_stack_inventory and not stacks:
            report.add(ERROR, "no_animation_stacks", "The FBX contains no animation stack.", "There is no clip to sample.", "Bake the desired action into an FBX animation stack.")
        elif len(stacks) > 1:
            report.add(WARNING, "multiple_animation_stacks", f"The FBX contains {len(stacks)} animation stacks.", "The intended clip must be selected explicitly.", "Select one stack; bake/flatten multi-layer stacks before building.")
        try:
            if stacks:
                selected_stack = getattr(document, "selected_animation_stack", None)
                selected_name = str(getattr(selected_stack, "name", "") or "")
                if (
                    animation_stack
                    and selected_name != animation_stack
                ) or (
                    not animation_stack
                    and selected_stack is None
                ):
                    document.select_animation_stack(animation_stack)
        except ValueError as exc:
            report.add(ERROR, "animation_stack_selection", str(exc), "Sampling an absent or layered stack would produce the wrong clip.", "Choose an available single-layer stack or bake/flatten it.")
        curves = dict(getattr(document, "curves", {}) or {})
        changing = []
        facial = []
        for (object_id, prop, axis), (_times, values) in curves.items():
            if len(values) < 2 or max(values) - min(values) <= 1.0e-8:
                continue
            scene = getattr(document, "scene", None)
            name = scene.model_names.get(object_id, "") if scene is not None else ""
            if object_id in document.limb_models.values():
                changing.append((name, prop, axis))
                if any(token in normalized_bone_name(name) for token in ("brow", "lip", "mouth", "eye", "cheek", "jaw", "nose")):
                    facial.append(name)
        report.inventory["changing_skeletal_channel_count"] = len(changing)
        if stacks and not changing:
            report.add(WARNING, "no_changing_skeletal_channels", "The selected stack has no changing skeletal TRS channels.", "The output would remain at bind pose.", "Select the intended body animation stack or rebake skeletal animation.")
        elif changing and set(facial) == {row[0] for row in changing}:
            report.add(WARNING, "facial_only_curves", "Only facial-like bones have changing curves.", "A body ANM2 export may be static.", "Use the facial/mimic workflow or select a body animation stack.")
    diagnostics = document.bind_diagnostics() if hasattr(document, "bind_diagnostics") else {}
    report.inventory["bind"] = diagnostics
    coverage = diagnostics.get("bind_coverage", {})
    if coverage and coverage.get("authoritative", 0) < coverage.get("total", 0):
        report.add(WARNING, "partial_bind_pose", f"Authoritative bind coverage is {coverage.get('authoritative')}/{coverage.get('total')} bones.", "Fallback Model transforms may not equal the skinned bind pose.", "Re-export with a complete BindPose or skin TransformLink matrices.")
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
        report.add(ERROR, "singular_bind_matrix", f"Non-finite or singular bind matrices affect: {', '.join(singular[:12])}", "The bind basis cannot be inverted safely.", "Remove zero scale/non-finite transforms and re-export.")
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
                ERROR,
                "required_target_bones_missing",
                f"Required target bones are missing: {', '.join(compatibility['required_missing_bones'][:20])}",
                "The FBX is not the same skeleton as the selected target .crig, so strict exact-rig export cannot reconstruct those tracks by name.",
                "Add the clip, then review the generated map in Root & .crig Mapping. Unmapped helpers can stay at bind pose; map every body bone that must animate.",
                can_continue=True,
            )
        if compatibility["optional_helper_missing_bones"]:
            report.add(WARNING, "optional_target_bones_missing", f"Optional/helper target bones are missing: {', '.join(compatibility['optional_helper_missing_bones'][:20])}", "Those helpers will remain at target bind pose.", "Continue if they are intentionally absent; otherwise export them from the source rig.")
        if compatibility["extra_source_bones"]:
            report.add(INFO, "source_has_extra_bones", f"The source contains {len(compatibility['extra_source_bones'])} extra bones.", "Facial, cloth, weapon and secondary chains are safe in a target-compatible superset.", "No change is required unless a required target bone is missing.")
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
