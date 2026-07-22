from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace
import json
import re

import numpy as np
import pytest

from dlanm2_gui.automatic_retarget import (
    build_automatic_retarget_plan,
    classify_retarget_readiness,
    validate_automatic_retarget_plan,
)
from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.fbx_core import FbxDocument
from dlanm2_gui.semantic_roles import (
    SEMANTIC_LEXICON_VERSION,
    normalize_bone_name,
)
from dlanm2_gui.skeleton_analysis import analyze_source_skeleton
from dlanm2_gui.skeleton_archetypes import detect_source_family_hints
from dlanm2_gui.target_retarget_policy import build_target_retarget_policy


_PARENTS = {
    "pelvis": None,
    "spine1": "pelvis",
    "spine2": "spine1",
    "chest": "spine2",
    "neck": "chest",
    "head": "neck",
    "head_end": "head",
    "left_clavicle": "chest",
    "left_upper_arm": "left_clavicle",
    "left_forearm": "left_upper_arm",
    "left_hand": "left_forearm",
    "right_clavicle": "chest",
    "right_upper_arm": "right_clavicle",
    "right_forearm": "right_upper_arm",
    "right_hand": "right_forearm",
    "left_thigh": "pelvis",
    "left_calf": "left_thigh",
    "left_foot": "left_calf",
    "left_toe": "left_foot",
    "right_thigh": "pelvis",
    "right_calf": "right_thigh",
    "right_foot": "right_calf",
    "right_toe": "right_foot",
}

_POSITIONS = {
    "pelvis": (0.0, 1.0, 0.0),
    "spine1": (0.0, 1.2, 0.0),
    "spine2": (0.0, 1.4, 0.0),
    "chest": (0.0, 1.6, 0.0),
    "neck": (0.0, 1.76, 0.0),
    "head": (0.0, 1.91, 0.0),
    "head_end": (0.0, 2.08, 0.0),
    "left_clavicle": (-0.14, 1.58, 0.0),
    "left_upper_arm": (-0.38, 1.56, 0.0),
    "left_forearm": (-0.66, 1.52, 0.0),
    "left_hand": (-0.88, 1.49, 0.0),
    "right_clavicle": (0.14, 1.58, 0.0),
    "right_upper_arm": (0.38, 1.56, 0.0),
    "right_forearm": (0.66, 1.52, 0.0),
    "right_hand": (0.88, 1.49, 0.0),
    "left_thigh": (-0.12, 0.82, 0.0),
    "left_calf": (-0.12, 0.43, 0.0),
    "left_foot": (-0.12, 0.08, 0.06),
    "left_toe": (-0.12, 0.04, 0.26),
    "right_thigh": (0.12, 0.82, 0.0),
    "right_calf": (0.12, 0.43, 0.0),
    "right_foot": (0.12, 0.08, 0.06),
    "right_toe": (0.12, 0.04, 0.26),
}


class _Scene:
    def __init__(self, model_names, model_subtypes, parent_ids, skin_clusters=()):
        self.model_names = dict(model_names)
        self.model_subtypes = dict(model_subtypes)
        self.model_ids = tuple(model_names)
        self.limb_ids = tuple(
            object_id for object_id in self.model_ids
            if self.model_subtypes[object_id] == "LimbNode"
        )
        self._parent_ids = dict(parent_ids)
        self.axis_settings = {
            "UpAxis": 1,
            "UpAxisSign": 1,
            "FrontAxis": 2,
            "FrontAxisSign": 1,
            "CoordAxis": 0,
            "CoordAxisSign": 1,
        }
        self.geometries = ()
        self.skin_clusters = tuple(skin_clusters)

    def model_parent_id(self, object_id):
        return self._parent_ids.get(object_id)


