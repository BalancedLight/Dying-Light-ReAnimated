from __future__ import annotations

from dlanm2_gui.bone_maps import BoneMapPair, GenericBoneMap
from dlanm2_gui.project_builder import _reviewed_mapping_is_name_identity
from dlanm2_gui.retarget_routing import select_exact_solver


def test_one_source_can_drive_body_and_multiple_helper_targets() -> None:
    profile = GenericBoneMap.create("Fanout", "target", "source")
    profile.pairs = [
        BoneMapPair(1, "head", "Head"),
        BoneMapPair(
            2,
            "refcamera",
            "Head",
            mapping_kind="helper_override",
            transfer_policy="rest_relative",
            component_policy="translation",
        ),
        BoneMapPair(
            3,
            "eyecamera",
            "Head",
            mapping_kind="helper_override",
            transfer_policy="rest_relative",
            component_policy="rotation_translation",
        ),
    ]

    assert profile.validate() == []
    assert len(profile.base_pairs) == 1
    assert len(profile.helper_pairs) == 2


def test_duplicate_target_name_or_descriptor_remains_invalid() -> None:
    duplicate_name = GenericBoneMap.create("Duplicate target", "target", "source")
    duplicate_name.pairs = [
        BoneMapPair(1, "refcamera", "Head"),
        BoneMapPair(2, "refcamera", "Camera"),
    ]
    assert any("target rig bone" in error.lower() for error in duplicate_name.validate())

    duplicate_descriptor = GenericBoneMap.create("Descriptor", "target", "source")
    duplicate_descriptor.pairs = [
        BoneMapPair(1, "refcamera", "Head"),
        BoneMapPair(1, "eyecamera", "Head"),
    ]
    assert any("descriptor" in error.lower() for error in duplicate_descriptor.validate())


def test_v1_json_defaults_and_unknown_pair_fields_roundtrip() -> None:
    payload = {
        "format": "dl-reanimated-bone-map",
        "schema_version": 1,
        "profile_id": "legacy",
        "name": "Legacy",
        "source_skeleton_hash": "target",
        "target_skeleton_hash": "source",
        "pairs": [
            {
                "source_descriptor": 1,
                "source_bone": "head",
                "target_bone": "Head",
                "future_pair_option": {"preserve": True},
            }
        ],
    }

    profile = GenericBoneMap.from_dict(payload)
    row = profile.pairs[0]
    assert row.mapping_kind == "bone"
    assert row.transfer_policy == "default"
    assert row.component_policy == "full_transform"
    assert row.extensions["unknown_fields"]["future_pair_option"] == {"preserve": True}
    saved = profile.to_dict()["pairs"][0]
    assert saved["extensions"]["unknown_fields"]["future_pair_option"] == {
        "preserve": True
    }


def test_helper_crosswire_does_not_change_base_identity_selection() -> None:
    profile = GenericBoneMap.create("Isolation", "target", "source")
    profile.pairs = [
        BoneMapPair(1, "pelvis", "pelvis"),
        BoneMapPair(2, "head", "head"),
        BoneMapPair(
            3,
            "refcamera",
            "head",
            mapping_kind="helper_override",
            transfer_policy="rest_relative",
            component_policy="translation",
        ),
    ]

    assert _reviewed_mapping_is_name_identity(profile)


def test_compatible_exact_map_uses_global_base_policy_when_helpers_need_value_stage() -> None:
    profile = GenericBoneMap.create("Exact helpers", "target", "source")
    profile.pairs = [
        BoneMapPair(1, "head", "head"),
        BoneMapPair(
            2,
            "refcamera",
            "head",
            mapping_kind="helper_override",
            transfer_policy="rest_relative",
            component_policy="translation",
        ),
    ]

    selection = select_exact_solver(
        {
            "classification": "exact_identity",
            "required_missing_bones": [],
            "hierarchy_mismatches": [],
        },
        profile,
    )

    assert selection.selected_engine == "MappedRigRetargetEngine"
    assert selection.selected_policy == "global_bind_basis_correction"
