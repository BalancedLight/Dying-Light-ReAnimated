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
PROFILE_SCHEMA_VERSION = 2
ROLE_MODES = ("auto", "direct", "inherit_bind", "static_bind")


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
    source_name_parent_hash: str = ""
    source_bind_hash: str = ""
    source_animation_hash: str = ""
    target_policy_id: str = ""
    target_rig_id: str = ""
    target_skeleton_hash: str = ""
    role_to_bone: dict[str, str] = field(default_factory=dict)
    role_modes: dict[str, str] = field(default_factory=dict)
    cleared_roles: list[str] = field(default_factory=list)
    confidence_by_role: dict[str, float] = field(default_factory=dict)
    method_by_role: dict[str, str] = field(default_factory=dict)
    evidence_by_role: dict[str, list[dict[str, Any]]] = field(default_factory=dict)
    # Target-neutral root/locomotion selections and arbitrary target-row
    # overrides are first-class profile data.  They are optional additions to
    # schema v2, so existing serialized profiles remain byte-semantically
    # compatible when these dictionaries are empty.
    root_motion: dict[str, Any] = field(default_factory=dict)
    locomotion: dict[str, Any] = field(default_factory=dict)
    target_bone_overrides: dict[str, dict[str, Any]] = field(default_factory=dict)
    compiled_map_cache_id: str = ""
    ignored_bones: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    extensions: dict[str, Any] = field(default_factory=dict)
    schema_version: int = PROFILE_SCHEMA_VERSION
    format: str = PROFILE_FORMAT

    @classmethod
    def empty(cls, source_bones: Iterable[str], *, name: str = "Custom Humanoid Mapping", parents: Mapping[str, str | None] | None = None) -> "SourceBoneMappingProfile":
        bones = tuple(str(value) for value in source_bones)
        signature = source_skeleton_hash(bones, parents=parents)
        return cls(
            str(uuid.uuid4()),
            name,
            signature,
            source_name_parent_hash=signature,
            ignored_bones=list(bones),
        )

    def canonical_aliases(self) -> dict[str, str]:
        return {CANONICAL_BY_ROLE[key]: value for key, value in self.role_to_bone.items() if key in CANONICAL_BY_ROLE and value}

    def mapped_bone(self, role_id: str) -> str | None:
        return self.role_to_bone.get(role_id)

    def set_mapping(
        self,
        role_id: str,
        bone_name: str | None,
        *,
        confidence: float = 1.0,
        method: str = "manual",
        mode: str | None = None,
        evidence: Iterable[Mapping[str, Any]] = (),
    ) -> None:
        if role_id not in ROLE_BY_ID:
            raise KeyError(f"unknown humanoid role: {role_id}")
        resolved_mode = str(
            mode or ("direct" if str(method).startswith("manual") else "auto")
        )
        if resolved_mode not in ROLE_MODES:
            raise ValueError(f"unsupported humanoid role mode: {resolved_mode}")
        self.role_modes[role_id] = resolved_mode
        if not bone_name:
            self.role_to_bone.pop(role_id, None)
            self.confidence_by_role.pop(role_id, None)
            self.method_by_role.pop(role_id, None)
            self.evidence_by_role.pop(role_id, None)
            if resolved_mode in {"inherit_bind", "static_bind"}:
                self.cleared_roles = [
                    value for value in self.cleared_roles if value != role_id
                ]
            elif role_id not in self.cleared_roles:
                self.cleared_roles.append(role_id)
            return
        self.cleared_roles = [value for value in self.cleared_roles if value != role_id]
        self.role_to_bone[role_id] = str(bone_name)
        self.confidence_by_role[role_id] = float(confidence)
        self.method_by_role[role_id] = str(method)
        self.evidence_by_role[role_id] = [dict(row) for row in evidence]

    def set_role_mode(self, role_id: str, mode: str) -> None:
        if role_id not in ROLE_BY_ID:
            raise KeyError(f"unknown humanoid role: {role_id}")
        value = str(mode)
        if value not in ROLE_MODES:
            raise ValueError(f"unsupported humanoid role mode: {value}")
        self.role_modes[role_id] = value
        if value in {"auto", "inherit_bind", "static_bind"}:
            self.role_to_bone.pop(role_id, None)
            self.confidence_by_role.pop(role_id, None)
            self.method_by_role.pop(role_id, None)
            self.evidence_by_role.pop(role_id, None)
        if value in {"inherit_bind", "static_bind"}:
            self.cleared_roles = [
                row for row in self.cleared_roles if row != role_id
            ]

    def role_mode(self, role_id: str) -> str:
        return str(self.role_modes.get(role_id, "auto") or "auto")

    @property
    def manual_override_count(self) -> int:
        role_count = sum(
            self.role_mode(role_id) in {"direct", "inherit_bind", "static_bind"}
            for role_id in set(self.role_modes) | set(self.role_to_bone)
        )
        target_count = sum(
            str(row.get("mode", "auto") or "auto") != "auto"
            for row in self.target_bone_overrides.values()
        )
        return role_count + target_count

    def set_target_bone_override(
        self,
        target_bone: str,
        *,
        mode: str = "auto",
        source_bone: str = "",
        transfer_policy: str = "default",
        component_policy: str = "rotation",
    ) -> None:
        from .bone_maps import COMPONENT_POLICIES, TRANSFER_POLICIES

        target = str(target_bone or "")
        if not target:
            raise ValueError("target bone override requires a target bone")
        resolved_mode = str(mode or "auto")
        if resolved_mode not in ROLE_MODES:
            raise ValueError(f"unsupported target bone mode: {resolved_mode}")
        if transfer_policy not in TRANSFER_POLICIES:
            raise ValueError(f"unsupported target transfer policy: {transfer_policy}")
        if component_policy not in COMPONENT_POLICIES:
            raise ValueError(f"unsupported target component policy: {component_policy}")
        if resolved_mode == "direct" and not source_bone:
            raise ValueError(f"direct target override {target!r} requires a source bone")
        if resolved_mode == "auto":
            self.target_bone_overrides.pop(target, None)
        else:
            self.target_bone_overrides[target] = {
                "mode": resolved_mode,
                "source_bone": str(source_bone or ""),
                "transfer_policy": str(transfer_policy),
                "component_policy": str(component_policy),
            }
        self.clear_compiled_cache()

    def clear_compiled_cache(self) -> None:
        self.compiled_map_cache_id = ""
        self.extensions.pop("compiled_map_hash", None)
        self.extensions.pop("compiled_validation", None)
        self.extensions.pop("selected_engine_hint", None)
        self.extensions.pop("current_automatic_retarget_plan", None)
        self.extensions.pop("retarget_readiness", None)

    def validate(self, source_bones: Iterable[str] | None = None) -> list[str]:
        source_set = set(source_bones or ())
        errors: list[str] = []
        used: dict[str, str] = {}
        for role in HUMANOID_ROLES:
            bone = self.role_to_bone.get(role.role_id)
            dynamic_target_profile = bool(self.target_policy_id)
            mode = self.role_mode(role.role_id)
            if role.required and not dynamic_target_profile and not bone:
                errors.append(f"Required role is not mapped: {role.label}")
            if mode == "direct" and not bone:
                errors.append(f"Direct role has no source bone: {role.label}")
            if bone and source_set and bone not in source_set:
                errors.append(f"Mapped bone does not exist: {role.label} -> {bone}")
            if (
                bone
                and mode == "direct"
                and bone in used
                and used[bone] != role.role_id
            ):
                errors.append(f"Source bone is used by more than one role: {bone} ({ROLE_BY_ID[used[bone]].label}, {role.label})")
            if bone:
                used[bone] = role.role_id
        from .bone_maps import COMPONENT_POLICIES, TRANSFER_POLICIES

        for target, row in self.target_bone_overrides.items():
            mode = str(row.get("mode", "auto") or "auto")
            source = str(row.get("source_bone", "") or "")
            transfer = str(row.get("transfer_policy", "default") or "default")
            component = str(row.get("component_policy", "rotation") or "rotation")
            if mode not in ROLE_MODES:
                errors.append(f"Target override has unsupported mode: {target} -> {mode}")
            if mode == "direct" and not source:
                errors.append(f"Direct target override has no source bone: {target}")
            if source and source_set and source not in source_set:
                errors.append(f"Target override source bone does not exist: {target} -> {source}")
            if transfer not in TRANSFER_POLICIES:
                errors.append(f"Target override has unsupported transfer policy: {target} -> {transfer}")
            if component not in COMPONENT_POLICIES:
                errors.append(f"Target override has unsupported component policy: {target} -> {component}")
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
        row.setdefault("source_name_parent_hash", row["source_skeleton_hash"])
        if version < 2:
            role_to_bone = dict(row.get("role_to_bone", {}) or {})
            methods = dict(row.get("method_by_role", {}) or {})
            migrated_modes = dict(row.get("role_modes", {}) or {})
            for role_id in role_to_bone:
                method = str(methods.get(role_id, "") or "").casefold()
                migrated_modes.setdefault(
                    role_id,
                    "direct" if method.startswith("manual") else "auto",
                )
            row["role_modes"] = migrated_modes
            extensions.setdefault("schema_migration", []).append(
                {
                    "from": version,
                    "to": PROFILE_SCHEMA_VERSION,
                    "manual_assignments_preserved": sum(
                        value == "direct" for value in migrated_modes.values()
                    ),
                }
            )
        row["schema_version"] = PROFILE_SCHEMA_VERSION
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


__all__ = ["CANONICAL_BY_ROLE", "HUMANOID_ROLES", "HumanoidRole", "PROFILE_FORMAT", "PROFILE_SCHEMA_VERSION", "ROLE_BY_ID", "ROLE_MODES", "SourceBoneMappingProfile", "apply_canonical_aliases", "auto_map_source_bones", "required_canonical_source_names", "source_skeleton_hash"]