class _SyntheticDocument:
    def __init__(
        self,
        labels: dict[str, str] | None = None,
        *,
        anonymous: bool = False,
        animated: set[str] | None = None,
        wrapper: bool = True,
    ):
        roles = tuple(_PARENTS)
        if anonymous:
            labels = {role: f"{index + 1:03d}" for index, role in enumerate(roles)}
        labels = {role: (labels or {}).get(role, role) for role in roles}
        ids = {role: index + 1 for index, role in enumerate(roles)}
        self.limb_models = {labels[role]: ids[role] for role in roles}
        self.parent_by_name = {
            labels[role]: (labels[parent] if parent else None)
            for role, parent in _PARENTS.items()
        }
        self.meters_per_unit = 1.0
        self.bind_global_matrices = {}
        for role in roles:
            matrix = np.eye(4, dtype=float)
            matrix[:3, 3] = _POSITIONS[role]
            self.bind_global_matrices[labels[role]] = matrix
        self.bind_source = "Pose::BindPose"
        self.bind_source_by_bone = {
            labels[role]: "Pose::BindPose" for role in roles
        }
        self.bind_coverage = {
            "Pose::BindPose": len(roles), "authoritative": len(roles), "total": len(roles)
        }
        wrapper_id = 999
        model_names = {ids[role]: labels[role] for role in roles}
        model_subtypes = {ids[role]: "LimbNode" for role in roles}
        parent_ids = {
            ids[role]: ids[parent]
            for role, parent in _PARENTS.items() if parent is not None
        }
        if wrapper:
            model_names[wrapper_id] = "Armature"
            model_subtypes[wrapper_id] = "Null"
            parent_ids[ids["pelvis"]] = wrapper_id
        self.scene = _Scene(
            model_names,
            model_subtypes,
            parent_ids,
            skin_clusters=(
                SimpleNamespace(
                    bone_id=ids["pelvis"], bone_name="", weights=(1.0, 0.5)
                ),
            ),
        )
        animated = animated if animated is not None else {
            "pelvis", "spine1", "spine2", "chest", "neck", "head",
            "left_upper_arm", "left_forearm", "left_hand",
            "right_upper_arm", "right_forearm", "right_hand",
            "left_thigh", "left_calf", "left_foot",
            "right_thigh", "right_calf", "right_foot",
        }
        self.curves = {
            (ids[role], "Lcl Rotation", "X"): ([0, 1], [0.0, 10.0])
            for role in animated
        }
        self.selected_animation_stack = SimpleNamespace(name="SyntheticTake")

    def normalized_matrix_to_target_space(self, _name, matrix):
        return np.asarray(matrix, dtype=float).copy()

    def target_basis_matrix(self):
        return np.eye(4, dtype=float)


def _remove_physical_roles(
    document: _SyntheticDocument, roles: set[str]
) -> _SyntheticDocument:
    removed_names = set(roles)
    removed_ids = {
        object_id
        for name, object_id in document.limb_models.items()
        if name in removed_names
    }
    remaining = {
        name: object_id
        for name, object_id in document.limb_models.items()
        if name not in removed_names
    }
    for name in tuple(remaining):
        parent = document.parent_by_name.get(name)
        while parent in removed_names:
            parent = _PARENTS.get(parent)
        document.parent_by_name[name] = parent
    document.parent_by_name = {
        name: document.parent_by_name.get(name) for name in remaining
    }
    document.limb_models = remaining
    for name in removed_names:
        document.bind_global_matrices.pop(name, None)
        document.bind_source_by_bone.pop(name, None)
    document.curves = {
        key: value
        for key, value in document.curves.items()
        if int(key[0]) not in removed_ids
    }
    scene = document.scene
    scene.model_names = {
        object_id: name
        for object_id, name in scene.model_names.items()
        if object_id not in removed_ids
    }
    scene.model_subtypes = {
        object_id: subtype
        for object_id, subtype in scene.model_subtypes.items()
        if object_id not in removed_ids
    }
    scene.model_ids = tuple(
        object_id for object_id in scene.model_ids if object_id not in removed_ids
    )
    scene.limb_ids = tuple(
        object_id for object_id in scene.limb_ids if object_id not in removed_ids
    )
    id_by_name = {name: object_id for name, object_id in remaining.items()}
    for name, object_id in remaining.items():
        parent = document.parent_by_name.get(name)
        if parent in id_by_name:
            scene._parent_ids[object_id] = id_by_name[parent]
        else:
            scene._parent_ids.pop(object_id, None)
    document.bind_coverage = {
        "Pose::BindPose": len(remaining),
        "authoritative": len(remaining),
        "total": len(remaining),
    }
    return document


@pytest.fixture(scope="module")
def dl2_advanced_target():
    root = Path(__file__).resolve().parents[1]
    rig = ChromeRig.load(root / "reference" / "dl2" / "player_skeleton.crig")
    return rig, build_target_retarget_policy(rig, clip_domain="body")


def _dl2_plan(analysis, target):
    rig, policy = target
    plan = build_automatic_retarget_plan(analysis, rig, policy)
    validation = validate_automatic_retarget_plan(plan, analysis, rig, policy)
    assert validation.ok, validation.errors
    assert classify_retarget_readiness(plan).ready
    return plan


