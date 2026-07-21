"""ANM2 decoding, rig reconstruction, and generic reverse retarget services."""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable
import hashlib
import json
import math

import numpy as np

from . import anm2
from .anm2_components import decode_all_frames_cached
from .bone_maps import GenericBoneMap, skeleton_signature
from .chrome_rig import ChromeRig, ChromeRigBone
from .chrome_rig_builder import decompose_local_matrix, _topological_bone_names
from .fbx_core import FbxDocument
from .root_heading import accumulated_heading_degrees, infer_target_up_axis

MOTION_HELPER_DESCRIPTOR = 0xCCC3CDDF
UNKNOWN_TRACK_POLICIES = ("sidecar", "helpers", "drop")
ANM2_COMPONENT_ORDER = ("rx", "ry", "rz", "tx", "ty", "tz", "sx", "sy", "sz")

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
    source_sha256: str = ""
    container: str = "dl1_header_version_1"
    signature: int = 42
    header_version: int = 1
    container_frame_count: int = 0
    static_stream_count: int = 0
    packed_stream_count: int = 0
    block_count: int = 0
    block_frame_spans: tuple[int, ...] = ()
    vfr_words: tuple[int, ...] = ()
    container_track_count: int = 0
    container_descriptors: tuple[int, ...] = ()
    unique_packed_slots_decoded: int = 0
    prepared_base_segment_count: int = 0

    @property
    def frame_count(self) -> int:
        return int(self.values.shape[0])

    @property
    def track_count(self) -> int:
        return len(self.descriptors)

    def decode_report(self, *, unknown_descriptor_count: int = 0) -> dict[str, Any]:
        """Return stable, JSON-ready provenance for a decoded clip."""

        return {
            "container": self.container,
            "signature": self.signature,
            "header_version": self.header_version,
            "frame_count": self.container_frame_count or self.frame_count,
            "track_count": self.container_track_count or self.track_count,
            "decoded_track_count": self.track_count,
            "static_stream_count": self.static_stream_count,
            "packed_stream_count": self.packed_stream_count,
            "block_count": self.block_count,
            "block_frame_spans": list(self.block_frame_spans),
            "vfr_words": list(self.vfr_words),
            "unknown_descriptor_count": int(unknown_descriptor_count),
            "source_anm2_sha256": self.source_sha256,
            "unique_packed_slots_decoded": self.unique_packed_slots_decoded,
            "prepared_base_segment_count": self.prepared_base_segment_count,
        }

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
        """Compatibility JSON view; production Blender jobs use sparse NPZ."""
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


@dataclass(frozen=True, slots=True)
class SparseFbxJob:
    metadata: dict[str, Any]
    arrays: dict[str, np.ndarray]

    @property
    def animated_bone_count(self) -> int:
        return int(self.metadata["sparse_summary"]["animated_bone_count"])

    @property
    def fcurve_count(self) -> int:
        return int(self.metadata["sparse_summary"]["fcurve_count"])

    @property
    def scalar_key_count(self) -> int:
        return int(self.metadata["sparse_summary"]["scalar_key_count"])


def _continuous_quaternion_array(values: np.ndarray) -> np.ndarray:
    result = np.asarray(values, dtype=np.float64).copy()
    norms = np.linalg.norm(result, axis=-1)
    if not np.isfinite(norms).all() or np.any(norms <= 1.0e-12):
        raise ValueError("animation contains a non-finite or singular quaternion")
    result /= norms[..., np.newaxis]
    if result.shape[0] > 1:
        steps = np.where(
            np.sum(result[1:] * result[:-1], axis=-1) < 0.0, -1.0, 1.0
        )
        signs = np.concatenate(
            (
                np.ones((1, *steps.shape[1:]), dtype=np.float64),
                np.cumprod(steps, axis=0),
            ),
            axis=0,
        )
        result *= signs[..., np.newaxis]
    return result


