"""ANM2 decoding, rig reconstruction, and generic reverse retarget services."""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable
import math

import numpy as np

from . import anm2
from .anm2_components import decode_samples
from .bone_maps import GenericBoneMap, skeleton_signature
from .chrome_rig import ChromeRig, ChromeRigBone
from .chrome_rig_builder import decompose_local_matrix, _topological_bone_names
from .oracle.binary_fbx_mixamo import _FbxDocument

MOTION_HELPER_DESCRIPTOR = 0xCCC3CDDF

@dataclass(frozen=True, slots=True)
class DecodedAnm2Animation:
    source_path: str
    name: str
    fps: int
    source_frame_start: int
    source_frame_end: int
    descriptors: tuple[int, ...]
    values: np.ndarray
    quaternions_wxyz: np.ndarray
    warnings: tuple[str, ...] = ()

    @property
    def frame_count(self) -> int:
        return int(self.values.shape[0])

@dataclass(frozen=True, slots=True)
class SceneBone:
    name: str
    parent_index: int
    descriptor: int | None
    bind_translation: tuple[float, float, float]
    bind_rotation_wxyz: tuple[float, float, float, float]
    bind_scale: tuple[float, float, float]
    deform: bool = True
    helper: bool = False

@dataclass(slots=True)
class AnimationScene:
    name: str
    fps: int
    bones: list[SceneBone]
    translations: np.ndarray
    rotations_wxyz: np.ndarray
    scales: np.ndarray
    source_frame_start: int = 0
    warnings: list[str] = field(default_factory=list)

    @property
    def frame_count(self) -> int:
        return int(self.translations.shape[0])

    def to_job_dict(self, output_path: str | Path) -> dict[str, Any]:
        return {
            "format": "dl-reanimated-blender-fbx-job",
            "schema_version": 1,
            "name": self.name,
            "fps": self.fps,
            "frame_start": 0,
            "frame_end": self.frame_count - 1,
            "source_frame_start": self.source_frame_start,
            "output_path": str(Path(output_path).resolve()),
            "bones": [
                {
                    "name": bone.name,
                    "parent_index": bone.parent_index,
                    "descriptor": bone.descriptor,
                    "bind_translation": list(bone.bind_translation),
                    "bind_rotation_wxyz": list(bone.bind_rotation_wxyz),
                    "bind_scale": list(bone.bind_scale),
                    "deform": bone.deform,
                    "helper": bone.helper,
                }
                for bone in self.bones
            ],
            "frames": [
                [
                    {
                        "translation": self.translations[f, b].tolist(),
                        "rotation_wxyz": self.rotations_wxyz[f, b].tolist(),
                        "scale": self.scales[f, b].tolist(),
                    }
                    for b in range(len(self.bones))
                ]
                for f in range(self.frame_count)
            ],
            "warnings": list(self.warnings),
        }

def cayley_to_quaternion_wxyz(vector: Iterable[float]) -> np.ndarray:
    value = np.asarray(tuple(vector), dtype=float)
    d = float(value @ value)
    result = np.asarray([(1.0 - d) / (1.0 + d), *(2.0 * value / (1.0 + d))], dtype=float)
    norm = float(np.linalg.norm(result))
    if not math.isfinite(norm) or norm <= 1.0e-12:
        raise ValueError("ANM2 rotation decoded to a singular quaternion")
    return result / norm

