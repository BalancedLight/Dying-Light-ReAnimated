from __future__ import annotations

from types import SimpleNamespace

import numpy as np
import pytest

from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.fbx_preflight import ERROR, FbxPreflightReport
from dlanm2_gui.model_importer.msh_builder import humanoid_bone_mapping
from dlanm2_gui.bone_maps import GenericBoneMap
from dlanm2_gui.retarget_mapping import (
    auto_map_crig_to_fbx,
    mapping_rows_for_ui,
    scan_humanoid_bones,
    source_mapping_evidence,
)
from dlanm2_gui.trackmap import dl_name_hash


def test_native_dl_finger_segments_are_classified_consistently() -> None:
    scan = scan_humanoid_bones(
        ["l_finger01", "l_finger11", "l_finger12", "l_finger13"]
    )
    assert scan["l_finger01"].role == "l_thumb_1"
    assert scan["l_finger11"].role == "l_index_1"
    assert scan["l_finger12"].role == "l_index_2"
    assert scan["l_finger13"].role == "l_index_3"


def test_mapping_repair_error_blocks_build_but_not_project_import() -> None:
    report = FbxPreflightReport("different.fbx", "animation")
    report.add(
        ERROR,
        "required_target_bones_missing",
        "Target bones are absent.",
        "The skeletons differ.",
        "Review the generated map.",
        can_continue=True,
    )

    assert report.blocking
    assert not report.import_blocking
    assert report.to_dict()["repairable"] is True
    with pytest.raises(ValueError, match="blocked the build"):
        report.require_buildable()


def test_model_import_uses_shared_scan_and_ignores_cc_helpers() -> None:
    source_names = [
        "root", "pelvis", "CC_Base_Pelvis",
        "spine_01", "spine_02", "spine_03", "neck_01", "head",
        "clavicle_l", "upperarm_l", "lowerarm_l", "hand_l",
        "clavicle_r", "upperarm_r", "lowerarm_r", "hand_r",
        "thigh_l", "calf_l", "foot_l", "ball_l",
        "thigh_r", "calf_r", "foot_r", "ball_r",
        "CC_Base_L_ForearmTwist01", "CC_Base_FacialBone",
    ]
    target_names = [
        "bip01", "pelvis", "spine", "spine2", "spine3", "neck", "head",
        "l_clavicle", "l_upperarm", "l_forearm", "l_hand",
        "r_clavicle", "r_upperarm", "r_forearm", "r_hand",
        "l_thigh", "l_calf", "l_foot", "l_toebase",
        "r_thigh", "r_calf", "r_foot", "r_toebase",
    ]
    scene = SimpleNamespace(model_names=dict(enumerate(source_names)))
    nodes = [SimpleNamespace(name=name) for name in target_names]

    mapping, report = humanoid_bone_mapping(
        scene, list(range(len(source_names))), nodes
    )
    mapped = {
        source_names[source]: target_names[target]
        for source, target in mapping.items()
        if target is not None
    }

    assert mapped["root"] == "bip01"
    assert mapped["pelvis"] == "pelvis"
    assert mapped["spine_01"] == "spine"
    assert mapped["spine_02"] == "spine2"
    assert mapped["lowerarm_l"] == "l_forearm"
    assert mapped["ball_r"] == "r_toebase"
    assert "CC_Base_Pelvis" not in mapped
    assert "CC_Base_L_ForearmTwist01" not in mapped
    assert report["directly_mapped_count"] == len(target_names)


def test_animation_workspace_mapper_uses_the_same_suffix_heuristics() -> None:
    rig_names = ["bip01", "pelvis", "spine", "spine2", "l_upperarm", "l_forearm"]
    rig_bones = tuple(
        ChromeRigBone(
            index=index,
            name=name,
            parent_index=index - 1,
            descriptor=dl_name_hash(name),
            bind_translation=(0.0, 0.0, 0.0),
            bind_rotation_wxyz=(1.0, 0.0, 0.0, 0.0),
            bind_scale=(1.0, 1.0, 1.0),
            deform=index != 0,
            helper=index == 0,
        )
        for index, name in enumerate(rig_names)
    )
    rig = ChromeRig(
        "test:humanoid",
        "Test Humanoid",
        "Humanoid",
        rig_bones,
        0,
        track_descriptors=tuple(bone.descriptor for bone in rig_bones),
    )
    source_names = [
        "root", "pelvis", "spine_01", "spine_02", "upperarm_l", "lowerarm_l"
    ]
    parents = {
        name: source_names[index - 1] if index else None
        for index, name in enumerate(source_names)
    }

    profile = auto_map_crig_to_fbx(rig, source_names, parents)
    pairs = {pair.source_bone: pair.target_bone for pair in profile.pairs}

    assert pairs["bip01"] == "root"
    assert pairs["pelvis"] == "pelvis"
    assert pairs["spine"] == "spine_01"
    assert pairs["spine2"] == "spine_02"
    assert pairs["l_upperarm"] == "upperarm_l"
    assert pairs["l_forearm"] == "lowerarm_l"