def build_sparse_fbx_job(
    scene: AnimationScene,
    output_path: str | Path,
    arrays_path: str | Path,
    *,
    tolerance: float = 1.0e-7,
) -> SparseFbxJob:
    """Build a complete bind skeleton plus sparse moving TRS component arrays."""

    if not math.isfinite(float(tolerance)) or tolerance <= 0.0:
        raise ValueError("sparse FBX tolerance must be finite and positive")
    frame_count = scene.frame_count
    bone_count = len(scene.bones)
    expected = (frame_count, bone_count)
    if scene.translations.shape[:2] != expected or scene.translations.shape[-1] != 3:
        raise ValueError("scene translation array does not match its frames and bones")
    if scene.rotations_wxyz.shape[:2] != expected or scene.rotations_wxyz.shape[-1] != 4:
        raise ValueError("scene rotation array does not match its frames and bones")
    if scene.scales.shape[:2] != expected or scene.scales.shape[-1] != 3:
        raise ValueError("scene scale array does not match its frames and bones")
    if not (
        np.isfinite(scene.translations).all()
        and np.isfinite(scene.rotations_wxyz).all()
        and np.isfinite(scene.scales).all()
    ):
        raise ValueError("scene animation arrays must be finite")

    bind_translation = np.asarray(
        [bone.bind_translation for bone in scene.bones], dtype=np.float64
    )
    bind_rotation = _continuous_quaternion_array(
        np.asarray([bone.bind_rotation_wxyz for bone in scene.bones], dtype=np.float64)[
            np.newaxis, ...
        ]
    )[0]
    bind_scale = np.asarray([bone.bind_scale for bone in scene.bones], dtype=np.float64)
    rotations = _continuous_quaternion_array(scene.rotations_wxyz)
    aligned = rotations.copy()
    aligned[
        np.sum(aligned * bind_rotation[np.newaxis, ...], axis=-1) < 0.0
    ] *= -1.0

    location_indices = np.flatnonzero(
        np.max(
            np.abs(scene.translations - bind_translation[np.newaxis, ...]),
            axis=(0, 2),
        )
        > tolerance
    ).astype(np.int32)
    rotation_indices = np.flatnonzero(
        np.max(
            np.abs(aligned - bind_rotation[np.newaxis, ...]), axis=(0, 2)
        )
        > tolerance
    ).astype(np.int32)
    scale_indices = np.flatnonzero(
        np.max(
            np.abs(scene.scales - bind_scale[np.newaxis, ...]), axis=(0, 2)
        )
        > tolerance
    ).astype(np.int32)
    arrays = {
        "frames": np.arange(frame_count, dtype=np.float64),
        "location_bone_indices": location_indices,
        "locations": np.asarray(
            scene.translations[:, location_indices, :], dtype=np.float64
        ),
        "rotation_bone_indices": rotation_indices,
        "rotations_wxyz": np.asarray(
            rotations[:, rotation_indices, :], dtype=np.float64
        ),
        "scale_bone_indices": scale_indices,
        "scales": np.asarray(scene.scales[:, scale_indices, :], dtype=np.float64),
    }
    animated_indices = sorted(
        set(map(int, location_indices))
        | set(map(int, rotation_indices))
        | set(map(int, scale_indices))
    )
    fcurve_count = (
        3 * len(location_indices)
        + 4 * len(rotation_indices)
        + 3 * len(scale_indices)
    )
    metadata = {
        "format": "dl-reanimated-blender-fbx-job",
        "schema_version": 2,
        "array_format": "numpy_npz_compressed",
        "arrays_path": str(Path(arrays_path).resolve()),
        "name": scene.name,
        "fps": scene.fps,
        "frame_start": 0,
        "frame_end": frame_count - 1,
        "source_frame_start": scene.source_frame_start,
        "output_path": str(Path(output_path).resolve()),
        "sparse_tolerance": float(tolerance),
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
            for bone in scene.bones
        ],
        "sparse_summary": {
            "skeleton_bone_count": sum(not bone.helper for bone in scene.bones),
            "helper_count": sum(bone.helper for bone in scene.bones),
            "animated_bone_count": len(animated_indices),
            "bind_only_bone_count": bone_count - len(animated_indices),
            "location_bone_count": len(location_indices),
            "rotation_bone_count": len(rotation_indices),
            "scale_bone_count": len(scale_indices),
            "fcurve_count": fcurve_count,
            "scalar_key_count": frame_count * fcurve_count,
            "frame_count": frame_count,
            "animated_bone_indices": animated_indices,
        },
        "warnings": list(scene.warnings),
    }
    return SparseFbxJob(metadata, arrays)


