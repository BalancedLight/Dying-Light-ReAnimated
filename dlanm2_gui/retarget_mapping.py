from __future__ import annotations

"""Shared semantic bone mapping for model and animation workspaces.

The mapper is deliberately conservative: exact/descriptor matches win, then a
small anatomy vocabulary covers Mixamo, Character Creator/CC_Base, Blender
suffixes, and the generic CC_Base armature names seen in the model-import corpus.
Every suggestion remains editable in the GUI.
"""

from collections import defaultdict
from dataclasses import dataclass
from typing import Any, Iterable, Mapping
import math
import re
import unicodedata

import numpy as np

from .bone_maps import (
    BoneMapPair,
    GenericBoneMap,
    auto_map_skeletons,
    set_mapping_profile_origin,
    skeleton_signature,
)
from .chrome_rig import ChromeRig
from .trackmap import dl_name_hash


def _plain(value: str) -> str:
    name = str(value).split(":")[-1]
    name = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", name)
    name = name.replace(".", "_").replace("-", "_")
    name = re.sub(r"[^A-Za-z0-9]+", "_", name).strip("_").lower()
    for prefix in (
        "mixamorig_",
        "cc_base_",
        "bip01_",
        "armature_",
        "def_",
    ):
        if name.startswith(prefix):
            name = name[len(prefix) :]
    return re.sub(r"_+", "_", name)


def _side(name: str) -> str:
    tokens = set(name.split("_"))
    if "left" in tokens or "l" in tokens or name.endswith("_l"):
        return "l"
    if "right" in tokens or "r" in tokens or name.endswith("_r"):
        return "r"
    if name.startswith("left"):
        return "l"
    if name.startswith("right"):
        return "r"
    return ""


@dataclass(frozen=True, slots=True)
class HumanoidBoneMatch:
    """A workspace-independent anatomical classification for one bone name."""

    role: str
    confidence: float
    method: str


