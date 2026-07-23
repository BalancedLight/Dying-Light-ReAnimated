"""Fail-closed target-rig policy for automatic semantic retargeting."""

from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any

from .chrome_rig import ChromeRig, ChromeRigBone
from .game_profiles import DL2_ADVANCED_RIG_REF, DL2_GAME_ID, DL2_LEGACY_RIG_REF
from .semantic_chain_alignment import SemanticChainNode


TARGET_RETARGET_POLICY_VERSION = "1"
DL2_ADVANCED_BODY_POLICY_ID = "dl2_advanced_body_bridge_v1"
DL2_LEGACY_BODY_POLICY_ID = "dl2_legacy_body_bridge_v1"
DL2_ADVANCED_EXPECTED_SKELETON_HASH = (
    "6e3ef3282f66c19028bbf7ecb4a3f2685e61416c159826ae68d7b6bbdcd2ae4c"
)
DL2_ADVANCED_EXPECTED_SOURCE_SMD_SHA256 = (
    "E298D421E8DD398ED66DEC44115D7E2DF03930E8EAEE3A162BABD0268F17A7C5"
)
DL2_ADVANCED_EXPECTED_REFERENCE_ANM2_SHA256 = (
    "9368914A4C59521BDD31FED064DF93A5D2D287E793FDC9447BE24ACD4A3FFF6D"
)
DL2_ADVANCED_EXPECTED_BONE_COUNT = 271
DL2_ADVANCED_EXPECTED_TRACK_COUNT = 368
DL2_ADVANCED_BODY_DIRECT_SLOT_COUNT = 52
DL2_LEGACY_EXPECTED_SKELETON_HASH = (
    "26f3013952da75db549e021296cc202dc9c2c56af37a3089b8746e048e7b1a1b"
)
DL2_LEGACY_EXPECTED_SOURCE_SMD_SHA256 = (
    "EC2D0D2E4FDF221491318E83B9A3FF8AAB82F856A94C6E1E1AAB23784B109A0D"
)
DL2_LEGACY_EXPECTED_BONE_COUNT = 81
DL2_LEGACY_EXPECTED_TRACK_COUNT = 82

_TARGET_CATEGORIES = (
    "body",
    "facial",
    "secondary_animation",
    "collar",
    "camera",
    "attachment",
)
_EXPECTED_CATEGORY_COUNTS = {
    "body": 76,
    "facial": 167,
    "secondary_animation": 14,
    "collar": 4,
    "camera": 2,
    "attachment": 8,
}


@dataclass(frozen=True, slots=True)
class TargetDirectSlot:
    semantic_role: str
    target_bone: str
    chain_id: str
    side: str = "center"
    source_segment_index: int = 0


@dataclass(frozen=True, slots=True)
class TargetSemanticChain:
    chain_id: str
    side: str
    nodes: tuple[SemanticChainNode, ...]
    direct_slots: tuple[TargetDirectSlot, ...]

    @property
    def target_bones(self) -> tuple[str, ...]:
        return tuple(row.name for row in self.nodes)


@dataclass(frozen=True, slots=True)
class TargetBonePolicy:
    target_bone: str
    descriptor: int
    target_category: str
    deform: bool
    helper: bool
    semantic_role: str
    chain_id: str
    default_mode: str


