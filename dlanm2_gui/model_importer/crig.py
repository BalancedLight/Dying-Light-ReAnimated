from __future__ import annotations

"""Write deterministic `.crig` packages compatible with DL ReAnimated 0.4.

A Chrome Rig is declarative target metadata. It does not contain executable code
or mesh geometry. The imported MSH remains the model resource; the `.crig`
allows the animation importer to target that exact skeleton later.
"""

from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Any, Sequence
import hashlib
import io
import json
import math
import os
import tempfile
import zipfile

import numpy as np

from .fbx_model import FbxScene

CRIG_FORMAT = "dl-reanimated-chrome-rig"
CRIG_SCHEMA_VERSION = 1
ANM2_FORMAT_VERSION = 42


def dl_name_hash(name: str) -> int:
    value = 0
    for byte in name.lower().encode("ascii", errors="ignore"):
        value = (byte + 41 * value) & 0xFFFFFFFF
    return value


def _json_bytes(value: Any) -> bytes:
    return (json.dumps(value, indent=2, ensure_ascii=False, sort_keys=True) + "\n").encode("utf-8")


@dataclass(frozen=True, slots=True)
class RigBone:
    index: int
    name: str
    parent_index: int
    descriptor: int
    bind_translation: tuple[float, float, float]
    bind_rotation_wxyz: tuple[float, float, float, float]
    bind_scale: tuple[float, float, float]
    deform: bool = True
    helper: bool = False
    aliases: tuple[str, ...] = ()
    tags: tuple[str, ...] = ()


