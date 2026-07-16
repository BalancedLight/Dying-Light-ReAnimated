from __future__ import annotations

"""Validation applied after ANM2 packing and before a build can be emitted."""

import math
from typing import Any, Sequence


# The existing DL2 source-superset regression requires packed decode error to
# remain below 0.004.  This is deliberately a consumer-side validation limit;
# it does not alter the known-good packed writer or its page construction.
DECODED_COMPONENT_ERROR_LIMIT = 4.0e-3


def validate_decoded_component_error(
    decoded: Any,
    expected_values: Sequence[Sequence[Sequence[float]]],
    sample_frames: Sequence[int],
    *,
    engine_name: str,
    tolerance: float = DECODED_COMPONENT_ERROR_LIMIT,
) -> float:
    """Return the worst packed-decode error or reject an unsafe payload.

    The diagnostic identifies the exact sampled frame, target track, and
    component.  Explicit finite checks are required because ``max(0, nan)``
    can otherwise hide a non-finite decoded value and report a false zero.
    """

    if not math.isfinite(float(tolerance)) or float(tolerance) <= 0.0:
        raise ValueError("decoded component validation tolerance must be finite and positive")
    decoded_frames = tuple(getattr(decoded, "frames", ()))
    if len(decoded_frames) != len(sample_frames):
        raise ValueError(
            f"{engine_name} packed ANM2 verification returned {len(decoded_frames)} "
            f"decoded sample(s) for {len(sample_frames)} requested frame(s). The payload "
            "was rejected before output; inspect the target writer profile and page tables."
        )

    maximum_error = 0.0
    worst: tuple[int, int, int, float, float] | None = None
    for decoded_frame, frame_index in zip(decoded_frames, sample_frames):
        if not 0 <= int(frame_index) < len(expected_values):
            raise ValueError(
                f"{engine_name} packed ANM2 verification requested invalid source frame "
                f"{frame_index}; the payload was rejected before output."
            )
        expected_frame = expected_values[int(frame_index)]
        actual_tracks = tuple(getattr(decoded_frame, "tracks", ()))
        if len(actual_tracks) != len(expected_frame):
            raise ValueError(
                f"{engine_name} packed ANM2 verification decoded {len(actual_tracks)} "
                f"track(s) at frame {frame_index}, expected {len(expected_frame)}. The "
                "payload was rejected before output; inspect descriptor and writer-profile "
                "selection."
            )
        for track_index, (actual_track, expected_track) in enumerate(
            zip(actual_tracks, expected_frame)
        ):
            if len(actual_track) != len(expected_track):
                raise ValueError(
                    f"{engine_name} packed ANM2 verification decoded {len(actual_track)} "
                    f"component(s) for frame {frame_index}, track {track_index}; expected "
                    f"{len(expected_track)}. The payload was rejected before output."
                )
            for component_index, (actual, expected) in enumerate(
                zip(actual_track, expected_track)
            ):
                actual_value = float(actual)
                expected_value = float(expected)
                if not math.isfinite(expected_value):
                    raise ValueError(
                        f"{engine_name} generated a non-finite pre-write value at frame "
                        f"{frame_index}, track {track_index}, component {component_index}: "
                        f"{expected_value!r}. Repair the source transform or mapped row; no "
                        "ANM2 output was accepted."
                    )
                if not math.isfinite(actual_value):
                    raise ValueError(
                        f"{engine_name} decoded a non-finite packed value at frame "
                        f"{frame_index}, track {track_index}, component {component_index}: "
                        f"{actual_value!r}. The payload was rejected before output; inspect "
                        "the selected target .crig writer profile."
                    )
                error = abs(actual_value - expected_value)
                if not math.isfinite(error):
                    raise ValueError(
                        f"{engine_name} produced a non-finite packed decode delta at frame "
                        f"{frame_index}, track {track_index}, component {component_index}. "
                        "No ANM2 output was accepted."
                    )
                if error > maximum_error:
                    maximum_error = error
                    worst = (
                        int(frame_index),
                        track_index,
                        component_index,
                        expected_value,
                        actual_value,
                    )

    if maximum_error >= float(tolerance):
        assert worst is not None
        frame_index, track_index, component_index, expected_value, actual_value = worst
        raise ValueError(
            f"{engine_name} packed ANM2 decode error {maximum_error:.9g} is not below "
            f"the validated {float(tolerance):.9g} component tolerance at frame "
            f"{frame_index}, track {track_index}, component {component_index} "
            f"(pre-write {expected_value:.9g}, decoded {actual_value:.9g}). The payload "
            "was rejected before output; inspect the source transforms, mapping policies, "
            "and selected target .crig writer profile."
        )
    return maximum_error


__all__ = [
    "DECODED_COMPONENT_ERROR_LIMIT",
    "validate_decoded_component_error",
]