@dataclass(frozen=True, slots=True)
class TargetRetargetPolicy:
    """Target-owned retarget domains; never a serialized-map certificate."""

    policy_id: str
    policy_version: str
    target_rig_id: str
    target_skeleton_hash: str
    game_id: str
    clip_domain: str
    target_archetype: str
    automatic_routing_authorized: bool
    bones: tuple[TargetBonePolicy, ...]
    semantic_chains: tuple[TargetSemanticChain, ...]
    direct_slots: tuple[TargetDirectSlot, ...]
    coherence_errors: tuple[str, ...] = ()

    @property
    def verified_automatic_routing(self) -> bool:
        return self.automatic_routing_authorized

    @property
    def target_row_count(self) -> int:
        return len(self.bones)

    @property
    def direct_slot_count(self) -> int:
        return len(self.direct_slots)

    @property
    def bind_default_count(self) -> int:
        return sum(row.default_mode != "direct" for row in self.bones)

    @property
    def direct_target_bones(self) -> tuple[str, ...]:
        return tuple(row.target_bone for row in self.direct_slots)

    @property
    def bind_default_targets(self) -> tuple[str, ...]:
        return tuple(row.target_bone for row in self.bones if row.default_mode != "direct")

    @property
    def helper_targets(self) -> tuple[str, ...]:
        return tuple(row.target_bone for row in self.bones if row.helper)

    @property
    def category_counts(self) -> dict[str, int]:
        counts: dict[str, int] = {}
        for row in self.bones:
            counts[row.target_category] = counts.get(row.target_category, 0) + 1
        return counts

    @property
    def mapping_mode_counts(self) -> dict[str, int]:
        counts: dict[str, int] = {}
        for row in self.bones:
            counts[row.default_mode] = counts.get(row.default_mode, 0) + 1
        return counts

    def bone_policy(self, target_bone: str) -> TargetBonePolicy:
        return next(row for row in self.bones if row.target_bone == target_bone)

    def semantic_chain(self, chain_id: str) -> TargetSemanticChain:
        return next(row for row in self.semantic_chains if row.chain_id == chain_id)

    def to_dict(self) -> dict[str, Any]:
        return {
            "policy_id": self.policy_id,
            "policy_version": self.policy_version,
            "target_rig_id": self.target_rig_id,
            "target_skeleton_hash": self.target_skeleton_hash,
            "game_id": self.game_id,
            "clip_domain": self.clip_domain,
            "target_archetype": self.target_archetype,
            "automatic_routing_authorized": self.automatic_routing_authorized,
            "target_row_count": self.target_row_count,
            "direct_slot_count": self.direct_slot_count,
            "bind_default_count": self.bind_default_count,
            "category_counts": self.category_counts,
            "mapping_mode_counts": self.mapping_mode_counts,
            "direct_slots": [asdict(row) for row in self.direct_slots],
            "semantic_chains": [
                {
                    "chain_id": row.chain_id,
                    "side": row.side,
                    "target_bones": list(row.target_bones),
                    "direct_roles": [slot.semantic_role for slot in row.direct_slots],
                }
                for row in self.semantic_chains
            ],
            "coherence_errors": list(self.coherence_errors),
        }


def _slot(
    role: str,
    target: str,
    chain: str,
    side: str = "center",
    index: int = 0,
) -> TargetDirectSlot:
    return TargetDirectSlot(role, target, chain, side, index)


def _chain(
    chain_id: str,
    side: str,
    bones: tuple[str, ...],
    slots: tuple[TargetDirectSlot, ...],
    *,
    optional_bones: tuple[str, ...] = (),
) -> TargetSemanticChain:
    direct_by_target = {row.target_bone: row for row in slots}
    optional = set(optional_bones)
    nodes = tuple(
        SemanticChainNode(
            name=bone,
            semantic_role=(
                direct_by_target[bone].semantic_role if bone in direct_by_target else ""
            ),
            side=side,
            parent=(bones[index - 1] if index else None),
            optional=bone in optional,
        )
        for index, bone in enumerate(bones)
    )
    return TargetSemanticChain(chain_id, side, nodes, slots)