def match_humanoid_bone(value: str) -> HumanoidBoneMatch | None:
    """Classify common DL, Mixamo, Blender, and CC humanoid names.

    This is intentionally a name classifier rather than a source-to-target
    mapper.  Every workspace can therefore use the same scan while retaining
    its own profile/pair serialization format.
    """

    raw = str(value)
    lowered = raw.casefold()
    is_mixamo = lowered.startswith("mixamorig:") or "mixamorig_" in lowered
    is_character_creator = "cc_base_" in lowered or "ccbase" in lowered
    name = _plain(raw)
    compact = name.replace("_", "")
    if not name:
        return None

    # Character Creator inserts a waist node and commonly uses two neck-twist
    # nodes as its actual torso/neck chain. Treat the first neck pivot as the
    # deforming neck role; the second can safely inherit at bind pose when a
    # source rig only has one neck bone.
    if is_character_creator and compact == "necktwist01":
        return HumanoidBoneMatch("neck_1", 0.97, "character_creator_chain")

    if any(
        token in name
        for token in (
            "ik", "pole", "sharebone", "twist", "breast", "ribs",
            "facial", "tongue", "teeth", "jaw", "eye", "hair", "pony",
            "tassle", "tassel", "hat", "skirt", "bow", "end_end",
        )
    ):
        return None
    side = _side(name)

    # Native DL fingers are named l_finger00..l_finger43.  The first digit is
    # the digit (thumb through pinky), and the second is the phalanx.
    native_finger = re.fullmatch(r"([lr])_?finger([0-4])_?([0-4])", name)
    if native_finger:
        side, digit, segment = native_finger.groups()
        finger = ("thumb", "index", "middle", "ring", "pinky")[int(digit)]
        # The second digit is the phalanx number in the actual DL hierarchy:
        # finger11 -> finger12 -> finger13, finger21 -> finger22 -> finger23,
        # and so on.  Treating finger11 as segment 2 shifted imported CC/Mixamo
        # fingers by one joint and left the first phalanx on the hand fallback.
        role_segment = int(segment)
        return HumanoidBoneMatch(
            f"{side}_{finger}_{min(3, max(1, role_segment))}",
            0.99,
            "native_dl",
        )

    # Fingers before generic hand matching. CC and Blender names commonly use
    # 01/02/03 suffixes, while CC rigs may use b/m/t prefixes.
    finger_aliases = {
        "thumb": "thumb", "index": "index", "pointer": "index",
        "middle": "middle", "mid": "middle", "ring": "ring", "pinky": "pinky",
        "pinkie": "pinky",
    }
    for token, finger in finger_aliases.items():
        if token in compact and "toe" not in compact:
            if not side:
                side = "l" if "left" in compact else "r" if "right" in compact else ""
            if not side:
                return None
            numbers = [int(row) for row in re.findall(r"(?<!\d)(?:0?)([1-4])(?!\d)", name)]
            segment = numbers[-1] if numbers else None
            if segment is None:
                segment = {"b": 1, "m": 2, "t": 3}.get(compact[:1], 1)
            role_segment = "end" if segment >= 4 or name.endswith("_end") else str(
                min(3, max(1, segment))
            )
            return HumanoidBoneMatch(
                f"{side}_{finger}_{role_segment}",
                0.96,
                "semantic_finger",
            )

    if name.endswith("_end") or compact.endswith("end") or compact.endswith("topend"):
        return None

    if is_character_creator:
        character_creator_chain = {
            "hip": "pelvis",
            "pelvis": "pelvis",
            "waist": "spine_1",
            "spine01": "spine_2",
            "spine02": "spine_3",
        }
        if compact in character_creator_chain:
            return HumanoidBoneMatch(
                character_creator_chain[compact], 0.98, "character_creator_chain"
            )

    if is_mixamo:
        mixamo_chain = {
            "hips": "pelvis",
            "spine": "spine_1",
            "spine1": "spine_2",
            "spine2": "spine_3",
            "neck": "neck_1",
        }
        if compact in mixamo_chain:
            return HumanoidBoneMatch(mixamo_chain[compact], 0.98, "mixamo_chain")

    exact = {
        "bip01": "root", "root": "root", "rootbone": "root",
        "rlboneroot": "root", "control": "root",
        "hips": "pelvis", "pelvis": "pelvis", "ccbasepelvis": "pelvis",
        "backbottom": "pelvis",
        "spine": "spine_1", "spine0": "spine_1", "spine00": "spine_1",
        "spine01": "spine_1", "spine1": "spine_1", "backmid": "spine_1",
        "spine02": "spine_2", "spine2": "spine_2", "backtop": "spine_2",
        "spine03": "spine_3", "spine3": "spine_3", "spine04": "spine_3",
        "upperchest": "spine_3", "chest": "spine_3", "hspine": "spine_3",
        "hspine1": "spine_3",
        "neck": "neck_1", "neck01": "neck_1", "neck1": "neck_1",
        "head": "head",
    }
    if compact in exact:
        method = "native_dl" if compact in {"bip01", "hspine", "hspine1"} else "semantic_name"
        return HumanoidBoneMatch(exact[compact], 0.98, method)

    if side:
        role = None
        if any(token in compact for token in ("clavicle", "shoulder")):
            role = "clavicle"
        elif any(token in compact for token in ("upperarm", "toparm", "armtop")) or compact in {
            f"{side}arm", f"arm{side}",
            "leftarm" if side == "l" else "rightarm",
        }:
            role = "upperarm"
        elif any(token in compact for token in ("forearm", "lowerarm", "bottomarm", "armbottom")):
            role = "forearm"
        elif ("hand" in compact and not any(token in compact for token in finger_aliases)) or "wrist" in compact:
            role = "hand"
        elif any(token in compact for token in ("upleg", "upperleg", "thigh", "legtop")):
            role = "thigh"
        elif any(token in compact for token in ("lowerleg", "calf", "legbottom")) or compact in {
            f"{side}leg", f"leg{side}",
            "leftleg" if side == "l" else "rightleg",
        }:
            role = "calf"
        elif "foot" in compact:
            role = "foot"
        elif any(token in compact for token in ("toebase", "toe", "ball")):
            role = "toe"
        if role:
            return HumanoidBoneMatch(f"{side}_{role}", 0.96, "semantic_name")
    return None


def scan_humanoid_bones(
    names: Iterable[str],
    parents: Mapping[str, str | None] | None = None,
) -> dict[str, HumanoidBoneMatch]:
    """Run the shared humanoid-name scan used by every mapping workspace."""

    del parents  # Reserved for hierarchy-based classifiers without changing the API.
    return {
        str(name): match
        for name in names
        if (match := match_humanoid_bone(str(name))) is not None
    }


def canonical_humanoid_role(value: str) -> str | None:
    """Return a stable anatomy role or ``None`` for helpers/twists/accessories."""
    match = match_humanoid_bone(value)
    return match.role if match else None


def _hierarchy_depth(
    name: str,
    parents: Mapping[str, str | None],
) -> int:
    depth = 0
    seen: set[str] = set()
    cursor = parents.get(name)
    while cursor is not None and cursor not in seen:
        seen.add(cursor)
        depth += 1
        cursor = parents.get(cursor)
    return depth


_ASSIGNMENT_FLOOR = 32.0
_REVIEW_MARGIN = 12.0
_HELPER_TOKENS = (
    "helper",
    "camera",
    "weapon",
    "socket",
    "accessory",
    "cloth",
    "control",
    "ik",
    "pole",
)