def _quat_wxyz(matrix: np.ndarray) -> tuple[float, float, float, float]:
    m = np.asarray(matrix, dtype=float)[:3, :3]
    trace = float(np.trace(m))
    if trace > 0.0:
        s = math.sqrt(trace + 1.0) * 2.0
        w, x, y, z = 0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2.0
        w, x, y, z = (m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2.0
        w, x, y, z = (m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2.0
        w, x, y, z = (m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s
    q = np.asarray((w, x, y, z), dtype=float)
    norm = float(np.linalg.norm(q))
    if not math.isfinite(norm) or norm <= 1.0e-12:
        raise ValueError("bind rotation could not be normalized")
    return tuple(float(v) for v in q / norm)


def _decompose(matrix: np.ndarray) -> tuple[tuple[float, float, float], tuple[float, float, float, float], tuple[float, float, float]]:
    value = np.asarray(matrix, dtype=float)
    if value.shape != (4, 4) or not np.isfinite(value).all():
        raise ValueError("bind matrix must be a finite 4x4 matrix")
    translation = value[:3, 3].copy()
    linear = value[:3, :3].copy()
    scale = np.linalg.norm(linear, axis=0)
    if np.any(scale <= 1.0e-10):
        raise ValueError("bind matrix has singular scale")
    rotation = linear / scale
    u, _s, vt = np.linalg.svd(rotation)
    rotation = u @ vt
    if float(np.linalg.det(rotation)) <= 0.0:
        raise ValueError("negative/reflected bind scale is not supported by .crig")
    return (
        tuple(float(v) for v in translation),
        _quat_wxyz(rotation),
        tuple(float(v) for v in scale),
    )


def build_crig_bytes(
    scene: FbxScene,
    *,
    name: str,
    category: str = "Generic Object",
    author: str = "",
    description: str = "",
    orientation_policy: str = "auto",
) -> tuple[bytes, dict[str, Any]]:
    weighted = {
        cluster.bone_id
        for geometry in scene.geometries
        for cluster in geometry.clusters
        if cluster.bone_id is not None and any(weight > 1.0e-12 for weight in cluster.weights)
    }
    bone_ids = scene.depth_first_bones_for_weighted_ids(weighted)
    if not bone_ids:
        raise ValueError("A .crig requires a skinned LimbNode armature")
    globals_units = scene.bone_globals(bone_ids)
    globals_m = {}
    for bone_id, matrix in globals_units.items():
        converted = np.asarray(matrix, dtype=float).copy()
        converted[:3, 3] *= scene.meters_per_unit
        globals_m[bone_id] = scene.to_chrome_global_matrix(converted, orientation_policy)
    index_by_id = {bone_id: index for index, bone_id in enumerate(bone_ids)}
    bones: list[RigBone] = []
    errors: list[str] = []
    warnings: list[str] = []
    descriptors: dict[int, str] = {}
    names_seen: set[str] = set()
    for index, bone_id in enumerate(bone_ids):
        bone_name = scene.model_names[bone_id]
        folded = bone_name.casefold()
        if folded in names_seen:
            errors.append(f"duplicate bone name: {bone_name}")
        names_seen.add(folded)
        parent_id = scene.model_parent_id(bone_id)
        parent_index = index_by_id.get(parent_id, -1)
        local = (
            np.linalg.inv(globals_m[parent_id]) @ globals_m[bone_id]
            if parent_id in index_by_id
            else globals_m[bone_id]
        )
        translation, rotation, scale = _decompose(local)
        descriptor = dl_name_hash(bone_name)
        previous = descriptors.get(descriptor)
        if previous is not None and previous != bone_name:
            errors.append(
                f"descriptor collision: {previous!r} and {bone_name!r} -> 0x{descriptor:08X}"
            )
        descriptors[descriptor] = bone_name
        if not bone_name.isascii():
            warnings.append(f"non-ASCII bone name: {bone_name}")
        if max(scale) - min(scale) > 1.0e-5:
            warnings.append(f"non-uniform bind scale: {bone_name}")
        bones.append(
            RigBone(
                index=index,
                name=bone_name,
                parent_index=parent_index,
                descriptor=descriptor,
                bind_translation=translation,
                bind_rotation_wxyz=rotation,
                bind_scale=scale,
            )
        )
    roots = [row.index for row in bones if row.parent_index < 0]
    if not roots:
        errors.append("no root bone")
    if len(roots) > 1:
        warnings.append(f"multiple roots ({len(roots)}); the first root is primary")
    if errors:
        raise ValueError("Invalid Chrome Rig:\n- " + "\n- ".join(errors))

    skeleton = {
        "bones": [asdict(row) for row in bones],
        "extra_track_descriptors": [],
        "track_descriptors": [row.descriptor for row in bones],
        "root_index": roots[0],
    }
    writer = {
        "format_version": ANM2_FORMAT_VERSION,
        "unknown06": 1,
        "rotation_encoding": "cayley_xyz",
        "component_order": ["rx", "ry", "rz", "tx", "ty", "tz", "sx", "sy", "sz"],
        "coordinate_convention": "dying_light_model_local_column_vectors_translation_meters",
        "default_fps": 30,
        "default_root_policy": "exact",
    }
    fingerprint = hashlib.sha256(
        "\n".join(f"{row.index}|{row.name}|{row.parent_index}|{row.descriptor:08x}" for row in bones).encode("utf-8")
    ).hexdigest()[:24]
    manifest = {
        "format": CRIG_FORMAT,
        "schema_version": CRIG_SCHEMA_VERSION,
        "rig_id": f"custom:{fingerprint}",
        "name": name,
        "category": category,
        "description": description,
        "author": author,
        "license": "",
        "source_model_name": scene.path.name,
        "bone_count": len(bones),
        "track_count": len(bones),
        "skeleton_sha256": hashlib.sha256(_json_bytes(skeleton)).hexdigest(),
        "writer_profile_sha256": hashlib.sha256(_json_bytes(writer)).hexdigest(),
        "extensions": {
            "source_unit_meters": scene.meters_per_unit,
            "builder": "dl_reanimated_model_importer_binary_fbx_v2",
            "model_axis_conversion": orientation_policy,
            "source_to_dying_light_basis": (
                "x,z,-y"
                if orientation_policy in {"auto", "fbx_y_up_to_dying_light"}
                else orientation_policy
            ),
            "model_msh_reference_policy": "inverse_global_bind",
            "source_fbx_sha256": scene.sha256,
        },
    }
    validation = {"errors": [], "warnings": list(dict.fromkeys(warnings))}
    members = {
        "manifest.json": _json_bytes(manifest),
        "skeleton.json": _json_bytes(skeleton),
        "writer_profile.json": _json_bytes(writer),
        "validation.json": _json_bytes(validation),
    }
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as archive:
        for member_name in sorted(members):
            info = zipfile.ZipInfo(member_name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 0
            info.external_attr = 0o600 << 16
            archive.writestr(info, members[member_name])
    report = {
        "format": "dl_reanimated_model_importer_crig_build_v1",
        "rig_id": manifest["rig_id"],
        "name": name,
        "bone_count": len(bones),
        "root_bones": [bones[index].name for index in roots],
        "skeleton_sha256": manifest["skeleton_sha256"],
        "warnings": validation["warnings"],
        "orientation_policy": orientation_policy,
        "coordinate_convention": writer["coordinate_convention"],
    }
    return output.getvalue(), report


def create_crig_file(
    scene: FbxScene,
    output_path: str | Path,
    *,
    name: str,
    orientation_policy: str = "auto",
) -> tuple[Path, dict[str, Any]]:
    payload, report = build_crig_bytes(
        scene, name=name, orientation_policy=orientation_policy
    )
    destination = Path(output_path)
    if destination.suffix.casefold() != ".crig":
        destination = destination.with_suffix(".crig")
    destination.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary = tempfile.mkstemp(prefix=destination.name + ".", suffix=".tmp", dir=destination.parent)
    try:
        with os.fdopen(handle, "wb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, destination)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    report.update({
        "path": str(destination),
        "size": len(payload),
        "sha256": hashlib.sha256(payload).hexdigest(),
    })
    return destination, report


__all__ = ["build_crig_bytes", "create_crig_file", "dl_name_hash"]