def _dl2_advanced_semantic_chains() -> tuple[TargetSemanticChain, ...]:
    chains: list[TargetSemanticChain] = []
    pelvis_slots = (_slot("pelvis", "pelvis", "pelvis"),)
    chains.append(_chain("pelvis", "center", ("pelvis",), pelvis_slots))

    spine_slots = (
        _slot("spine_1", "spine", "spine", index=1),
        _slot("spine_2", "spine2", "spine", index=2),
        _slot("spine_3", "spine3", "spine", index=3),
    )
    chains.append(
        _chain(
            "spine",
            "center",
            ("hspine", "spine", "spine1", "spine2", "spine3", "hspine1"),
            spine_slots,
            optional_bones=("hspine", "spine1", "hspine1"),
        )
    )
    neck_slots = (_slot("neck_1", "neck", "neck", index=1),)
    chains.append(
        _chain(
            "neck",
            "center",
            ("neck", "neck1"),
            neck_slots,
            optional_bones=("neck1",),
        )
    )
    head_slots = (_slot("head", "head", "head"),)
    chains.append(_chain("head", "center", ("head",), head_slots))

    for side, prefix in (("left", "l"), ("right", "r")):
        arm_id = f"{side}_arm"
        arm_slots = tuple(
            _slot(f"{side}_{role}", f"{prefix}_{target}", arm_id, side, index)
            for index, (role, target) in enumerate(
                (
                    ("clavicle", "clavicle"),
                    ("upper_arm", "upperarm"),
                    ("forearm", "forearm"),
                    ("hand", "hand"),
                ),
                start=1,
            )
        )
        chains.append(
            _chain(
                arm_id,
                side,
                tuple(row.target_bone for row in arm_slots),
                arm_slots,
            )
        )

        leg_id = f"{side}_leg"
        leg_slots = tuple(
            _slot(f"{side}_{role}", f"{prefix}_{target}", leg_id, side, index)
            for index, (role, target) in enumerate(
                (
                    ("thigh", "thigh"),
                    ("calf", "calf"),
                    ("foot", "foot"),
                    ("toe", "toebase"),
                ),
                start=1,
            )
        )
        chains.append(
            _chain(
                leg_id,
                side,
                tuple(row.target_bone for row in leg_slots),
                leg_slots,
            )
        )

        for digit_name, digit in (
            ("thumb", "0"),
            ("index", "1"),
            ("middle", "2"),
            ("ring", "3"),
            ("pinky", "4"),
        ):
            chain_id = f"{side}_{digit_name}"
            if digit == "0":
                targets = tuple(f"{prefix}_finger0{segment}" for segment in (1, 2, 3))
            else:
                targets = tuple(
                    f"{prefix}_finger{digit}{segment}" for segment in (0, 1, 2, 3)
                )
            direct_targets = targets if digit == "0" else targets[1:]
            finger_slots = tuple(
                _slot(
                    f"{side}_{digit_name}_{segment}",
                    target,
                    chain_id,
                    side,
                    segment,
                )
                for segment, target in enumerate(direct_targets, start=1)
            )
            chains.append(
                _chain(
                    chain_id,
                    side,
                    targets,
                    finger_slots,
                    optional_bones=((targets[0],) if digit != "0" else ()),
                )
            )
    return tuple(chains)


_DL2_ADVANCED_CHAINS = _dl2_advanced_semantic_chains()
_DL2_ADVANCED_DIRECT_SLOTS = tuple(
    slot for chain in _DL2_ADVANCED_CHAINS for slot in chain.direct_slots
)


def _target_category(bone: ChromeRigBone, *, target_archetype: str) -> str:
    tags = set(bone.tags)
    for category in _TARGET_CATEGORIES:
        if category in tags:
            return category
    if target_archetype == "humanoid" and not bone.helper:
        return "body"
    return "helper" if bone.helper else "generic"