def source_mapping_evidence(document: Any) -> dict[str, Any]:
    """Return optional bind/deformation evidence exposed by an FBX document.

    Animation-only FBXs commonly have no mesh clusters. In that case the bind
    globals still participate and deformation-class scoring simply remains
    unavailable. The helper is intentionally tolerant so lightweight document
    fixtures and older callers keep working.
    """

    bind_globals = dict(getattr(document, "bind_global_matrices", {}) or {})
    skin_weights: dict[str, float] = defaultdict(float)
    scene = getattr(document, "scene", None)
    geometries = tuple(getattr(scene, "geometries", ()) or ())
    model_names = dict(getattr(scene, "model_names", {}) or {})
    for geometry in geometries:
        for cluster in tuple(getattr(geometry, "clusters", ()) or ()):
            bone_name = str(getattr(cluster, "bone_name", "") or "")
            if not bone_name:
                bone_id = getattr(cluster, "bone_id", None)
                if bone_id is not None:
                    bone_name = str(model_names.get(int(bone_id), "") or "")
            if not bone_name:
                continue
            total = sum(
                float(weight)
                for weight in tuple(getattr(cluster, "weights", ()) or ())
                if math.isfinite(float(weight)) and float(weight) > 0.0
            )
            if total > 0.0:
                skin_weights[bone_name] += total
    return {
        "source_bind_globals": bind_globals or None,
        "source_deform_bones": frozenset(skin_weights) or None,
        "source_skin_weights": dict(skin_weights) or None,
    }


