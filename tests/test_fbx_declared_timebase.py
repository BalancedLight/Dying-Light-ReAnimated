from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace

import pytest

from dlanm2_gui.fbx_core import (
    FBX_TIME_MODE_FPS,
    FbxDocument,
    resolve_fbx_declared_timebase,
)
from dlanm2_gui.fbx_blendshapes import scan_fbx_blendshapes
from dlanm2_gui.model_importer.fbx_model import FBX_TICKS_PER_SECOND
from dlanm2_gui.model_importer.fbx_model import FbxNode


def test_complete_fbx_time_mode_table() -> None:
    expected = {
        0: 30.0,
        1: 120.0,
        2: 100.0,
        3: 60.0,
        4: 50.0,
        5: 48.0,
        6: 30.0,
        7: 30.0,
        8: 30_000.0 / 1_001.0,
        9: 30_000.0 / 1_001.0,
        10: 25.0,
        11: 24.0,
        12: 1_000.0,
        13: 24_000.0 / 1_001.0,
        15: 96.0,
        16: 72.0,
        17: 60_000.0 / 1_001.0,
        18: 120_000.0 / 1_001.0,
    }
    assert FBX_TIME_MODE_FPS == expected


def test_declared_mode_wins_over_key_spacing() -> None:
    result = resolve_fbx_declared_timebase(
        {"TimeMode": [11], "CustomFrameRate": [60.0]},
        key_time_deltas=[round(FBX_TICKS_PER_SECOND / 30.0)] * 10,
    )
    assert result.declared_fps == 24.0
    assert result.source == "GlobalSettings.TimeMode"
    assert result.confidence == "declared"


def test_custom_mode_and_low_confidence_fallback() -> None:
    custom = resolve_fbx_declared_timebase(
        {"TimeMode": [14], "CustomFrameRate": [47.952]}
    )
    assert custom.declared_fps == pytest.approx(47.952)
    assert custom.custom_frame_rate == pytest.approx(47.952)

    inferred = resolve_fbx_declared_timebase(
        {}, key_time_deltas=[round(FBX_TICKS_PER_SECOND / 25.0)] * 4
    )
    assert inferred.declared_fps == pytest.approx(25.0, rel=1.0e-8)
    assert inferred.confidence == "inferred_low"

    fallback = resolve_fbx_declared_timebase({"TimeMode": [14]})
    assert fallback.declared_fps == 30.0
    assert fallback.confidence == "fallback_low"


def test_facial_scan_keeps_declared_rate_and_exact_tick_span(tmp_path: Path) -> None:
    duration = 12.6333338419
    start_tick = FBX_TICKS_PER_SECOND
    stop_tick = start_tick + round(duration * FBX_TICKS_PER_SECOND)
    document = SimpleNamespace(
        path=tmp_path / "face.fbx",
        selected_animation_stack=None,
        animation_stacks=(),
        animation_start_tick=start_tick,
        animation_stop_tick=stop_tick,
        declared_timebase=SimpleNamespace(declared_fps=24.0),
        frame_ticks=lambda *, fps: list(range(381)),
        children={},
        parents={},
        object_by_id={},
        objects=SimpleNamespace(children=[]),
    )

    scan = scan_fbx_blendshapes(document=document, fps=30.0)

    assert scan.fps == 30.0
    assert scan.frame_count == 381
    assert scan.source_fps == 24.0
    assert scan.source_duration_seconds == pytest.approx(duration, abs=1.0e-9)


def test_facial_only_keys_define_span_and_fallback_timebase(tmp_path: Path) -> None:
    interval = FBX_TICKS_PER_SECOND // 24
    channel = FbxNode(
        "Deformer",
        [30, "SubDeformer::jawOpen", "BlendShapeChannel"],
        [FbxNode("DeformPercent", [0.0], [], 0, 0)],
        0,
        0,
    )
    curve_node = FbxNode("AnimationCurveNode", [20, "Face", ""], [], 0, 0)
    curve = FbxNode(
        "AnimationCurve",
        [40, "Jaw", ""],
        [
            FbxNode("KeyTime", [[0, interval, interval * 2]], [], 0, 0),
            FbxNode("KeyValueFloat", [[0.0, 50.0, 100.0]], [], 0, 0),
        ],
        0,
        0,
    )
    document = SimpleNamespace(
        path=tmp_path / "face_only.fbx",
        selected_animation_stack=SimpleNamespace(
            name="Face", layer_ids=(100,)
        ),
        animation_stacks=(),
        animation_start_tick=0,
        animation_stop_tick=0,
        declared_timebase=resolve_fbx_declared_timebase(None),
        top={},
        curves={},
        children={
            100: [("OO", 20, [])],
            20: [("OP", 40, ["d|DeformPercent"])],
            30: [],
        },
        parents={20: [("OP", 30, ["DeformPercent"])]},
        object_by_id={20: curve_node, 30: channel, 40: curve},
        objects=SimpleNamespace(children=[channel, curve_node, curve]),
    )

    def frame_ticks(*, fps: float) -> list[int]:
        count = round(
            (document.animation_stop_tick - document.animation_start_tick)
            * fps
            / FBX_TICKS_PER_SECOND
        ) + 1
        return [
            round(
                document.animation_start_tick
                + index * FBX_TICKS_PER_SECOND / fps
            )
            for index in range(count)
        ]

    document.frame_ticks = frame_ticks
    scan = scan_fbx_blendshapes(document=document, fps=30.0)

    assert document.animation_stop_tick == interval * 2
    assert scan.source_fps == pytest.approx(24.0)
    assert scan.source_duration_seconds == pytest.approx(2.0 / 24.0)
    assert scan.frame_count == 3
    assert scan.animated_shape_names == ("jawOpen",)


PRIVATE_FIXTURE = Path(r"S:\Downloads\left_hand_jump_test.fbx")


@pytest.mark.skipif(not PRIVATE_FIXTURE.is_file(), reason="private timing fixture unavailable")
def test_private_left_hand_jump_declares_24_fps() -> None:
    document = FbxDocument(
        PRIVATE_FIXTURE,
        animation_stack="Armature|m_fpp_unarmed_jumpsprint_mirror_Armature",
        purpose="animation",
    )
    assert document.declared_timebase.time_mode == 11
    assert document.declared_fps == 24.0
    assert document.frame_count(document.declared_fps) == 305
    assert (
        document.animation_stop_tick - document.animation_start_tick
    ) / FBX_TICKS_PER_SECOND == pytest.approx(12.6333338419)
