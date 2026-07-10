from __future__ import annotations

import hashlib
import json
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any


Vec3 = tuple[float, float, float]
Quat = tuple[float, float, float, float]
Mat4 = list[list[float]]


def identity_matrix() -> Mat4:
    return [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]


@dataclass(slots=True)
class Bone:
    name: str
    parent_index: int = -1
    rest_matrix: Mat4 = field(default_factory=identity_matrix)


@dataclass(slots=True)
class TransformKey:
    frame: int
    translation: Vec3 = (0.0, 0.0, 0.0)
    rotation: Quat = (1.0, 0.0, 0.0, 0.0)
    scale: Vec3 = (1.0, 1.0, 1.0)


@dataclass(slots=True)
class BoneTrack:
    bone_name: str
    keys: list[TransformKey] = field(default_factory=list)


@dataclass(slots=True)
class NormalizedAnimation:
    source_path: str
    source_hash: str
    fps: int
    frame_count: int
    bones: list[Bone] = field(default_factory=list)
    tracks: list[BoneTrack] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)

    @classmethod
    def from_path_hash(cls, path: str | Path, fps: int, frame_count: int) -> "NormalizedAnimation":
        path = str(path)
        digest = hashlib.sha256(path.encode("utf-8")).hexdigest()
        return cls(source_path=path, source_hash=digest, fps=fps, frame_count=frame_count)


@dataclass(slots=True)
class TargetSkeleton:
    skeleton_id: str
    family: str
    display_name: str
    bones: list[Bone] = field(default_factory=list)
    source_asset_path: str = ""
    warnings: list[str] = field(default_factory=list)

    @property
    def bone_names(self) -> list[str]:
        return [bone.name for bone in self.bones]


@dataclass(slots=True)
class BoneMapping:
    source_bone: str
    target_bone: str
    confidence: float
    method: str
    manual: bool = False


@dataclass(slots=True)
class BoneRemapProfile:
    source_hash: str
    target_skeleton_id: str
    mappings: list[BoneMapping] = field(default_factory=list)
    ignored_tracks: list[str] = field(default_factory=list)
    manual_overrides: dict[str, str] = field(default_factory=dict)
    notes: list[str] = field(default_factory=list)

    def mapped_target_for(self, source_bone: str) -> str | None:
        for mapping in self.mappings:
            if mapping.source_bone == source_bone:
                return mapping.target_bone
        return None


@dataclass(slots=True)
class Anm2ExportReport:
    output_name: str
    codec_mode: str
    mapped_track_count: int
    validation_status: str
    warnings: list[str] = field(default_factory=list)
    header: dict[str, Any] | None = None
    compiler_smoke_result: str | None = None
    written_files: list[str] = field(default_factory=list)


def to_json(data: Any) -> str:
    return json.dumps(asdict(data), indent=2)


def write_json(path: str | Path, data: Any) -> Path:
    out = Path(path)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(to_json(data), encoding="utf-8")
    return out

