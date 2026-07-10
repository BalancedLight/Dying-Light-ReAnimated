"""Sidecar manifests for tool-created RPack libraries."""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
import hashlib
import json
from pathlib import Path
from typing import Any, Iterable

from . import __version__


MANIFEST_FORMAT = "dl-reanimated-rpack-manifest"
MANIFEST_SCHEMA_VERSION = 1


@dataclass(slots=True)
class PackResourceManifest:
    resource_name: str
    script_resource: str
    source_fbx: str
    root_policy: str
    frame_count: int
    fps: int
    sha256: str
    mapping_profile_id: str = ""
    ik_preset: str = "runtime"
    extensions: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class PackManifest:
    pack_name: str
    pack_sha256: str
    project_id: str
    animation_resources: list[PackResourceManifest]
    animation_scripts: list[str]
    build_mode: str
    schema_version: int = MANIFEST_SCHEMA_VERSION
    format: str = MANIFEST_FORMAT
    created_with: str = __version__
    extensions: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    def save_for_pack(self, pack_path: str | Path) -> Path:
        path = manifest_path_for_pack(pack_path)
        path.write_text(json.dumps(self.to_dict(), indent=2) + "\n", encoding="utf-8")
        return path

    @classmethod
    def load_for_pack(cls, pack_path: str | Path) -> "PackManifest | None":
        path = manifest_path_for_pack(pack_path)
        if not path.exists():
            return None
        payload = json.loads(path.read_text(encoding="utf-8"))
        if payload.get("format") != MANIFEST_FORMAT:
            raise ValueError("existing sidecar is not a DL ReAnimated pack manifest")
        if int(payload.get("schema_version", 0)) > MANIFEST_SCHEMA_VERSION:
            raise ValueError("RPack manifest is newer than this build")
        resources = [PackResourceManifest(**row) for row in payload.get("animation_resources", [])]
        return cls(
            pack_name=str(payload.get("pack_name", Path(pack_path).name)),
            pack_sha256=str(payload.get("pack_sha256", "")),
            project_id=str(payload.get("project_id", "")),
            animation_resources=resources,
            animation_scripts=[str(value) for value in payload.get("animation_scripts", [])],
            build_mode=str(payload.get("build_mode", "unknown")),
            schema_version=int(payload.get("schema_version", MANIFEST_SCHEMA_VERSION)),
            format=str(payload.get("format", MANIFEST_FORMAT)),
            created_with=str(payload.get("created_with", "unknown")),
            extensions=dict(payload.get("extensions", {})),
        )

    def verify_pack_hash(self, pack_path: str | Path) -> bool:
        return self.pack_sha256.upper() == sha256_file(pack_path).upper()


def manifest_path_for_pack(pack_path: str | Path) -> Path:
    path = Path(pack_path)
    return path.with_name(path.name + ".dlrmanifest.json")


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def sha256_file(path: str | Path) -> str:
    return sha256_bytes(Path(path).read_bytes())


__all__ = [
    "MANIFEST_FORMAT",
    "MANIFEST_SCHEMA_VERSION",
    "PackManifest",
    "PackResourceManifest",
    "manifest_path_for_pack",
    "sha256_bytes",
    "sha256_file",
]
