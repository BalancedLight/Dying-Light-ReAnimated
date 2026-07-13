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
from .vendor.chrome_mesh_tools.math3d import matrix4_from_matrix3x4
from .vendor.chrome_mesh_tools.writer import SourceMsh

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


def _package_crig(
    bones: Sequence[RigBone],
    *,
    name: str,
    source_model_name: str,
    category: str,
    author: str,
    description: str,
    extensions: dict[str, Any],
    warnings: Sequence[str] = (),
) -> tuple[bytes, dict[str, Any]]:
    errors: list[str] = []
    names_seen: set[str] = set()
    descriptors: dict[int, str] = {}
    for index, bone in enumerate(bones):
        if bone.index != index:
            errors.append(f"non-contiguous rig index {bone.index}; expected {index}")
        folded = bone.name.casefold()
        if folded in names_seen:
            errors.append(f"duplicate bone name: {bone.name}")
        names_seen.add(folded)
        previous = descriptors.get(int(bone.descriptor))
        if previous is not None and previous != bone.name:
            errors.append(
                f"descriptor collision: {previous!r} and {bone.name!r} "
                f"-> 0x{bone.descriptor:08X}"
            )
        descriptors[int(bone.descriptor)] = bone.name
        if bone.parent_index >= index or bone.parent_index < -1:
            errors.append(
                f"bone {bone.name!r} has invalid parent index {bone.parent_index}"
            )
    roots = [row.index for row in bones if row.parent_index < 0]
    if not roots:
        errors.append("no root bone")
    if errors:
        raise ValueError("Invalid Chrome Rig:\n- " + "\n- ".join(errors))

    validation_warnings = list(dict.fromkeys(str(value) for value in warnings))
    if len(roots) > 1:
        validation_warnings.append(
            f"multiple roots ({len(roots)}); the first root is primary"
        )
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
    # Bind transforms are part of a Chrome Rig's identity.  Using only names
    # and topology allowed two models with different authored binds to share a
    # rig reference and overwrite one another in the registry.  Fingerprinting
    # the complete canonical skeleton payload prevents a stale CRIG from being
    # silently accepted for a newly rebuilt mesh.
    skeleton_sha256 = hashlib.sha256(_json_bytes(skeleton)).hexdigest()
    fingerprint = skeleton_sha256[:24]
    manifest = {
        "format": CRIG_FORMAT,
        "schema_version": CRIG_SCHEMA_VERSION,
        "rig_id": f"custom:{fingerprint}",
        "name": name,
        "category": category,
        "description": description,
        "author": author,
        "license": "",
        "source_model_name": source_model_name,
        "bone_count": len(bones),
        "track_count": len(bones),
        "skeleton_sha256": skeleton_sha256,
        "writer_profile_sha256": hashlib.sha256(_json_bytes(writer)).hexdigest(),
        "extensions": dict(extensions),
    }
    validation = {"errors": [], "warnings": validation_warnings}
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
        "format": "dl_reanimated_model_importer_crig_build_v2",
        "rig_id": manifest["rig_id"],
        "name": name,
        "bone_count": len(bones),
        "deform_bone_count": sum(bool(row.deform) for row in bones),
        "helper_count": sum(bool(row.helper) for row in bones),
        "root_bones": [bones[index].name for index in roots],
        "skeleton_sha256": manifest["skeleton_sha256"],
        "warnings": validation["warnings"],
        "coordinate_convention": writer["coordinate_convention"],
    }
    return output.getvalue(), report


def build_crig_bytes(
    scene: FbxScene,
    *,
    name: str,
    category: str = "Generic Object",
    author: str = "",
    description: str = "",
    orientation_policy: str = "auto",
) -> tuple[bytes, dict[str, Any]]:
    resolved_orientation = scene.resolved_orientation_policy(orientation_policy)
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
        globals_m[bone_id] = scene.to_chrome_global_matrix(converted, resolved_orientation)
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
    if errors:
        raise ValueError("Invalid Chrome Rig:\n- " + "\n- ".join(errors))
    payload, report = _package_crig(
        bones,
        name=name,
        source_model_name=scene.path.name,
        category=category,
        author=author,
        description=description,
        warnings=warnings,
        extensions={
            "source_unit_meters": scene.meters_per_unit,
            "builder": "dl_reanimated_model_importer_binary_fbx_v3",
            "model_axis_conversion": resolved_orientation,
            "requested_model_axis_conversion": orientation_policy,
            "resolved_model_axis_conversion": resolved_orientation,
            "source_to_dying_light_basis": (
                "x,z,-y"
                if resolved_orientation == "fbx_y_up_to_dying_light"
                else resolved_orientation
            ),
            "model_msh_reference_policy": "inverse_global_bind",
            "source_fbx_sha256": scene.sha256,
        },
    )
    report.update(
        {
            "orientation_policy": orientation_policy,
            "resolved_orientation_policy": resolved_orientation,
        }
    )
    return payload, report