def decode_anm2_animation(
    path: str | Path,
    *,
    fps: int = 30,
    start_frame: int | None = None,
    end_frame: int | None = None,
) -> DecodedAnm2Animation:
    source = Path(path)
    data = source.read_bytes()
    from .dl2_anm2 import detect_anm2_format
    detected_format = detect_anm2_format(data)
    if detected_format == 42:
        raise ValueError(
            "Dying Light 2 ANM2 format 42 detected. Header and descriptor inspection is "
            "available, but the animation curve decoder is incomplete; no static FBX was exported."
        )
    header = anm2.Anm2Header.parse(data)
    layout = anm2.probe_v1_layout(header, data)
    if layout is None:
        raise ValueError("Only the decoded PC ANM2 Version-1 sampler layout is supported.")
    if layout.validation_errors:
        raise ValueError("Invalid ANM2 layout:\n- " + "\n- ".join(layout.validation_errors))
    if not 1 <= int(fps) <= 240:
        raise ValueError("FBX playback FPS must be between 1 and 240.")
    first = 0 if start_frame is None else int(start_frame)
    last = header.frame_count - 1 if end_frame is None else int(end_frame)
    if first < 0 or last < first or last >= header.frame_count:
        raise ValueError(
            f"Frame range {first}..{last} is outside ANM2 range 0..{header.frame_count - 1}."
        )
    sample = decode_samples(data, [float(frame) for frame in range(first, last + 1)])
    values = np.asarray([frame.tracks for frame in sample.frames], dtype=float)
    quaternions = np.empty((values.shape[0], values.shape[1], 4), dtype=float)
    for frame_index in range(values.shape[0]):
        for track_index in range(values.shape[1]):
            quaternion = cayley_to_quaternion_wxyz(values[frame_index, track_index, :3])
            if frame_index and float(quaternion @ quaternions[frame_index - 1, track_index]) < 0.0:
                quaternion = -quaternion
            quaternions[frame_index, track_index] = quaternion
    return DecodedAnm2Animation(
        str(source.resolve()), source.stem, int(fps), first, last,
        sample.descriptors, values, quaternions,
    )

def _matrix_from_trs(translation, rotation_wxyz, scale) -> np.ndarray:
    w, x, y, z = map(float, rotation_wxyz)
    rotation = np.asarray(
        [
            [1 - 2 * (y*y + z*z), 2 * (x*y - z*w), 2 * (x*z + y*w)],
            [2 * (x*y + z*w), 1 - 2 * (x*x + z*z), 2 * (y*z - x*w)],
            [2 * (x*z - y*w), 2 * (y*z + x*w), 1 - 2 * (x*x + y*y)],
        ], dtype=float,
    )
    result = np.eye(4)
    result[:3, :3] = rotation @ np.diag(np.asarray(scale, dtype=float))
    result[:3, 3] = np.asarray(translation, dtype=float)
    return result

def _scene_bone_from_crig(bone: ChromeRigBone, *, parent_index: int | None = None) -> SceneBone:
    return SceneBone(
        bone.name, bone.parent_index if parent_index is None else parent_index, bone.descriptor,
        bone.bind_translation, bone.bind_rotation_wxyz, bone.bind_scale,
        bone.deform, bone.helper,
    )

def reconstruct_native_scene(
    animation: DecodedAnm2Animation,
    rig: ChromeRig,
    *,
    preserve_extra_tracks: bool = True,
) -> AnimationScene:
    rig.validate().require_valid()
    track_by_descriptor = {value: index for index, value in enumerate(animation.descriptors)}
    bones: list[SceneBone] = []
    motion_index: int | None = None
    extra_descriptors = [
        value for value in animation.descriptors
        if value not in {bone.descriptor for bone in rig.bones}
    ]
    if preserve_extra_tracks and MOTION_HELPER_DESCRIPTOR in extra_descriptors:
        motion_index = 0
        bones.append(SceneBone(
            "DLR_OffsetHelper_CCC3CDDF", -1, MOTION_HELPER_DESCRIPTOR,
            (0.0, 0.0, 0.0), (1.0, 0.0, 0.0, 0.0), (1.0, 1.0, 1.0), False, True,
        ))
    offset = len(bones)
    for bone in rig.bones:
        parent = bone.parent_index + offset if bone.parent_index >= 0 else (
            motion_index if motion_index is not None else -1
        )
        bones.append(_scene_bone_from_crig(bone, parent_index=parent))
    if preserve_extra_tracks:
        for descriptor in extra_descriptors:
            if descriptor == MOTION_HELPER_DESCRIPTOR:
                continue
            bones.append(SceneBone(
                f"DLR_Track_{descriptor:08X}", -1, descriptor,
                (0.0, 0.0, 0.0), (1.0, 0.0, 0.0, 0.0), (1.0, 1.0, 1.0), False, True,
            ))
    frames, count = animation.frame_count, len(bones)
    translations = np.zeros((frames, count, 3), dtype=float)
    rotations = np.zeros((frames, count, 4), dtype=float)
    scales = np.ones((frames, count, 3), dtype=float)
    for bone_index, bone in enumerate(bones):
        track_index = track_by_descriptor.get(bone.descriptor) if bone.descriptor is not None else None
        if track_index is None:
            translations[:, bone_index] = bone.bind_translation
            rotations[:, bone_index] = bone.bind_rotation_wxyz
            scales[:, bone_index] = bone.bind_scale
        else:
            translations[:, bone_index] = animation.values[:, track_index, 3:6]
            rotations[:, bone_index] = animation.quaternions_wxyz[:, track_index]
            scales[:, bone_index] = animation.values[:, track_index, 6:9]
    missing = [bone.name for bone in rig.bones if bone.descriptor not in track_by_descriptor]
    warnings = []
    if missing:
        warnings.append(f"{len(missing)} rig bone(s) are absent from the ANM2 and remain at bind pose.")
    return AnimationScene(
        animation.name, animation.fps, bones, translations, rotations, scales,
        animation.source_frame_start, warnings,
    )

