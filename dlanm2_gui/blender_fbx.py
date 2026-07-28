"""Blender discovery and background FBX export orchestration."""

from __future__ import annotations

import base64
from dataclasses import dataclass, replace
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from typing import Callable
import time
import string
import queue
import threading

import numpy as np

from .anm2_fbx import (
    AnimationScene,
    DecodedAnm2Animation,
    MOTION_HELPER_DESCRIPTOR,
    append_motion_accumulator_helper,
    append_unknown_track_helpers,
    bake_motion_accumulator_into_root,
    chrome_rig_from_fbx_skeleton,
    decode_anm2_animation,
    inspect_motion_accumulator,
    normalize_unknown_track_policy,
    reconstruct_native_scene,
    resample_animation_scene,
    resolve_translation_scale,
    retarget_decoded_animation,
    unknown_track_indices,
    write_sparse_fbx_job,
    write_unknown_track_sidecar,
)
from .anm2_provenance import load_anm2_provenance
from .bone_maps import GenericBoneMap
from .chrome_rig import ChromeRig
from .fbx_core import FbxDocument
from .roundtrip_contract import (
    embedded_native_metadata,
    finalize_roundtrip_contract,
    write_roundtrip_sidecar,
)
from .runtime_paths import resource_root

@dataclass(frozen=True, slots=True)
class FbxExportResult:
    output_path: str
    frame_count: int
    fps: float
    bone_count: int
    warnings: tuple[str, ...]
    blender_log: str
    unknown_track_policy: str = ""
    unknown_track_count: int = 0
    unknown_tracks_sidecar: str = ""
    animated_bone_count: int = 0
    fcurve_count: int = 0
    scalar_key_count: int = 0
    job_metadata_bytes: int = 0
    job_array_bytes: int = 0
    elapsed_seconds: float = 0.0
    anm2_input_fps: float = 0.0
    fbx_output_fps: float = 0.0
    timing_metadata_status: str = ""
    timing_metadata_path: str = ""
    root_parity_max_angular_degrees: float = 0.0
    root_parity_max_heading_degrees: float = 0.0
    root_parity_max_translation_m: float = 0.0
    native_rest_basis_max_rotation_degrees: float = 0.0
    motion_accumulator_detected: bool = False
    motion_accumulator_active: bool = False
    motion_accumulator_baked: bool = False
    motion_accumulator_helper_preserved: bool = False
    motion_accumulator_root: str = ""
    bind_pose_exported: bool = False
    bind_pose_bone_count: int = 0
    bind_pose_node_count: int = 0
    roundtrip_metadata_path: str = ""


class _StageProgress:
    def __init__(self, callback: Callable[[str], None] | None) -> None:
        self.callback = callback
        self.started = time.monotonic()

    def __call__(self, stage: str, current: int, total: int) -> None:
        if self.callback is None:
            return
        elapsed = time.monotonic() - self.started
        self.callback(
            f"{stage} — {int(current)}/{int(total)} — {elapsed:.1f}s elapsed"
        )