def test_automatic_mapping_records_reviewable_top_and_runner_up_evidence() -> None:
    rig_bones = (
        ChromeRigBone(
            index=0,
            name="pelvis",
            parent_index=-1,
            descriptor=dl_name_hash("pelvis"),
            bind_translation=(0.0, 0.0, 0.0),
            bind_rotation_wxyz=(1.0, 0.0, 0.0, 0.0),
            bind_scale=(1.0, 1.0, 1.0),
            deform=True,
            helper=False,
        ),
    )
    rig = ChromeRig(
        "test:evidence",
        "Evidence target",
        "Humanoid",
        rig_bones,
        0,
        track_descriptors=(rig_bones[0].descriptor,),
    )
    source_names = ["hips", "root"]
    source_parents = {"hips": None, "root": None}

    profile = auto_map_crig_to_fbx(rig, source_names, source_parents)
    evidence = profile.extensions["automatic_mapping_evidence_v2"]
    assert len(evidence) == 1
    assert evidence[0]["top_candidate"]["source_fbx_bone"] == "hips"
    assert evidence[0]["runner_up_candidate"]["source_fbx_bone"] == "root"
    assert evidence[0]["spatial_evidence_available"] is False
    assert evidence[0]["review_required"] is True

    loaded = GenericBoneMap.from_dict(profile.to_dict())
    _profile, rows = mapping_rows_for_ui(
        rig,
        source_names,
        source_parents,
        loaded,
    )
    assert rows[0]["review_state"] == "automatic_unreviewed"
    assert rows[0]["review_required"] is True
    assert rows[0]["top_candidate"] == "hips"
    assert rows[0]["runner_up"] == "root"


def test_spatial_global_assignment_is_deterministic_and_keeps_ties_unreviewed() -> None:
    rig_bones = (
        ChromeRigBone(
            0,
            "rig_root",
            -1,
            dl_name_hash("rig_root"),
            (0.0, 0.0, 0.0),
            (1.0, 0.0, 0.0, 0.0),
            deform=False,
            helper=True,
        ),
        ChromeRigBone(
            1,
            "rig_first",
            0,
            dl_name_hash("rig_first"),
            (0.0, 1.0, 0.0),
            (1.0, 0.0, 0.0, 0.0),
        ),
        ChromeRigBone(
            2,
            "rig_second",
            0,
            dl_name_hash("rig_second"),
            (0.0, 1.0, 0.0),
            (1.0, 0.0, 0.0, 0.0),
        ),
    )
    rig = ChromeRig(
        "test:spatial-tie",
        "Spatial tie",
        "Other",
        rig_bones,
        0,
        track_descriptors=tuple(row.descriptor for row in rig_bones),
    )
    parents = {
        "anim_root": None,
        "alpha": "anim_root",
        "beta": "anim_root",
    }
    root = np.eye(4, dtype=float)
    child = np.eye(4, dtype=float)
    child[1, 3] = 100.0
    bind_globals = {"anim_root": root, "alpha": child, "beta": child.copy()}

    first = auto_map_crig_to_fbx(
        rig,
        ["beta", "anim_root", "alpha"],
        parents,
        source_bind_globals=bind_globals,
        source_deform_bones={"alpha", "beta"},
    )
    second = auto_map_crig_to_fbx(
        rig,
        ["alpha", "beta", "anim_root"],
        parents,
        source_bind_globals=bind_globals,
        source_deform_bones={"alpha", "beta"},
    )
    first_pairs = {
        row.target_rig_bone: row.source_fbx_bone for row in first.pairs
    }
    second_pairs = {
        row.target_rig_bone: row.source_fbx_bone for row in second.pairs
    }

    assert first_pairs == second_pairs
    assert first_pairs["rig_first"] == "alpha"
    assert first_pairs["rig_second"] == "beta"
    evidence = {
        row["target_rig_bone"]: row
        for row in first.extensions["automatic_mapping_evidence_v2"]
    }
    ambiguous = evidence["rig_first"]
    assert ambiguous["top_candidate"]["source_fbx_bone"] == "alpha"
    assert ambiguous["runner_up_candidate"]["source_fbx_bone"] == "beta"
    assert ambiguous["score_margin"] == pytest.approx(0.0)
    assert ambiguous["spatial_evidence_available"] is True
    assert ambiguous["assignment_policy"] == "deterministic_global_one_to_one"
    assert ambiguous["review_required"] is True
    assert next(
        row for row in first.pairs if row.target_rig_bone == "rig_first"
    ).review_state == "automatic_unreviewed"


