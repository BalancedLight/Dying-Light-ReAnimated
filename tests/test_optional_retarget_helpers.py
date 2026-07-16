from __future__ import annotations

import math

import numpy as np

from dlanm2_gui.background_tasks import TaskFailure
from dlanm2_gui.oracle.custom_fbx_smd_intrinsic_absolute_editor_rpack import (
    _add_absolute_terminal_global,
    _add_absolute_torso_globals,
)
from dlanm2_gui.oracle.custom_fbx_smd_two_vector_fullbody_editor_rpack import (
    LimbSpec,
)


def _transform(
    position: tuple[float, float, float],
    rotation: np.ndarray | None = None,
) -> np.ndarray:
    result = np.eye(4, dtype=float)
    result[:3, :3] = np.eye(3, dtype=float) if rotation is None else rotation
    result[:3, 3] = np.asarray(position, dtype=float)
    return result


def test_missing_optional_head_end_recovers_head_axis_from_animation() -> None:
    source_positions = {
        "mixamorig:Hips": np.asarray((0.0, 0.0, 0.0)),
        "mixamorig:Spine": np.asarray((0.0, 0.5, 0.0)),
        "mixamorig:Spine1": np.asarray((0.0, 1.0, 0.0)),
        "mixamorig:Spine2": np.asarray((0.0, 1.5, 0.0)),
        "mixamorig:Neck": np.asarray((0.0, 2.0, 0.0)),
        "mixamorig:Head": np.asarray((0.0, 2.5, 0.0)),
        "mixamorig:LeftShoulder": np.asarray((-1.0, 1.75, 0.0)),
        "mixamorig:RightShoulder": np.asarray((1.0, 1.75, 0.0)),
    }
    angle = math.radians(25.0)
    animated_head_rotation = np.asarray(
        (
            (math.cos(angle), -math.sin(angle), 0.0),
            (math.sin(angle), math.cos(angle), 0.0),
            (0.0, 0.0, 1.0),
        ),
        dtype=float,
    )
    source_globals = [
        {"mixamorig:Head": _transform((0.0, 2.5, 0.0))},
        {
            "mixamorig:Head": _transform(
                (0.0, 2.5, 0.0), animated_head_rotation
            )
        },
    ]
    source_rest_globals = {
        "mixamorig:Head": _transform((0.0, 2.5, 0.0))
    }

    target_positions = {
        "pelvis": (0.0, 0.0, 0.0),
        "hspine": (0.0, 0.5, 0.0),
        "spine": (0.0, 0.75, 0.0),
        "spine1": (0.0, 1.0, 0.0),
        "spine2": (0.0, 1.5, 0.0),
        "spine3": (0.0, 2.0, 0.0),
        "hspine1": (0.0, 2.5, 0.0),
        "neck": (0.0, 3.0, 0.0),
        "neck1": (0.0, 3.5, 0.0),
        "head": (0.0, 4.0, 0.0),
        "headend": (0.0, 4.5, 0.0),
        "l_clavicle": (-1.0, 2.5, 0.0),
        "r_clavicle": (1.0, 2.5, 0.0),
    }
    target_global = {
        name: _transform(position) for name, position in target_positions.items()
    }
    desired = [{}, {}]

    details = _add_absolute_torso_globals(
        desired,
        source_positions=[dict(source_positions), dict(source_positions)],
        source_body_frames=[np.eye(3), np.eye(3)],
        target_body_frames=[np.eye(3), np.eye(3)],
        target_body_bind=np.eye(3),
        target_global=target_global,
        source_globals=source_globals,
        source_rest_globals=source_rest_globals,
        source_rest_positions=source_positions,
    )

    assert "mixamorig:HeadTop_End" not in source_positions
    assert details["direction_strategy_by_target"]["head"] == (
        "animated_head_rotation_from_rest_incoming_axis"
    )
    assert "head" in desired[0]
    assert "head" in desired[1]
    assert not np.allclose(desired[0]["head"], desired[1]["head"])


def test_missing_optional_limb_terminal_holds_only_terminal_at_bind() -> None:
    limb = LimbSpec(
        "test_arm",
        "root",
        "mid",
        "end",
        "target_root",
        "target_mid",
        "target_end",
        "optional_terminal",
        "target_terminal",
    )

    assert _add_absolute_terminal_global(
        [{}],
        limb=limb,
        source_positions=[
            {
                "root": np.asarray((0.0, 0.0, 0.0)),
                "mid": np.asarray((1.0, 0.0, 0.0)),
                "end": np.asarray((1.0, 1.0, 0.0)),
            }
        ],
        source_body_frames=[np.eye(3)],
        target_body_frames=[np.eye(3)],
        target_global={},
    ) is False


def test_background_key_error_message_explains_what_the_name_means() -> None:
    failure = TaskFailure(
        "'mixamorig:HeadTop_End'",
        "technical traceback",
        "KeyError",
    )

    message = failure.display_message()

    assert message.startswith(
        "The imported skeleton does not contain the optional Head End helper bone."
    )
    assert "Missing item: mixamorig:HeadTop_End" in message
    assert "non-deforming marker above the head" in message
    assert "not an actual body part" in message
    assert "should not block export" in message
    assert "build log" in message


def test_normal_gui_failure_message_has_no_traceback_hint() -> None:
    failure = TaskFailure("Malformed animation curve data", "Traceback: developer details", "ValueError")

    message = failure.display_message(False)

    assert message == "Malformed animation curve data"
    assert "Traceback" not in message
    assert "build log" not in message