def _build_roundtrip_contract(
    animation: DecodedAnm2Animation,
    source_rig: ChromeRig,
    scene: AnimationScene,
    *,
    unknown_track_policy: str,
    accumulator_info: object | None,
) -> tuple[dict[str, object], dict[str, object]]:
    descriptor_nodes: dict[int, dict[str, object]] = {}
    for node in scene.bones:
        if node.descriptor is None:
            continue
        descriptor = int(node.descriptor)
        if descriptor in descriptor_nodes:
            raise ValueError(
                f"FBX scene maps descriptor 0x{descriptor:08X} to more than one node"
            )
        descriptor_nodes[descriptor] = {
            "descriptor": descriptor,
            "node_name": node.name,
            "node_kind": node.node_kind,
            "semantic": node.semantic,
        }

    source_descriptors = tuple(
        int(value)
        for value in (
            animation.container_descriptors or animation.descriptors
        )
    )
    if len(set(source_descriptors)) != len(source_descriptors):
        raise ValueError("Source ANM2 descriptor table contains duplicates")
    mapped_source_nodes = [
        descriptor_nodes[descriptor]
        for descriptor in source_descriptors
        if descriptor in descriptor_nodes
    ]
    missing_source_descriptors = [
        descriptor
        for descriptor in source_descriptors
        if descriptor not in descriptor_nodes
    ]
    if animation.frame_count == scene.frame_count and math.isclose(
        float(animation.fps),
        float(scene.fps),
        rel_tol=0.0,
        abs_tol=1.0e-9,
    ):
        nearest_source_frames = np.arange(scene.frame_count, dtype=np.int64)
    elif animation.frame_count <= 1:
        nearest_source_frames = np.zeros(scene.frame_count, dtype=np.int64)
    else:
        positions = np.arange(scene.frame_count, dtype=np.float64) * (
            float(animation.fps) / float(scene.fps)
        )
        positions[-1] = float(animation.frame_count - 1)
        nearest_source_frames = np.clip(
            np.rint(positions).astype(np.int64),
            0,
            animation.frame_count - 1,
        )
    animation_index = {
        int(descriptor): index
        for index, descriptor in enumerate(animation.descriptors)
    }
    rotation_branch_bits: dict[str, str] = {}
    rotation_orientation_bits: dict[str, str] = {}
    for descriptor in source_descriptors:
        track_index = animation_index.get(descriptor)
        if track_index is None:
            continue
        source_cayley = np.asarray(
            animation.values[nearest_source_frames, track_index, :3],
            dtype=np.float64,
        )
        far_branch = np.sum(source_cayley * source_cayley, axis=1) > 1.0
        packed = np.packbits(far_branch, bitorder="little")
        rotation_branch_bits[f"{descriptor:08X}"] = base64.b64encode(
            packed.tobytes()
        ).decode("ascii")
        dominant = np.argmax(np.abs(source_cayley), axis=1)
        dominant_values = source_cayley[
            np.arange(len(source_cayley)),
            dominant,
        ]
        positive_orientation = dominant_values >= 0.0
        orientation_packed = np.packbits(
            positive_orientation,
            bitorder="little",
        )
        rotation_orientation_bits[
            f"{descriptor:08X}"
        ] = base64.b64encode(orientation_packed.tobytes()).decode("ascii")

    skeleton_rows: list[dict[str, object]] = []
    for node in scene.bones:
        if node.node_kind != "bone":
            continue
        parent_name = (
            scene.bones[node.parent_index].name
            if node.parent_index >= 0
            else None
        )
        skeleton_rows.append(
            {
                "name": node.name,
                "parent_name": parent_name,
                "descriptor": int(node.descriptor or 0),
                "bind_translation": list(node.bind_translation),
                "bind_rotation_wxyz": list(node.bind_rotation_wxyz),
                "bind_scale": list(node.bind_scale),
                "deform": bool(node.deform),
                "helper": bool(node.helper),
            }
        )
    info_present = bool(getattr(accumulator_info, "present", False))
    info_active = bool(getattr(accumulator_info, "active", False))
    motion: dict[str, object] = {
        **dict(scene.motion_accumulator),
        "present": info_present,
        "active": info_active,
        "baked": bool(scene.motion_accumulator.get("baked", False)),
        "descriptor": MOTION_HELPER_DESCRIPTOR,
    }
    if motion["baked"]:
        helper_index = next(
            (
                index
                for index, node in enumerate(scene.bones)
                if int(node.descriptor or -1) == MOTION_HELPER_DESCRIPTOR
                and node.node_kind == "empty"
            ),
            None,
        )
        if helper_index is None:
            raise ValueError(
                "Baked motion accumulator has no preserved FBX helper node"
            )
        motion["original_bake_samples"] = [
            {
                "translation": [
                    float(value)
                    for value in scene.translations[frame, helper_index]
                ],
                "rotation_wxyz": [
                    float(value)
                    for value in scene.rotations_wxyz[frame, helper_index]
                ],
                "scale": [
                    float(value) for value in scene.scales[frame, helper_index]
                ],
            }
            for frame in range(scene.frame_count)
        ]

    contract: dict[str, object] = {
        "format": "dl-reanimated-native-roundtrip-contract",
        "schema_version": 1,
        "source_anm2_name": Path(animation.source_path).name,
        "source_anm2_sha256": animation.source_sha256,
        "source_container": animation.container,
        "source_descriptors": list(source_descriptors),
        "source_frame_start": animation.source_frame_start,
        "source_frame_end": animation.source_frame_end,
        "source_frame_count": animation.frame_count,
        "anm2_input_fps": float(
            scene.anm2_input_fps
            if scene.anm2_input_fps is not None
            else animation.fps
        ),
        "fbx_output_fps": float(scene.fps),
        "fbx_frame_count": scene.frame_count,
        "animation_stack": scene.name,
        "armature_object_name": scene.name,
        "rig_id": source_rig.rig_id,
        "rig_skeleton_hash": source_rig.skeleton_hash,
        "primary_root_name": source_rig.bones[source_rig.root_index].name,
        "expected_skeleton": skeleton_rows,
        "source_track_nodes": mapped_source_nodes,
        "source_rotation_branch_bits": rotation_branch_bits,
        "source_rotation_orientation_bits": rotation_orientation_bits,
        "source_rotation_branch_bit_order": "little",
        "missing_source_descriptors": missing_source_descriptors,
        "unknown_track_policy": unknown_track_policy,
        "roundtrip_capable": not missing_source_descriptors,
        "edit_policy": {
            "translation_tolerance_m": 2.0e-5,
            "scale_tolerance": 2.0e-5,
            "rotation_tolerance_degrees": 0.01,
            "allowed": [
                "pose_animation_on_named_bones",
                "object_animation_on_exported_track_empties",
            ],
        },
        "motion_accumulator": motion,
        "armature_object_transform_policy": "blender_identity_axis_export_v1",
    }
    return finalize_roundtrip_contract(contract), motion