def test_unicode_normalization_preserves_original_and_extracts_finger_identity() -> None:
    row = normalize_bone_name("Ａrmature|mixamorig:LeftHandIndex1")
    assert row.original_name == "Ａrmature|mixamorig:LeftHandIndex1"
    assert row.normalized_unicode_name.startswith("armature|")
    assert row.comparison_name == "left hand index 1"
    assert row.side == "left"
    assert row.finger == "index"
    assert row.ordinal == 1
    assert "finger" in row.anatomical_roles
    assert row.transliterated_name == "left hand index 1"
    assert row.to_dict()["lexicon_version"] == SEMANTIC_LEXICON_VERSION


@pytest.mark.parametrize(
    ("name", "role", "side", "script"),
    (
        ("Hueso|Cadera_Izquierda", "pelvis", "left", "Latin"),
        ("Os|AvantBras_Droite", "forearm", "right", "Latin"),
        ("Knochen|Unterarm_Links", "forearm", "left", "Latin"),
        ("Kość|Przedramię_Prawa", "forearm", "right", "Latin"),
        ("Osso|Antebraço_Esquerda", "forearm", "left", "Latin"),
        ("Osso|Avambraccio_Destra", "forearm", "right", "Latin"),
        ("Риг|Левое_Предплечье", "forearm", "left", "Cyrillic"),
        ("Ріг|Праве_Передпліччя", "forearm", "right", "Cyrillic"),
        ("骨架|左前臂", "forearm", "left", "Han"),
        ("アーマチュア|右前腕", "forearm", "right", "Han"),
        ("아마추어|왼쪽_아래팔", "forearm", "left", "Hangul"),
    ),
)
def test_multilingual_anatomy_and_side_lexicon(name, role, side, script) -> None:
    row = normalize_bone_name(name)
    assert role in row.anatomical_roles
    assert row.side == side
    assert script in row.scripts
    if script in {"Han", "Hangul"}:
        assert row.transliterated_name is None


def test_family_adapters_are_hints_into_one_analysis_route() -> None:
    cases = {
        "Mixamo": ["mixamorig:Hips", "mixamorig:LeftArm"],
        "Blender Rigify": ["DEF-spine", "ORG-upper_arm.L", "MCH-hand_ik.L"],
        "Auto-Rig Pro": ["c_root_master.x", "arp_spine"],
        "Maya HumanIK": ["Character1:HIK_Hips", "Character1:HIK_LeftArm"],
        "3ds Max Biped": ["Bip01", "Bip01 L UpperArm"],
        "3ds Max CAT": ["CATParent", "CATRig:LArm"],
        "Unreal Mannequin": [
            "pelvis", "spine_01", "spine_02", "clavicle_l", "clavicle_r",
            "upperarm_l", "upperarm_r", "thigh_l", "thigh_r",
        ],
        "Unity Humanoid": [
            "LeftUpperArm", "RightUpperArm", "LeftUpperLeg", "RightUpperLeg",
        ],
        "MotionBuilder": ["MotionBuilder:Character Controls", "Hips"],
        "Rokoko": ["Rokoko_Hips", "SmartSuit_LeftArm"],
        "AccuRig / ActorCore": ["RL_BoneRoot", "CC_Base_Hip"],
        "generic Blender": ["Armature", "Bone.001"],
        "generic Maya": ["world|skeleton|joint1"],
    }
    for expected, names in cases.items():
        wrappers = ("Armature",) if expected == "generic Blender" else ()
        assert expected in detect_source_family_hints(names, wrappers)


