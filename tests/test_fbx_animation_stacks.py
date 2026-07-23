from __future__ import annotations

from collections import defaultdict
from pathlib import Path

import pytest

from dlanm2_gui.oracle.binary_fbx_mixamo import (
    FBX_TICKS_PER_SECOND,
    FbxAnimationStack,
    FbxNode,
    _FbxDocument,
)


def _node(name: str, properties: list[object], children: list[FbxNode] | None = None) -> FbxNode:
    return FbxNode(name, properties, children or [], 0, 0)


def _selectable_document() -> _FbxDocument:
    document = object.__new__(_FbxDocument)
    document.path = Path("synthetic.fbx")
    model = _node("Model", [10, "Bone", "LimbNode"])
    first_curve_node = _node("AnimationCurveNode", [20, "First", ""])
    second_curve_node = _node("AnimationCurveNode", [21, "Second", ""])
    first_curve = _node(
        "AnimationCurve",
        [30, "FirstX", ""],
        [_node("KeyTime", [[100, 200]]), _node("KeyValueFloat", [[1.0, 2.0]])],
    )
    second_curve = _node(
        "AnimationCurve",
        [31, "SecondX", ""],
        [_node("KeyTime", [[1000, 1200]]), _node("KeyValueFloat", [[8.0, 9.0]])],
    )
    document.object_by_id = {
        10: model,
        20: first_curve_node,
        21: second_curve_node,
        30: first_curve,
        31: second_curve,
    }
    document.children = defaultdict(
        list,
        {
            101: [("OO", 20, [])],
            102: [("OO", 21, [])],
            20: [("OP", 30, ["d|X"])],
            21: [("OP", 31, ["d|X"])],
        },
    )
    document.parents = defaultdict(
        list,
        {
            20: [("OP", 10, ["Lcl Rotation"])],
            21: [("OP", 10, ["Lcl Rotation"])],
        },
    )
    document.animation_stacks = (
        FbxAnimationStack("Walk", ("WalkLayer",), 100, 200, 1, (101,)),
        FbxAnimationStack("Run", ("Skeleton|RunAction",), 1000, 1200, 2, (102,)),
    )
    document.selected_animation_stack = None
    document.curves = {}
    document.animation_start_tick = 0
    document.animation_stop_tick = 0
    document.limb_models = {"Bone": 10}
    return document


def test_arbitrary_layer_names_and_stack_selection_isolate_curves() -> None:
    document = _selectable_document()
    with pytest.raises(ValueError, match="multiple animations"):
        document.select_animation_stack(None)

    selected = document.select_animation_stack("Run")
    assert selected is not None and selected.name == "Run"
    assert document.animation_start_tick == 1000
    assert document.animation_stop_tick == 1200
    assert document.curves[(10, "Lcl Rotation", "X")][1] == [8.0, 9.0]

    document.select_animation_stack("Walk")
    assert document.curves[(10, "Lcl Rotation", "X")][1] == [1.0, 2.0]


def test_reselecting_a_stack_reuses_its_transform_contract() -> None:
    document = _selectable_document()
    document.transform_contract = object()
    document._transform_contract_cache = {}
    builds: list[str] = []

    def build_contract() -> object:
        builds.append(str(document.selected_animation_stack.name))
        return object()

    document._build_transform_contract = build_contract  # type: ignore[method-assign]

    document.select_animation_stack("Walk")
    document.select_animation_stack("Walk")
    document.select_animation_stack("Run")
    document.select_animation_stack("Walk")

    assert builds == ["Walk", "Run"]


def test_stack_timing_starts_at_selected_nonzero_tick() -> None:
    document = _selectable_document()
    document.animation_stacks = (
        FbxAnimationStack(
            "Walk",
            ("WalkLayer",),
            FBX_TICKS_PER_SECOND,
            FBX_TICKS_PER_SECOND * 2,
            1,
            (101,),
        ),
    )
    document.select_animation_stack("Walk")
    ticks = document.frame_ticks(fps=2)
    assert ticks == [
        FBX_TICKS_PER_SECOND,
        FBX_TICKS_PER_SECOND + FBX_TICKS_PER_SECOND // 2,
        FBX_TICKS_PER_SECOND * 2,
    ]


def test_multilayer_stack_requires_bake_or_flatten() -> None:
    document = _selectable_document()
    document.animation_stacks = (
        FbxAnimationStack("Blend", ("Base", "Additive"), 0, 100, 1, (101, 102)),
    )
    with pytest.raises(ValueError, match="bake/flatten"):
        document.select_animation_stack("Blend")


def test_stack_inventory_follows_connections_not_layer0_name() -> None:
    document = object.__new__(_FbxDocument)
    stack = _node("AnimationStack", [1, "Skeleton|SkeletonAction\x00\x01AnimStack", ""])
    layer = _node("AnimationLayer", [2, "Skeleton|SkeletonAction\x00\x01AnimLayer", ""])
    document.objects = _node("Objects", [], [stack, layer])
    document.children = defaultdict(list, {1: [("OO", 2, [])]})
    document.top = {}
    inventory = document._animation_stack_inventory()
    assert len(inventory) == 1
    assert inventory[0].name == "Skeleton|SkeletonAction"
    assert inventory[0].layer_names == ("Skeleton|SkeletonAction",)


def test_unique_changing_skeletal_stack_is_preferred_over_static_peer() -> None:
    document = _selectable_document()
    first_values = next(
        child
        for child in document.object_by_id[30].children
        if child.name == "KeyValueFloat"
    )
    first_values.properties[0] = [1.0, 1.0]

    preferred = document.preferred_animation_stack()

    assert preferred is not None
    assert preferred.name == "Run"
    activity = {row.name: row for row in document.animation_stack_activity()}
    assert activity["Walk"].skeletal_channel_count == 1
    assert activity["Walk"].changing_skeletal_channel_count == 0
    assert activity["Run"].changing_skeletal_channel_count == 1


def test_malformed_curve_is_actionable_stack_data_not_fbx_unreadable() -> None:
    document = _selectable_document()
    document.scene = type("Scene", (), {"model_names": {10: "Bone"}})()
    values = next(
        child
        for child in document.object_by_id[31].children
        if child.name == "KeyValueFloat"
    )
    values.properties[0] = [8.0]

    activity = {row.name: row for row in document.animation_stack_activity()}

    assert not activity["Run"].usable
    assert "KeyTime rows" in activity["Run"].reason