def write_sparse_fbx_job(
    scene: AnimationScene,
    job_path: str | Path,
    arrays_path: str | Path,
    output_path: str | Path,
    *,
    tolerance: float = 1.0e-7,
) -> SparseFbxJob:
    job = build_sparse_fbx_job(
        scene, output_path, arrays_path, tolerance=tolerance
    )
    metadata_path = Path(job_path)
    binary_path = Path(arrays_path)
    metadata_path.write_text(
        json.dumps(job.metadata, separators=(",", ":"), ensure_ascii=False),
        encoding="utf-8",
    )
    np.savez_compressed(binary_path, **job.arrays)
    return job

def cayley_to_quaternion_wxyz(vector: Iterable[float]) -> np.ndarray:
    value = np.asarray(tuple(vector), dtype=float)
    d = float(value @ value)
    result = np.asarray([(1.0 - d) / (1.0 + d), *(2.0 * value / (1.0 + d))], dtype=float)
    norm = float(np.linalg.norm(result))
    if not math.isfinite(norm) or norm <= 1.0e-12:
        raise ValueError("ANM2 rotation decoded to a singular quaternion")
    return result / norm


def cayley_to_quaternions_wxyz(vectors: np.ndarray) -> np.ndarray:
    """Vectorized Cayley conversion with frame-axis hemisphere continuity."""

    value = np.asarray(vectors, dtype=np.float64)
    if value.ndim < 1 or value.shape[-1] != 3 or not np.isfinite(value).all():
        raise ValueError("ANM2 Cayley rotations must be a finite array ending in XYZ")
    squared = np.sum(value * value, axis=-1)
    denominator = 1.0 + squared
    result = np.empty((*value.shape[:-1], 4), dtype=np.float64)
    result[..., 0] = (1.0 - squared) / denominator
    result[..., 1:4] = 2.0 * value / denominator[..., np.newaxis]
    norms = np.linalg.norm(result, axis=-1)
    if not np.isfinite(norms).all() or np.any(norms <= 1.0e-12):
        raise ValueError("ANM2 rotation decoded to a singular quaternion")
    result /= norms[..., np.newaxis]
    if result.shape[0] > 1:
        adjacent_dot = np.sum(result[1:] * result[:-1], axis=-1)
        steps = np.where(adjacent_dot < 0.0, -1.0, 1.0)
        signs = np.concatenate(
            (
                np.ones((1, *steps.shape[1:]), dtype=np.float64),
                np.cumprod(steps, axis=0),
            ),
            axis=0,
        )
        result *= signs[..., np.newaxis]
    return result

