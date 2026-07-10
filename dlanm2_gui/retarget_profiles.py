"""Humanoid source-bone mapping profiles.

The validated retargeter consumes a canonical Mixamo-style semantic skeleton.
A mapping profile aliases arbitrary FBX bone names onto those semantic roles,
which keeps the solver independent from namespaces and authoring-tool naming.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
import hashlib
import json
from pathlib import Path
import re
import uuid
from typing import Any, Iterable, Mapping

from .remap import normalize_bone_name


PROFILE_FORMAT = "dl-reanimated-retarget-profile"
PROFILE_SCHEMA_VERSION = 1


@dataclass(frozen=True, slots=True)
class HumanoidRole:
    role_id: str
    label: str
    canonical_source_name: str
    group: str
    required: bool = True
    parent_role: str | None = None
    aliases: tuple[str, ...] = ()


def _role(
    role_id: str,
    label: str,
    canonical: str,
    group: str,
    *,
    required: bool = True,
    parent: str | None = None,
    aliases: tuple[str, ...] = (),
) -> HumanoidRole:
    return HumanoidRole(role_id, label, canonical, group, required, parent, aliases)


_BASE_ROLES: list[HumanoidRole] = [
    _role("hips", "Hips", "mixamorig:Hips", "Body", aliases=("pelvis", "root", "hip")),
    _role("spine", "Spine", "mixamorig:Spine", "Body", parent="hips", aliases=("spine0", "lower_spine")),
    _role("chest", "Chest", "mixamorig:Spine1", "Body", parent="spine", aliases=("spine1", "chest", "mid_spine")),
    _role("upper_chest", "Upper Chest", "mixamorig:Spine2", "Body", parent="chest", aliases=("spine2", "upperchest", "upper_chest")),
    _role("neck", "Neck", "mixamorig:Neck", "Body", parent="upper_chest"),
    _role("head", "Head", "mixamorig:Head", "Body", parent="neck"),
    _role("head_end", "Head End", "mixamorig:HeadTop_End", "Body", required=False, parent="head", aliases=("headtop_end", "head_end", "head_tip")),
    _role("left_shoulder", "Left Shoulder", "mixamorig:LeftShoulder", "Left Arm", parent="upper_chest", aliases=("clavicle_l", "shoulder_l")),
    _role("left_upper_arm", "Left Upper Arm", "mixamorig:LeftArm", "Left Arm", parent="left_shoulder", aliases=("upperarm_l", "upper_arm_l", "arm_l")),
    _role("left_lower_arm", "Left Lower Arm", "mixamorig:LeftForeArm", "Left Arm", parent="left_upper_arm", aliases=("forearm_l", "lowerarm_l", "lower_arm_l")),
    _role("left_hand", "Left Hand", "mixamorig:LeftHand", "Left Arm", parent="left_lower_arm", aliases=("hand_l",)),
    _role("right_shoulder", "Right Shoulder", "mixamorig:RightShoulder", "Right Arm", parent="upper_chest", aliases=("clavicle_r", "shoulder_r")),
    _role("right_upper_arm", "Right Upper Arm", "mixamorig:RightArm", "Right Arm", parent="right_shoulder", aliases=("upperarm_r", "upper_arm_r", "arm_r")),
    _role("right_lower_arm", "Right Lower Arm", "mixamorig:RightForeArm", "Right Arm", parent="right_upper_arm", aliases=("forearm_r", "lowerarm_r", "lower_arm_r")),
    _role("right_hand", "Right Hand", "mixamorig:RightHand", "Right Arm", parent="right_lower_arm", aliases=("hand_r",)),
    _role("left_upper_leg", "Left Upper Leg", "mixamorig:LeftUpLeg", "Left Leg", parent="hips", aliases=("thigh_l", "upperleg_l", "upper_leg_l")),
    _role("left_lower_leg", "Left Lower Leg", "mixamorig:LeftLeg", "Left Leg", parent="left_upper_leg", aliases=("calf_l", "lowerleg_l", "lower_leg_l")),
    _role("left_foot", "Left Foot", "mixamorig:LeftFoot", "Left Leg", parent="left_lower_leg", aliases=("foot_l", "ankle_l")),
    _role("left_toes", "Left Toes", "mixamorig:LeftToeBase", "Left Leg", parent="left_foot", aliases=("toes_l", "toe_l", "toe_base_l")),
    _role("right_upper_leg", "Right Upper Leg", "mixamorig:RightUpLeg", "Right Leg", parent="hips", aliases=("thigh_r", "upperleg_r", "upper_leg_r")),
    _role("right_lower_leg", "Right Lower Leg", "mixamorig:RightLeg", "Right Leg", parent="right_upper_leg", aliases=("calf_r", "lowerleg_r", "lower_leg_r")),
    _role("right_foot", "Right Foot", "mixamorig:RightFoot", "Right Leg", parent="right_lower_leg", aliases=("foot_r", "ankle_r")),
    _role("right_toes", "Right Toes", "mixamorig:RightToeBase", "Right Leg", parent="right_foot", aliases=("toes_r", "toe_r", "toe_base_r")),
]

_DIGITS = (
    ("thumb", "Thumb"),
    ("index", "Index"),
    ("middle", "Middle"),
    ("ring", "Ring"),
    ("pinky", "Pinky"),
)


def _finger_roles() -> list[HumanoidRole]:
    rows: list[HumanoidRole] = []
    for side, source_side, hand_role, group in (
        ("left", "Left", "left_hand", "Left Fingers"),
        ("right", "Right", "right_hand", "Right Fingers"),
    ):
        for digit_id, digit_label in _DIGITS:
            previous = hand_role
            for index in range(1, 5):
                role_id = f"{side}_{digit_id}_{index}"
                required = index <= 3
                rows.append(
                    _role(
                        role_id,
                        f"{side.title()} {digit_label} {index}",
                        f"mixamorig:{source_side}Hand{digit_label}{index}",
                        group,
                        required=False,
                        parent=previous,
                        aliases=(
                            f"{digit_id}{index}_{'l' if side == 'left' else 'r'}",
                            f"hand_{digit_id}{index}_{'l' if side == 'left' else 'r'}",
                            f"finger_{digit_id}_{index}_{'l' if side == 'left' else 'r'}",
                        ),
                    )
                )
                previous = role_id
    return rows


HUMANOID_ROLES: tuple[HumanoidRole, ...] = tuple(_BASE_ROLES + _finger_roles())
ROLE_BY_ID: dict[str, HumanoidRole] = {role.role_id: role for role in HUMANOID_ROLES}
CANONICAL_BY_ROLE: dict[str, str] = {
    role.role_id: role.canonical_source_name for role in HUMANOID_ROLES
}
ROLE_BY_CANONICAL: dict[str, HumanoidRole] = {
    role.canonical_source_name: role for role in HUMANOID_ROLES
}


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
    def empty(
        cls,
        source_bones: Iterable[str],
        *,
        name: str = "Custom Humanoid Mapping",
        parents: Mapping[str, str | None] | None = None,
    ) -> "SourceBoneMappingProfile":
        bones = tuple(source_bones)
        return cls(
            profile_id=str(uuid.uuid4()),
            name=name,
            source_skeleton_hash=source_skeleton_hash(bones, parents=parents),
            ignored_bones=list(bones),
        )

    def canonical_aliases(self) -> dict[str, str]:
        """Return ``canonical Mixamo name -> actual FBX name`` aliases."""
        return {
            CANONICAL_BY_ROLE[role_id]: bone_name
            for role_id, bone_name in self.role_to_bone.items()
            if role_id in CANONICAL_BY_ROLE and bone_name
        }

    def mapped_bone(self, role_id: str) -> str | None:
        return self.role_to_bone.get(role_id)

    def set_mapping(
        self,
        role_id: str,
        bone_name: str | None,
        *,
        confidence: float = 1.0,
        method: str = "manual",
    ) -> None:
        if role_id not in ROLE_BY_ID:
            raise KeyError(f"unknown humanoid role: {role_id}")
        if not bone_name:
            self.role_to_bone.pop(role_id, None)
            self.confidence_by_role.pop(role_id, None)
            self.method_by_role.pop(role_id, None)
            return
        self.role_to_bone[role_id] = bone_name
        self.confidence_by_role[role_id] = float(confidence)
        self.method_by_role[role_id] = method

    def validate(self, source_bones: Iterable[str] | None = None) -> list[str]:
        errors: list[str] = []
        source_set = set(source_bones or ())
        used: dict[str, str] = {}
        for role in HUMANOID_ROLES:
            bone = self.role_to_bone.get(role.role_id)
            if role.required and not bone:
                errors.append(f"Required role is not mapped: {role.label}")
            if bone and source_set and bone not in source_set:
                errors.append(f"Mapped bone does not exist: {role.label} -> {bone}")
            if bone:
                previous = used.get(bone)
                if previous and previous != role.role_id:
                    errors.append(
                        f"Source bone is used by more than one role: {bone} "
                        f"({ROLE_BY_ID[previous].label}, {role.label})"
                    )
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
            raise ValueError(
                f"mapping profile schema {version} is newer than supported schema "
                f"{PROFILE_SCHEMA_VERSION}"
            )
        row = dict(payload)
        if version == 0:
            row.setdefault("profile_id", str(uuid.uuid4()))
            row.setdefault("format", PROFILE_FORMAT)
            row["schema_version"] = 1
            row.setdefault("confidence_by_role", {})
            row.setdefault("method_by_role", {})
            row.setdefault("ignored_bones", [])
            row.setdefault("notes", [])
            row.setdefault("extensions", {})
        allowed = {
            "profile_id",
            "name",
            "source_skeleton_hash",
            "role_to_bone",
            "confidence_by_role",
            "method_by_role",
            "ignored_bones",
            "notes",
            "extensions",
            "schema_version",
            "format",
        }
        unknown = {key: value for key, value in row.items() if key not in allowed}
        extensions = dict(row.get("extensions", {}))
        if unknown:
            extensions.setdefault("unknown_fields", {}).update(unknown)
        return cls(
            profile_id=str(row.get("profile_id") or uuid.uuid4()),
            name=str(row.get("name", "Imported Mapping")),
            source_skeleton_hash=str(row.get("source_skeleton_hash", "")),
            role_to_bone={str(k): str(v) for k, v in dict(row.get("role_to_bone", {})).items()},
            confidence_by_role={str(k): float(v) for k, v in dict(row.get("confidence_by_role", {})).items()},
            method_by_role={str(k): str(v) for k, v in dict(row.get("method_by_role", {})).items()},
            ignored_bones=[str(value) for value in row.get("ignored_bones", [])],
            notes=[str(value) for value in row.get("notes", [])],
            extensions=extensions,
            schema_version=int(row.get("schema_version", PROFILE_SCHEMA_VERSION)),
            format=str(row.get("format", PROFILE_FORMAT)),
        )

    def save(self, path: str | Path) -> Path:
        destination = Path(path)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(json.dumps(self.to_dict(), indent=2) + "\n", encoding="utf-8")
        return destination

    @classmethod
    def load(cls, path: str | Path) -> "SourceBoneMappingProfile":
        payload = json.loads(Path(path).read_text(encoding="utf-8"))
        if not isinstance(payload, dict):
            raise ValueError("mapping profile root must be an object")
        return cls.from_dict(payload)


def source_skeleton_hash(
    bones: Iterable[str],
    *,
    parents: Mapping[str, str | None] | None = None,
) -> str:
    rows = []
    for bone in sorted(str(value) for value in bones):
        parent = "" if parents is None else str(parents.get(bone) or "")
        rows.append(f"{bone}\t{parent}")
    return hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest()


def auto_map_source_bones(
    source_bones: Iterable[str],
    *,
    parents: Mapping[str, str | None] | None = None,
    profile_name: str = "Auto-mapped Humanoid",
) -> SourceBoneMappingProfile:
    bones = tuple(dict.fromkeys(str(value) for value in source_bones))
    profile = SourceBoneMappingProfile.empty(bones, name=profile_name, parents=parents)
    normalized = {bone: normalize_bone_name(bone) for bone in bones}
    canonical_normalized = {
        role.role_id: normalize_bone_name(role.canonical_source_name)
        for role in HUMANOID_ROLES
    }
    used: set[str] = set()

    # Exact full-name matches are handled first so standard Mixamo rigs remain
    # deterministic. Normalized canonical matching is deliberately deferred
    # until after explicit aliases: names such as ``Chest`` and ``UpperChest``
    # would otherwise collide with normalized Mixamo ``Spine1``/``Spine2``.
    for role in HUMANOID_ROLES:
        exact = next(
            (
                bone
                for bone in bones
                if bone.lower() == role.canonical_source_name.lower()
            ),
            None,
        )
        if exact is not None:
            profile.set_mapping(role.role_id, exact, confidence=1.0, method="exact")
            used.add(exact)

    # Common humanoid names (Pelvis, Chest, UpperChest, UpperArm.L, etc.)
    # should beat fuzzy scoring. This second deterministic pass checks every
    # role alias after the canonical Mixamo pass above.
    for role in HUMANOID_ROLES:
        if role.role_id in profile.role_to_bone:
            continue
        aliases = {
            normalize_bone_name(alias)
            for alias in (*role.aliases, role.canonical_source_name)
        }
        exact_alias = next(
            (
                bone
                for bone in bones
                if bone not in used and normalized[bone] in aliases
            ),
            None,
        )
        if exact_alias is not None:
            profile.set_mapping(
                role.role_id,
                exact_alias,
                confidence=0.93,
                method="alias",
            )
            used.add(exact_alias)

    # Namespaced or tool-prefixed Mixamo names often only match after
    # normalization. At this point common aliases have already claimed their
    # unambiguous roles, so the fallback cannot steal ``Chest`` from Chest.
    for role in HUMANOID_ROLES:
        if role.role_id in profile.role_to_bone:
            continue
        exact_normal = next(
            (
                bone
                for bone in bones
                if bone not in used
                and normalized[bone] == canonical_normalized[role.role_id]
            ),
            None,
        )
        if exact_normal is not None:
            profile.set_mapping(
                role.role_id,
                exact_normal,
                confidence=0.96,
                method="normalized",
            )
            used.add(exact_normal)

    for role in HUMANOID_ROLES:
        if role.role_id in profile.role_to_bone:
            continue
        best_bone: str | None = None
        best_score = 0.0
        for bone in bones:
            if bone in used:
                continue
            score = _role_match_score(
                role,
                bone,
                normalized_name=normalized[bone],
                parents=parents,
                profile=profile,
            )
            if score > best_score:
                best_score = score
                best_bone = bone
        threshold = 0.58 if role.required else 0.72
        if best_bone is not None and best_score >= threshold:
            profile.set_mapping(
                role.role_id,
                best_bone,
                confidence=min(best_score, 0.95),
                method="heuristic",
            )
            used.add(best_bone)

    profile.ignored_bones = [bone for bone in bones if bone not in used]
    missing = [role.label for role in HUMANOID_ROLES if role.required and role.role_id not in profile.role_to_bone]
    if missing:
        profile.notes.append(
            "Manual mapping is required for: " + ", ".join(missing)
        )
    return profile


def _role_match_score(
    role: HumanoidRole,
    bone: str,
    *,
    normalized_name: str,
    parents: Mapping[str, str | None] | None,
    profile: SourceBoneMappingProfile,
) -> float:
    canonical = normalize_bone_name(role.canonical_source_name)
    aliases = {normalize_bone_name(alias) for alias in role.aliases}
    aliases.add(canonical)
    score = 0.0
    if normalized_name in aliases:
        score = max(score, 0.92)

    candidate_tokens = set(normalized_name.split("_"))
    role_tokens = set(canonical.split("_"))
    alias_tokens = set().union(*(set(alias.split("_")) for alias in aliases)) if aliases else set()
    overlap = len(candidate_tokens & (role_tokens | alias_tokens))
    if overlap:
        score = max(score, 0.42 + 0.13 * overlap)

    role_side = _role_side(role.role_id)
    candidate_side = _name_side(normalized_name)
    if role_side and candidate_side:
        score += 0.15 if role_side == candidate_side else -0.45

    expected_digits = _role_digit_tokens(role.role_id)
    if expected_digits:
        if expected_digits.issubset(candidate_tokens):
            score += 0.24
        elif candidate_tokens & {"thumb", "index", "middle", "ring", "pinky"}:
            score -= 0.35
        index_match = re.search(r"(?:^|_)([1-4])(?:$|_)", normalized_name)
        expected_index = next((value for value in ("1", "2", "3", "4") if role.role_id.endswith("_" + value)), None)
        if expected_index and index_match:
            score += 0.12 if index_match.group(1) == expected_index else -0.20

    if parents and role.parent_role:
        mapped_parent = profile.role_to_bone.get(role.parent_role)
        actual_parent = parents.get(bone)
        if mapped_parent and actual_parent:
            score += 0.18 if actual_parent == mapped_parent else -0.08

    return max(0.0, min(score, 1.0))


def _role_side(role_id: str) -> str:
    if role_id.startswith("left_"):
        return "left"
    if role_id.startswith("right_"):
        return "right"
    return ""


def _name_side(normalized_name: str) -> str:
    if normalized_name.endswith("_l") or normalized_name.startswith("l_"):
        return "left"
    if normalized_name.endswith("_r") or normalized_name.startswith("r_"):
        return "right"
    return ""


def _role_digit_tokens(role_id: str) -> set[str]:
    return {
        token
        for token in ("thumb", "index", "middle", "ring", "pinky")
        if f"_{token}_" in role_id
    }


def apply_canonical_aliases(
    values_by_name: Mapping[str, Any],
    aliases: Mapping[str, str] | None,
) -> dict[str, Any]:
    """Return a copy with canonical semantic keys added.

    Existing canonical keys are preserved unless an explicit alias is supplied.
    This makes identity Mixamo profiles and custom-name profiles use the same
    downstream retarget code.
    """

    result = dict(values_by_name)
    if not aliases:
        return result
    for canonical_name, source_name in aliases.items():
        if source_name not in values_by_name:
            raise KeyError(
                f"source bone mapping refers to a missing FBX bone: "
                f"{canonical_name} -> {source_name}"
            )
        result[canonical_name] = values_by_name[source_name]
    return result


def required_canonical_source_names(*, include_fingers: bool = False) -> tuple[str, ...]:
    return tuple(
        role.canonical_source_name
        for role in HUMANOID_ROLES
        if role.required or include_fingers
    )


__all__ = [
    "CANONICAL_BY_ROLE",
    "HUMANOID_ROLES",
    "HumanoidRole",
    "PROFILE_FORMAT",
    "PROFILE_SCHEMA_VERSION",
    "ROLE_BY_ID",
    "SourceBoneMappingProfile",
    "apply_canonical_aliases",
    "auto_map_source_bones",
    "required_canonical_source_names",
    "source_skeleton_hash",
]
