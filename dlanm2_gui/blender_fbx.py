"""Blender discovery and background FBX export orchestration."""

from __future__ import annotations

from dataclasses import dataclass, replace
import json
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
    append_unknown_track_helpers,
    chrome_rig_from_fbx_skeleton,
    decode_anm2_animation,
    normalize_unknown_track_policy,
    reconstruct_native_scene,
    resample_animation_scene,
    retarget_decoded_animation,
    unknown_track_indices,
    write_sparse_fbx_job,
    write_unknown_track_sidecar,
)
from .anm2_provenance import load_anm2_provenance
from .bone_maps import GenericBoneMap
from .chrome_rig import ChromeRig
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
        for line in log.splitlines():
            if line.startswith("DLR_ROOT_PARITY:"):
                try:
                    parity_payload = json.loads(
                        line[len("DLR_ROOT_PARITY:") :].strip()
                    )
                except (TypeError, ValueError, json.JSONDecodeError):
                    parity_payload = None
        if not isinstance(parity_payload, dict):
            Path(temporary_output_name).unlink(missing_ok=True)
            tail = "\n".join(log.splitlines()[-30:])
            raise RuntimeError(
                "Blender exited without confirming native root parity."
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
        str(destination.resolve()), scene.frame_count, scene.fps, len(scene.bones),
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
    animation_with_helpers = animation
    if unresolved_count and resolved_unknown_policy in {"sidecar", "helpers"}:
        # Unknown transforms are intentionally decoded in their own selected
        # pass. They never inflate the main 271-bone skeleton decode/job.
        unknown_animation = decode_anm2_animation(
            anm2_path,
            fps=input_rate,
            start_frame=start_frame,
            end_frame=end_frame,
            selected_descriptors=unresolved_descriptors,
            progress=stages,
            cancel_check=cancel_check,
        )
        if resolved_unknown_policy == "helpers":
            animation_with_helpers = _merge_decoded_tracks(
                animation, unknown_animation
            )
    if target_fbx is None:
        scene = reconstruct_native_scene(
            animation_with_helpers,
            source_rig,
            unknown_track_policy=resolved_unknown_policy,
        )
        if unresolved_count and resolved_unknown_policy == "sidecar":
            scene.warnings.append(
                f"{unresolved_count} unresolved ANM2 track(s) are excluded from the skeleton "
                "and will be preserved in a deterministic .dlr_unknown_tracks.json sidecar."
            )
    else:
        if bone_map is None:
            raise ValueError("Cross-rig FBX export requires a reviewed generic bone map.")
        target_rig = chrome_rig_from_fbx_skeleton(target_fbx)
        scene = retarget_decoded_animation(
            animation, source_rig, target_rig, bone_map,
            translation_scale=translation_scale,
        )
        if unresolved_count and resolved_unknown_policy == "helpers":
            scene = append_unknown_track_helpers(
                scene, animation_with_helpers, source_rig
            )
        elif unresolved_count and resolved_unknown_policy == "sidecar":
            scene.warnings.append(
                f"{unresolved_count} unresolved ANM2 track(s) are excluded from the retargeted "
                "skeleton and will be preserved in a deterministic .dlr_unknown_tracks.json sidecar."
            )
        elif unresolved_count:
            scene.warnings.append(
                f"{unresolved_count} unresolved ANM2 track(s) were explicitly dropped; "
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
    return replace(
        result,
        unknown_track_policy=resolved_unknown_policy,
        unknown_track_count=unresolved_count,
        unknown_tracks_sidecar=str(sidecar or ""),
        anm2_input_fps=input_rate,
        fbx_output_fps=output_rate,
        timing_metadata_status=provenance_status,
        timing_metadata_path=provenance.path,
    )

__all__ = [
    "FbxExportResult", "discover_blender", "export_anm2_to_fbx", "run_blender_export",
]