def decode_anm2_animation(
    path: str | Path,
    *,
    fps: int = 30,
    start_frame: int | None = None,
    end_frame: int | None = None,
    selected_descriptors: Iterable[int] | None = None,
    progress: Any | None = None,
    cancel_check: Any | None = None,
) -> DecodedAnm2Animation:
    source = Path(path)
    data = source.read_bytes()
    if progress is not None:
        progress("Reading ANM2", 1, 1)
    if not 1 <= int(fps) <= 240:
        raise ValueError("FBX playback FPS must be between 1 and 240.")
    cached = decode_all_frames_cached(
        data,
        selected_descriptors=selected_descriptors,
        progress=progress,
        cancel_check=cancel_check,
    )
    total_frame_count = cached.frame_count
    first = 0 if start_frame is None else int(start_frame)
    last = total_frame_count - 1 if end_frame is None else int(end_frame)
    if first < 0 or last < first or last >= total_frame_count:
        raise ValueError(
            f"Frame range {first}..{last} is outside ANM2 range 0..{total_frame_count - 1}."
        )
    values = cached.values[first : last + 1].copy()
    quaternions = cayley_to_quaternions_wxyz(values[..., :3])
    metadata: dict[str, Any] = {
        "source_sha256": hashlib.sha256(data).hexdigest().upper(),
        "container": cached.container,
        "signature": cached.signature,
        "header_version": cached.header_version,
        "container_frame_count": total_frame_count,
        "static_stream_count": cached.static_stream_count,
        "packed_stream_count": cached.packed_stream_count,
        "block_count": cached.block_count,
        "block_frame_spans": cached.block_frame_spans,
        "vfr_words": cached.vfr_words,
        "container_track_count": cached.container_track_count,
        "container_descriptors": cached.container_descriptors,
        "unique_packed_slots_decoded": cached.unique_packed_slots_decoded,
        "prepared_base_segment_count": cached.prepared_base_segment_count,
    }
    return DecodedAnm2Animation(
        str(source.resolve()), source.stem, int(fps), first, last,
        cached.descriptors, values, quaternions, **metadata,
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
        # CRIG helper/non-deform rows are still members of the authored
        # skeleton hierarchy. ``SceneBone.helper`` is reserved for optional
        # unknown-track EMPTY objects outside the armature.
        bone.deform, False,
    )


def normalize_unknown_track_policy(
    animation: DecodedAnm2Animation,
    policy: str | None = None,
    *,
    preserve_extra_tracks: bool | None = None,
) -> str:
    """Resolve the compatibility flag and the explicit three-way policy."""

    if policy is not None:
        value = str(policy).strip().casefold()
        if value not in UNKNOWN_TRACK_POLICIES:
            raise ValueError(
                "Unknown-track policy must be one of: " + ", ".join(UNKNOWN_TRACK_POLICIES)
            )
        return value
    if preserve_extra_tracks is not None:
        return "helpers" if preserve_extra_tracks else "drop"
    return "sidecar" if animation.header_version == 2 else "helpers"


def unknown_track_indices(
    animation: DecodedAnm2Animation,
    rig: ChromeRig,
) -> tuple[int, ...]:
    """Return unresolved animation tracks in original descriptor-table order."""

    bone_descriptors = {int(bone.descriptor) for bone in rig.bones}
    return tuple(
        index
        for index, descriptor in enumerate(animation.descriptors)
        if int(descriptor) not in bone_descriptors
    )


def build_decode_report(
    animation: DecodedAnm2Animation,
    rig: ChromeRig | None = None,
) -> dict[str, Any]:
    unknown_count = 0
    if rig is not None:
        known = {int(bone.descriptor) for bone in rig.bones}
        inventory = animation.container_descriptors or animation.descriptors
        unknown_count = sum(int(value) not in known for value in inventory)
    report = animation.decode_report(unknown_descriptor_count=unknown_count)
    if rig is not None:
        report["root_motion_diagnostics"] = build_root_motion_decode_diagnostics(
            animation, rig
        )
    return report


def _decoded_track_diagnostics(
    animation: DecodedAnm2Animation,
    track_index: int,
    *,
    up_axis: tuple[float, float, float],
) -> dict[str, Any]:
    rows = np.asarray(animation.values[:, track_index], dtype=float)
    translations = rows[:, 3:6]
    globals_: list[np.ndarray] = []
    for values, quaternion in zip(rows, animation.quaternions_wxyz[:, track_index]):
        globals_.append(_matrix_from_trs(values[3:6], quaternion, values[6:9]))
    return {
        "translation_start_m": translations[0].tolist(),
        "translation_end_m": translations[-1].tolist(),
        "translation_net_m": (translations[-1] - translations[0]).tolist(),
        "translation_min_m": np.min(translations, axis=0).tolist(),
        "translation_max_m": np.max(translations, axis=0).tolist(),
        "translation_range_m": np.ptp(translations, axis=0).tolist(),
        "accumulated_heading_degrees": accumulated_heading_degrees(
            globals_, up_axis
        ),
        "finite": bool(np.isfinite(rows).all()),
    }


