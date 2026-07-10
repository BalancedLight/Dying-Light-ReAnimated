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