def test_rigify_helper_prefixes_remain_evidence_and_cannot_steal_body_roles() -> None:
    assert normalize_bone_name("ORG-upper_arm.L").helper_tokens == ("org",)
    assert normalize_bone_name("MCH-upper_arm.L").helper_tokens == ("mch",)
    assert normalize_bone_name("DEF-upper_arm.L").helper_tokens == ()

    labels = {
        role: (
            "DEF-" + role.removeprefix("left_").removeprefix("right_")
            + (".L" if role.startswith("left_") else ".R" if role.startswith("right_") else "")
        )
        for role in _PARENTS
    }
    document = _SyntheticDocument(labels)
    for offset, name in enumerate(("ORG-upper_arm.L", "MCH-upper_arm.L"), 1):
        object_id = 1000 + offset
        document.limb_models[name] = object_id
        document.parent_by_name[name] = None
        matrix = np.eye(4, dtype=float)
        matrix[:3, 3] = _POSITIONS["left_upper_arm"]
        document.bind_global_matrices[name] = matrix
        document.bind_source_by_bone[name] = "Pose::BindPose"
        document.scene.model_names[object_id] = name
        document.scene.model_subtypes[object_id] = "LimbNode"
        document.scene.model_ids = (*document.scene.model_ids, object_id)
        document.scene.limb_ids = (*document.scene.limb_ids, object_id)
        document.curves[(object_id, "Lcl Rotation", "X")] = ([0, 1], [0.0, 10.0])

    analysis = analyze_source_skeleton(document)
    deform_name = labels["left_upper_arm"]
    assert analysis.semantic_roles["left_upper_arm"].bone_name == deform_name
    nodes = {row.name: row for row in analysis.nodes}
    assert nodes["ORG-upper_arm.L"].helper_likelihood >= 0.9
    assert nodes["MCH-upper_arm.L"].control_likelihood >= 0.9
    assert nodes[deform_name].helper_likelihood < 0.75

    target_bone = ChromeRigBone(
        index=0,
        name="target_left_upper_arm",
        parent_index=-1,
        descriptor=0x1234,
        bind_translation=(0.0, 0.0, 0.0),
        bind_rotation_wxyz=(1.0, 0.0, 0.0, 0.0),
        tags=("body",),
    )
    rig = ChromeRig(
        "test:rigify-helper-safety",
        "Rigify helper safety",
        "humanoid",
        (target_bone,),
        0,
    )
    policy = SimpleNamespace(
        policy_id="test-rigify-helper-policy-v1",
        policy_version="test-rigify-helper-policy-v1",
        target_archetype="humanoid",
        minimum_confidence=0.70,
        minimum_confidence_margin=0.08,
        bones=(
            SimpleNamespace(
                target_bone=target_bone.name,
                target_category="body",
                semantic_role="left_upper_arm",
                safe_automatic_mapping=True,
                helper=False,
                critical=True,
            ),
        ),
        semantic_chains={},
    )
    plan = build_automatic_retarget_plan(analysis, rig, policy)
    assert plan.decisions[0].mode == "direct"
    assert plan.decisions[0].source_bones == (deform_name,)
    assert not plan.unresolved_animated_chains
    assert classify_retarget_readiness(plan).ready


def test_rigify_control_branches_and_inserted_twists_are_collapsed_from_topology(
    dl2_advanced_target,
) -> None:
    labels = {
        role: (
            "DEF-" + role.removeprefix("left_").removeprefix("right_")
            + (
                ".L"
                if role.startswith("left_")
                else ".R"
                if role.startswith("right_")
                else ""
            )
        )
        for role in _PARENTS
    }
    document = _SyntheticDocument(labels)
    helper_rows: list[tuple[str, str, tuple[float, float, float]]] = []
    for side, suffix in (("left", ".L"), ("right", ".R")):
        helper_rows.extend(
            (
                (
                    f"ORG-upper_arm{suffix}",
                    labels["chest"],
                    _POSITIONS[f"{side}_upper_arm"],
                ),
                (
                    f"MCH-forearm{suffix}",
                    f"ORG-upper_arm{suffix}",
                    _POSITIONS[f"{side}_forearm"],
                ),
                (
                    f"CTRL-hand{suffix}",
                    f"MCH-forearm{suffix}",
                    _POSITIONS[f"{side}_hand"],
                ),
                (
                    f"upper_arm_twist{suffix}",
                    labels[f"{side}_upper_arm"],
                    tuple(
                        (
                            np.asarray(_POSITIONS[f"{side}_upper_arm"])
                            + np.asarray(_POSITIONS[f"{side}_forearm"])
                        )
                        / 2.0
                    ),
                ),
            )
        )
    helper_models: dict[str, int] = {}
    next_id = 1000
    for name, parent, position in helper_rows:
        next_id += 1
        helper_models[name] = next_id
        document.parent_by_name[name] = parent
        matrix = np.eye(4, dtype=float)
        matrix[:3, 3] = position
        document.bind_global_matrices[name] = matrix
        document.bind_source_by_bone[name] = "Pose::BindPose"
        document.curves[(next_id, "Lcl Rotation", "X")] = ([0, 1], [0.0, 10.0])
        document.scene.model_names[next_id] = name
        document.scene.model_subtypes[next_id] = "LimbNode"

    # Put helper/control branches first and insert twists directly between the
    # deform upper-arm and forearm nodes.
    document.limb_models = {**helper_models, **document.limb_models}
    for side, suffix in (("left", ".L"), ("right", ".R")):
        document.parent_by_name[labels[f"{side}_forearm"]] = (
            f"upper_arm_twist{suffix}"
        )
    id_by_name = dict(document.limb_models)
    document.scene.model_ids = tuple(
        [*helper_models.values(), *(
            value
            for value in document.scene.model_ids
            if value not in helper_models.values()
        )]
    )
    document.scene.limb_ids = tuple(document.limb_models.values())
    for name, object_id in document.limb_models.items():
        parent = document.parent_by_name.get(name)
        if parent in id_by_name:
            document.scene._parent_ids[object_id] = id_by_name[parent]
    document.bind_coverage = {
        "Pose::BindPose": len(document.limb_models),
        "authoritative": len(document.limb_models),
        "total": len(document.limb_models),
    }

    analysis = analyze_source_skeleton(document)

    helper_names = set(helper_models)
    assert not helper_names.intersection(
        bone
        for chain in analysis.semantic_chains.values()
        for bone in chain.bone_names
    )
    assert not helper_names.intersection(analysis.unresolved_animated_chains)
    for side in ("left", "right"):
        assert analysis.semantic_roles[f"{side}_upper_arm"].bone_name == labels[
            f"{side}_upper_arm"
        ]
        assert analysis.semantic_roles[f"{side}_forearm"].bone_name == labels[
            f"{side}_forearm"
        ]
        assert analysis.semantic_roles[f"{side}_hand"].bone_name == labels[
            f"{side}_hand"
        ]
    assert any(
        finding.code == "collapsed_helper_limbnodes"
        for finding in analysis.findings
    )

    plan = _dl2_plan(analysis, dl2_advanced_target)
    assert plan.mapping_modes["manual_required"] == 0