def build_crig_from_source_msh_bytes(
    source: SourceMsh,
    *,
    name: str,
    source_model_name: str,
    source_sha256: str = "",
    category: str = "Generic Object",
    author: str = "",
    description: str = "",
    aliases_by_name: dict[str, Sequence[str]] | None = None,
    resolved_orientation_policy: str = "none",
) -> tuple[bytes, dict[str, Any]]:
    """Build a rig from the exact hierarchy written into the source MSH.

    FBX bone axes are re-authored to Chrome's local +X convention and humanoid
    pivots may be fitted before MSH serialization.  A `.crig` built from the
    raw FBX therefore describes a different bind from the compiled mesh.  This
    path makes animation retargeting use precisely the local transforms that
    Techland's mesh frontend receives.
    """

    selected = [
        index for index, node in enumerate(source.nodes) if int(node.node_type) in {4, 8}
    ]
    if not selected:
        raise ValueError("A .crig requires BONE or HELPER animation entities")
    physical_by_source = {source_index: physical for physical, source_index in enumerate(selected)}
    alias_rows = {
        str(key).casefold(): tuple(str(value) for value in values if str(value).strip())
        for key, values in dict(aliases_by_name or {}).items()
    }
    bones: list[RigBone] = []
    warnings: list[str] = []
    for physical, source_index in enumerate(selected):
        node = source.nodes[source_index]
        parent = (
            physical_by_source.get(int(node.parent_index), -1)
            if int(node.parent_index) >= 0
            else -1
        )
        if int(node.parent_index) >= 0 and int(node.parent_index) not in physical_by_source:
            raise ValueError(
                f"animation entity {node.name!r} has non-animation parent "
                f"{node.parent_index}; cannot construct a coherent .crig"
            )
        local = np.asarray(matrix4_from_matrix3x4(node.local_matrix), dtype=float)
        translation, rotation, scale = _decompose(local)
        if not node.name.isascii():
            warnings.append(f"non-ASCII bone name: {node.name}")
        if max(scale) - min(scale) > 1.0e-5:
            warnings.append(f"non-uniform bind scale: {node.name}")
        bones.append(
            RigBone(
                index=physical,
                name=node.name,
                parent_index=parent,
                descriptor=dl_name_hash(node.name),
                bind_translation=translation,
                bind_rotation_wxyz=rotation,
                bind_scale=scale,
                deform=int(node.node_type) == 8,
                helper=int(node.node_type) == 4,
                aliases=alias_rows.get(node.name.casefold(), ()),
            )
        )
    payload, report = _package_crig(
        bones,
        name=name,
        source_model_name=source_model_name,
        category=category,
        author=author,
        description=description,
        warnings=warnings,
        extensions={
            "builder": "dl_reanimated_model_importer_authored_msh_v1",
            "resolved_model_axis_conversion": str(resolved_orientation_policy),
            "model_msh_reference_policy": "inverse_global_bind",
            "bind_source": "exact_authored_source_msh_animation_entities",
            "requires_bind_basis_retarget": True,
            "source_fbx_sha256": source_sha256,
        },
    )
    report["bind_source"] = "source_msh"
    report["animation_entity_count"] = len(bones)
    report["resolved_orientation_policy"] = str(resolved_orientation_policy)
    return payload, report


def _write_crig_payload(
    payload: bytes,
    report: dict[str, Any],
    output_path: str | Path,
) -> tuple[Path, dict[str, Any]]:
    destination = Path(output_path)
    if destination.suffix.casefold() != ".crig":
        destination = destination.with_suffix(".crig")
    destination.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary = tempfile.mkstemp(
        prefix=destination.name + ".", suffix=".tmp", dir=destination.parent
    )
    try:
        with os.fdopen(handle, "wb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, destination)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    result = dict(report)
    result.update(
        {
            "path": str(destination),
            "size": len(payload),
            "sha256": hashlib.sha256(payload).hexdigest(),
        }
    )
    return destination, result


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
    return _write_crig_payload(payload, report, output_path)


def create_crig_from_source_msh(
    source: SourceMsh,
    output_path: str | Path,
    *,
    name: str,
    source_model_name: str,
    source_sha256: str = "",
    aliases_by_name: dict[str, Sequence[str]] | None = None,
    resolved_orientation_policy: str = "none",
) -> tuple[Path, dict[str, Any]]:
    payload, report = build_crig_from_source_msh_bytes(
        source,
        name=name,
        source_model_name=source_model_name,
        source_sha256=source_sha256,
        aliases_by_name=aliases_by_name,
        resolved_orientation_policy=resolved_orientation_policy,
    )
    return _write_crig_payload(payload, report, output_path)


__all__ = [
    "build_crig_bytes",
    "build_crig_from_source_msh_bytes",
    "create_crig_file",
    "create_crig_from_source_msh",
    "dl_name_hash",
]
