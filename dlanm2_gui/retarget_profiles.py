"""Humanoid source-bone mapping profiles.

Mapping profiles alias arbitrary FBX bone names onto the canonical semantic
skeleton consumed by the retargeter.  The serialized format is intentionally
small and remains compatible with profiles created by earlier releases.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
import hashlib
import json
from pathlib import Path
import re
from typing import Any, Iterable, Mapping
import uuid


PROFILE_FORMAT = "dl-reanimated-retarget-profile"
PROFILE_SCHEMA_VERSION = 1


@dataclass(frozen=True, slots=True)
class HumanoidRole:
    role_id: str
    label: str
    canonical_source_name: str
    group: str
    required: bool = True
    target_name: str = ""
    parent_role: str | None = None
    aliases: tuple[str, ...] = ()


def _role(
    role_id: str,
    label: str,
    canonical: str,
    group: str,
    target: str,
    *,
    required: bool = True,
    parent: str | None = None,
    aliases: tuple[str, ...] = (),
) -> HumanoidRole:
    return HumanoidRole(role_id, label, canonical, group, required, target, parent, aliases)


_BASE_ROLES = [
    # A scene root is not a hips transform. Treating "root" as a hips alias
    # caused rigs with both root and pelvis to bind the body to the wrong node.
    _role("hips", "Hips", "mixamorig:Hips", "Body", "bip01", aliases=("hips", "pelvis", "hip")),
    _role("pelvis", "Pelvis", "mixamorig:Hips", "Body", "pelvis", required=False, parent="hips", aliases=("pelvis", "hips")),
    _role("spine", "Spine", "mixamorig:Spine", "Body", "hspine", parent="hips", aliases=("spine0", "lower_spine")),
    _role("chest", "Chest", "mixamorig:Spine1", "Body", "spine1", parent="spine", aliases=("spine1", "chest", "mid_spine")),
    _role("upper_chest", "Upper Chest", "mixamorig:Spine2", "Body", "spine3", parent="chest", aliases=("spine2", "upperchest", "upper_chest")),
    _role("neck", "Neck", "mixamorig:Neck", "Body", "neck", parent="upper_chest"),
    _role("head", "Head", "mixamorig:Head", "Body", "head", parent="neck"),
    _role("head_end", "Head End", "mixamorig:HeadTop_End", "Body", "head_end", required=False, parent="head", aliases=("headtop_end", "head_end", "head_tip")),
    _role("left_shoulder", "Left Shoulder", "mixamorig:LeftShoulder", "Left Arm", "l_clavicle", parent="upper_chest", aliases=("clavicle_l", "shoulder_l")),
    _role("left_upper_arm", "Left Upper Arm", "mixamorig:LeftArm", "Left Arm", "l_upperarm", parent="left_shoulder", aliases=("upperarm_l", "upper_arm_l", "arm_l")),
    _role("left_lower_arm", "Left Lower Arm", "mixamorig:LeftForeArm", "Left Arm", "l_forearm", parent="left_upper_arm", aliases=("forearm_l", "lowerarm_l", "lower_arm_l")),
    _role("left_hand", "Left Hand", "mixamorig:LeftHand", "Left Arm", "l_hand", parent="left_lower_arm", aliases=("hand_l",)),
    _role("right_shoulder", "Right Shoulder", "mixamorig:RightShoulder", "Right Arm", "r_clavicle", parent="upper_chest", aliases=("clavicle_r", "shoulder_r")),
    _role("right_upper_arm", "Right Upper Arm", "mixamorig:RightArm", "Right Arm", "r_upperarm", parent="right_shoulder", aliases=("upperarm_r", "upper_arm_r", "arm_r")),
    _role("right_lower_arm", "Right Lower Arm", "mixamorig:RightForeArm", "Right Arm", "r_forearm", parent="right_upper_arm", aliases=("forearm_r", "lowerarm_r", "lower_arm_r")),
    _role("right_hand", "Right Hand", "mixamorig:RightHand", "Right Arm", "r_hand", parent="right_lower_arm", aliases=("hand_r",)),
    _role("left_upper_leg", "Left Upper Leg", "mixamorig:LeftUpLeg", "Left Leg", "l_thigh", parent="hips", aliases=("thigh_l", "upperleg_l", "upper_leg_l")),
    _role("left_lower_leg", "Left Lower Leg", "mixamorig:LeftLeg", "Left Leg", "l_calf", parent="left_upper_leg", aliases=("calf_l", "lowerleg_l", "lower_leg_l")),
    _role("left_foot", "Left Foot", "mixamorig:LeftFoot", "Left Leg", "l_foot", parent="left_lower_leg", aliases=("foot_l", "ankle_l")),
    _role("left_toes", "Left Toes", "mixamorig:LeftToeBase", "Left Leg", "l_toebase", required=False, parent="left_foot", aliases=("toes_l", "toe_l", "toe_base_l")),
    _role("right_upper_leg", "Right Upper Leg", "mixamorig:RightUpLeg", "Right Leg", "r_thigh", parent="hips", aliases=("thigh_r", "upperleg_r", "upper_leg_r")),
    _role("right_lower_leg", "Right Lower Leg", "mixamorig:RightLeg", "Right Leg", "r_calf", parent="right_upper_leg", aliases=("calf_r", "lowerleg_r", "lower_leg_r")),
    _role("right_foot", "Right Foot", "mixamorig:RightFoot", "Right Leg", "r_foot", parent="right_lower_leg", aliases=("foot_r", "ankle_r")),
    _role("right_toes", "Right Toes", "mixamorig:RightToeBase", "Right Leg", "r_toebase", required=False, parent="right_foot", aliases=("toes_r", "toe_r", "toe_base_r")),
]


def _finger_roles() -> list[HumanoidRole]:
    rows: list[HumanoidRole] = []
    digit_offsets = {"thumb": 0, "index": 1, "middle": 2, "ring": 3, "pinky": 4}
    for side, source_side, hand_role, group, target_side in (
        ("left", "Left", "left_hand", "Left Fingers", "l"),
        ("right", "Right", "right_hand", "Right Fingers", "r"),
    ):
        for digit_id, digit_label in (("thumb", "Thumb"), ("index", "Index"), ("middle", "Middle"), ("ring", "Ring"), ("pinky", "Pinky")):
            previous = hand_role
            for index in range(1, 5):
                role_id = f"{side}_{digit_id}_{index}"
                dl_segment = index if digit_id == "thumb" else index - 1
                dl_finger = f"{target_side}_finger{digit_offsets[digit_id]}{dl_segment}"
                rows.append(_role(
                    role_id,
                    f"{side.title()} {digit_label} {index}",
                    f"mixamorig:{source_side}Hand{digit_label}{index}",
                    group,
                    f"{target_side}_{digit_id}{index}",
                    required=False,
                    parent=previous,
                    aliases=(
                        dl_finger,
                        f"{digit_id}{index}_{target_side}",
                        f"hand_{digit_id}{index}_{target_side}",
                        f"finger_{digit_id}_{index}_{target_side}",
                    ),
                ))
                previous = role_id
    return rows


HUMANOID_ROLES = tuple(_BASE_ROLES + _finger_roles())
ROLE_BY_ID = {role.role_id: role for role in HUMANOID_ROLES}
CANONICAL_BY_ROLE = {role.role_id: role.canonical_source_name for role in HUMANOID_ROLES}


def normalize_bone_name(value: str) -> str:
    return re.sub(r"_+", "_", re.sub(r"[^a-z0-9]+", "_", value.rsplit(":", 1)[-1].lower()).strip("_"))


@dataclass(slots=True)
class SourceBoneMappingProfile:
    profile_id: str
    name: str
    source_skeleton_hash: str
    role_to_bone: dict[str, str] = field(default_factory=dict)
    confidence_by_role: dict[str, float] = field(default_factory=dict)
    method_by_role: dict[str, str] = field(default_factory=dict)
    ignored_bones: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    extensions: dict[str, Any] = field(default_factory=dict)
    schema_version: int = PROFILE_SCHEMA_VERSION
    format: str = PROFILE_FORMAT

    @classmethod
    def empty(cls, source_bones: Iterable[str], *, name: str = "Custom Humanoid Mapping", parents: Mapping[str, str | None] | None = None) -> "SourceBoneMappingProfile":
        bones = tuple(str(value) for value in source_bones)
        return cls(str(uuid.uuid4()), name, source_skeleton_hash(bones, parents=parents), ignored_bones=list(bones))

    def canonical_aliases(self) -> dict[str, str]:
        return {CANONICAL_BY_ROLE[key]: value for key, value in self.role_to_bone.items() if key in CANONICAL_BY_ROLE and value}

    def mapped_bone(self, role_id: str) -> str | None:
        return self.role_to_bone.get(role_id)

    def set_mapping(self, role_id: str, bone_name: str | None, *, confidence: float = 1.0, method: str = "manual") -> None:
        if role_id not in ROLE_BY_ID:
            raise KeyError(f"unknown humanoid role: {role_id}")
        if not bone_name:
            self.role_to_bone.pop(role_id, None)
            self.confidence_by_role.pop(role_id, None)
            self.method_by_role.pop(role_id, None)
            return
        self.role_to_bone[role_id] = str(bone_name)
        self.confidence_by_role[role_id] = float(confidence)
        self.method_by_role[role_id] = str(method)

    def validate(self, source_bones: Iterable[str] | None = None) -> list[str]:
        source_set = set(source_bones or ())
        errors: list[str] = []
        used: dict[str, str] = {}
        for role in HUMANOID_ROLES:
            bone = self.role_to_bone.get(role.role_id)
            if role.required and not bone:
                errors.append(f"Required role is not mapped: {role.label}")
            if bone and source_set and bone not in source_set:
                errors.append(f"Mapped bone does not exist: {role.label} -> {bone}")
            if bone and bone in used and used[bone] != role.role_id:
                errors.append(f"Source bone is used by more than one role: {bone} ({ROLE_BY_ID[used[bone]].label}, {role.label})")
            if bone:
                used[bone] = role.role_id
        return errors

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "SourceBoneMappingProfile":
        format_name = str(payload.get("format", PROFILE_FORMAT))
        if format_name != PROFILE_FORMAT:
            raise ValueError(f"unsupported mapping profile format: {format_name}")
        version = int(payload.get("schema_version", 0))
        if version > PROFILE_SCHEMA_VERSION:
            raise ValueError(f"mapping profile schema {version} is newer than supported schema {PROFILE_SCHEMA_VERSION}")
        allowed = set(cls.__dataclass_fields__)
        row = {key: value for key, value in payload.items() if key in allowed}
        unknown = {key: value for key, value in payload.items() if key not in allowed}
        extensions = dict(row.get("extensions", {}))
        if unknown:
            extensions.setdefault("unknown_fields", {}).update(unknown)
        row["extensions"] = extensions
        row.setdefault("profile_id", str(uuid.uuid4()))
        row.setdefault("name", "Imported Mapping")
        row.setdefault("source_skeleton_hash", "")
        row.setdefault("schema_version", PROFILE_SCHEMA_VERSION)
        row.setdefault("format", PROFILE_FORMAT)
        return cls(**row)

    def save(self, path: str | Path) -> Path:
        destination = Path(path)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(json.dumps(self.to_dict(), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        return destination

    @classmethod
    def load(cls, path: str | Path) -> "SourceBoneMappingProfile":
        payload = json.loads(Path(path).read_text(encoding="utf-8-sig"))
        if not isinstance(payload, dict):
            raise ValueError("mapping profile root must be an object")
        return cls.from_dict(payload)


def source_skeleton_hash(bones: Iterable[str], *, parents: Mapping[str, str | None] | None = None) -> str:
    rows = [f"{bone}\t{'' if parents is None else parents.get(bone) or ''}" for bone in sorted(str(value) for value in bones)]
    return hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest()


def auto_map_source_bones(source_bones: Iterable[str], *, parents: Mapping[str, str | None] | None = None, profile_name: str = "Auto-mapped Humanoid") -> SourceBoneMappingProfile:
    bones = tuple(dict.fromkeys(str(value) for value in source_bones))
    profile = SourceBoneMappingProfile.empty(bones, name=profile_name, parents=parents)
    used: set[str] = set()
    normalized = {bone: normalize_bone_name(bone) for bone in bones}
    for role in HUMANOID_ROLES:
        aliases = {normalize_bone_name(role.canonical_source_name), normalize_bone_name(role.target_name), *(normalize_bone_name(value) for value in role.aliases)}
        candidates: list[tuple[float, str]] = []
        for bone in bones:
            if bone in used:
                continue
            name = normalized[bone]
            exact_full_name = bone.casefold() == role.canonical_source_name.casefold()
            score = 1.0 if exact_full_name else 0.93 if name in aliases else 0.0
            if score:
                candidates.append((score, bone))
        if candidates:
            score, bone = max(candidates, key=lambda row: (row[0], -bones.index(row[1])))
            profile.set_mapping(role.role_id, bone, confidence=score, method="exact" if score == 1.0 else "alias")
            used.add(bone)

    # Fill the remaining roles with the same anatomical scan used by model
    # import and the animation workspace. Exact/canonical matches above always
    # win, so this only improves previously unresolved rows.
    from .retarget_mapping import scan_humanoid_bones

    role_for_semantic = {
        "pelvis": "hips",
        "spine_1": "spine",
        "spine_2": "chest",
        "spine_3": "upper_chest",
        "neck_1": "neck",
        "head": "head",
        "l_clavicle": "left_shoulder",
        "l_upperarm": "left_upper_arm",
        "l_forearm": "left_lower_arm",
        "l_hand": "left_hand",
        "r_clavicle": "right_shoulder",
        "r_upperarm": "right_upper_arm",
        "r_forearm": "right_lower_arm",
        "r_hand": "right_hand",
        "l_thigh": "left_upper_leg",
        "l_calf": "left_lower_leg",
        "l_foot": "left_foot",
        "l_toe": "left_toes",
        "r_thigh": "right_upper_leg",
        "r_calf": "right_lower_leg",
        "r_foot": "right_foot",
        "r_toe": "right_toes",
    }
    for side, role_side in (("l", "left"), ("r", "right")):
        for digit in ("thumb", "index", "middle", "ring", "pinky"):
            for segment in range(1, 4):
                role_for_semantic[f"{side}_{digit}_{segment}"] = (
                    f"{role_side}_{digit}_{segment}"
                )

    by_role: dict[str, list[tuple[tuple[int, int, int], str, float, str]]] = {}
    for bone, match in scan_humanoid_bones(bones, parents).items():
        role_id = role_for_semantic.get(match.role)
        if not role_id or bone in used or role_id in profile.role_to_bone:
            continue
        plain = normalize_bone_name(bone)
        decorated = int(any(token in plain for token in ("base", "armature", "def")))
        exact_role_name = int(plain.replace("_", "") != match.role.replace("_", ""))
        by_role.setdefault(role_id, []).append(
            ((decorated, exact_role_name, len(plain)), bone, match.confidence, match.method)
        )
    for role_id, candidates in by_role.items():
        _priority, bone, confidence, method = min(candidates, key=lambda row: row[0])
        profile.set_mapping(
            role_id,
            bone,
            confidence=confidence,
            method=f"shared_{method}",
        )
        used.add(bone)
    profile.ignored_bones = [bone for bone in bones if bone not in used]
    return profile


def apply_canonical_aliases(values: Mapping[str, Any], aliases: Mapping[str, str] | None) -> dict[str, Any]:
    result = dict(values)
    for canonical, source in (aliases or {}).items():
        if source not in values:
            raise KeyError(f"source bone mapping refers to a missing FBX bone: {canonical} -> {source}")
        result[canonical] = values[source]
    return result


def required_canonical_source_names(*, include_fingers: bool = False) -> tuple[str, ...]:
    return tuple(role.canonical_source_name for role in HUMANOID_ROLES if role.required or (include_fingers and "Fingers" in role.group))


__all__ = ["CANONICAL_BY_ROLE", "HUMANOID_ROLES", "HumanoidRole", "PROFILE_FORMAT", "PROFILE_SCHEMA_VERSION", "ROLE_BY_ID", "SourceBoneMappingProfile", "apply_canonical_aliases", "auto_map_source_bones", "required_canonical_source_names", "source_skeleton_hash"]