def chrome_rig_from_fbx_skeleton(path: str | Path) -> ChromeRig:
    document = _FbxDocument(Path(path))
    names = _topological_bone_names(document)
    index_by_name = {name: index for index, name in enumerate(names)}
    meters = float(document.meters_per_unit)
    bones: list[ChromeRigBone] = []
    from .trackmap import dl_name_hash
    for index, name in enumerate(names):
        local = document._local_matrix(document.limb_models[name], tick=0, use_animation=False)
        translation, quaternion, scale = decompose_local_matrix(local)
        parent_name = document.parent_by_name.get(name)
        bones.append(ChromeRigBone(
            index, name, index_by_name.get(str(parent_name), -1), dl_name_hash(name),
            tuple(float(v * meters) for v in translation),
            tuple(float(v) for v in quaternion), tuple(float(v) for v in scale),
        ))
    roots = [bone.index for bone in bones if bone.parent_index < 0]
    signature = skeleton_signature(
        (bone.name, bones[bone.parent_index].name if bone.parent_index >= 0 else None)
        for bone in bones
    )
    return ChromeRig(
        rig_id=f"fbx-target:{signature[:24]}", name=Path(path).stem,
        category="FBX Target", bones=tuple(bones), root_index=roots[0],
        source_model_name=Path(path).name,
    )

def _global_matrices(local: list[np.ndarray], bones: list[SceneBone]) -> list[np.ndarray]:
    result: list[np.ndarray | None] = [None] * len(bones)
    def resolve(index: int) -> np.ndarray:
        if result[index] is not None:
            return result[index]  # type: ignore[return-value]
        parent = bones[index].parent_index
        result[index] = resolve(parent) @ local[index] if parent >= 0 else local[index]
        return result[index]  # type: ignore[return-value]
    return [resolve(index) for index in range(len(bones))]

def _translation_scale(source: ChromeRig, target: ChromeRig, mapping: GenericBoneMap) -> float:
    source_by_descriptor = {bone.descriptor: bone for bone in source.bones}
    target_by_name = {bone.name: bone for bone in target.bones}
    pairs = {row.source_descriptor: row.target_bone for row in mapping.pairs}
    ratios: list[float] = []
    for descriptor, target_name in pairs.items():
        source_bone = source_by_descriptor.get(descriptor)
        target_bone = target_by_name.get(target_name)
        if source_bone is None or target_bone is None or source_bone.parent_index < 0 or target_bone.parent_index < 0:
            continue
        source_parent = source.bones[source_bone.parent_index]
        if pairs.get(source_parent.descriptor) != target.bones[target_bone.parent_index].name:
            continue
        sl = float(np.linalg.norm(source_bone.bind_translation))
        tl = float(np.linalg.norm(target_bone.bind_translation))
        if sl > 1.0e-8 and tl > 1.0e-8:
            ratios.append(tl / sl)
    return float(np.median(ratios)) if ratios else 1.0