def test_anonymous_humanoid_is_inferred_from_topology_bind_and_symmetry() -> None:
    analysis = analyze_source_skeleton(_SyntheticDocument(anonymous=True))
    assert analysis.archetype == "humanoid"
    assert analysis.archetype_confidence >= 0.75
    assert analysis.body_frame is not None
    assert analysis.semantic_roles["pelvis"].source == "topology_bind"
    assert analysis.semantic_roles["left_thigh"].bone_name != analysis.semantic_roles["right_thigh"].bone_name
    assert analysis.semantic_chains["left_arm"].bone_names
    assert analysis.animation_domain == "full_body"
    assert analysis.observed_motion_domain == "full_body"
    assert analysis.clip_domain == "body"
    assert analysis.wrapper_models == ("Armature",)
    pelvis = next(row for row in analysis.nodes if row.name == analysis.semantic_roles["pelvis"].bone_name)
    assert pelvis.skin_weight == pytest.approx(1.5)
    assert pelvis.bind_global_matrix is not None
    assert pelvis.descendant_count == len(analysis.nodes) - 1
    assert analysis.name_parent_hash == analysis.skeleton_hash
    assert analysis.hierarchy_hash == analysis.skeleton_hash
    assert len(analysis.animation_hash) == 64
    json.dumps(analysis.to_dict(), ensure_ascii=False)


def test_non_latin_humanoid_uses_lexicon_and_the_same_topology_route() -> None:
    labels = {
        "pelvis": "骨盆",
        "spine1": "脊柱1",
        "spine2": "脊柱2",
        "chest": "胸部",
        "neck": "颈",
        "head": "头",
        "head_end": "头_末端",
        "left_clavicle": "左锁骨",
        "left_upper_arm": "左上臂",
        "left_forearm": "左前臂",
        "left_hand": "左手",
        "right_clavicle": "右锁骨",
        "right_upper_arm": "右上臂",
        "right_forearm": "右前臂",
        "right_hand": "右手",
        "left_thigh": "左大腿",
        "left_calf": "左小腿",
        "left_foot": "左脚",
        "left_toe": "左脚趾",
        "right_thigh": "右大腿",
        "right_calf": "右小腿",
        "right_foot": "右脚",
        "right_toe": "右脚趾",
    }
    analysis = analyze_source_skeleton(_SyntheticDocument(labels))
    assert analysis.archetype == "humanoid"
    assert analysis.semantic_roles["left_forearm"].bone_name == "左前臂"
    assert "script:Han" in analysis.source_name_languages_or_scripts
    assert "language:zh" in analysis.source_name_languages_or_scripts
    assert not [row for row in analysis.findings if row.severity == "blocking"]


def test_missing_optional_terminal_and_upper_body_domain_do_not_block_analysis() -> None:
    document = _SyntheticDocument(animated={
        "spine1", "spine2", "chest", "left_upper_arm", "left_forearm",
        "right_upper_arm", "right_forearm",
    })
    analysis = analyze_source_skeleton(document)
    assert analysis.animation_domain == "upper_body"
    assert analysis.unresolved_animated_chains == ()
    assert set(analysis.animated_chains_detected) >= {"spine", "left_arm", "right_arm"}
    assert not [row for row in analysis.findings if row.severity == "blocking"]