def _dl2_advanced_coherence_errors(
    rig: ChromeRig,
    *,
    game_id: str,
) -> tuple[str, ...]:
    errors: list[str] = []
    if rig.rig_id != DL2_ADVANCED_RIG_REF:
        errors.append(f"target rig ID is {rig.rig_id!r}, not {DL2_ADVANCED_RIG_REF!r}")
    if game_id != DL2_GAME_ID:
        errors.append(f"game ID is {game_id!r}, not {DL2_GAME_ID!r}")
    if rig.skeleton_hash != DL2_ADVANCED_EXPECTED_SKELETON_HASH:
        errors.append("target skeleton hash does not match the bundled 271-node advanced rig")
    if len(rig.bones) != DL2_ADVANCED_EXPECTED_BONE_COUNT:
        errors.append(
            f"target bone count is {len(rig.bones)}, expected {DL2_ADVANCED_EXPECTED_BONE_COUNT}"
        )
    if len(rig.descriptors) != DL2_ADVANCED_EXPECTED_TRACK_COUNT:
        errors.append(
            f"target descriptor count is {len(rig.descriptors)}, expected "
            f"{DL2_ADVANCED_EXPECTED_TRACK_COUNT}"
        )
    roots = [bone.name for bone in rig.bones if bone.parent_index < 0]
    if roots != ["pelvis"] or rig.root_index != 0:
        errors.append(f"target root inventory is {roots!r}, expected one pelvis root at index 0")

    extensions = dict(rig.extensions or {})
    expected_extensions = {
        "game_id": DL2_GAME_ID,
        "primary_root": "pelvis",
        "finger_policy": "dl2_explicit_finger10_20_30_40_roots",
        "hash_collision_count": 0,
        "reference_descriptor_count": 189,
        "unmatched_reference_descriptor_count": 97,
    }
    for key, expected in expected_extensions.items():
        if extensions.get(key) != expected:
            errors.append(
                f"target provenance field {key!r} is {extensions.get(key)!r}, expected {expected!r}"
            )

    computed_counts: dict[str, int] = {key: 0 for key in _TARGET_CATEGORIES}
    for bone in rig.bones:
        category = _target_category(bone, target_archetype="humanoid")
        if category in computed_counts:
            computed_counts[category] += 1
    if computed_counts != _EXPECTED_CATEGORY_COUNTS:
        errors.append(
            f"target category inventory is {computed_counts!r}, expected "
            f"{_EXPECTED_CATEGORY_COUNTS!r}"
        )
    if extensions.get("bone_category_counts") != _EXPECTED_CATEGORY_COUNTS:
        errors.append("target manifest category inventory does not match the expected advanced rig")

    validation = rig.validate(test_writer_capacity=False)
    if validation.errors:
        errors.extend(f"target rig validation: {message}" for message in validation.errors)

    by_name = {bone.name: bone for bone in rig.bones}
    direct_names = [row.target_bone for row in _DL2_ADVANCED_DIRECT_SLOTS]
    if len(direct_names) != DL2_ADVANCED_BODY_DIRECT_SLOT_COUNT:
        errors.append("internal DL2 body policy does not contain exactly 52 direct slots")
    if len(set(direct_names)) != len(direct_names):
        errors.append("internal DL2 body policy contains duplicate target slots")
    missing = sorted(set(direct_names) - by_name.keys(), key=str.casefold)
    if missing:
        errors.append("target is missing policy bones: " + ", ".join(missing))
    non_body = sorted(
        (
            name
            for name in direct_names
            if name in by_name
            and (
                _target_category(by_name[name], target_archetype="humanoid") != "body"
                or by_name[name].helper
            )
        ),
        key=str.casefold,
    )
    if non_body:
        errors.append("body policy maps non-body/helper targets: " + ", ".join(non_body))

    for side in ("l", "r"):
        hand = f"{side}_hand"
        for digit in ("1", "2", "3", "4"):
            root_name = f"{side}_finger{digit}0"
            if root_name in direct_names:
                errors.append(f"finger metacarpal/base {root_name!r} must remain bind-held")
            expected_parent = hand
            for segment in range(4):
                name = f"{side}_finger{digit}{segment}"
                bone = by_name.get(name)
                if bone is None:
                    continue
                parent_name = (
                    by_name[rig.bones[bone.parent_index].name].name
                    if bone.parent_index >= 0
                    else ""
                )
                if parent_name != expected_parent:
                    errors.append(
                        f"finger chain parent mismatch for {name!r}: "
                        f"expected {expected_parent!r}, got {parent_name!r}"
                    )
                expected_parent = name
    return tuple(dict.fromkeys(errors))