def _pump_process_stream(stream, output: queue.Queue[str]) -> None:
    try:
        for line in iter(stream.readline, ""):
            output.put(line)
    finally:
        stream.close()

def discover_blender(explicit_path: str | Path | None = None) -> Path | None:
    candidates: list[Path] = []
    if explicit_path:
        explicit = Path(explicit_path)
        if explicit.is_file():
            return explicit.resolve()
        candidates.append(explicit)
    found = shutil.which("blender") or shutil.which("blender.exe")
    if found:
        return Path(found).resolve()
    bundled = resource_root() / "external" / "Blender" / "blender.exe"
    if bundled.is_file():
        return bundled.resolve()
    program_files = [os.environ.get("ProgramFiles"), os.environ.get("ProgramFiles(x86)")]
    for root in program_files:
        if not root:
            continue
        folder = Path(root) / "Blender Foundation"
        if folder.is_dir():
            candidates.extend(sorted(folder.glob("Blender */blender.exe"), reverse=True))
        candidates.append(Path(root) / "Steam" / "steamapps" / "common" / "Blender" / "blender.exe")
    if os.name == "nt":
        for letter in string.ascii_uppercase:
            drive = Path(f"{letter}:\\")
            if drive.exists():
                candidates.append(
                    drive / "SteamLibrary" / "steamapps" / "common" / "Blender" / "blender.exe"
                )
    valid = [candidate for candidate in candidates if candidate.is_file()]
    if not valid:
        return None
    return max(valid, key=lambda path: path.stat().st_mtime).resolve()

