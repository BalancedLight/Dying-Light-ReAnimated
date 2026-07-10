"""Create a self-contained ``.crig`` target definition from one model FBX."""

from __future__ import annotations

import hashlib
from pathlib import Path
from typing import Any

import numpy as np

from .chrome_rig import ChromeRig, ChromeRigBone
from .oracle.binary_fbx_mixamo import _FbxDocument
from .oracle.smd_bind_pose import (
    parse_smd_bind_pose,
    quaternion_wxyz_from_matrix,
    smd_extrinsic_xyz_matrix,
)
from .trackmap import dl_name_hash, read_track_descriptors


def decompose_local_matrix(matrix: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Return translation, unit quaternion, and positive scale from an FBX local matrix."""

    value = np.asarray(matrix, dtype=float)
    if value.shape != (4, 4) or not np.isfinite(value).all():
        raise ValueError("local bind matrix must be a finite 4x4 matrix")
    translation = value[:3, 3].copy()
    basis = value[:3, :3].copy()
    scale = np.linalg.norm(basis, axis=0)
    if np.any(scale <= 1.0e-10):
        raise ValueError("local bind matrix contains singular scale")
    rotation = basis / scale
    if float(np.linalg.det(rotation)) <= 0.0:
        raise ValueError("negative or reflected local scale is not supported")
    orthogonality = float(np.max(np.abs(rotation.T @ rotation - np.eye(3))))
    if orthogonality > 1.0e-4:
        raise ValueError(f"local matrix contains unsupported shear ({orthogonality:.3g})")
    quaternion = quaternion_wxyz_from_matrix(rotation)
    return translation, quaternion, scale


def _topological_bone_names(document: _FbxDocument) -> list[str]:
    available = set(document.limb_models)
    result: list[str] = []
    visiting: set[str] = set()
    visited: set[str] = set()

    def visit(name: str) -> None:
        if name in visited:
            return
        if name in visiting:
            raise ValueError(f"FBX bone hierarchy contains a cycle at {name!r}")
        visiting.add(name)
        parent = document.parent_by_name.get(name)
        if parent in available:
            visit(str(parent))
        visiting.remove(name)
        visited.add(name)
        result.append(name)

    for name in sorted(available, key=str.lower):
        visit(name)
    return result


def build_chrome_rig_from_fbx(
    model_fbx: str | Path,
    *,
    name: str | None = None,
    category: str = "Generic Object",
    author: str = "",
    description: str = "",
    document_factory: Any = _FbxDocument,
) -> ChromeRig:
    source = Path(model_fbx)
    document = document_factory(source)
    if not document.limb_models:
        raise ValueError(
            "The model has no LimbNode armature. Add one root bone and skin the object to it."
        )
    names = _topological_bone_names(document)
    index_by_name = {bone_name: index for index, bone_name in enumerate(names)}
    meters_per_unit = float(document.meters_per_unit)
    bones: list[ChromeRigBone] = []
    for index, bone_name in enumerate(names):
        object_id = document.limb_models[bone_name]
        matrix = document._local_matrix(object_id, tick=0, use_animation=False)
        translation, quaternion, scale = decompose_local_matrix(matrix)
        parent_name = document.parent_by_name.get(bone_name)
        parent_index = index_by_name.get(str(parent_name), -1)
        bones.append(
            ChromeRigBone(
                index=index,
                name=bone_name,
                parent_index=parent_index,
                descriptor=dl_name_hash(bone_name),
                bind_translation=tuple(float(v * meters_per_unit) for v in translation),
                bind_rotation_wxyz=tuple(float(v) for v in quaternion),
                bind_scale=tuple(float(v) for v in scale),
                deform=True,
                helper=False,
            )
        )
    roots = [bone.index for bone in bones if bone.parent_index < 0]
    fingerprint = hashlib.sha256(
        "\n".join(
            f"{bone.index}|{bone.name}|{bone.parent_index}|{bone.descriptor:08x}"
            for bone in bones
        ).encode("utf-8")
    ).hexdigest()[:24]
    rig = ChromeRig(
        rig_id=f"custom:{fingerprint}",
        name=(name or source.stem).strip() or "Custom Rig",
        category=category,
        bones=tuple(bones),
        root_index=roots[0],
        source_model_name=source.name,
        author=author,
        description=description,
        extensions={
            "source_unit_meters": meters_per_unit,
            "builder": "binary_fbx_limb_nodes_v1",
            "deform_classification": "all_limb_nodes",
        },
    )
    rig.validate().require_valid()
    return rig


def create_chrome_rig_file(
    model_fbx: str | Path,
    output_path: str | Path,
    **kwargs: Any,
) -> Path:
    return build_chrome_rig_from_fbx(model_fbx, **kwargs).save(output_path)


def build_chrome_rig_from_smd_template(
    canonical_smd: str | Path,
    template_anm2: str | Path,
    *,
    rig_id: str = "builtin:male_npc_infected",
    name: str = "Dying Light Male NPC / Infected",
    category: str = "Humanoid",
) -> ChromeRig:
    """Convert the validated legacy target assets to the shared rig model.

    This keeps the humanoid solver unchanged while ensuring target hierarchy,
    descriptors, bind transforms, and writer settings use the same Chrome Rig
    representation as custom targets.
    """

    pose = parse_smd_bind_pose(canonical_smd)
    header, descriptors = read_track_descriptors(template_anm2)
    by_descriptor = {dl_name_hash(bone.name): bone for bone in pose.bones}
    animated = [by_descriptor[value] for value in descriptors if value in by_descriptor]
    track_index_by_smd_index = {bone.index: index for index, bone in enumerate(animated)}
    bones: list[ChromeRigBone] = []
    for index, bone in enumerate(animated):
        parent_index = (
            -1
            if bone.parent_index < 0
            else track_index_by_smd_index.get(bone.parent_index, -1)
        )
        quaternion = quaternion_wxyz_from_matrix(
            smd_extrinsic_xyz_matrix(bone.euler_xyz_radians)
        )
        bones.append(
            ChromeRigBone(
                index=index,
                name=bone.name,
                parent_index=parent_index,
                descriptor=dl_name_hash(bone.name),
                bind_translation=bone.translation,
                bind_rotation_wxyz=tuple(float(value) for value in quaternion),
                bind_scale=(1.0, 1.0, 1.0),
                deform=True,
                helper=False,
            )
        )
    matched = {bone.descriptor for bone in bones}
    extra = tuple(value for value in descriptors if value not in matched)
    roots = [bone.index for bone in bones if bone.parent_index < 0]
    rig = ChromeRig(
        rig_id=rig_id,
        name=name,
        category=category,
        bones=tuple(bones),
        root_index=roots[0],
        extra_track_descriptors=extra,
        track_descriptors=tuple(descriptors),
        source_model_name=Path(canonical_smd).name,
        description="Bundled editor-validated male NPC/infected humanoid target.",
        extensions={
            "legacy_template": Path(template_anm2).name,
            "legacy_template_unknown06": header.unknown06,
            "semantic_retarget_engine": "humanoid",
        },
    )
    # Preserve the template's validated header variant.
    rig.writer_profile = rig.writer_profile.__class__(
        format_version=header.format_version,
        unknown06=header.unknown06,
    )
    rig.validate().require_valid()
    return rig


__all__ = [
    "build_chrome_rig_from_fbx",
    "build_chrome_rig_from_smd_template",
    "create_chrome_rig_file",
    "decompose_local_matrix",
]
