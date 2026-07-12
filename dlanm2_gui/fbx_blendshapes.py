"""Binary-FBX BlendShapeChannel discovery and sampled facial curves.

The existing FBX evaluator focuses on skeletal ``Model`` nodes.  This module
adds the parallel path for ``Deformer::BlendShapeChannel`` objects and their
animated ``DeformPercent`` properties without changing the body evaluator.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable
import math

import numpy as np

from .oracle.binary_fbx_mixamo import (
    FBX_TICKS_PER_SECOND,
    _FbxDocument,
    _child_value,
    _clean_name,
    _properties70,
    _sample_curve,
)


@dataclass(frozen=True, slots=True)
class FbxBlendShapeCurve:
    name: str
    channel_id: int
    aliases: tuple[str, ...]
    values: tuple[float, ...]
    default_value: float
    source_scale: float
    animated: bool

    @property
    def minimum(self) -> float:
        return min(self.values, default=self.default_value)

    @property
    def maximum(self) -> float:
        return max(self.values, default=self.default_value)

    @property
    def range(self) -> float:
        return self.maximum - self.minimum


@dataclass(frozen=True, slots=True)
class FbxFacialScan:
    source_path: str
    animation_stack: str
    fps: int
    frame_count: int
    curves: tuple[FbxBlendShapeCurve, ...]
    warnings: tuple[str, ...] = ()

    @property
    def shape_names(self) -> tuple[str, ...]:
        return tuple(row.name for row in self.curves)

    @property
    def animated_curves(self) -> tuple[FbxBlendShapeCurve, ...]:
        return tuple(row for row in self.curves if row.animated)

    @property
    def animated_shape_names(self) -> tuple[str, ...]:
        return tuple(row.name for row in self.animated_curves)

    @property
    def has_facial_animation(self) -> bool:
        return bool(self.animated_curves)

    def curve_by_name(self) -> dict[str, FbxBlendShapeCurve]:
        return {row.name: row for row in self.curves}

    def summary(self) -> dict[str, Any]:
        return {
            "source_path": self.source_path,
            "animation_stack": self.animation_stack,
            "fps": self.fps,
            "frame_count": self.frame_count,
            "shape_count": len(self.curves),
            "animated_shape_count": len(self.animated_curves),
            "shape_names": list(self.shape_names),
            "animated_shape_names": list(self.animated_shape_names),
            "warnings": list(self.warnings),
        }


def _channel_type(node: Any) -> str:
    return str(node.properties[2]) if len(node.properties) > 2 else ""


def _channel_name(node: Any) -> str:
    raw = _clean_name(node.properties[1]) if len(node.properties) > 1 else "BlendShape"
    # Blender and Autodesk commonly include class prefixes in the object name.
    for separator in ("::", "|"):
        raw = raw.split(separator)[-1]
    return raw or "BlendShape"


def _channel_default(node: Any) -> float:
    direct = _child_value(node, "DeformPercent", None)
    if direct is not None:
        try:
            return float(direct)
        except (TypeError, ValueError):
            pass
    props = _properties70(node)
    values = props.get("DeformPercent") or props.get("Deform Percent") or [0.0]
    return float(values[0]) if values else 0.0


def _shape_aliases(document: _FbxDocument, channel_id: int, channel_name: str) -> tuple[str, ...]:
    aliases = [channel_name]
    for kind, child_id, _rest in document.children.get(channel_id, ()):
        child = document.object_by_id.get(child_id)
        if kind == "OO" and child is not None and child.name == "Geometry":
            geometry_type = str(child.properties[2]) if len(child.properties) > 2 else ""
            if geometry_type == "Shape" and len(child.properties) > 1:
                aliases.append(_clean_name(child.properties[1]).split("::")[-1])
    return tuple(dict.fromkeys(value for value in aliases if value))


def _selected_layer_id(document: _FbxDocument, animation_stack: str | None) -> int | None:
    if animation_stack:
        document.select_animation_stack(animation_stack)
    elif getattr(document, "selected_animation_stack", None) is None:
        stacks = tuple(getattr(document, "animation_stacks", ()))
        if len(stacks) == 1:
            document.select_animation_stack(stacks[0].name)
        elif len(stacks) > 1:
            raise ValueError(
                "FBX contains multiple animations; choose an animation stack before facial detection"
            )
    selected = getattr(document, "selected_animation_stack", None)
    if selected is None:
        return None
    layer_ids = tuple(getattr(selected, "layer_ids", ()))
    if len(layer_ids) != 1:
        raise ValueError("Facial import requires one baked animation layer per FBX stack")
    return int(layer_ids[0])


def _channel_curves_for_layer(
    document: _FbxDocument,
    layer_id: int | None,
) -> dict[int, tuple[list[int], list[float]]]:
    if layer_id is None:
        return {}
    result: dict[int, tuple[list[int], list[float]]] = {}
    for kind, curve_node_id, _rest in document.children.get(layer_id, ()):
        curve_node = document.object_by_id.get(curve_node_id)
        if kind != "OO" or curve_node is None or curve_node.name != "AnimationCurveNode":
            continue
        channel_id: int | None = None
        property_name = ""
        for parent_kind, parent_id, connection_rest in document.parents.get(curve_node_id, ()):
            parent = document.object_by_id.get(parent_id)
            if (
                parent_kind == "OP"
                and parent is not None
                and parent.name == "Deformer"
                and _channel_type(parent) == "BlendShapeChannel"
            ):
                channel_id = int(parent_id)
                property_name = str(connection_rest[0]) if connection_rest else ""
                break
        if channel_id is None or "deform" not in property_name.lower():
            continue
        curve_rows: list[tuple[list[int], list[float]]] = []
        for child_kind, curve_id, connection_rest in document.children.get(curve_node_id, ()):
            curve = document.object_by_id.get(curve_id)
            if child_kind != "OP" or curve is None or curve.name != "AnimationCurve":
                continue
            property_axis = str(connection_rest[0]) if connection_rest else ""
            if property_axis and "deform" not in property_axis.lower() and property_axis not in {"d", "d|X"}:
                # Some exporters call the scalar channel d|DeformPercent; others simply d|X.
                continue
            times = [int(value) for value in _child_value(curve, "KeyTime", [])]
            values = [float(value) for value in _child_value(curve, "KeyValueFloat", [])]
            if times and len(times) == len(values):
                curve_rows.append((times, values))
        if curve_rows:
            # A BlendShapeChannel is scalar. Prefer the curve with the most keys if an exporter
            # redundantly connected more than one scalar curve.
            result[channel_id] = max(curve_rows, key=lambda row: len(row[0]))
    return result


def _percent_scale(default_value: float, raw_values: Iterable[float]) -> float:
    maximum = max([abs(default_value), *(abs(float(value)) for value in raw_values)], default=0.0)
    # FBX DeformPercent is conventionally 0..100. A few DCC tools export normalized 0..1.
    return 0.01 if maximum > 2.0 else 1.0


def scan_fbx_blendshapes(
    source: str | Path | None = None,
    *,
    document: _FbxDocument | None = None,
    fps: int = 30,
    animation_stack: str | None = None,
) -> FbxFacialScan:
    if not 1 <= int(fps) <= 240:
        raise ValueError("Facial sample FPS must be between 1 and 240")
    if document is None:
        if source is None:
            raise ValueError("source or document is required")
        document = _FbxDocument(Path(source))
    source_path = str(Path(getattr(document, "path", source or "")).resolve())
    layer_id = _selected_layer_id(document, animation_stack)
    if hasattr(document, "frame_ticks"):
        ticks = list(document.frame_ticks(fps=int(fps)))
    else:  # Adjacent 0.3.x compatibility; those evaluators sampled from tick zero.
        count = max(1, int(document.frame_count(fps=int(fps))))
        ticks = [int(round(index * FBX_TICKS_PER_SECOND / int(fps))) for index in range(count)]
    if not ticks:
        ticks = [0]
    curves_by_channel = _channel_curves_for_layer(document, layer_id)

    channels = [
        node
        for node in document.objects.children
        if node.name == "Deformer" and _channel_type(node) == "BlendShapeChannel"
    ]
    rows: list[FbxBlendShapeCurve] = []
    for channel in channels:
        channel_id = int(channel.properties[0])
        name = _channel_name(channel)
        default_raw = _channel_default(channel)
        raw_curve = curves_by_channel.get(channel_id)
        raw_values_for_scale = raw_curve[1] if raw_curve else [default_raw]
        scale = _percent_scale(default_raw, raw_values_for_scale)
        if raw_curve is None:
            values = [default_raw * scale for _tick in ticks]
        else:
            values = [
                _sample_curve(raw_curve, tick, default_raw) * scale
                for tick in ticks
            ]
        finite = [float(value) if math.isfinite(float(value)) else 0.0 for value in values]
        spread = max(finite, default=0.0) - min(finite, default=0.0)
        aliases = _shape_aliases(document, channel_id, name)
        rows.append(FbxBlendShapeCurve(
            name=name,
            channel_id=channel_id,
            aliases=aliases,
            values=tuple(finite),
            default_value=float(default_raw * scale),
            source_scale=scale,
            animated=spread > 1.0e-6,
        ))

    warnings: list[str] = []
    if not channels:
        warnings.append("No FBX BlendShapeChannel objects were found.")
    elif channels and not any(row.animated for row in rows):
        warnings.append("Blendshape channels exist, but no changing DeformPercent curve was found in the selected animation stack.")
    return FbxFacialScan(
        source_path=source_path,
        animation_stack=(
            str(getattr(getattr(document, "selected_animation_stack", None), "name", ""))
        ),
        fps=int(fps),
        frame_count=len(ticks),
        curves=tuple(sorted(rows, key=lambda row: row.name.lower())),
        warnings=tuple(warnings),
    )


def detect_facial_animation(
    source: str | Path,
    *,
    fps: int = 30,
    animation_stack: str | None = None,
) -> bool:
    return scan_fbx_blendshapes(
        source,
        fps=fps,
        animation_stack=animation_stack,
    ).has_facial_animation


__all__ = [
    "FbxBlendShapeCurve",
    "FbxFacialScan",
    "detect_facial_animation",
    "scan_fbx_blendshapes",
]
