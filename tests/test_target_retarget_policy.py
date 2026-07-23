from __future__ import annotations

from copy import deepcopy
from pathlib import Path

from dlanm2_gui.chrome_rig import Anm2WriterProfile, ChromeRig, ChromeRigBone
from dlanm2_gui.game_profiles import DL1_GAME_ID, DL2_GAME_ID
from dlanm2_gui.semantic_chain_alignment import (
    SemanticChainNode,
    align_semantic_chains,
)
from dlanm2_gui.target_retarget_policy import (
    DL2_ADVANCED_BODY_POLICY_ID,
    DL2_ADVANCED_EXPECTED_SKELETON_HASH,
    build_target_retarget_policy,
)


ROOT = Path(__file__).resolve().parents[1]
DL2_ADVANCED_CRIG = ROOT / "reference" / "dl2" / "player_skeleton.crig"


def _advanced_rig() -> ChromeRig:
    return ChromeRig.load(DL2_ADVANCED_CRIG)


def _minimal_rig(*, category: str, rig_id: str = "custom:test") -> ChromeRig:
    root = ChromeRigBone(
        0,
        "root",
        -1,
        0x00000001,
        (0.0, 0.0, 0.0),
        (1.0, 0.0, 0.0, 0.0),
        deform=True,
        helper=False,
    )
    return ChromeRig(
        rig_id=rig_id,
        name="Synthetic target",
        category=category,
        bones=(root,),
        root_index=0,
        writer_profile=Anm2WriterProfile(),
        track_descriptors=(root.descriptor,),
    )


def test_coherent_dl2_advanced_body_policy_has_exact_inventory_and_no_deform_mutation() -> None:
    rig = _advanced_rig()
    deform_before = tuple((bone.name, bone.deform, bone.helper) for bone in rig.bones)

    policy = build_target_retarget_policy(rig, DL2_GAME_ID, "body")

    assert policy.policy_id == DL2_ADVANCED_BODY_POLICY_ID
    assert policy.target_skeleton_hash == DL2_ADVANCED_EXPECTED_SKELETON_HASH
    assert policy.automatic_routing_authorized
    assert policy.coherence_errors == ()
    assert policy.target_row_count == 271
    assert policy.direct_slot_count == 52
    assert policy.bind_default_count == 219
    assert policy.category_counts == {
        "body": 76,
        "facial": 167,
        "secondary_animation": 14,
        "attachment": 8,
        "collar": 4,
        "camera": 2,
    }
    assert len(policy.helper_targets) == 12
    assert policy.mapping_mode_counts == {
        "direct": 52,
        "inherit_bind": 22,
        "static_bind": 197,
    }
    assert deform_before == tuple((bone.name, bone.deform, bone.helper) for bone in rig.bones)


def test_dl2_direct_slots_are_anatomical_and_finger_roots_remain_bind_held() -> None:
    policy = build_target_retarget_policy(_advanced_rig(), DL2_GAME_ID)
    slots = {row.semantic_role: row.target_bone for row in policy.direct_slots}

    assert slots["pelvis"] == "pelvis"
    assert [slots[f"spine_{index}"] for index in (1, 2, 3)] == [
        "spine",
        "spine2",
        "spine3",
    ]
    assert slots["neck_1"] == "neck"
    assert slots["head"] == "head"
    for side, prefix in (("left", "l"), ("right", "r")):
        assert slots[f"{side}_clavicle"] == f"{prefix}_clavicle"
        assert slots[f"{side}_upper_arm"] == f"{prefix}_upperarm"
        assert slots[f"{side}_forearm"] == f"{prefix}_forearm"
        assert slots[f"{side}_hand"] == f"{prefix}_hand"
        assert slots[f"{side}_thigh"] == f"{prefix}_thigh"
        assert slots[f"{side}_calf"] == f"{prefix}_calf"
        assert slots[f"{side}_foot"] == f"{prefix}_foot"
        assert slots[f"{side}_toe"] == f"{prefix}_toebase"
        for digit_name, digit in (
            ("thumb", "0"),
            ("index", "1"),
            ("middle", "2"),
            ("ring", "3"),
            ("pinky", "4"),
        ):
            for segment in (1, 2, 3):
                assert slots[f"{side}_{digit_name}_{segment}"] == (
                    f"{prefix}_finger{digit}{segment}"
                )
            if digit != "0":
                root = policy.bone_policy(f"{prefix}_finger{digit}0")
                assert root.default_mode == "inherit_bind"
                assert root.target_bone not in policy.direct_target_bones


def test_dl2_semantic_chains_expose_full_target_topology_and_three_spine_slots() -> None:
    policy = build_target_retarget_policy(_advanced_rig(), DL2_GAME_ID)
    spine = policy.semantic_chain("spine")
    assert spine.target_bones == (
        "hspine",
        "spine",
        "spine1",
        "spine2",
        "spine3",
        "hspine1",
    )
    assert [row.semantic_role for row in spine.direct_slots] == [
        "spine_1",
        "spine_2",
        "spine_3",
    ]
    assert policy.semantic_chain("left_index").target_bones == (
        "l_finger10",
        "l_finger11",
        "l_finger12",
        "l_finger13",
    )
    assert policy.semantic_chain("left_thumb").target_bones == (
        "l_finger01",
        "l_finger02",
        "l_finger03",
    )


