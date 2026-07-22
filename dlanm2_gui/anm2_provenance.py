from __future__ import annotations

"""Deterministic timing provenance for standalone/intermediate ANM2 files."""

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping
import hashlib
import json
import math
import os
import tempfile


ANM2_PROVENANCE_FORMAT = "dl-reanimated-anm2-provenance"
ANM2_PROVENANCE_SCHEMA_VERSION = 1


def anm2_provenance_path(path: str | Path) -> Path:
    return Path(str(Path(path)) + ".dlrmeta.json")


def _positive_fps(value: Any, field: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{field} must be a number")
    try:
        result = float(value)
    except (OverflowError, TypeError, ValueError) as exc:
        raise ValueError(f"{field} must be a finite number") from exc
    if not math.isfinite(result) or result <= 0.0:
        raise ValueError(f"{field} must be finite and positive")
    return result


def _nonnegative_duration(value: Any) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError("source_duration_seconds must be a number")
    try:
        duration = float(value)
    except (OverflowError, TypeError, ValueError) as exc:
        raise ValueError(
            "source_duration_seconds must be a finite number"
        ) from exc
    if not math.isfinite(duration) or duration < 0.0:
        raise ValueError(
            "source_duration_seconds must be finite and non-negative"
        )
    return duration


def _positive_frame_count(value: Any) -> int:
    if isinstance(value, bool):
        raise ValueError("frame_count must be a positive integer")
    if isinstance(value, int):
        count = value
    elif isinstance(value, float) and math.isfinite(value) and value.is_integer():
        count = int(value)
    else:
        raise ValueError("frame_count must be a positive integer")
    if count < 1:
        raise ValueError("frame_count must be a positive integer")
    return count


def build_anm2_provenance(
    anm2_payload: bytes,
    *,
    source_fbx: str | Path,
    source_fbx_sha256: str,
    source_fbx_fps: float,
    sample_fps: float,
    playback_fps: float,
    source_duration_seconds: float,
    frame_count: int,
    root_motion_mode: str,
    root_heading_mode: str,
) -> dict[str, Any]:
    duration = _nonnegative_duration(source_duration_seconds)
    count = _positive_frame_count(frame_count)
    return {
        "format": ANM2_PROVENANCE_FORMAT,
        "schema_version": ANM2_PROVENANCE_SCHEMA_VERSION,
        "anm2_sha256": hashlib.sha256(anm2_payload).hexdigest().upper(),
        "source_fbx": str(source_fbx),
        "source_fbx_sha256": str(source_fbx_sha256).upper(),
        "source_fbx_fps": _positive_fps(source_fbx_fps, "source_fbx_fps"),
        "sample_fps": _positive_fps(sample_fps, "sample_fps"),
        "playback_fps": _positive_fps(playback_fps, "playback_fps"),
        "source_duration_seconds": duration,
        "frame_count": count,
        "root_motion_mode": str(root_motion_mode),
        "root_heading_mode": str(root_heading_mode),
    }


def write_anm2_provenance(
    anm2_path: str | Path,
    payload: Mapping[str, Any],
) -> Path:
    source = Path(anm2_path)
    if not source.is_file():
        raise FileNotFoundError(source)
    rendered = dict(payload)
    rendered["format"] = ANM2_PROVENANCE_FORMAT
    rendered["schema_version"] = ANM2_PROVENANCE_SCHEMA_VERSION
    rendered["anm2_sha256"] = hashlib.sha256(source.read_bytes()).hexdigest().upper()
    destination = anm2_provenance_path(source)
    destination.parent.mkdir(parents=True, exist_ok=True)
    data = (
        json.dumps(rendered, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    ).encode("utf-8")
    handle, temporary = tempfile.mkstemp(
        prefix=destination.name + ".", suffix=".tmp", dir=destination.parent
    )
    try:
        with os.fdopen(handle, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, destination)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    return destination


@dataclass(frozen=True, slots=True)
class Anm2ProvenanceLoad:
    status: str
    payload: dict[str, Any]
    warnings: tuple[str, ...] = ()
    path: str = ""

    @property
    def valid(self) -> bool:
        return self.status == "valid"


def load_anm2_provenance(path: str | Path) -> Anm2ProvenanceLoad:
    source = Path(path)
    sidecar = anm2_provenance_path(source)
    if not sidecar.is_file():
        return Anm2ProvenanceLoad("missing", {}, (), str(sidecar))
    try:
        payload = json.loads(sidecar.read_text(encoding="utf-8-sig"))
        if not isinstance(payload, dict):
            raise ValueError("metadata root is not an object")
        if payload.get("format") != ANM2_PROVENANCE_FORMAT:
            raise ValueError("metadata format is not recognized")
        schema_version = payload.get("schema_version")
        if (
            isinstance(schema_version, bool)
            or not isinstance(schema_version, int)
            or schema_version != ANM2_PROVENANCE_SCHEMA_VERSION
        ):
            raise ValueError("metadata schema version is not supported")
        for field in ("source_fbx_fps", "sample_fps", "playback_fps"):
            payload[field] = _positive_fps(payload.get(field), field)
        payload["source_duration_seconds"] = _nonnegative_duration(
            payload.get("source_duration_seconds")
        )
        payload["frame_count"] = _positive_frame_count(payload.get("frame_count"))
        for field in ("source_fbx", "source_fbx_sha256", "root_motion_mode", "root_heading_mode"):
            if not isinstance(payload.get(field), str):
                raise ValueError(f"{field} must be a string")
        expected = str(payload.get("anm2_sha256", "")).upper()
        if len(expected) != 64 or any(character not in "0123456789ABCDEF" for character in expected):
            raise ValueError("anm2_sha256 must be a SHA-256 digest")
        actual = hashlib.sha256(source.read_bytes()).hexdigest().upper()
        if expected != actual:
            return Anm2ProvenanceLoad(
                "hash_mismatch",
                {},
                (
                    "ANM2 timing metadata was ignored because its SHA-256 does not "
                    "match the selected ANM2.",
                ),
                str(sidecar),
            )
        return Anm2ProvenanceLoad("valid", payload, (), str(sidecar))
    except (OSError, OverflowError, TypeError, ValueError, json.JSONDecodeError) as exc:
        return Anm2ProvenanceLoad(
            "invalid",
            {},
            (f"ANM2 timing metadata was ignored: {exc}",),
            str(sidecar),
        )


__all__ = [
    "ANM2_PROVENANCE_FORMAT",
    "ANM2_PROVENANCE_SCHEMA_VERSION",
    "Anm2ProvenanceLoad",
    "anm2_provenance_path",
    "build_anm2_provenance",
    "load_anm2_provenance",
    "write_anm2_provenance",
]
