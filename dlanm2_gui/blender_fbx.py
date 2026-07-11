"""Blender discovery and background FBX export orchestration."""

from __future__ import annotations

from dataclasses import dataclass
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from typing import Callable
import time
import string

from .anm2_fbx import (
    AnimationScene,
    DecodedAnm2Animation,
    chrome_rig_from_fbx_skeleton,
    decode_anm2_animation,
    reconstruct_native_scene,
    retarget_decoded_animation,
)
from .bone_maps import GenericBoneMap
from .chrome_rig import ChromeRig
from .runtime_paths import resource_root


@dataclass(frozen=True, slots=True)
class FbxExportResult:
    output_path: str
    frame_count: int
    fps: int
    bone_count: int
    warnings: tuple[str, ...]
    blender_log: str


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
) -> FbxExportResult:
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
        job = scene.to_job_dict(temporary_output_name)
        job_path.write_text(json.dumps(job, separators=(",", ":")), encoding="utf-8")
        command = [
            str(blender), "--background", "--factory-startup", "--python", str(helper),
            "--", "--job", str(job_path),
        ]
        if progress:
            progress(f"Starting Blender export: {destination.name}")
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        started = time.monotonic()
        while process.poll() is None:
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
        stdout, stderr = process.communicate()
        log = (stdout or "") + ("\n" + stderr if stderr else "")
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
        temporary_output = Path(temporary_output_name)
        if not temporary_output.is_file() or temporary_output.stat().st_size == 0:
            tail = "\n".join(log.splitlines()[-40:])
            raise RuntimeError(
                "Blender reported success but did not create the FBX output."
                + (f"\n\nBlender log:\n{tail}" if tail else "")
            )
        os.replace(temporary_output, destination)
        if progress:
            progress(f"Exported {destination}")
    return FbxExportResult(
        str(destination.resolve()), scene.frame_count, scene.fps, len(scene.bones),
        tuple(scene.warnings), log,
    )


def export_anm2_to_fbx(
    anm2_path: str | Path,
    source_rig: ChromeRig,
    output_path: str | Path,
    *,
    fps: int = 30,
    start_frame: int | None = None,
    end_frame: int | None = None,
    target_fbx: str | Path | None = None,
    bone_map: GenericBoneMap | None = None,
    translation_scale: str | float = "auto",
    blender_executable: str | Path | None = None,
    progress: Callable[[str], None] | None = None,
    cancel_check: Callable[[], bool] | None = None,
) -> FbxExportResult:
    animation: DecodedAnm2Animation = decode_anm2_animation(
        anm2_path, fps=fps, start_frame=start_frame, end_frame=end_frame
    )
    if target_fbx is None:
        scene = reconstruct_native_scene(animation, source_rig)
    else:
        if bone_map is None:
            raise ValueError("Cross-rig FBX export requires a reviewed generic bone map.")
        target_rig = chrome_rig_from_fbx_skeleton(target_fbx)
        scene = retarget_decoded_animation(
            animation, source_rig, target_rig, bone_map,
            translation_scale=translation_scale,
        )
    return run_blender_export(
        scene,
        output_path,
        blender_executable=blender_executable,
        progress=progress,
        cancel_check=cancel_check,
    )


__all__ = [
    "FbxExportResult", "discover_blender", "export_anm2_to_fbx", "run_blender_export",
]