def retarget_decoded_animation(
    animation: DecodedAnm2Animation,
    source_rig: ChromeRig,
    target_rig: ChromeRig,
    mapping: GenericBoneMap,
    *,
    translation_scale: str | float = "auto",
) -> AnimationScene:
    errors = mapping.validate()
    if errors:
        raise ValueError("Invalid generic bone map:\n- " + "\n- ".join(errors))
    if mapping.source_skeleton_hash and mapping.source_skeleton_hash != source_rig.skeleton_hash:
        raise ValueError("Bone map source skeleton hash does not match the selected source rig.")
    if mapping.target_skeleton_hash and mapping.target_skeleton_hash != target_rig.skeleton_hash:
        raise ValueError("Bone map target skeleton hash does not match the selected target FBX.")
    scale_factor = _translation_scale(source_rig, target_rig, mapping) if translation_scale == "auto" else float(translation_scale)
    native = reconstruct_native_scene(animation, source_rig, preserve_extra_tracks=False)
    source_bones = native.bones
    source_index = {bone.descriptor: index for index, bone in enumerate(source_bones)}
    target_bones = [_scene_bone_from_crig(bone) for bone in target_rig.bones]
    target_index = {bone.name: index for index, bone in enumerate(target_bones)}
    map_source_to_target = {
        source_index[row.source_descriptor]: target_index[row.target_bone]
        for row in mapping.pairs
        if row.source_descriptor in source_index and row.target_bone in target_index
    }
    frames, count = animation.frame_count, len(target_bones)
    translations = np.zeros((frames, count, 3), dtype=float)
    rotations = np.zeros((frames, count, 4), dtype=float)
    scales = np.ones((frames, count, 3), dtype=float)
    source_bind_local = [
        _matrix_from_trs(b.bind_translation, b.bind_rotation_wxyz, b.bind_scale)
        for b in source_bones
    ]
    target_bind_local = [
        _matrix_from_trs(b.bind_translation, b.bind_rotation_wxyz, b.bind_scale)
        for b in target_bones
    ]
    source_bind_global = _global_matrices(source_bind_local, source_bones)
    target_bind_global = _global_matrices(target_bind_local, target_bones)
    reverse_map = {target: source for source, target in map_source_to_target.items()}
    for frame in range(frames):
        source_local = [
            _matrix_from_trs(native.translations[frame, i], native.rotations_wxyz[frame, i], native.scales[frame, i])
            for i in range(len(source_bones))
        ]
        source_global = _global_matrices(source_local, source_bones)
        target_local = [value.copy() for value in target_bind_local]
        target_global: list[np.ndarray | None] = [None] * len(target_bones)
        for target_i, target_bone in enumerate(target_bones):
            source_i = reverse_map.get(target_i)
            if source_i is None:
                local = target_bind_local[target_i].copy()
            else:
                source_parent = source_bones[source_i].parent_index
                target_parent = target_bone.parent_index
                parents_match = source_parent >= 0 and map_source_to_target.get(source_parent) == target_parent
                if parents_match or (source_parent < 0 and target_parent < 0):
                    delta = np.linalg.inv(source_bind_local[source_i]) @ source_local[source_i]
                    delta[:3, 3] *= scale_factor
                    local = target_bind_local[target_i] @ delta
                else:
                    delta = np.linalg.inv(source_bind_global[source_i]) @ source_global[source_i]
                    delta[:3, 3] *= scale_factor
                    desired_global = target_bind_global[target_i] @ delta
                    parent_global = target_global[target_parent] if target_parent >= 0 else None
                    if target_parent >= 0 and parent_global is None:
                        parent_global = target_bind_global[target_parent]
                    local = np.linalg.inv(parent_global) @ desired_global if parent_global is not None else desired_global
            target_local[target_i] = local
            parent = target_bone.parent_index
            target_global[target_i] = (
                target_global[parent] @ local if parent >= 0 and target_global[parent] is not None else
                target_bind_global[parent] @ local if parent >= 0 else local
            )
            translation, quaternion, local_scale = decompose_local_matrix(local)
            translations[frame, target_i] = translation
            rotations[frame, target_i] = quaternion
            scales[frame, target_i] = local_scale
            if frame and float(rotations[frame, target_i] @ rotations[frame - 1, target_i]) < 0:
                rotations[frame, target_i] *= -1.0
    mapped_descriptors = {row.source_descriptor for row in mapping.pairs}
    warnings = []
    unmapped = [bone.name for bone in source_rig.bones if bone.descriptor not in mapped_descriptors]
    if unmapped:
        warnings.append(f"{len(unmapped)} source bone(s) are unmapped and were not transferred.")
    return AnimationScene(
        animation.name, animation.fps, target_bones, translations, rotations, scales,
        animation.source_frame_start, warnings,
    )

__all__ = [
    "AnimationScene", "DecodedAnm2Animation", "MOTION_HELPER_DESCRIPTOR", "SceneBone",
    "cayley_to_quaternion_wxyz", "chrome_rig_from_fbx_skeleton",
    "decode_anm2_animation", "reconstruct_native_scene", "retarget_decoded_animation",
]