def test_dl2_policy_fails_closed_for_wrong_game_domain_hash_or_provenance() -> None:
    rig = _advanced_rig()
    wrong_game = build_target_retarget_policy(rig, DL1_GAME_ID)
    assert not wrong_game.automatic_routing_authorized
    assert any("game ID" in row for row in wrong_game.coherence_errors)

    facial = build_target_retarget_policy(rig, DL2_GAME_ID, "facial")
    assert not facial.automatic_routing_authorized
    assert facial.coherence_errors == ()
    assert facial.direct_slot_count == 0
    assert facial.bind_default_count == 271

    tampered = deepcopy(rig)
    tampered.bones = (
        ChromeRigBone(
            **{
                **{
                    field: getattr(tampered.bones[0], field)
                    for field in tampered.bones[0].__dataclass_fields__
                },
                "descriptor": tampered.bones[0].descriptor ^ 1,
            }
        ),
        *tampered.bones[1:],
    )
    changed_hash = build_target_retarget_policy(tampered, DL2_GAME_ID)
    assert not changed_hash.automatic_routing_authorized
    assert any("skeleton hash" in row for row in changed_hash.coherence_errors)

    stale_provenance = deepcopy(rig)
    stale_provenance.extensions["source_smd_sha256"] = "0" * 64
    provenance = build_target_retarget_policy(stale_provenance, DL2_GAME_ID)
    assert provenance.automatic_routing_authorized
    assert provenance.coherence_errors == ()


def test_generic_humanoid_and_unknown_targets_never_self_authorize() -> None:
    humanoid = build_target_retarget_policy(_minimal_rig(category="Humanoid"))
    assert humanoid.target_archetype == "humanoid"
    assert humanoid.policy_id == "conservative_humanoid_target_v1"
    assert not humanoid.automatic_routing_authorized
    assert humanoid.direct_slots == ()

    unknown = build_target_retarget_policy(_minimal_rig(category="Creature"))
    assert unknown.target_archetype == "unknown"
    assert unknown.policy_id == "conservative_unknown_target_v1"
    assert not unknown.automatic_routing_authorized


def test_semantic_chain_alignment_direct_composed_and_distributed_modes() -> None:
    direct = align_semantic_chains(
        [
            SemanticChainNode("source_a", "a", "left"),
            SemanticChainNode("source_b", "b", "left", "source_a"),
        ],
        [
            SemanticChainNode("target_a", "a", "left"),
            SemanticChainNode("target_b", "b", "left", "target_a"),
        ],
    )
    assert [row.mode for row in direct] == ["direct", "direct"]

    composed = align_semantic_chains(
        ["source_1", "source_2", "source_3", "source_4"],
        ["target_1", "target_2"],
    )
    assert [row.mode for row in composed] == ["composed", "composed"]
    assert composed[0].source_bones == ("source_1", "source_2")
    assert composed[1].source_bones == ("source_3", "source_4")

    distributed = align_semantic_chains(
        ["source_1", "source_2"],
        ["target_1", "target_2", "target_3", "target_4"],
    )
    assert [row.mode for row in distributed] == [
        "distributed",
        "distributed",
        "distributed",
        "distributed",
    ]
    assert [row.source_weights for row in distributed] == [(0.5,)] * 4


def test_semantic_chain_alignment_missing_terminals_inherit_bind_without_manual_rows() -> None:
    decisions = align_semantic_chains(
        [
            SemanticChainNode("source_thigh", "thigh", "left"),
            SemanticChainNode("source_calf", "calf", "left", "source_thigh"),
        ],
        [
            SemanticChainNode("target_thigh", "thigh", "left"),
            SemanticChainNode("target_calf", "calf", "left", "target_thigh"),
            SemanticChainNode(
                "target_foot", "foot", "left", "target_calf", optional=True
            ),
            SemanticChainNode(
                "target_toe", "toe", "left", "target_foot", optional=True
            ),
        ],
    )
    assert [row.mode for row in decisions] == [
        "direct",
        "direct",
        "inherit_bind",
        "inherit_bind",
    ]
    assert not any(row.mode == "manual_required" for row in decisions)


def test_semantic_chain_alignment_static_side_topology_and_margin_safety() -> None:
    static = align_semantic_chains(
        [],
        [
            SemanticChainNode("socket", "socket", static=True),
            SemanticChainNode("optional_child", "", parent="socket", optional=True),
        ],
    )
    assert [row.mode for row in static] == ["static_bind", "inherit_bind"]

    wrong_side = align_semantic_chains(
        [SemanticChainNode("source_arm", "arm", "left")],
        [SemanticChainNode("target_arm", "arm", "right")],
    )
    assert wrong_side[0].mode == "static_bind"
    assert "conflicts" in wrong_side[0].reason

    broken_topology = align_semantic_chains(
        [
            SemanticChainNode("a", "a"),
            SemanticChainNode("b", "b", parent="not_a"),
        ],
        [SemanticChainNode("target", "a")],
    )
    assert broken_topology[0].mode == "static_bind"
    assert "parent-consistent" in broken_topology[0].reason

    ambiguous = align_semantic_chains(
        [SemanticChainNode("source", "arm", "left")],
        [SemanticChainNode("target", "arm", "left")],
        confidence=0.95,
        confidence_margin=0.01,
    )
    assert ambiguous[0].mode == "static_bind"
    assert "margin" in ambiguous[0].reason