def _dl2_legacy_coherence_errors(
    rig: ChromeRig,
    *,
    game_id: str,
) -> tuple[str, ...]:
    """Verify the bundled 81-row compatibility target without trusting its ID."""

    errors: list[str] = []
    if rig.rig_id != DL2_LEGACY_RIG_REF:
        errors.append(f"target rig ID is {rig.rig_id!r}, not {DL2_LEGACY_RIG_REF!r}")
    if game_id != DL2_GAME_ID:
        errors.append(f"game ID is {game_id!r}, not {DL2_GAME_ID!r}")
    if rig.skeleton_hash != DL2_LEGACY_EXPECTED_SKELETON_HASH:
        errors.append("target skeleton hash does not match the bundled 81-node legacy rig")
    if len(rig.bones) != DL2_LEGACY_EXPECTED_BONE_COUNT:
        errors.append(
            f"target bone count is {len(rig.bones)}, expected {DL2_LEGACY_EXPECTED_BONE_COUNT}"
        )
    if len(rig.descriptors) != DL2_LEGACY_EXPECTED_TRACK_COUNT:
        errors.append(
            f"target descriptor count is {len(rig.descriptors)}, expected "
            f"{DL2_LEGACY_EXPECTED_TRACK_COUNT}"
        )

    extensions = dict(rig.extensions or {})
    expected_extensions = {
        "game_id": DL2_GAME_ID,
        "primary_root": "pelvis",
        "finger_policy": "dl2_explicit_finger10_20_30_40_roots",
        "hash_collision_count": 0,
        "reference_descriptor_count": 189,
        "unmatched_reference_descriptor_count": 113,
    }
    for key, expected in expected_extensions.items():
        if extensions.get(key) != expected:
            errors.append(
                f"target provenance field {key!r} is {extensions.get(key)!r}, expected {expected!r}"
            )

    validation = rig.validate(test_writer_capacity=False)
    if validation.errors:
        errors.extend(f"target rig validation: {message}" for message in validation.errors)

    by_name = {bone.name: bone for bone in rig.bones}
    direct_names = [row.target_bone for row in _DL2_ADVANCED_DIRECT_SLOTS]
    if len(direct_names) != DL2_ADVANCED_BODY_DIRECT_SLOT_COUNT:
        errors.append("internal DL2 body policy does not contain exactly 52 direct slots")
    if len(set(direct_names)) != len(direct_names):
        errors.append("internal DL2 body policy contains duplicate target slots")
    missing = sorted(set(direct_names) - by_name.keys(), key=str.casefold)
    if missing:
        errors.append("target is missing policy bones: " + ", ".join(missing))
    helpers = sorted(
        (name for name in direct_names if name in by_name and by_name[name].helper),
        key=str.casefold,
    )
    if helpers:
        errors.append("body policy maps helper targets: " + ", ".join(helpers))
    return tuple(dict.fromkeys(errors))


def _target_archetype(rig: ChromeRig) -> str:
    category = str(rig.category or "").strip().casefold()
    if "humanoid" in category or "character" in category:
        return "humanoid"
    if any(token in category for token in ("object", "mechanical", "prop", "vehicle", "generic")):
        return "generic"
    return "unknown"


def _build_bone_policies(
    rig: ChromeRig,
    *,
    archetype: str,
    chains: tuple[TargetSemanticChain, ...],
    direct_slots: tuple[TargetDirectSlot, ...],
) -> tuple[TargetBonePolicy, ...]:
    slot_by_target = {row.target_bone: row for row in direct_slots}
    chain_by_target = {
        node.name: chain.chain_id for chain in chains for node in chain.nodes
    }
    rows: list[TargetBonePolicy] = []
    for bone in rig.bones:
        slot = slot_by_target.get(bone.name)
        category = _target_category(bone, target_archetype=archetype)
        if slot is not None:
            mode = "direct"
        elif category == "body" and not bone.helper:
            mode = "inherit_bind"
        else:
            mode = "static_bind"
        rows.append(
            TargetBonePolicy(
                target_bone=bone.name,
                descriptor=int(bone.descriptor),
                target_category=category,
                deform=bool(bone.deform),
                helper=bool(bone.helper),
                semantic_role=slot.semantic_role if slot is not None else "",
                chain_id=chain_by_target.get(bone.name, ""),
                default_mode=mode,
            )
        )
    return tuple(rows)