def test_physically_absent_bilateral_legs_remain_safe_upper_body_humanoid(
    dl2_advanced_target,
) -> None:
    leg_roles = {
        f"{side}_{role}"
        for side in ("left", "right")
        for role in ("thigh", "calf", "foot", "toe")
    }
    document = _remove_physical_roles(_SyntheticDocument(), leg_roles)

    analysis = analyze_source_skeleton(document)

    assert analysis.archetype == "humanoid"
    assert analysis.archetype_confidence >= 0.75
    assert analysis.animation_domain == "upper_body"
    assert analysis.body_frame is not None
    assert analysis.body_frame.pelvis_bone == "pelvis"
    assert not leg_roles.intersection(analysis.semantic_roles)
    assert set(analysis.animated_chains_detected) >= {
        "spine", "left_arm", "right_arm"
    }

    plan = _dl2_plan(analysis, dl2_advanced_target)
    by_target = {row.target_bone: row for row in plan.decisions}
    for side in ("l", "r"):
        for target_name in (
            f"{side}_thigh",
            f"{side}_calf",
            f"{side}_foot",
            f"{side}_toebase",
        ):
            assert by_target[target_name].mode == "inherit_bind"
            assert by_target[target_name].source_bones == ()
    assert not plan.unresolved_required_roles
    assert plan.warnings_shown_to_user == ()


def test_named_three_node_arm_without_hands_does_not_shift_roles(
    dl2_advanced_target,
) -> None:
    document = _remove_physical_roles(
        _SyntheticDocument(), {"left_hand", "right_hand"}
    )

    analysis = analyze_source_skeleton(document)

    for side in ("left", "right"):
        assert analysis.semantic_roles[f"{side}_clavicle"].bone_name == (
            f"{side}_clavicle"
        )
        assert analysis.semantic_roles[f"{side}_upper_arm"].bone_name == (
            f"{side}_upper_arm"
        )
        assert analysis.semantic_roles[f"{side}_forearm"].bone_name == (
            f"{side}_forearm"
        )
        assert f"{side}_hand" not in analysis.semantic_roles
        assert analysis.semantic_chains[f"{side}_arm"].semantic_roles == (
            f"{side}_clavicle",
            f"{side}_upper_arm",
            f"{side}_forearm",
        )

    plan = _dl2_plan(analysis, dl2_advanced_target)
    by_target = {row.target_bone: row for row in plan.decisions}
    for prefix in ("l", "r"):
        assert by_target[f"{prefix}_clavicle"].mode == "direct"
        assert by_target[f"{prefix}_upperarm"].mode == "direct"
        assert by_target[f"{prefix}_forearm"].mode == "direct"
        assert by_target[f"{prefix}_hand"].mode == "inherit_bind"


def test_named_three_node_arm_without_clavicles_does_not_shift_roles(
    dl2_advanced_target,
) -> None:
    document = _remove_physical_roles(
        _SyntheticDocument(), {"left_clavicle", "right_clavicle"}
    )

    analysis = analyze_source_skeleton(document)

    for side in ("left", "right"):
        assert f"{side}_clavicle" not in analysis.semantic_roles
        assert analysis.semantic_roles[f"{side}_upper_arm"].bone_name == (
            f"{side}_upper_arm"
        )
        assert analysis.semantic_roles[f"{side}_forearm"].bone_name == (
            f"{side}_forearm"
        )
        assert analysis.semantic_roles[f"{side}_hand"].bone_name == f"{side}_hand"
        assert analysis.semantic_chains[f"{side}_arm"].semantic_roles == (
            f"{side}_upper_arm",
            f"{side}_forearm",
            f"{side}_hand",
        )

    plan = _dl2_plan(analysis, dl2_advanced_target)
    by_target = {row.target_bone: row for row in plan.decisions}
    for prefix in ("l", "r"):
        assert by_target[f"{prefix}_clavicle"].mode == "inherit_bind"
        assert by_target[f"{prefix}_upperarm"].mode == "direct"
        assert by_target[f"{prefix}_forearm"].mode == "direct"
        assert by_target[f"{prefix}_hand"].mode == "direct"


