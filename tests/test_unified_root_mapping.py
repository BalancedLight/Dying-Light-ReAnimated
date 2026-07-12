from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace

import pytest

from dlanm2_gui.root_mapping import (
    DescriptorNameAliasMap,
    dl_name_hash,
    RootMappingSelection,
    read_smd_hierarchy,
    resolve_source_root,
    resolve_target_smd_root,
)

FIXTURE = Path(__file__).parent / "fixtures" / "no_bip01_target.smd"


def test_target_without_literal_bip01_uses_pelvis() -> None:
    rows = read_smd_hierarchy(FIXTURE)
    descriptors = [dl_name_hash(row.name) for row in rows]
    resolved = resolve_target_smd_root(FIXTURE, descriptors)
    assert resolved.bone_name == "pelvis"
    assert resolved.track_index == 1
    assert resolved.method == "automatic_fallback"
    assert resolved.literal_bip01_present is False


def test_descriptor_alias_keeps_real_name_and_adds_bip01() -> None:
    descriptor = dl_name_hash("pelvis")
    names = DescriptorNameAliasMap({descriptor: "pelvis"}, {"bip01": descriptor})
    by_name = {name: value for value, name in names.items()}
    assert by_name["pelvis"] == descriptor
    assert by_name["bip01"] == descriptor
    assert names[descriptor] == "pelvis"


def test_manual_target_missing_from_template_has_actionable_error() -> None:
    descriptors = [dl_name_hash("spine")]
    with pytest.raises(ValueError, match="descriptor .* absent from the selected target ANM2 template"):
        resolve_target_smd_root(FIXTURE, descriptors, requested_bone="pelvis")


def test_source_root_prefers_reviewed_humanoid_hips_mapping() -> None:
    source, method = resolve_source_root(
        ["ArmatureRoot", "CustomPelvis", "Spine"],
        {"ArmatureRoot": None, "CustomPelvis": "ArmatureRoot", "Spine": "CustomPelvis"},
        humanoid_aliases={"mixamorig:Hips": "CustomPelvis"},
    )
    assert source == "CustomPelvis"
    assert method == "mapped_humanoid_hips"


def test_root_mapping_is_saved_per_animation_extension() -> None:
    animation = SimpleNamespace(extensions={})
    RootMappingSelection("Root", "pelvis").store(animation)
    assert RootMappingSelection.from_animation(animation) == RootMappingSelection(
        "Root", "pelvis"
    )
