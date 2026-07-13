from __future__ import annotations

"""Shared semantic bone mapping for model and animation workspaces.

The mapper is deliberately conservative: exact/descriptor matches win, then a
small anatomy vocabulary covers Mixamo, Character Creator/CC_Base, Blender
suffixes, and the generic Wada armature names seen in the model-import corpus.
Every suggestion remains editable in the GUI.
"""

from collections import defaultdict
from typing import Any, Iterable, Mapping
import re

from .bone_maps import BoneMapPair, GenericBoneMap, auto_map_skeletons, skeleton_signature
from .chrome_rig import ChromeRig


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


def canonical_humanoid_role(value: str) -> str | None:
    """Return a stable anatomy role or ``None`` for helpers/twists/accessories."""

    name = _plain(value)
    compact = name.replace("_", "")
    if not name or any(
        token in name
        for token in (
            "ik",
            "pole",
            "sharebone",
            "twist",
            "breast",
            "ribs",
            "facial",
            "tongue",
            "teeth",
            "jaw",
            "eye",
            "hair",
            "pony",
            "tassle",
            "tassel",
            "hat",
            "skirt",
            "bow",
            "end_end",
        )
    ):
        return None
    if name.endswith("_end") or compact.endswith("end") or compact.endswith("topend"):
        return None

    side = _side(name)

    # Fingers before generic hand matching.
    finger_aliases = {
        "thumb": "thumb",
        "index": "index",
        "pointer": "index",
        "middle": "middle",
        "ring": "ring",
        "pinky": "pinky",
        "pinkie": "pinky",
    }
    for token, finger in finger_aliases.items():
        if token in compact:
            if not side:
                side = "l" if "left" in compact else "r" if "right" in compact else ""
            if not side:
                return None
            numbers = [int(row) for row in re.findall(r"([1-4])", name)]
            segment = numbers[-1] if numbers else None
            # Wada uses b/m/t prefixes instead of numeric phalanges.
            if segment is None:
                leading = compact[:1]
                segment = {"b": 1, "m": 2, "t": 3}.get(leading, 1)
            segment = min(3, max(1, int(segment)))
            return f"{side}_{finger}_{segment}"

    exact = {
        "hips": "pelvis",
        "pelvis": "pelvis",
        "ccbasepelvis": "pelvis",
        "backbottom": "pelvis",
        "root": "root",
        "control": "root",
        "spine": "spine_1",
        "spine01": "spine_1",
        "spine1": "spine_1",
        "backmid": "spine_1",
        "spine02": "spine_2",
        "spine2": "spine_2",
        "backtop": "spine_2",
        "spine03": "spine_3",
        "spine3": "spine_3",
        "upperchest": "spine_3",
        "chest": "spine_3",
        "neck": "neck_1",
        "neck01": "neck_1",
        "neck1": "neck_1",
        "head": "head",
    }
    if compact in exact:
        return exact[compact]

    if side:
        if any(token in compact for token in ("clavicle", "shoulder")):
            return f"{side}_clavicle"
        if any(token in compact for token in ("upperarm", "toparm", "armtop")):
            return f"{side}_upperarm"
        if any(token in compact for token in ("forearm", "lowerarm", "bottomarm", "armbottom")):
            return f"{side}_forearm"
        if compact in {f"{side}hand", f"hand{side}", f"wrist{side}", f"{side}wrist"} or (
            "hand" in compact and not any(token in compact for token in finger_aliases)
        ) or "wrist" in compact:
            return f"{side}_hand"
        if any(token in compact for token in ("upleg", "upperleg", "thigh", "legtop")):
            return f"{side}_thigh"
        if any(token in compact for token in ("lowerleg", "calf", "legbottom")):
            return f"{side}_calf"
        if "foot" in compact:
            return f"{side}_foot"
        if any(token in compact for token in ("toebase", "toe", "ball")):
            return f"{side}_toe"
    return None


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
    mapped_rig_bones = {row.source_bone for row in profile.pairs}
    used_source = {row.target_bone for row in profile.pairs}

    source_by_role: dict[str, list[str]] = defaultdict(list)
    for source_name in names:
        role = canonical_humanoid_role(source_name)
        if role:
            source_by_role[role].append(source_name)

    rig_by_role: dict[str, list[Any]] = defaultdict(list)
    for bone in rig.bones:
        role = canonical_humanoid_role(bone.name)
        if role:
            rig_by_role[role].append(bone)

    # Root is allowed to remain fixed when pelvis/hips already owns the only
    # source root; this is safer than assigning the same source bone twice.
    for role, rig_bones in sorted(rig_by_role.items()):
        available_sources = [row for row in source_by_role.get(role, ()) if row not in used_source]
        if not available_sources:
            continue
        available_sources.sort(key=lambda value: _role_priority(value, role))
        candidates = [bone for bone in rig_bones if bone.name not in mapped_rig_bones]
        candidates.sort(key=lambda bone: _role_priority(bone.name, role))
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
                "role": canonical_humanoid_role(bone.name) or "",
            }
        )
    return current, rows


__all__ = [
    "auto_map_crig_to_fbx",
    "canonical_humanoid_role",
    "mapping_rows_for_ui",
]
