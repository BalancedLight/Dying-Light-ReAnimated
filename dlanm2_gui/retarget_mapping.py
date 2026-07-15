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
import re

from .bone_maps import (
    BoneMapPair,
    GenericBoneMap,
    auto_map_skeletons,
    set_mapping_profile_origin,
    skeleton_signature,
)
from .chrome_rig import ChromeRig
from .root_mapping import resolve_source_root


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


def _role_priority(name: str, role: str) -> tuple[int, int, str]:
    plain = _plain(name)
    penalty = 0
    if any(token in plain for token in ("twist", "share", "helper", "end", "ik")):
        penalty += 100
    if role == "pelvis":
        if plain in {"pelvis", "hips", "back_bottom", "backbottom"}:
            penalty -= 20
        if "cc_base_pelvis" in plain:
            penalty -= 10
    if role == "root" and plain in {"root", "control"}:
        penalty -= 20
    # Shallower/shorter canonical names win over decorated duplicates.
    return penalty, len(plain), plain


def auto_map_crig_to_fbx(
    rig: ChromeRig,
    target_names: Iterable[str],
    target_parents: Mapping[str, str | None],
) -> GenericBoneMap:
    """Auto-map a target `.crig` skeleton to a source animation FBX skeleton."""

    names = list(target_names)
    profile = auto_map_skeletons(
        rig,
        names,
        target_parents,
        target_skeleton_hash=skeleton_signature(
            (name, target_parents.get(name)) for name in sorted(names)
        ),
    )
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
    set_mapping_profile_origin(
        profile,
        "automatic_identity"
        if not (required_names - source_name_set) and not hierarchy_mismatch
        else "automatic_repair",
    )

    # The generic fuzzy matcher is useful for props and identical skeletons,
    # but a cross-rig humanoid suggestion must never retain a confident-looking
    # pair whose anatomy roles disagree (for example CC Spine01 -> Mixamo Spine).
    rig_matches = scan_humanoid_bones(bone.name for bone in rig.bones)
    source_matches = scan_humanoid_bones(names, target_parents)
    profile.pairs = [
        row
        for row in profile.pairs
        if not (
            row.source_bone in rig_matches
            and row.target_bone in source_matches
            and rig_matches[row.source_bone].role != source_matches[row.target_bone].role
        )
    ]

    # Root-motion extraction and pelvis pose are separate jobs.  A non-deforming
    # wrapper such as RL_BoneRoot must not consume Mixamo Hips as its pose row;
    # doing so rotates the whole model while the real CC_Base_Hip stays at bind.
    # Leave the source root available for the semantic pelvis pass below.  The
    # root-motion resolver samples it independently and writes displacement only
    # to the user-selected Bip01/root track.
    try:
        source_roots = [
            name for name in names if target_parents.get(name) not in set(names)
        ]
        source_root = (
            source_roots[0]
            if len(source_roots) == 1
            else resolve_source_root(names, target_parents)[0]
        )
        target_root = rig.bones[rig.root_index]
        same_named_root = _plain(target_root.name) == _plain(source_root)
        if target_root.helper and not target_root.deform and not same_named_root:
            profile.pairs = [
                row
                for row in profile.pairs
                if row.source_bone != target_root.name
                and row.target_bone != source_root
            ]
    except (IndexError, ValueError):
        # A malformed/empty hierarchy is reported by normal FBX/.crig
        # validation. Keeping auto-map best-effort lets the editor still open.
        pass

    mapped_rig_bones = {row.source_bone for row in profile.pairs}
    used_source = {row.target_bone for row in profile.pairs}

    source_by_role: dict[str, list[str]] = defaultdict(list)
    for source_name, match in source_matches.items():
        source_by_role[match.role].append(source_name)

    rig_by_role: dict[str, list[Any]] = defaultdict(list)
    for bone in rig.bones:
        if bone.name in rig_matches:
            rig_by_role[rig_matches[bone.name].role].append(bone)

    # Root is allowed to remain fixed when pelvis/hips already owns the only
    # source root; this is safer than assigning the same source bone twice.
    for role, rig_bones in sorted(rig_by_role.items()):
        available_sources = [row for row in source_by_role.get(role, ()) if row not in used_source]
        if not available_sources:
            continue
        available_sources.sort(key=lambda value: _role_priority(value, role))
        candidates = [bone for bone in rig_bones if bone.name not in mapped_rig_bones]
        candidates.sort(
            key=lambda bone: (
                0 if bone.deform and not bone.helper else 1,
                *_role_priority(bone.name, role),
            )
        )
        for bone, source_name in zip(candidates, available_sources):
            profile.pairs.append(
                BoneMapPair(
                    source_descriptor=bone.descriptor,
                    source_bone=bone.name,
                    target_bone=source_name,
                    confidence=0.94,
                    method=f"semantic:{role}",
                )
            )
            mapped_rig_bones.add(bone.name)
            used_source.add(source_name)

    profile.pairs.sort(key=lambda row: next(
        (bone.index for bone in rig.bones if bone.name == row.source_bone), 1_000_000
    ))
    mapped_roles = {
        rig_matches[row.source_bone].role
        for row in profile.pairs
        if row.source_bone in rig_matches
    }
    available_roles = set(source_by_role).intersection(
        match.role for match in rig_matches.values()
    )
    profile.extensions["auto_map_summary"] = {
        "target_bone_count": len(rig.bones),
        "source_bone_count": len(names),
        "mapped_bone_count": len(profile.pairs),
        "mapped_anatomy_roles": sorted(mapped_roles),
        "unmapped_available_anatomy_roles": sorted(available_roles - mapped_roles),
    }
    return profile


def mapping_rows_for_ui(
    rig: ChromeRig,
    target_names: Iterable[str],
    target_parents: Mapping[str, str | None],
    profile: GenericBoneMap | None = None,
) -> tuple[GenericBoneMap, list[dict[str, Any]]]:
    current = profile or auto_map_crig_to_fbx(rig, target_names, target_parents)
    by_target = {row.source_bone: row for row in current.pairs}
    rows = []
    for bone in rig.bones:
        pair = by_target.get(bone.name)
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
]