def build_target_retarget_policy(
    target_rig: ChromeRig,
    game_id: str | None = None,
    clip_domain: str = "body",
) -> TargetRetargetPolicy:
    """Build a target policy; only the coherent DL2 advanced/body case authorizes automation.

    This function deliberately does not trust a rig ID alone.  The complete
    built-in provenance, skeleton hash, hierarchy/category inventory, and
    finger policy are recomputed before returning an authorized policy.
    Generic humanoid/object/unknown targets remain useful for route selection
    but cannot act as verified automatic-map certificates.
    """

    if not isinstance(target_rig, ChromeRig):
        raise TypeError("target_rig must be a ChromeRig")
    domain = str(clip_domain or "body").strip().casefold()
    if not domain:
        domain = "body"
    extensions = dict(target_rig.extensions or {})
    resolved_game_id = str(game_id or extensions.get("game_id", "") or "")
    archetype = _target_archetype(target_rig)

    if target_rig.rig_id in {DL2_ADVANCED_RIG_REF, DL2_LEGACY_RIG_REF}:
        advanced = target_rig.rig_id == DL2_ADVANCED_RIG_REF
        errors = (
            _dl2_advanced_coherence_errors(target_rig, game_id=resolved_game_id)
            if advanced
            else _dl2_legacy_coherence_errors(target_rig, game_id=resolved_game_id)
        )
        coherent = not errors
        authorized = coherent and domain == "body"
        chains = _DL2_ADVANCED_CHAINS if coherent else ()
        direct_slots = _DL2_ADVANCED_DIRECT_SLOTS if authorized else ()
        policy_id = (
            (DL2_ADVANCED_BODY_POLICY_ID if advanced else DL2_LEGACY_BODY_POLICY_ID)
            if authorized
            else f"dl2_{'advanced' if advanced else 'legacy'}_{domain}_target_policy_v1"
            if coherent
            else f"conservative_unverified_dl2_{'advanced' if advanced else 'legacy'}_target_v1"
        )
        bones = _build_bone_policies(
            target_rig,
            archetype="humanoid",
            chains=chains,
            direct_slots=direct_slots,
        )
        return TargetRetargetPolicy(
            policy_id=policy_id,
            policy_version=TARGET_RETARGET_POLICY_VERSION,
            target_rig_id=target_rig.rig_id,
            target_skeleton_hash=target_rig.skeleton_hash,
            game_id=resolved_game_id,
            clip_domain=domain,
            target_archetype="humanoid",
            automatic_routing_authorized=authorized,
            bones=bones,
            semantic_chains=chains,
            direct_slots=direct_slots,
            coherence_errors=errors,
        )

    policy_id = f"conservative_{archetype}_target_v1"
    bones = _build_bone_policies(
        target_rig,
        archetype=archetype,
        chains=(),
        direct_slots=(),
    )
    return TargetRetargetPolicy(
        policy_id=policy_id,
        policy_version=TARGET_RETARGET_POLICY_VERSION,
        target_rig_id=target_rig.rig_id,
        target_skeleton_hash=target_rig.skeleton_hash,
        game_id=resolved_game_id,
        clip_domain=domain,
        target_archetype=archetype,
        automatic_routing_authorized=False,
        bones=bones,
        semantic_chains=(),
        direct_slots=(),
    )


__all__ = [
    "DL2_ADVANCED_BODY_DIRECT_SLOT_COUNT",
    "DL2_ADVANCED_BODY_POLICY_ID",
    "DL2_ADVANCED_EXPECTED_BONE_COUNT",
    "DL2_ADVANCED_EXPECTED_SKELETON_HASH",
    "DL2_ADVANCED_EXPECTED_TRACK_COUNT",
    "DL2_LEGACY_BODY_POLICY_ID",
    "DL2_LEGACY_EXPECTED_BONE_COUNT",
    "DL2_LEGACY_EXPECTED_SKELETON_HASH",
    "DL2_LEGACY_EXPECTED_TRACK_COUNT",
    "TARGET_RETARGET_POLICY_VERSION",
    "TargetBonePolicy",
    "TargetDirectSlot",
    "TargetRetargetPolicy",
    "TargetSemanticChain",
    "build_target_retarget_policy",
]