def run_blender_export(
    scene: AnimationScene,
    output_path: str | Path,
    *,
    blender_executable: str | Path | None = None,
    progress: Callable[[str], None] | None = None,
    cancel_check: Callable[[], bool] | None = None,
    timeout: int = 3600,
    stage_progress: _StageProgress | None = None,
) -> FbxExportResult:
    operation_started = time.monotonic()
    stages = stage_progress or _StageProgress(progress)
    blender = discover_blender(blender_executable)
    if blender is None:
        raise FileNotFoundError(
            "Blender was not found. Install Blender or choose blender.exe in the ANM2 to FBX workspace."
        )
    destination = Path(output_path)
    if destination.suffix.lower() != ".fbx":
        destination = destination.with_suffix(".fbx")
    destination.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary_output_name = tempfile.mkstemp(
        prefix=destination.stem + ".", suffix=".tmp.fbx", dir=destination.parent
    )
    os.close(handle)
    Path(temporary_output_name).unlink(missing_ok=True)
    helper = resource_root() / "dlanm2_gui" / "blender_scripts" / "export_anm2_fbx.py"
    if not helper.is_file():
        raise FileNotFoundError(f"Bundled Blender export helper is missing: {helper}")
    with tempfile.TemporaryDirectory(prefix="dlr_anm2_fbx_") as temp_dir:
        job_path = Path(temp_dir) / "job.json"
        arrays_path = Path(temp_dir) / "job_arrays.npz"
        stages("Building sparse curves", 0, scene.frame_count)
        sparse_job = write_sparse_fbx_job(
            scene,
            job_path,
            arrays_path,
            temporary_output_name,
            tolerance=1.0e-7,
        )
        stages("Building sparse curves", scene.frame_count, scene.frame_count)
        metadata_bytes = job_path.stat().st_size
        array_bytes = arrays_path.stat().st_size
        command = [
            str(blender), "--background", "--factory-startup", "--python", str(helper),
            "--", "--job", str(job_path),
        ]
        stages("Starting Blender", 0, 1)
        process = subprocess.Popen(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        started = time.monotonic()
        assert process.stdout is not None and process.stderr is not None
        lines: queue.Queue[str] = queue.Queue()
        readers = [
            threading.Thread(
                target=_pump_process_stream, args=(process.stdout, lines), daemon=True
            ),
            threading.Thread(
                target=_pump_process_stream, args=(process.stderr, lines), daemon=True
            ),
        ]
        for reader in readers:
            reader.start()
        log_rows: list[str] = []
        while process.poll() is None:
            while True:
                try:
                    line = lines.get_nowait()
                except queue.Empty:
                    break
                log_rows.append(line)
                if line.startswith("DLR_PROGRESS:"):
                    payload = line[len("DLR_PROGRESS:") :].strip()
                    try:
                        stage, current, total = payload.split("|", 2)
                        stages(stage, int(current), int(total))
                    except (TypeError, ValueError):
                        pass
            if cancel_check and cancel_check():
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                Path(temporary_output_name).unlink(missing_ok=True)
                raise RuntimeError("ANM2 to FBX export was cancelled.")
            if time.monotonic() - started > timeout:
                process.kill()
                raise TimeoutError(f"Blender FBX export exceeded {timeout} seconds.")
            time.sleep(0.1)
        for reader in readers:
            reader.join(timeout=2.0)
        while True:
            try:
                log_rows.append(lines.get_nowait())
            except queue.Empty:
                break
        log = "".join(log_rows)
        if process.returncode != 0:
            Path(temporary_output_name).unlink(missing_ok=True)
            tail = "\n".join(log.splitlines()[-30:])
            raise RuntimeError(f"Blender FBX export failed with exit code {process.returncode}:\n{tail}")
        if "DLR_EXPORT_COMPLETE:" not in log:
            Path(temporary_output_name).unlink(missing_ok=True)
            tail = "\n".join(log.splitlines()[-30:])
            raise RuntimeError(
                "Blender exited without confirming the DL ReAnimated FBX export."
                + (f"\n\nBlender log:\n{tail}" if tail else "")
            )
        parity_payload = None
        bind_pose_payload = None
        for line in log.splitlines():
            if line.startswith("DLR_ROOT_PARITY:"):
                try:
                    parity_payload = json.loads(
                        line[len("DLR_ROOT_PARITY:") :].strip()
                    )
                except (TypeError, ValueError, json.JSONDecodeError):
                    parity_payload = None
            elif line.startswith("DLR_BIND_POSE:"):
                try:
                    bind_pose_payload = json.loads(
                        line[len("DLR_BIND_POSE:") :].strip()
                    )
                except (TypeError, ValueError, json.JSONDecodeError):
                    bind_pose_payload = None
        if not isinstance(parity_payload, dict):
            Path(temporary_output_name).unlink(missing_ok=True)
            tail = "\n".join(log.splitlines()[-30:])
            raise RuntimeError(
                "Blender exited without confirming native root parity."
                + (f"\n\nBlender log:\n{tail}" if tail else "")
            )
        expected_bind_bones = sum(
            bone.node_kind == "bone" for bone in scene.bones
        )
        expected_bind_nodes = (
            expected_bind_bones
            + 1
            + int(bool(scene.roundtrip_contract))
        )
        if (
            not isinstance(bind_pose_payload, dict)
            or not bool(bind_pose_payload.get("exported", False))
            or int(bind_pose_payload.get("bone_count", -1))
            != expected_bind_bones
            or int(bind_pose_payload.get("node_count", -1))
            != expected_bind_nodes
        ):
            Path(temporary_output_name).unlink(missing_ok=True)
            tail = "\n".join(log.splitlines()[-30:])
            raise RuntimeError(
                "Blender exited without confirming a complete FBX bind pose."
                + (f"\n\nBlender log:\n{tail}" if tail else "")
            )
        temporary_output = Path(temporary_output_name)
        if not temporary_output.is_file() or temporary_output.stat().st_size == 0:
            tail = "\n".join(log.splitlines()[-40:])
            raise RuntimeError(
                "Blender reported success but did not create the FBX output."
                + (f"\n\nBlender log:\n{tail}" if tail else "")
            )
        os.replace(temporary_output, destination)
        stages("Starting Blender", 1, 1)
    return FbxExportResult(
        str(destination.resolve()),
        scene.frame_count,
        scene.fps,
        sum(bone.node_kind == "bone" for bone in scene.bones),
        tuple(scene.warnings), log,
        animated_bone_count=sparse_job.animated_bone_count,
        fcurve_count=sparse_job.fcurve_count,
        scalar_key_count=sparse_job.scalar_key_count,
        job_metadata_bytes=metadata_bytes,
        job_array_bytes=array_bytes,
        elapsed_seconds=time.monotonic() - operation_started,
        root_parity_max_angular_degrees=float(
            parity_payload["max_angular_error_degrees"]
        ),
        root_parity_max_heading_degrees=float(
            parity_payload["max_heading_error_degrees"]
        ),
        root_parity_max_translation_m=float(
            parity_payload["max_translation_error_m"]
        ),
        native_rest_basis_max_rotation_degrees=float(
            parity_payload["native_rest_basis_max_rotation_degrees"]
        ),
        bind_pose_exported=bool(bind_pose_payload["exported"]),
        bind_pose_bone_count=int(bind_pose_payload["bone_count"]),
        bind_pose_node_count=int(bind_pose_payload["node_count"]),
    )


def _merge_decoded_tracks(
    primary: DecodedAnm2Animation,
    secondary: DecodedAnm2Animation,
) -> DecodedAnm2Animation:
    if (
        primary.source_sha256 != secondary.source_sha256
        or primary.source_frame_start != secondary.source_frame_start
        or primary.source_frame_end != secondary.source_frame_end
        or primary.frame_count != secondary.frame_count
    ):
        raise ValueError("separate selected-track decoder passes do not describe one clip")
    sources = {
        int(descriptor): (animation, index)
        for animation in (primary, secondary)
        for index, descriptor in enumerate(animation.descriptors)
    }
    order = tuple(
        int(descriptor)
        for descriptor in (
            primary.container_descriptors
            or (*primary.descriptors, *secondary.descriptors)
        )
        if int(descriptor) in sources
    )
    values = np.stack(
        [sources[descriptor][0].values[:, sources[descriptor][1], :] for descriptor in order],
        axis=1,
    )
    quaternions = np.stack(
        [
            sources[descriptor][0].quaternions_wxyz[:, sources[descriptor][1], :]
            for descriptor in order
        ],
        axis=1,
    )
    return replace(
        primary,
        descriptors=order,
        values=values,
        quaternions_wxyz=quaternions,
        unique_packed_slots_decoded=(
            primary.unique_packed_slots_decoded
            + secondary.unique_packed_slots_decoded
        ),
        prepared_base_segment_count=(
            primary.prepared_base_segment_count
            + secondary.prepared_base_segment_count
        ),
    )

def export_anm2_to_fbx(
    anm2_path: str | Path,
    source_rig: ChromeRig,
    output_path: str | Path,
    *,
    fps: float | None = None,
    anm2_input_fps: float | None = None,
    fbx_output_fps: float | None = None,
    start_frame: int | None = None,
    end_frame: int | None = None,
    target_fbx: str | Path | None = None,
    bone_map: GenericBoneMap | None = None,
    translation_scale: str | float = "auto",
    unknown_track_policy: str | None = None,
    bake_motion_accumulator: bool = True,
    blender_executable: str | Path | None = None,
    progress: Callable[[str], None] | None = None,
    cancel_check: Callable[[], bool] | None = None,
) -> FbxExportResult:
    stages = _StageProgress(progress)
    if cancel_check is not None and cancel_check():
        raise RuntimeError("ANM2 to FBX export was cancelled.")
    stages("Reading ANM2", 0, 1)
    provenance = load_anm2_provenance(anm2_path)
    provenance_payload = provenance.payload if provenance.valid else {}

    def resolve_rate(explicit: float | None, metadata_field: str) -> float:
        candidate = (
            explicit
            if explicit is not None
            else fps
            if fps is not None
            else provenance_payload.get(metadata_field, 30.0)
        )
        value = float(candidate)
        if not np.isfinite(value) or value <= 0.0:
            raise ValueError(f"{metadata_field} must be finite and positive.")
        return value

    input_rate = resolve_rate(anm2_input_fps, "sample_fps")
    output_rate = resolve_rate(fbx_output_fps, "source_fbx_fps")
    source_bone_descriptors = tuple(int(bone.descriptor) for bone in source_rig.bones)
    animation: DecodedAnm2Animation = decode_anm2_animation(
        anm2_path,
        fps=input_rate,
        start_frame=start_frame,
        end_frame=end_frame,
        selected_descriptors=source_bone_descriptors,
        progress=stages,
        cancel_check=cancel_check,
    )
    provenance_warnings = list(provenance.warnings)
    provenance_status = provenance.status
    if provenance.valid and int(provenance_payload["frame_count"]) != int(
        animation.container_frame_count
    ):
        provenance_status = "frame_count_mismatch"
        provenance_payload = {}
        provenance_warnings = [
            "ANM2 timing metadata was ignored because its frame count does not "
            "match the selected ANM2."
        ]
        input_rate = resolve_rate(anm2_input_fps, "sample_fps")
        output_rate = resolve_rate(fbx_output_fps, "source_fbx_fps")
        animation = replace(animation, fps=input_rate)
    resolved_unknown_policy = normalize_unknown_track_policy(animation, unknown_track_policy)
    rig_descriptors = set(source_bone_descriptors)
    container_descriptors = (
        animation.container_descriptors or animation.descriptors
    )
    unresolved_descriptors = tuple(
        int(value) for value in container_descriptors if int(value) not in rig_descriptors
    )
    unresolved_count = len(unresolved_descriptors)
    unknown_animation: DecodedAnm2Animation | None = None
    helper_is_unresolved = MOTION_HELPER_DESCRIPTOR in unresolved_descriptors
    selected_unresolved_descriptors: tuple[int, ...]
    if resolved_unknown_policy in {"sidecar", "helpers"}:
        selected_unresolved_descriptors = unresolved_descriptors
    elif bake_motion_accumulator and helper_is_unresolved:
        selected_unresolved_descriptors = (MOTION_HELPER_DESCRIPTOR,)
    else:
        selected_unresolved_descriptors = ()
    if selected_unresolved_descriptors:
        # Unknown transforms are intentionally decoded in their own selected
        # pass. They never inflate the main 271-bone skeleton decode/job.
        unknown_animation = decode_anm2_animation(
            anm2_path,
            fps=input_rate,
            start_frame=start_frame,
            end_frame=end_frame,
            selected_descriptors=selected_unresolved_descriptors,
            progress=stages,
            cancel_check=cancel_check,
        )
    roundtrip_animation = (
        _merge_decoded_tracks(animation, unknown_animation)
        if unknown_animation is not None
        else animation
    )

    accumulator_animation: DecodedAnm2Animation | None = None
    if MOTION_HELPER_DESCRIPTOR in animation.descriptors:
        accumulator_animation = animation
    elif (
        unknown_animation is not None
        and MOTION_HELPER_DESCRIPTOR in unknown_animation.descriptors
    ):
        accumulator_animation = unknown_animation
    accumulator_info = (
        inspect_motion_accumulator(accumulator_animation)
        if accumulator_animation is not None
        else None
    )
    bake_active_accumulator = bool(
        bake_motion_accumulator
        and accumulator_info is not None
        and accumulator_info.active
    )
    motion_translation_scale = 1.0
    if target_fbx is None:
        scene = reconstruct_native_scene(
            animation,
            source_rig,
            unknown_track_policy="drop",
        )
    else:
        if bone_map is None:
            raise ValueError("Cross-rig FBX export requires a reviewed generic bone map.")
        target_rig = chrome_rig_from_fbx_skeleton(target_fbx)
        motion_translation_scale = (
            resolve_translation_scale(source_rig, target_rig, bone_map)
            if translation_scale == "auto"
            else float(translation_scale)
        )
        scene = retarget_decoded_animation(
            animation, source_rig, target_rig, bone_map,
            translation_scale=translation_scale,
        )
    if bake_active_accumulator:
        assert accumulator_animation is not None
        scene = bake_motion_accumulator_into_root(
            scene,
            accumulator_animation,
            translation_scale=motion_translation_scale,
        )
        scene = append_motion_accumulator_helper(scene, accumulator_animation)
    if unknown_animation is not None and resolved_unknown_policy == "helpers":
        scene = append_unknown_track_helpers(
            scene,
            unknown_animation,
            source_rig,
            excluded_descriptors=(MOTION_HELPER_DESCRIPTOR,) if bake_active_accumulator else (),
        )
    if unresolved_count and resolved_unknown_policy == "sidecar":
        scope = "retargeted " if target_fbx is not None else ""
        scene.warnings.append(
            f"{unresolved_count} unresolved ANM2 track(s) are excluded from the {scope}skeleton "
            "and will be preserved in a deterministic .dlr_unknown_tracks.json sidecar."
        )
    elif unresolved_count and resolved_unknown_policy == "drop":
        dropped_count = unresolved_count - int(bake_active_accumulator and helper_is_unresolved)
        if dropped_count:
            scene.warnings.append(
                f"{dropped_count} unresolved ANM2 track(s) were explicitly dropped; "
                "their transform curves are not present in the FBX or a sidecar."
            )
    scene.warnings[:] = list(dict.fromkeys([*provenance_warnings, *scene.warnings]))
    stages("Resampling animation", 0, scene.frame_count)
    scene = resample_animation_scene(
        scene,
        input_fps=input_rate,
        output_fps=output_rate,
    )
    stages("Resampling animation", scene.frame_count, scene.frame_count)
    helper_roundtrip = bool(
        source_rig.extensions.get("roundtrip_contract_required", False)
    )
    if (
        target_fbx is None
        and animation.header_version == 1
        and helper_roundtrip
    ):
        roundtrip_contract, motion_contract = _build_roundtrip_contract(
            roundtrip_animation,
            source_rig,
            scene,
            unknown_track_policy=resolved_unknown_policy,
            accumulator_info=accumulator_info,
        )
        roundtrip_warnings = list(scene.warnings)
        if not bool(roundtrip_contract["roundtrip_capable"]):
            roundtrip_warnings.append(
                "This FBX is a one-way export because one or more source "
                "descriptors have no editable FBX node. Choose helper roots "
                "for lossless exact reimport."
            )
        scene = replace(
            scene,
            warnings=list(dict.fromkeys(roundtrip_warnings)),
            motion_accumulator=motion_contract,
            roundtrip_contract=roundtrip_contract,
        )
    result = run_blender_export(
        scene,
        output_path,
        blender_executable=blender_executable,
        progress=progress,
        cancel_check=cancel_check,
        stage_progress=stages,
    )
    sidecar = None
    if unresolved_count and resolved_unknown_policy == "sidecar":
        assert unknown_animation is not None
        sidecar = write_unknown_track_sidecar(
            unknown_animation, source_rig, result.output_path
        )
        if progress and sidecar is not None:
            progress(f"Preserved {unresolved_count} unresolved track(s): {sidecar.name}")
    roundtrip_metadata = None
    if scene.roundtrip_contract and Path(result.output_path).is_file():
        exported_document = FbxDocument(result.output_path)
        native_metadata = embedded_native_metadata(exported_document)
        roundtrip_metadata = write_roundtrip_sidecar(
            result.output_path,
            native_metadata,
        )
        if progress:
            progress(f"Wrote round-trip contract: {roundtrip_metadata.name}")
    motion_metadata = dict(scene.motion_accumulator)
    return replace(
        result,
        unknown_track_policy=resolved_unknown_policy,
        unknown_track_count=unresolved_count,
        unknown_tracks_sidecar=str(sidecar or ""),
        anm2_input_fps=input_rate,
        fbx_output_fps=output_rate,
        timing_metadata_status=provenance_status,
        timing_metadata_path=provenance.path,
        motion_accumulator_detected=bool(
            accumulator_info is not None and accumulator_info.present
        ),
        motion_accumulator_active=bool(
            accumulator_info is not None and accumulator_info.active
        ),
        motion_accumulator_baked=bool(motion_metadata.get("baked", False)),
        motion_accumulator_helper_preserved=bool(
            motion_metadata.get("preserved_helper", False)
        ),
        motion_accumulator_root=str(motion_metadata.get("root_name", "")),
        roundtrip_metadata_path=str(roundtrip_metadata or ""),
    )

__all__ = [
    "FbxExportResult", "discover_blender", "export_anm2_to_fbx", "run_blender_export",
]