def _rig_bind_globals(rig: ChromeRig) -> dict[str, np.ndarray]:
    locals_by_name: dict[str, np.ndarray] = {}
    for bone in rig.bones:
        w, x, y, z = map(float, bone.bind_rotation_wxyz)
        norm = math.sqrt(w * w + x * x + y * y + z * z)
        if norm <= 1.0e-12 or not math.isfinite(norm):
            continue
        w, x, y, z = w / norm, x / norm, y / norm, z / norm
        rotation = np.asarray(
            (
                (1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
                (2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
                (2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)),
            ),
            dtype=float,
        )
        local = np.eye(4, dtype=float)
        local[:3, :3] = rotation @ np.diag(np.asarray(bone.bind_scale, dtype=float))
        local[:3, 3] = np.asarray(bone.bind_translation, dtype=float)
        locals_by_name[bone.name] = local

    by_index = {bone.index: bone for bone in rig.bones}
    result: dict[str, np.ndarray] = {}
    visiting: set[int] = set()

    def resolve(index: int) -> np.ndarray:
        bone = by_index[index]
        if bone.name in result:
            return result[bone.name]
        if index in visiting:
            raise ValueError(f"target .crig hierarchy contains a cycle at {bone.name!r}")
        visiting.add(index)
        local = locals_by_name[bone.name]
        if bone.parent_index >= 0 and bone.parent_index in by_index:
            value = resolve(bone.parent_index) @ local
        else:
            value = local.copy()
        visiting.remove(index)
        result[bone.name] = value
        return value

    for bone in rig.bones:
        if bone.name in locals_by_name:
            resolve(bone.index)
    return result


def _valid_bind_globals(
    names: Iterable[str],
    values: Mapping[str, Any] | None,
) -> dict[str, np.ndarray]:
    result: dict[str, np.ndarray] = {}
    if not values:
        return result
    for name in names:
        raw = values.get(name)
        if raw is None:
            continue
        matrix = np.asarray(raw, dtype=float)
        if matrix.shape == (4, 4) and np.isfinite(matrix).all():
            result[name] = matrix.copy()
    return result


def _spatial_features(
    names: Iterable[str],
    parents: Mapping[str, str | None],
    globals_by_name: Mapping[str, np.ndarray],
) -> dict[str, dict[str, Any]]:
    ordered = [name for name in names if name in globals_by_name]
    if not ordered:
        return {}
    pivots = {
        name: np.asarray(globals_by_name[name][:3, 3], dtype=float)
        for name in ordered
    }
    roots = [name for name in ordered if parents.get(name) not in pivots]
    origin = (
        np.mean([pivots[name] for name in roots], axis=0)
        if roots
        else np.mean(list(pivots.values()), axis=0)
    )
    extent = max(
        (float(np.linalg.norm(value - origin)) for value in pivots.values()),
        default=0.0,
    )
    if extent <= 1.0e-12:
        stacked = np.asarray(list(pivots.values()), dtype=float)
        extent = float(np.linalg.norm(np.max(stacked, axis=0) - np.min(stacked, axis=0)))
    extent = max(extent, 1.0e-12)
    maximum_depth = max((_hierarchy_depth(name, parents) for name in ordered), default=0)
    result: dict[str, dict[str, Any]] = {}
    for name in ordered:
        parent = parents.get(name)
        vector = (
            pivots[name] - pivots[parent]
            if parent in pivots
            else pivots[name] - origin
        )
        length = float(np.linalg.norm(vector))
        direction = vector / length if length > 1.0e-12 else np.zeros(3, dtype=float)
        result[name] = {
            "normalized_pivot": (pivots[name] - origin) / extent,
            "parent_direction": direction,
            "segment_extent": length / extent,
            "radial_extent": float(np.linalg.norm(pivots[name] - origin)) / extent,
            "normalized_depth": (
                _hierarchy_depth(name, parents) / maximum_depth
                if maximum_depth
                else 0.0
            ),
        }
    return result


def _safe_descriptor_match(source_name: str, descriptor: int) -> bool:
    try:
        return dl_name_hash(source_name) == int(descriptor)
    except ValueError:
        return False


def _source_deform_lookup(
    source_deform_bones: Iterable[str] | Mapping[str, bool] | None,
    source_skin_weights: Mapping[str, float] | None,
) -> dict[str, bool]:
    if isinstance(source_deform_bones, Mapping):
        result = {str(name): bool(value) for name, value in source_deform_bones.items()}
    else:
        result = {str(name): True for name in tuple(source_deform_bones or ())}
    for name, value in dict(source_skin_weights or {}).items():
        if math.isfinite(float(value)) and float(value) > 0.0:
            result[str(name)] = True
    return result


def _mapping_candidates(
    rig: ChromeRig,
    source_names: list[str],
    source_parents: Mapping[str, str | None],
    *,
    source_bind_globals: Mapping[str, Any] | None,
    source_deform_bones: Iterable[str] | Mapping[str, bool] | None,
    source_skin_weights: Mapping[str, float] | None,
) -> tuple[dict[str, list[dict[str, Any]]], bool, str]:
    rig_parents = {
        bone.name: (
            rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None
        )
        for bone in rig.bones
    }
    source_name_set = set(source_names)
    source_children: dict[str, list[str]] = defaultdict(list)
    for name in source_names:
        parent = source_parents.get(name)
        if parent is not None:
            source_children[str(parent)].append(name)
    rig_children: dict[str, list[str]] = defaultdict(list)
    for bone in rig.bones:
        parent = rig_parents[bone.name]
        if parent is not None:
            rig_children[parent].append(bone.name)

    target_globals = _rig_bind_globals(rig)
    source_globals = _valid_bind_globals(source_names, source_bind_globals)
    target_spatial = _spatial_features(rig_parents, rig_parents, target_globals)
    source_spatial = _spatial_features(source_names, source_parents, source_globals)
    spatial_available = bool(target_spatial and source_spatial)
    spatial_note = (
        "source and target bind pivots, segment directions, extents, and chain depths were scored"
        if spatial_available
        else "source bind globals were not supplied (or were invalid); spatial scoring was omitted"
    )
    source_deform = _source_deform_lookup(source_deform_bones, source_skin_weights)
    skin = {
        str(name): max(0.0, float(value))
        for name, value in dict(source_skin_weights or {}).items()
        if math.isfinite(float(value))
    }
    maximum_skin = max(skin.values(), default=0.0)
    target_plain_by_name = {bone.name: _plain(bone.name) for bone in rig.bones}
    results: dict[str, list[dict[str, Any]]] = {}
    for bone in rig.bones:
        target_role = canonical_humanoid_role(bone.name)
        target_plain = target_plain_by_name[bone.name]
        target_side = _side(target_plain)
        target_parent_role = canonical_humanoid_role(rig_parents[bone.name] or "")
        target_child_roles = {
            role
            for child in rig_children.get(bone.name, ())
            if (role := canonical_humanoid_role(child)) is not None
        }
        target_normal = unicodedata.normalize("NFKC", bone.name).casefold()
        alias_normals = {_plain(alias) for alias in bone.aliases}
        candidates: list[dict[str, Any]] = []
        for source_name in source_names:
            components: dict[str, float] = {}
            evidence: list[str] = []
            source_normal = unicodedata.normalize("NFKC", source_name).casefold()
            source_plain = _plain(source_name)
            if _safe_descriptor_match(source_name, bone.descriptor):
                components["descriptor"] = 120.0
                evidence.append("exact descriptor")
            elif source_name == bone.name:
                components["exact_name"] = 116.0
                evidence.append("exact name")
            elif source_normal == target_normal:
                components["unicode_name"] = 112.0
                evidence.append("Unicode NFKC/casefold name")
            elif source_plain == target_plain:
                components["normalized_name"] = 105.0
                evidence.append("namespace-stripped normalized name")
            elif source_plain in alias_normals:
                components["alias"] = 96.0
                evidence.append("target .crig alias")
            else:
                ignored = {"target", "source", "bone", "joint", "jnt", "def"}
                target_tokens = set(target_plain.split("_")) - ignored
                source_tokens = set(source_plain.split("_")) - ignored
                overlap = len(target_tokens & source_tokens)
                if overlap:
                    components["name_tokens"] = min(24.0, overlap * 8.0)
                    evidence.append(f"{overlap} normalized name token(s)")

            source_role = canonical_humanoid_role(source_name)
            if target_role and source_role == target_role:
                components["semantic_role"] = 60.0
                evidence.append(f"semantic role {target_role}")
            elif target_role and source_role and source_role != target_role:
                components["semantic_role"] = -60.0
                evidence.append(f"semantic mismatch {target_role}/{source_role}")
            source_side = _side(source_plain)
            if target_side and source_side == target_side:
                components["side"] = 12.0
                evidence.append(f"side {target_side}")
            elif target_side and source_side and source_side != target_side:
                components["side"] = -55.0
                evidence.append("left/right conflict")

            source_parent = source_parents.get(source_name)
            source_parent_role = canonical_humanoid_role(source_parent or "")
            if target_parent_role and source_parent_role == target_parent_role:
                components["parent_role"] = 12.0
                evidence.append("parent semantic role")
            source_child_roles = {
                role
                for child in source_children.get(source_name, ())
                if (role := canonical_humanoid_role(child)) is not None
            }
            child_overlap = len(target_child_roles & source_child_roles)
            if child_overlap:
                components["child_roles"] = min(12.0, 4.0 * child_overlap)
                evidence.append(f"{child_overlap} child semantic role(s)")
            depth_delta = abs(
                _hierarchy_depth(bone.name, rig_parents)
                - _hierarchy_depth(source_name, source_parents)
            )
            components["chain_depth"] = max(0.0, 12.0 - 3.0 * depth_delta)
            if depth_delta <= 1:
                evidence.append("compatible chain depth")
            target_root = rig_parents[bone.name] is None
            source_root = source_parents.get(source_name) not in source_name_set
            components["root_membership"] = 10.0 if target_root == source_root else -15.0
            if target_root == source_root:
                evidence.append("root membership")

            target_helper_like = bone.helper or any(
                token in target_plain for token in _HELPER_TOKENS
            )
            source_helper_like = any(token in source_plain for token in _HELPER_TOKENS)
            if source_name in source_deform:
                source_helper_like = not source_deform[source_name]
            components["deform_helper_class"] = (
                10.0 if target_helper_like == source_helper_like else -22.0
            )
            if target_helper_like == source_helper_like:
                evidence.append("deform/helper class")
            if skin and source_name in skin:
                normalized_weight = skin[source_name] / maximum_skin if maximum_skin else 0.0
                if bone.deform and not bone.helper:
                    components["skin_ownership"] = 6.0 + 6.0 * math.sqrt(normalized_weight)
                    evidence.append("positive source skin ownership")
                elif bone.helper:
                    components["skin_ownership"] = -12.0
                    evidence.append("source skin ownership conflicts with target helper")

            target_position = target_spatial.get(bone.name)
            source_position = source_spatial.get(source_name)
            if target_position is not None and source_position is not None:
                pivot_distance = float(
                    np.linalg.norm(
                        target_position["normalized_pivot"]
                        - source_position["normalized_pivot"]
                    )
                )
                components["bind_pivot"] = max(-8.0, 24.0 * (1.0 - pivot_distance / 1.5))
                direction_dot = float(
                    np.clip(
                        np.dot(
                            target_position["parent_direction"],
                            source_position["parent_direction"],
                        ),
                        -1.0,
                        1.0,
                    )
                )
                components["parent_child_direction"] = 15.0 * direction_dot
                target_extent = float(target_position["segment_extent"])
                source_extent = float(source_position["segment_extent"])
                if target_extent <= 1.0e-9 and source_extent <= 1.0e-9:
                    extent_similarity = 1.0
                elif target_extent <= 1.0e-9 or source_extent <= 1.0e-9:
                    extent_similarity = 0.0
                else:
                    extent_similarity = math.exp(
                        -abs(math.log(target_extent / source_extent))
                    )
                components["segment_extent"] = 14.0 * extent_similarity
                radial_delta = abs(
                    float(target_position["radial_extent"])
                    - float(source_position["radial_extent"])
                )
                components["hierarchy_extent"] = max(0.0, 8.0 * (1.0 - radial_delta))
                normalized_depth_delta = abs(
                    float(target_position["normalized_depth"])
                    - float(source_position["normalized_depth"])
                )
                components["normalized_chain_depth"] = max(
                    0.0, 8.0 * (1.0 - normalized_depth_delta)
                )
                evidence.extend(
                    (
                        "normalized bind pivot",
                        "parent-child bind direction",
                        "relative segment extent",
                    )
                )
            score = float(sum(components.values()))
            candidates.append(
                {
                    "source_fbx_bone": source_name,
                    "score": round(score, 6),
                    "evidence": evidence,
                    "components": {
                        key: round(value, 6) for key, value in sorted(components.items())
                    },
                }
            )
        candidates.sort(
            key=lambda row: (
                -float(row["score"]),
                unicodedata.normalize("NFKC", str(row["source_fbx_bone"])).casefold(),
                str(row["source_fbx_bone"]),
            )
        )
        results[bone.name] = candidates
    return results, spatial_available, spatial_note


def _maximum_weight_assignment(
    target_names: list[str],
    source_names: list[str],
    candidates_by_target: Mapping[str, list[dict[str, Any]]],
) -> dict[str, str]:
    """Solve one deterministic maximum-weight target<-source assignment.

    Each target gets a private dummy option at the review floor, so weak
    hierarchy-only coincidences remain unmapped. Source names are sorted before
    the Hungarian solve and a tiny target-priority tie break makes results
    independent of input iteration order.
    """

    if not target_names or not source_names:
        return {}
    # Exact descriptor rows are collision-checked by ChromeRig validation and
    # cannot benefit from fuzzy competition. Locking them both protects exact
    # tracks from aggregate-score tradeoffs and keeps same-rig/superset mapping
    # linear before the global solve handles the genuinely ambiguous remainder.
    descriptor_targets_by_source: dict[str, list[str]] = defaultdict(list)
    descriptor_source_by_target: dict[str, str] = {}
    for target_name in target_names:
        matches = [
            str(row["source_fbx_bone"])
            for row in candidates_by_target.get(target_name, ())
            if float(dict(row.get("components", {}) or {}).get("descriptor", 0.0)) > 0.0
        ]
        if len(matches) == 1:
            descriptor_source_by_target[target_name] = matches[0]
            descriptor_targets_by_source[matches[0]].append(target_name)
    locked = {
        target_name: source_name
        for target_name, source_name in descriptor_source_by_target.items()
        if len(descriptor_targets_by_source[source_name]) == 1
    }
    locked_sources = set(locked.values())
    target_names = [name for name in target_names if name not in locked]
    source_names = [name for name in source_names if name not in locked_sources]
    if not target_names or not source_names:
        return locked
    ordered_sources = sorted(
        dict.fromkeys(source_names),
        key=lambda value: (unicodedata.normalize("NFKC", value).casefold(), value),
    )
    source_index = {name: index for index, name in enumerate(ordered_sources)}
    real_count = len(ordered_sources)
    row_count = len(target_names)
    column_count = real_count + row_count
    weights: list[list[float]] = []
    raw_by_target: dict[str, dict[str, float]] = {}
    for target_index, target_name in enumerate(target_names):
        raw = {
            str(row["source_fbx_bone"]): float(row["score"])
            for row in candidates_by_target.get(target_name, ())
        }
        raw_by_target[target_name] = raw
        row = [_ASSIGNMENT_FLOOR] * column_count
        for source_name, score in raw.items():
            if source_name not in source_index:
                continue
            # Earlier target tracks win exact ties; lexicographically earlier
            # source names win ties within that target.
            source_rank = source_index[source_name]
            tie_break = 1.0e-7 * (row_count - target_index) * (real_count - source_rank)
            row[source_rank] = score + tie_break
        weights.append(row)

    # Hungarian algorithm for a rectangular minimization matrix (rows <= cols).
    # Negating the weights produces the required maximum-weight assignment.
    u = [0.0] * (row_count + 1)
    v = [0.0] * (column_count + 1)
    p = [0] * (column_count + 1)
    way = [0] * (column_count + 1)
    for row_index in range(1, row_count + 1):
        p[0] = row_index
        minimum = [float("inf")] * (column_count + 1)
        used = [False] * (column_count + 1)
        column0 = 0
        while True:
            used[column0] = True
            active_row = p[column0]
            delta = float("inf")
            column1 = 0
            for column in range(1, column_count + 1):
                if used[column]:
                    continue
                current = -weights[active_row - 1][column - 1] - u[active_row] - v[column]
                if current < minimum[column]:
                    minimum[column] = current
                    way[column] = column0
                if minimum[column] < delta:
                    delta = minimum[column]
                    column1 = column
            for column in range(column_count + 1):
                if used[column]:
                    u[p[column]] += delta
                    v[column] -= delta
                else:
                    minimum[column] -= delta
            column0 = column1
            if p[column0] == 0:
                break
        while True:
            column1 = way[column0]
            p[column0] = p[column1]
            column0 = column1
            if column0 == 0:
                break

    selected_column = [-1] * row_count
    for column in range(1, column_count + 1):
        if p[column]:
            selected_column[p[column] - 1] = column - 1
    result: dict[str, str] = dict(locked)
    for target_index, column in enumerate(selected_column):
        if 0 <= column < real_count:
            target_name = target_names[target_index]
            source_name = ordered_sources[column]
            if raw_by_target[target_name].get(source_name, -float("inf")) > _ASSIGNMENT_FLOOR:
                result[target_name] = source_name
    return result


def _candidate_method(row: Mapping[str, Any]) -> str:
    components = dict(row.get("components", {}) or {})
    if components.get("descriptor", 0.0) > 0.0:
        return "descriptor"
    if components.get("exact_name", 0.0) > 0.0:
        return "exact"
    if any(components.get(name, 0.0) > 0.0 for name in ("unicode_name", "normalized_name")):
        return "normalized"
    if components.get("alias", 0.0) > 0.0:
        return "alias"
    if components.get("semantic_role", 0.0) > 0.0:
        return "semantic"
    if components.get("bind_pivot", 0.0) > 0.0:
        return "spatial_bind"
    return "hierarchy"


def _automatic_mapping_evidence(
    rig: ChromeRig,
    source_names: list[str],
    source_parents: Mapping[str, str | None],
    profile: GenericBoneMap,
    *,
    candidates_by_target: Mapping[str, list[dict[str, Any]]],
    spatial_evidence_available: bool,
    spatial_evidence_note: str,
) -> list[dict[str, Any]]:
    """Attach deterministic candidate/runner-up evidence for map review.

    The report reuses the exact candidate matrix consumed by global assignment,
    so review displays cannot disagree with the actual automatic selection.
    Low-margin rows remain unreviewed and therefore build-blocking.
    """
    del source_names, source_parents
    selected_by_target = {row.target_rig_bone: row for row in profile.base_pairs}
    rows: list[dict[str, Any]] = []
    for bone in rig.bones:
        candidates = list(candidates_by_target.get(bone.name, ()))
        top = candidates[0] if candidates else None
        runner = candidates[1] if len(candidates) > 1 else None
        margin = (
            float(top["score"]) - float(runner["score"])
            if top is not None and runner is not None
            else float(top["score"]) if top is not None else 0.0
        )
        pair = selected_by_target.get(bone.name)
        selected_is_top = bool(
            pair is not None
            and top is not None
            and pair.source_fbx_bone == top["source_fbx_bone"]
        )
        review_required = bool(
            pair is None
            or pair.review_state == "automatic_unreviewed"
            or not selected_is_top
            or (top is not None and float(top["score"]) < 55.0)
            or margin < _REVIEW_MARGIN
        )
        report = {
            "target_rig_bone": bone.name,
            "selected_source_fbx_bone": pair.source_fbx_bone if pair else "",
            "top_candidate": top,
            "runner_up_candidate": runner,
            "score_margin": round(margin, 6),
            "selected_is_top_candidate": selected_is_top,
            "review_required": review_required,
            "spatial_evidence_available": spatial_evidence_available,
            "spatial_evidence_note": spatial_evidence_note,
            "assignment_policy": "deterministic_global_one_to_one",
            "assignment_floor": _ASSIGNMENT_FLOOR,
            "review_margin": _REVIEW_MARGIN,
        }
        if pair is not None:
            pair.extensions = dict(pair.extensions)
            pair.extensions["automatic_evidence"] = report
            if review_required and pair.method != "manual" and pair.review_state not in {
                "manually_reviewed",
                "imported_reviewed",
            }:
                pair.review_state = "automatic_unreviewed"
        rows.append(report)
    return rows


def auto_map_crig_to_fbx(
    rig: ChromeRig,
    target_names: Iterable[str],
    target_parents: Mapping[str, str | None],
    *,
    source_bind_globals: Mapping[str, Any] | None = None,
    source_deform_bones: Iterable[str] | Mapping[str, bool] | None = None,
    source_skin_weights: Mapping[str, float] | None = None,
) -> GenericBoneMap:
    """Auto-map a target `.crig` skeleton to a source animation FBX skeleton."""

    names = list(dict.fromkeys(str(name) for name in target_names))
    profile = auto_map_skeletons(
        rig,
        names,
        target_parents,
        target_skeleton_hash=skeleton_signature(
            (name, target_parents.get(name)) for name in sorted(names)
        ),
    )
    profile.pairs = []
    source_name_set = set(names)
    target_name_set = {bone.name for bone in rig.bones}
    required_names = {
        bone.name for bone in rig.bones if bone.deform and not bone.helper
    }

    def nearest_target_ancestor(name: str) -> str | None:
        seen: set[str] = set()
        cursor = target_parents.get(name)
        while cursor is not None and cursor not in seen:
            if cursor in target_name_set:
                return cursor
            seen.add(cursor)
            cursor = target_parents.get(cursor)
        return None

    hierarchy_mismatch = False
    for bone in rig.bones:
        if bone.name not in source_name_set:
            continue
        expected_parent = (
            rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None
        )
        if nearest_target_ancestor(bone.name) != expected_parent:
            hierarchy_mismatch = True
            break
    identity = not (required_names - source_name_set) and not hierarchy_mismatch
    set_mapping_profile_origin(
        profile, "automatic_identity" if identity else "automatic_repair"
    )

    candidates_by_target, spatial_available, spatial_note = _mapping_candidates(
        rig,
        names,
        target_parents,
        source_bind_globals=source_bind_globals,
        source_deform_bones=source_deform_bones,
        source_skin_weights=source_skin_weights,
    )
    selected = _maximum_weight_assignment(
        [bone.name for bone in rig.bones], names, candidates_by_target
    )
    by_candidate = {
        target: {str(row["source_fbx_bone"]): row for row in rows}
        for target, rows in candidates_by_target.items()
    }
    for bone in rig.bones:
        source_name = selected.get(bone.name)
        if source_name is None:
            continue
        candidate = by_candidate[bone.name][source_name]
        method = _candidate_method(candidate)
        exact_identity_row = identity and method in {"descriptor", "exact", "normalized"}
        profile.pairs.append(
            BoneMapPair(
                target_rig_descriptor=bone.descriptor,
                target_rig_bone=bone.name,
                source_fbx_bone=source_name,
                confidence=max(0.0, min(1.0, float(candidate["score"]) / 160.0)),
                method=method,
                review_state=(
                    "automatic_accepted"
                    if exact_identity_row
                    else "automatic_unreviewed"
                ),
            )
        )

    profile.pairs.sort(key=lambda row: next(
        (bone.index for bone in rig.bones if bone.name == row.source_bone), 1_000_000
    ))
    rig_matches = scan_humanoid_bones(bone.name for bone in rig.bones)
    source_matches = scan_humanoid_bones(names, target_parents)
    mapped_roles = {
        rig_matches[row.source_bone].role
        for row in profile.pairs
        if row.source_bone in rig_matches
    }
    available_roles = set(match.role for match in source_matches.values()).intersection(
        match.role for match in rig_matches.values()
    )
    profile.extensions["auto_map_summary"] = {
        "target_bone_count": len(rig.bones),
        "source_bone_count": len(names),
        "mapped_bone_count": len(profile.pairs),
        "mapped_anatomy_roles": sorted(mapped_roles),
        "unmapped_available_anatomy_roles": sorted(available_roles - mapped_roles),
        "assignment_policy": "deterministic_global_one_to_one",
        "spatial_evidence_available": spatial_available,
        "skin_evidence_available": bool(source_skin_weights),
    }
    profile.extensions["automatic_mapping_evidence_v2"] = _automatic_mapping_evidence(
        rig,
        names,
        target_parents,
        profile,
        candidates_by_target=candidates_by_target,
        spatial_evidence_available=spatial_available,
        spatial_evidence_note=spatial_note,
    )
    return profile


def mapping_rows_for_ui(
    rig: ChromeRig,
    target_names: Iterable[str],
    target_parents: Mapping[str, str | None],
    profile: GenericBoneMap | None = None,
    *,
    source_bind_globals: Mapping[str, Any] | None = None,
    source_deform_bones: Iterable[str] | Mapping[str, bool] | None = None,
    source_skin_weights: Mapping[str, float] | None = None,
) -> tuple[GenericBoneMap, list[dict[str, Any]]]:
    current = profile or auto_map_crig_to_fbx(
        rig,
        target_names,
        target_parents,
        source_bind_globals=source_bind_globals,
        source_deform_bones=source_deform_bones,
        source_skin_weights=source_skin_weights,
    )
    by_target = {row.source_bone: row for row in current.pairs}
    rows = []
    for bone in rig.bones:
        pair = by_target.get(bone.name)
        evidence = (
            pair.extensions.get("automatic_evidence", {})
            if pair is not None
            else {}
        )
        top_candidate = evidence.get("top_candidate") or {}
        runner_up = evidence.get("runner_up_candidate") or {}
        rows.append(
            {
                "target_bone": bone.name,
                "target_parent": (
                    rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None
                ),
                "source_bone": pair.target_bone if pair else "",
                "confidence": pair.confidence if pair else 0.0,
                "method": pair.method if pair else "unmapped",
                "role": (
                    "helper" if bone.helper else canonical_humanoid_role(bone.name) or ""
                ),
                "mapping_kind": pair.mapping_kind if pair else "bone",
                "transfer_policy": pair.transfer_policy if pair else "default",
                "component_policy": (
                    pair.component_policy if pair else "full_transform"
                ),
                "target_helper": bool(bone.helper),
                "review_state": (
                    pair.review_state if pair else "intentionally_unmapped"
                ),
                "review_required": bool(evidence.get("review_required", False)),
                "top_candidate": str(top_candidate.get("source_fbx_bone", "")),
                "runner_up": str(runner_up.get("source_fbx_bone", "")),
                "score_margin": float(evidence.get("score_margin", 0.0)),
                "mapping_evidence": evidence,
            }
        )
    return current, rows


__all__ = [
    "HumanoidBoneMatch",
    "auto_map_crig_to_fbx",
    "canonical_humanoid_role",
    "match_humanoid_bone",
    "mapping_rows_for_ui",
    "scan_humanoid_bones",
    "source_mapping_evidence",
]