def test_document_mapping_evidence_includes_bind_and_positive_skin_ownership() -> None:
    bind = np.eye(4, dtype=float)
    document = SimpleNamespace(
        bind_global_matrices={"weighted": bind},
        scene=SimpleNamespace(
            model_names={7: "weighted"},
            geometries=(
                SimpleNamespace(
                    clusters=(
                        SimpleNamespace(
                            bone_name=None,
                            bone_id=7,
                            weights=(0.25, 0.75, 0.0),
                        ),
                    )
                ),
            ),
        ),
    )

    evidence = source_mapping_evidence(document)

    np.testing.assert_array_equal(evidence["source_bind_globals"]["weighted"], bind)
    assert evidence["source_deform_bones"] == frozenset({"weighted"})
    assert evidence["source_skin_weights"] == {"weighted": pytest.approx(1.0)}


def test_animation_mapper_aligns_mixamo_to_character_creator_chain_and_fingers() -> None:
    rig_names = [
        "RL_BoneRoot",
        "CC_Base_Hip",
        "CC_Base_Waist",
        "CC_Base_Spine01",
        "CC_Base_Spine02",
        "CC_Base_NeckTwist01",
        "CC_Base_NeckTwist02",
        "CC_Base_Head",
        "CC_Base_L_Hand",
        "CC_Base_L_Mid1",
        "CC_Base_L_Mid2",
        "CC_Base_L_Mid3",
        "CC_Base_L_Mid3_end",
    ]
    rig_bones = tuple(
        ChromeRigBone(
            index=index,
            name=name,
            parent_index=index - 1,
            descriptor=dl_name_hash(name),
            bind_translation=(0.0, 0.0, 0.0),
            bind_rotation_wxyz=(1.0, 0.0, 0.0, 0.0),
            bind_scale=(1.0, 1.0, 1.0),
            deform=index != 0,
            helper=index == 0,
        )
        for index, name in enumerate(rig_names)
    )
    rig = ChromeRig(
        "test:cc",
        "CC target",
        "Humanoid",
        rig_bones,
        0,
        track_descriptors=tuple(bone.descriptor for bone in rig_bones),
    )
    source_names = [
        "mixamorig:Hips",
        "mixamorig:Spine",
        "mixamorig:Spine1",
        "mixamorig:Spine2",
        "mixamorig:Neck",
        "mixamorig:Head",
        "mixamorig:LeftHand",
        "mixamorig:LeftHandMiddle1",
        "mixamorig:LeftHandMiddle2",
        "mixamorig:LeftHandMiddle3",
        "mixamorig:LeftHandMiddle4",
    ]
    parents = {
        name: source_names[index - 1] if index else None
        for index, name in enumerate(source_names)
    }

    profile = auto_map_crig_to_fbx(rig, source_names, parents)
    pairs = {pair.source_bone: pair.target_bone for pair in profile.pairs}

    assert "RL_BoneRoot" not in pairs
    assert pairs["CC_Base_Hip"] == "mixamorig:Hips"
    assert pairs["CC_Base_Waist"] == "mixamorig:Spine"
    assert pairs["CC_Base_Spine01"] == "mixamorig:Spine1"
    assert pairs["CC_Base_Spine02"] == "mixamorig:Spine2"
    assert pairs["CC_Base_NeckTwist01"] == "mixamorig:Neck"
    assert pairs["CC_Base_L_Mid1"] == "mixamorig:LeftHandMiddle1"
    assert pairs["CC_Base_L_Mid3_end"] == "mixamorig:LeftHandMiddle4"


def test_animation_mapper_keeps_same_named_helper_root_for_identical_rig() -> None:
    rig_bones = (
        ChromeRigBone(
            index=0,
            name="RL_BoneRoot",
            parent_index=-1,
            descriptor=dl_name_hash("RL_BoneRoot"),
            bind_translation=(0.0, 0.0, 0.0),
            bind_rotation_wxyz=(1.0, 0.0, 0.0, 0.0),
            bind_scale=(1.0, 1.0, 1.0),
            deform=False,
            helper=True,
        ),
        ChromeRigBone(
            index=1,
            name="CC_Base_Hip",
            parent_index=0,
            descriptor=dl_name_hash("CC_Base_Hip"),
            bind_translation=(0.0, 0.8, 0.0),
            bind_rotation_wxyz=(1.0, 0.0, 0.0, 0.0),
            bind_scale=(1.0, 1.0, 1.0),
            deform=True,
            helper=False,
        ),
    )
    rig = ChromeRig(
        "test:identical_cc",
        "Identical CC target",
        "Humanoid",
        rig_bones,
        0,
        track_descriptors=tuple(bone.descriptor for bone in rig_bones),
    )
    source_names = ["RL_BoneRoot", "CC_Base_Hip"]
    parents = {"RL_BoneRoot": None, "CC_Base_Hip": "RL_BoneRoot"}

    profile = auto_map_crig_to_fbx(rig, source_names, parents)
    pairs = {pair.source_bone: pair.target_bone for pair in profile.pairs}

    assert pairs["RL_BoneRoot"] == "RL_BoneRoot"
    assert pairs["CC_Base_Hip"] == "CC_Base_Hip"
