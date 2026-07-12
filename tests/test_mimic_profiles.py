from __future__ import annotations

from dlanm2_gui.mimic_profiles import (
    BUILTIN_COMMON46_REF,
    MimicMappingRow,
    MimicProfile,
    auto_map_shapes,
    builtin_common46_profile,
)


def test_common46_profile_is_declarative_and_unique():
    profile = builtin_common46_profile()
    assert profile.profile_id == BUILTIN_COMMON46_REF
    assert len(profile.targets) == 46
    assert len(set(profile.descriptors)) == 46
    assert all(row.semantic == "morph_scalar_tx" for row in profile.targets)
    assert all(row.component == "tx" for row in profile.targets)
    by_name = {row.name: row for row in profile.targets}
    for name in ("morph_jaw_open", "open", "wide", "w", "fv", "pbm", "morph_nose"):
        assert name in by_name


def test_auto_map_supports_consolidation_and_blink_companion():
    profile = builtin_common46_profile()
    rows = auto_map_shapes(
        ["jawOpen", "mouthOpen", "eyeBlinkLeft", "mouthSmileRight"],
        profile,
    )
    enabled = [row for row in rows if row.enabled]
    jaw = profile.target(next(row.target_descriptor for row in enabled if row.source == "jawOpen"))
    mouth = profile.target(next(row.target_descriptor for row in enabled if row.source == "mouthOpen"))
    assert jaw is not None and jaw.name == "morph_jaw_open"
    assert mouth is not None and mouth.name == "morph_jaw_open"
    left_blink_targets = {
        profile.target(row.target_descriptor).name
        for row in enabled
        if row.source == "eyeBlinkLeft"
    }
    assert left_blink_targets == {"morph_l_u_lid", "morph_l_b_lid"}
    assert any(
        profile.target(row.target_descriptor).name == "morph_lips_R_smile"
        for row in enabled
        if row.source == "mouthSmileRight"
    )


def test_profile_round_trip_keeps_unknown_descriptor_semantics(tmp_path):
    profile = builtin_common46_profile()
    path = profile.save(tmp_path / "copy.dlrmimic.json")
    loaded = MimicProfile.load(path)
    assert loaded.to_dict() == profile.to_dict()
    unresolved = [row for row in loaded.targets if row.name_status != "resolved"]
    assert unresolved
    assert all(row.descriptor for row in unresolved)