def build_root_motion_decode_diagnostics(
    animation: DecodedAnm2Animation,
    rig: ChromeRig,
) -> dict[str, Any]:
    """Measure decoded root/accumulator curves without altering export arrays."""

    primary = rig.bones[rig.root_index]
    up_axis = infer_target_up_axis(rig)
    descriptor_to_index = {
        int(descriptor): index for index, descriptor in enumerate(animation.descriptors)
    }
    result: dict[str, Any] = {
        "target_primary_root": primary.name,
        "target_primary_root_descriptor": f"0x{int(primary.descriptor):08X}",
        "target_up_axis": list(up_axis),
        "diagnostic_only_no_curve_mutation": True,
    }
    root_track = descriptor_to_index.get(int(primary.descriptor))
    if root_track is None:
        result["skeletal_root"] = {"available": False}
    else:
        result["skeletal_root"] = {
            "available": True,
            "track_index": root_track,
            **_decoded_track_diagnostics(
                animation, root_track, up_axis=up_axis
            ),
        }
    motion_track = descriptor_to_index.get(MOTION_HELPER_DESCRIPTOR)
    if motion_track is None:
        result["motion_accumulator"] = {"available": False}
    else:
        result["motion_accumulator"] = {
            "available": True,
            "descriptor": f"0x{MOTION_HELPER_DESCRIPTOR:08X}",
            "track_index": motion_track,
            **_decoded_track_diagnostics(
                animation, motion_track, up_axis=up_axis
            ),
        }
    return result


def build_unknown_track_sidecar(
    animation: DecodedAnm2Animation,
    rig: ChromeRig,
) -> dict[str, Any]:
    """Preserve unresolved transforms without claiming they are skeleton bones."""

    unresolved = unknown_track_indices(animation, rig)
    source_hash = animation.source_sha256
    tracks: list[dict[str, Any]] = []
    for track_index in unresolved:
        frame_table = [
            [
                animation.source_frame_start + frame_index,
                *(float(value) for value in animation.values[frame_index, track_index]),
            ]
            for frame_index in range(animation.frame_count)
        ]
        tracks.append(
            {
                "track_index": (
                    animation.container_descriptors.index(
                        animation.descriptors[track_index]
                    )
                    if animation.container_descriptors
                    else track_index
                ),
                "descriptor": f"0x{int(animation.descriptors[track_index]):08X}",
                "semantic": "unknown_transform_track",
                "source_anm2_sha256": source_hash,
                "frame_table": frame_table,
            }
        )
    return {
        "format": "dl-reanimated-unknown-tracks",
        "schema_version": 1,
        "container": animation.container,
        "source_anm2_name": Path(animation.source_path).name,
        "source_anm2_sha256": source_hash,
        "source_frame_start": animation.source_frame_start,
        "source_frame_end": animation.source_frame_end,
        "frame_count": animation.frame_count,
        "component_order": ["frame", *ANM2_COMPONENT_ORDER],
        "unknown_descriptor_count": len(tracks),
        "tracks": tracks,
    }


def unknown_track_sidecar_path(output_path: str | Path) -> Path:
    destination = Path(output_path)
    return destination.with_suffix(".dlr_unknown_tracks.json")


