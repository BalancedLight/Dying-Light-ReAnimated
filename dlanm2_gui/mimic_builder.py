"""Build facial/mimic ANM2 payloads from sampled FBX blendshape curves."""

from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Any, Iterable, Mapping

import numpy as np

from .anm2 import Anm2Header
from .anm2_components import decode_samples
from .fbx_blendshapes import FbxFacialScan
from .mimic_profiles import MimicMappingRow, MimicProfile, auto_map_shapes, mapping_from_payload

try:  # Current 0.4.x production location.
    from .anm2_writer import build_payload_from_values
except ImportError:  # 0.3.x compatibility for local regression tests.
    from .oracle.turn_duration_candidate_builder import (
        _build_payload_from_values as build_payload_from_values,
    )


@dataclass(frozen=True, slots=True)
class MimicBuild:
    payload: bytes
    frame_count: int
    fps: float
    profile_id: str
    mapping: tuple[MimicMappingRow, ...]
    report: dict[str, Any]


def _lookup_curve(scan: FbxFacialScan, source_name: str):
    exact = scan.curve_by_name().get(source_name)
    if exact is not None:
        return exact
    normalized = source_name.lower()
    for curve in scan.curves:
        if normalized in {value.lower() for value in curve.aliases}:
            return curve
    return None


def _soft_clip(value: float, minimum: float, maximum: float) -> float:
    if maximum <= minimum:
        return value
    midpoint = 0.5 * (minimum + maximum)
    half = 0.5 * (maximum - minimum)
    return midpoint + half * math.tanh((value - midpoint) / max(half, 1.0e-8))


def _mapping_rows(
    scan: FbxFacialScan,
    profile: MimicProfile,
    mapping: Iterable[MimicMappingRow | Mapping[str, Any]] | None,
) -> list[MimicMappingRow]:
    if mapping is None:
        return auto_map_shapes(scan.animated_shape_names, profile)
    rows: list[MimicMappingRow] = []
    for row in mapping:
        rows.append(row if isinstance(row, MimicMappingRow) else MimicMappingRow.from_dict(row))
    return rows