@pytest.mark.parametrize(
    ("missing_role", "missing_target"),
    (
        ("upper_arm", "upperarm"),
        ("forearm", "forearm"),
    ),
)
def test_named_arm_interior_gap_is_not_positionally_compressed(
    dl2_advanced_target,
    missing_role: str,
    missing_target: str,
) -> None:
    document = _remove_physical_roles(
        _SyntheticDocument(),
        {f"left_{missing_role}", f"right_{missing_role}"},
    )

    analysis = analyze_source_skeleton(document)

    expected_roles = tuple(
        role
        for role in ("clavicle", "upper_arm", "forearm", "hand")
        if role != missing_role
    )
    for side in ("left", "right"):
        assert f"{side}_{missing_role}" not in analysis.semantic_roles
        for role in expected_roles:
            assert analysis.semantic_roles[f"{side}_{role}"].bone_name == (
                f"{side}_{role}"
            )
        assert analysis.semantic_chains[f"{side}_arm"].bone_names == tuple(
            f"{side}_{role}" for role in expected_roles
        )
        assert analysis.semantic_chains[f"{side}_arm"].semantic_roles == tuple(
            f"{side}_{role}" for role in expected_roles
        )

    rig, policy = dl2_advanced_target
    plan = build_automatic_retarget_plan(analysis, rig, policy)
    by_target = {row.target_bone: row for row in plan.decisions}
    for side, prefix in (("left", "l"), ("right", "r")):
        missing = by_target[f"{prefix}_{missing_target}"]
        assert missing.mode == "inherit_bind"
        assert missing.source_bones == ()
        assert missing.critical
        assert not missing.animated
        for role, target in (
            ("clavicle", "clavicle"),
            ("upper_arm", "upperarm"),
            ("forearm", "forearm"),
            ("hand", "hand"),
        ):
            if role != missing_role:
                assert by_target[f"{prefix}_{target}"].mode == "direct"
                assert by_target[f"{prefix}_{target}"].source_bones == (
                    f"{side}_{role}",
                )

    assert plan.unresolved_required_roles == ()
    readiness = classify_retarget_readiness(plan)
    assert readiness.ready
    validation = validate_automatic_retarget_plan(plan, analysis, rig, policy)
    assert validation.ok


def test_anonymous_animated_arm_gap_stays_unresolved_without_side_only_roles(
    dl2_advanced_target,
) -> None:
    document = _remove_physical_roles(
        _SyntheticDocument(), {"left_upper_arm", "right_upper_arm"}
    )
    gap_names: list[str] = []
    for offset, side in enumerate(("left", "right"), start=1):
        gap_name = f"{side}_gap_segment"
        gap_names.append(gap_name)
        object_id = 2000 + offset
        document.limb_models[gap_name] = object_id
        document.parent_by_name[gap_name] = f"{side}_clavicle"
        document.parent_by_name[f"{side}_forearm"] = gap_name
        matrix = np.eye(4, dtype=float)
        matrix[:3, 3] = _POSITIONS[f"{side}_upper_arm"]
        document.bind_global_matrices[gap_name] = matrix
        document.bind_source_by_bone[gap_name] = "Pose::BindPose"
        document.curves[(object_id, "Lcl Rotation", "X")] = (
            [0, 1],
            [0.0, 10.0],
        )
        document.scene.model_names[object_id] = gap_name
        document.scene.model_subtypes[object_id] = "LimbNode"
        document.scene.model_ids = (*document.scene.model_ids, object_id)
        document.scene.limb_ids = (*document.scene.limb_ids, object_id)
        document.scene._parent_ids[object_id] = document.limb_models[
            f"{side}_clavicle"
        ]
        document.scene._parent_ids[document.limb_models[f"{side}_forearm"]] = (
            object_id
        )
    document.bind_coverage = {
        "Pose::BindPose": len(document.limb_models),
        "authoritative": len(document.limb_models),
        "total": len(document.limb_models),
    }

    analysis = analyze_source_skeleton(document)

    assert "left_" not in analysis.semantic_roles
    assert "right_" not in analysis.semantic_roles
    assert not set(gap_names).intersection(
        candidate.bone_name for candidate in analysis.semantic_roles.values()
    )
    assert set(gap_names).issubset(analysis.unresolved_animated_chains)
    for side, gap_name in zip(("left", "right"), gap_names):
        chain = analysis.semantic_chains[f"{side}_arm"]
        assert gap_name not in chain.bone_names
        assert chain.semantic_roles == (
            f"{side}_clavicle",
            f"{side}_forearm",
            f"{side}_hand",
        )

    rig, policy = dl2_advanced_target
    plan = build_automatic_retarget_plan(analysis, rig, policy)
    by_target = {row.target_bone: row for row in plan.decisions}
    assert set(gap_names).issubset(plan.unresolved_animated_chains)
    for prefix in ("l", "r"):
        assert by_target[f"{prefix}_upperarm"].mode == "inherit_bind"
        assert by_target[f"{prefix}_upperarm"].source_bones == ()
    readiness = classify_retarget_readiness(plan)
    assert readiness.ready
    validation = validate_automatic_retarget_plan(plan, analysis, rig, policy)
    assert validation.ok
    assert any(
        "ignored unmapped animated source chains" in warning
        for warning in validation.warnings
    )


