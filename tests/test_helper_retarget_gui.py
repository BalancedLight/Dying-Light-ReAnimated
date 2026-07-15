from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace

from dlanm2_gui.bone_maps import GenericBoneMap
from dlanm2_gui.workspaces.animation_mapping import (
    CrigMappingWorkspace,
    mapping_row_visible,
    shared_source_status,
)


ROOT = Path(__file__).resolve().parents[1]


class _Status:
    def setText(self, _value: str) -> None:
        pass


def _workspace_for_profile(profile: GenericBoneMap):
    workspace = object.__new__(CrigMappingWorkspace)
    animation = SimpleNamespace()
    rig = SimpleNamespace(
        bones=(
            SimpleNamespace(index=0, name="head", descriptor=1),
            SimpleNamespace(index=1, name="refcamera", descriptor=2),
        )
    )
    document = SimpleNamespace(
        limb_models={"Head": 1, "Camera": 2},
        parent_by_name={"Head": None, "Camera": None},
    )
    workspace._selected_animation = lambda: animation
    workspace._load_rig = lambda: rig
    workspace._document = lambda _animation: document
    workspace._current_profile = lambda _animation: profile
    workspace._store_profile = lambda _animation, _profile: None
    workspace.refresh = lambda: None
    workspace.status = _Status()
    return workspace


def test_exact_mapping_ui_does_not_steal_shared_source() -> None:
    profile = GenericBoneMap.create("UI fanout", "target", "source")
    workspace = _workspace_for_profile(profile)

    workspace._set_pair("head", "Head")
    workspace._set_pair(
        "refcamera",
        "Head",
        mapping_kind="helper_override",
        transfer_policy="rest_relative",
        component_policy="translation",
    )

    assert [(row.source_bone, row.target_bone) for row in profile.pairs] == [
        ("head", "Head"),
        ("refcamera", "Head"),
    ]


def test_changing_helper_source_preserves_body_mapping_and_policy() -> None:
    profile = GenericBoneMap.create("UI fanout", "target", "source")
    workspace = _workspace_for_profile(profile)
    workspace._set_pair("head", "Head")
    workspace._set_pair(
        "refcamera",
        "Head",
        mapping_kind="helper_override",
        transfer_policy="rest_relative",
        component_policy="translation",
    )

    workspace._set_pair(
        "refcamera",
        "Camera",
        mapping_kind="helper_override",
        transfer_policy="rotation_delta",
        component_policy="rotation_translation",
    )

    by_target = {row.source_bone: row for row in profile.pairs}
    assert by_target["head"].target_bone == "Head"
    assert by_target["refcamera"].target_bone == "Camera"
    assert by_target["refcamera"].component_policy == "rotation_translation"


def test_shared_source_status_is_explicit() -> None:
    assert shared_source_status("Head", "bone", 2) == "Mapped — shared source"
    assert shared_source_status("Head", "helper_override", 3) == (
        "Helper override — shared by 3 targets"
    )
    assert "keep bind" in shared_source_status("", "helper_override", 0)


def test_show_helper_bones_toggle_controls_helper_row_visibility() -> None:
    assert not mapping_row_visible(
        is_helper=True,
        show_helpers=False,
        matches_filter=True,
        only_unmapped=False,
        mapped=False,
    )
    assert mapping_row_visible(
        is_helper=True,
        show_helpers=True,
        matches_filter=True,
        only_unmapped=False,
        mapped=False,
    )


def test_normal_target_lists_camera_helpers_directly_from_its_smd() -> None:
    workspace = object.__new__(CrigMappingWorkspace)
    workspace.controller = SimpleNamespace(
        project=SimpleNamespace(
            rig=SimpleNamespace(canonical_smd=str(ROOT / "reference/player_1_tpp.smd"))
        )
    )
    animation = SimpleNamespace(extensions={})
    document = SimpleNamespace(limb_models={"Head": 1})

    rows, description = workspace._normal_helper_rows(animation, document)
    by_name = {row["target_bone"]: row for row in rows}

    assert "selected target rig" in description
    assert by_name["refcamera"]["source_bone"] == ""
    assert by_name["refcamera"]["suggested_source"] == "Head"
    assert by_name["refcamera"]["component_policy"] == "translation"
    assert "eyecamera" in by_name