def build_mimic_anm2(
    scan: FbxFacialScan,
    profile: MimicProfile,
    *,
    mapping: Iterable[MimicMappingRow | Mapping[str, Any]] | None = None,
    clamp_mode: str = "none",
) -> MimicBuild:
    """Create a morph-only ANM2.

    Each profile descriptor receives the ordinary nine-component ANM2 row, but
    only ``tx`` varies. Chrome's mimic consumer interprets that component as a
    morph scalar rather than as skeletal translation.
    """

    if scan.frame_count < 1:
        raise ValueError("facial scan has no frames")
    if clamp_mode not in {"none", "hard", "soft"}:
        raise ValueError(f"unknown mimic clamp mode: {clamp_mode!r}")
    rows = _mapping_rows(scan, profile, mapping)
    targets_by_descriptor = profile.by_descriptor()
    unknown_targets = sorted({row.target_descriptor for row in rows if row.target_descriptor not in targets_by_descriptor})
    if unknown_targets:
        rendered = ", ".join(f"0x{value:08X}" for value in unknown_targets)
        raise ValueError(f"mimic mapping refers to descriptor(s) not in target profile: {rendered}")

    frame_count = scan.frame_count
    values: list[list[list[float]]] = [
        [
            [0.0, 0.0, 0.0, float(target.neutral), 0.0, 0.0, 1.0, 1.0, 1.0]
            for target in profile.targets
        ]
        for _frame in range(frame_count)
    ]
    source_activity: dict[str, float] = {}
    active_sources = {curve.name for curve in scan.animated_curves}
    mapped_sources: set[str] = set()
    mapping_warnings: list[str] = []
    contributions_by_target: dict[int, list[str]] = {}

    for curve in scan.curves:
        baseline = float(curve.values[0]) if curve.values else 0.0
        source_activity[curve.name] = float(np.mean(np.abs(np.asarray(curve.values) - baseline)))

    for row in rows:
        if not row.enabled:
            continue
        curve = _lookup_curve(scan, row.source)
        if curve is None:
            mapping_warnings.append(f"Mapped source shape was not found in the selected FBX stack: {row.source}")
            continue
        target = targets_by_descriptor[row.target_descriptor]
        mapped_sources.add(curve.name)
        contributions_by_target.setdefault(target.descriptor, []).append(curve.name)
        for frame_index, source_value in enumerate(curve.values):
            values[frame_index][target.index][3] += float(source_value) * row.weight + row.bias

    if clamp_mode != "none":
        for frame in values:
            for target in profile.targets:
                value = float(frame[target.index][3])
                if clamp_mode == "hard":
                    value = max(target.recommended_min, min(target.recommended_max, value))
                else:
                    value = _soft_clip(value, target.recommended_min, target.recommended_max)
                frame[target.index][3] = value

    packed_flags: list[list[bool]] = []
    active_targets: list[str] = []
    for target in profile.targets:
        curve = [frame[target.index][3] for frame in values]
        dynamic = max(curve) - min(curve) > 1.0e-8
        flags = [False] * 9
        flags[3] = dynamic
        packed_flags.append(flags)
        if dynamic or abs(curve[0] - target.neutral) > 1.0e-8:
            active_targets.append(target.name)

    header = Anm2Header(
        format_version=42,
        unknown06=1,
        frame_count=frame_count,
        track_count=len(profile.targets),
        unknown12=0,
        unknown14=0,
        declared_length=0,
        unknown20=0,
        unknown24=0,
        unknown28=0,
    )
    payload = build_payload_from_values(
        header,
        list(profile.descriptors),
        values,
        packed_flags,
    )

    sample_frames = sorted({0, frame_count // 2, frame_count - 1})
    decoded = decode_samples(payload, [float(frame) for frame in sample_frames])
    maximum_error = 0.0
    for decoded_frame, frame_index in zip(decoded.frames, sample_frames):
        expected = values[frame_index]
        for actual_track, expected_track in zip(decoded_frame.tracks, expected):
            maximum_error = max(
                maximum_error,
                max(abs(float(actual) - float(wanted)) for actual, wanted in zip(actual_track, expected_track)),
            )

    total_activity = sum(source_activity.get(name, 0.0) for name in active_sources)
    captured_activity = sum(source_activity.get(name, 0.0) for name in active_sources & mapped_sources)
    unmapped = sorted(active_sources - mapped_sources)
    consolidated = {
        f"0x{descriptor:08X}": sorted(set(names))
        for descriptor, names in contributions_by_target.items()
        if len(set(names)) > 1
    }
    report = {
        "resource_kind": "mimic",
        "profile_id": profile.profile_id,
        "profile_name": profile.name,
        "weight_component": "tx",
        "frame_count": frame_count,
        "fps": scan.fps,
        "source_fbx": scan.source_path,
        "source_animation_stack": scan.animation_stack,
        "source_shape_count": len(scan.curves),
        "animated_source_shape_count": len(scan.animated_curves),
        "mapped_source_shape_count": len(active_sources & mapped_sources),
        "unmapped_animated_shapes": unmapped,
        "target_track_count": len(profile.targets),
        "active_target_tracks": active_targets,
        "consolidated_targets": consolidated,
        "captured_source_activity_ratio": (
            captured_activity / total_activity if total_activity > 1.0e-12 else 1.0
        ),
        "clamp_mode": clamp_mode,
        "mapping": [row.to_dict() for row in rows],
        "warnings": [*scan.warnings, *mapping_warnings],
        "decoded_sample_frames": sample_frames,
        "decoded_max_component_error": maximum_error,
    }
    return MimicBuild(
        payload=payload,
        frame_count=frame_count,
        fps=scan.fps,
        profile_id=profile.profile_id,
        mapping=tuple(rows),
        report=report,
    )


__all__ = ["MimicBuild", "build_mimic_anm2"]