def test_name_tokens_do_not_force_a_non_humanoid_chain() -> None:
    names = ("root", "pelvis", "head", "left_leg")
    document = SimpleNamespace(
        limb_models={name: index + 1 for index, name in enumerate(names)},
        parent_by_name={"root": None, "pelvis": "root", "head": "pelvis", "left_leg": "head"},
        bind_global_matrices={
            name: np.eye(4, dtype=float) for name in names
        },
        bind_source="synthetic",
        bind_source_by_bone={},
        bind_coverage={"total": 4},
        meters_per_unit=1.0,
        curves={},
        selected_animation_stack=None,
    )
    for index, name in enumerate(names):
        document.bind_global_matrices[name][:3, 3] = (0.0, float(index), 0.0)
    analysis = analyze_source_skeleton(document)
    assert analysis.archetype in {"generic", "unknown"}
    assert analysis.archetype != "humanoid"
    assert any(row.code == "conservative_archetype_rejection" for row in analysis.findings)


def test_nfkc_casefold_collision_is_reported_without_losing_originals() -> None:
    names = ("Ｂｏｎｅ_É", "bone_e\u0301")
    document = SimpleNamespace(
        limb_models={names[0]: 1, names[1]: 2},
        parent_by_name={names[0]: None, names[1]: names[0]},
        bind_global_matrices={names[0]: np.eye(4), names[1]: np.eye(4)},
        bind_source="synthetic",
        bind_source_by_bone={},
        bind_coverage={"total": 2},
        meters_per_unit=1.0,
        curves={},
        selected_animation_stack=None,
    )
    document.bind_global_matrices[names[1]][1, 3] = 1.0
    analysis = analyze_source_skeleton(document)
    collision = next(row for row in analysis.findings if row.code == "normalized_name_collision")
    assert collision.bone_names == names
    assert {row.name for row in analysis.nodes} == set(names)


def test_equal_semantic_name_candidates_keep_a_zero_runner_up_margin() -> None:
    names = ("Root", "LeftUpperArm_A", "LeftUpperArm_B")
    matrices = {name: np.eye(4, dtype=float) for name in names}
    matrices[names[1]][:3, 3] = (-0.5, 1.0, 0.0)
    matrices[names[2]][:3, 3] = (-0.5, 1.1, 0.0)
    document = SimpleNamespace(
        limb_models={name: index + 1 for index, name in enumerate(names)},
        parent_by_name={names[0]: None, names[1]: names[0], names[2]: names[0]},
        bind_global_matrices=matrices,
        bind_source="synthetic",
        bind_source_by_bone={},
        bind_coverage={"total": len(names)},
        meters_per_unit=1.0,
        curves={(2, "Lcl Rotation", "X"): ([0, 1], [0.0, 10.0])},
        selected_animation_stack=SimpleNamespace(name="AmbiguousTake"),
    )

    analysis = analyze_source_skeleton(document)

    candidate = analysis.semantic_roles["left_upper_arm"]
    assert candidate.confidence_margin == 0.0
    assert any(
        row.code == "ambiguous_semantic_role"
        for row in analysis.findings
    )


@pytest.mark.skipif(
    not Path(r"S:\Downloads\Thriller Combined - Parts 1-4.fbx").is_file(),
    reason="private Thriller regression fixture is not available",
)
def test_private_thriller_fixture_has_52_usable_animated_body_roles() -> None:
    document = FbxDocument(
        Path(r"S:\Downloads\Thriller Combined - Parts 1-4.fbx"),
        animation_stack="mixamo.com",
        purpose="animation",
        tolerance="recommended",
    )
    analysis = analyze_source_skeleton(document)
    assert len(analysis.nodes) == 65
    assert analysis.archetype == "humanoid"
    assert "Mixamo" in analysis.source_family_hints
    digit_pattern = re.compile(
        r"^(left|right)_(thumb|index|middle|ring|pinky)_[123]$"
    )
    digit_roles = {
        role for role in analysis.semantic_roles if digit_pattern.fullmatch(role)
    }
    assert len(digit_roles) == 30
    assert all(
        analysis.semantic_roles[role].confidence >= 0.85
        and analysis.semantic_roles[role].confidence_margin >= 0.1
        for role in digit_roles
    )
    endpoint_roles = {
        role for role in analysis.semantic_roles if "_endpoint_4" in role
    }
    assert len(endpoint_roles) == 10
    usable_animated = {
        candidate.bone_name
        for role, candidate in analysis.semantic_roles.items()
        if candidate.confidence >= 0.7
        and candidate.bone_name in analysis.animated_bones
        and "endpoint" not in role
    }
    assert len(usable_animated) == 52
    assert analysis.animation_domain == "full_body"
    assert not [row for row in analysis.findings if row.severity == "blocking"]