def write_unknown_track_sidecar(
    animation: DecodedAnm2Animation,
    rig: ChromeRig,
    output_path: str | Path,
) -> Path | None:
    payload = build_unknown_track_sidecar(animation, rig)
    if not payload["tracks"]:
        return None
    destination = unknown_track_sidecar_path(output_path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(
        json.dumps(payload, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    return destination.resolve()


def append_unknown_track_helpers(
    scene: AnimationScene,
    animation: DecodedAnm2Animation,
    source_rig: ChromeRig,
) -> AnimationScene:
    """Attach unresolved tracks as independent, non-deforming FBX helper roots."""

    unresolved = unknown_track_indices(animation, source_rig)
    if not unresolved:
        return scene
    helper_bones = [
        SceneBone(
            (
                "DLR_OffsetHelper_CCC3CDDF"
                if animation.descriptors[index] == MOTION_HELPER_DESCRIPTOR
                else f"DLR_Track_{animation.descriptors[index]:08X}"
            ),
            -1,
            animation.descriptors[index],
            (0.0, 0.0, 0.0),
            (1.0, 0.0, 0.0, 0.0),
            (1.0, 1.0, 1.0),
            False,
            True,
        )
        for index in unresolved
    ]
    helper_translations = animation.values[:, unresolved, 3:6]
    helper_rotations = animation.quaternions_wxyz[:, unresolved]
    helper_scales = animation.values[:, unresolved, 6:9]
    return AnimationScene(
        scene.name,
        scene.fps,
        [*scene.bones, *helper_bones],
        np.concatenate((scene.translations, helper_translations), axis=1),
        np.concatenate((scene.rotations_wxyz, helper_rotations), axis=1),
        np.concatenate((scene.scales, helper_scales), axis=1),
        scene.source_frame_start,
        [
            *scene.warnings,
            f"{len(unresolved)} unresolved ANM2 track(s) are included as "
            "non-deforming hash-named helper roots.",
        ],
    )

def reconstruct_native_scene(
    animation: DecodedAnm2Animation,
    rig: ChromeRig,
    *,
    preserve_extra_tracks: bool | None = None,
    unknown_track_policy: str | None = None,
) -> AnimationScene:
    rig.validate().require_valid()
    resolved_policy = normalize_unknown_track_policy(
        animation,
        unknown_track_policy,
        preserve_extra_tracks=preserve_extra_tracks,
    )
    track_by_descriptor = {value: index for index, value in enumerate(animation.descriptors)}
    bones: list[SceneBone] = []
    motion_index: int | None = None
    extra_descriptors = [
        value for value in animation.descriptors
        if value not in {bone.descriptor for bone in rig.bones}
    ]
    include_helpers = resolved_policy == "helpers"
    if include_helpers and MOTION_HELPER_DESCRIPTOR in extra_descriptors:
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
    if include_helpers:
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
    warnings = []
    # Missing descriptors are the normal sparse case: the complete skeleton is
    # retained at bind and receives no curves. Do not surface that as a warning.
    if extra_descriptors:
        if resolved_policy == "sidecar":
            warnings.append(
                f"{len(extra_descriptors)} unresolved ANM2 track(s) are excluded from the skeleton "
                "and will be preserved in a deterministic .dlr_unknown_tracks.json sidecar."
            )
        elif resolved_policy == "helpers":
            warnings.append(
                f"{len(extra_descriptors)} unresolved ANM2 track(s) are included as "
                "non-deforming hash-named helper roots."
            )
        else:
            warnings.append(
                f"{len(extra_descriptors)} unresolved ANM2 track(s) were explicitly dropped; "
                "their transform curves are not present in the FBX or a sidecar."
            )
    return AnimationScene(
        animation.name, animation.fps, bones, translations, rotations, scales,
        animation.source_frame_start, warnings,
    )

def chrome_rig_from_fbx_skeleton(path: str | Path) -> ChromeRig:
    document = FbxDocument(Path(path))
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
    "ANM2_COMPONENT_ORDER", "AnimationScene", "DecodedAnm2Animation",
    "MOTION_HELPER_DESCRIPTOR", "SceneBone", "UNKNOWN_TRACK_POLICIES",
    "append_unknown_track_helpers", "build_decode_report", "build_root_motion_decode_diagnostics", "build_unknown_track_sidecar",
    "SparseFbxJob", "build_sparse_fbx_job", "cayley_to_quaternion_wxyz",
    "cayley_to_quaternions_wxyz", "chrome_rig_from_fbx_skeleton",
    "decode_anm2_animation", "normalize_unknown_track_policy", "reconstruct_native_scene",
    "retarget_decoded_animation", "unknown_track_indices", "unknown_track_sidecar_path",
    "write_sparse_fbx_job", "write_unknown_track_sidecar",
]
