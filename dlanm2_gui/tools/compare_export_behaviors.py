"""Compare Current exports with artifacts produced by the 0.5.0 contract.

The legacy artifacts should be generated from an isolated checkout of tag
0.5.0. The checkout identity is recorded so the comparison remains auditable.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from typing import Any, Sequence

import numpy as np

from ..anm2_fbx import decode_anm2_animation
from ..blender_fbx import discover_blender


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def compare_anm2_artifacts(
    current_path: Path,
    legacy_path: Path,
    *,
    component_tolerance: float = 1.0e-6,
) -> dict[str, Any]:
    """Return first/middle/final decoded-track differences."""

    current = decode_anm2_animation(current_path)
    legacy = decode_anm2_animation(legacy_path)
    current_by_descriptor = {
        descriptor: index for index, descriptor in enumerate(current.descriptors)
    }
    legacy_by_descriptor = {
        descriptor: index for index, descriptor in enumerate(legacy.descriptors)
    }
    common = tuple(sorted(set(current_by_descriptor) & set(legacy_by_descriptor)))
    frame_count = min(current.frame_count, legacy.frame_count)
    sample_frames = tuple(sorted({0, frame_count // 2, frame_count - 1}))
    samples: list[dict[str, Any]] = []
    for frame in sample_frames:
        current_indices = [current_by_descriptor[value] for value in common]
        legacy_indices = [legacy_by_descriptor[value] for value in common]
        current_values = current.values[frame, current_indices]
        legacy_values = legacy.values[frame, legacy_indices]
        component_delta = np.abs(current_values - legacy_values)
        current_quaternions = current.quaternions_wxyz[frame, current_indices]
        legacy_quaternions = legacy.quaternions_wxyz[frame, legacy_indices]
        dots = np.clip(
            np.abs(np.sum(current_quaternions * legacy_quaternions, axis=1)),
            0.0,
            1.0,
        )
        angular_degrees = np.degrees(2.0 * np.arccos(dots))
        translation_delta = np.linalg.norm(
            current_values[:, 3:6] - legacy_values[:, 3:6], axis=1
        )
        samples.append(
            {
                "frame": frame,
                "differing_track_count": int(
                    np.count_nonzero(
                        np.any(component_delta > component_tolerance, axis=1)
                    )
                ),
                "max_component_delta": float(np.max(component_delta, initial=0.0)),
                "max_rotation_delta_degrees": float(
                    np.max(angular_degrees, initial=0.0)
                ),
                "max_translation_delta": float(
                    np.max(translation_delta, initial=0.0)
                ),
            }
        )
    current_hash = _sha256(current_path)
    legacy_hash = _sha256(legacy_path)
    return {
        "current": {
            "path": str(current_path.resolve()),
            "sha256": current_hash,
            "size": current_path.stat().st_size,
            "frame_count": current.frame_count,
            "track_count": current.track_count,
        },
        "legacy_5_0": {
            "path": str(legacy_path.resolve()),
            "sha256": legacy_hash,
            "size": legacy_path.stat().st_size,
            "frame_count": legacy.frame_count,
            "track_count": legacy.track_count,
        },
        "common_track_count": len(common),
        "current_only_track_count": len(set(current_by_descriptor) - set(legacy_by_descriptor)),
        "legacy_only_track_count": len(set(legacy_by_descriptor) - set(current_by_descriptor)),
        "sampled_differences": samples,
        "artifacts_differ": current_hash != legacy_hash,
    }


def _blender_snapshots(
    blender: Path,
    fbx_path: Path,
    bones: Sequence[str],
) -> dict[str, Any]:
    script = "\n".join(
        (
            "import bpy, json",
            f"bpy.ops.import_scene.fbx(filepath={json.dumps(str(fbx_path.resolve()))})",
            "armature = next(obj for obj in bpy.context.scene.objects if obj.type == 'ARMATURE')",
            "action = armature.animation_data.action if armature.animation_data else None",
            "first = int(round(action.frame_range[0])) if action else bpy.context.scene.frame_start",
            "final = int(round(action.frame_range[1])) if action else bpy.context.scene.frame_end",
            f"wanted = {json.dumps(list(bones))}",
            "result = {'first_frame': first, 'final_frame': final, 'bones': {}}",
            "for name in wanted:",
            "    if name not in armature.data.bones: continue",
            "    bone = armature.data.bones[name]",
            "    row = {'rest_head': list(armature.matrix_world @ bone.head_local)}",
            "    for label, frame in (('first_pose_head', first), ('final_pose_head', final)):",
            "        bpy.context.scene.frame_set(frame)",
            "        bpy.context.view_layer.update()",
            "        row[label] = list(armature.matrix_world @ armature.pose.bones[name].head)",
            "    result['bones'][name] = row",
            "print('DLR_EXPORT_COMPARISON:' + json.dumps(result))",
        )
    )
    completed = subprocess.run(
        [
            str(blender),
            "--background",
            "--factory-startup",
            "--python-expr",
            script,
        ],
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    prefix = "DLR_EXPORT_COMPARISON:"
    for line in completed.stdout.splitlines():
        if line.startswith(prefix):
            result = json.loads(line[len(prefix) :])
            for row in result["bones"].values():
                rest = np.asarray(row["rest_head"], dtype=float)
                first = np.asarray(row["first_pose_head"], dtype=float)
                final = np.asarray(row["final_pose_head"], dtype=float)
                row["rest_to_first_distance"] = float(np.linalg.norm(rest - first))
                row["rest_to_final_distance"] = float(np.linalg.norm(rest - final))
            return result
    raise RuntimeError(
        "Blender did not emit an export comparison snapshot.\n"
        f"{completed.stdout}\n{completed.stderr}"
    )


def _worktree_identity(path: Path) -> dict[str, str]:
    def git(*arguments: str) -> str:
        return subprocess.run(
            ["git", "-c", f"safe.directory={path.resolve()}", *arguments],
            cwd=path,
            check=True,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        ).stdout.strip()

    return {
        "path": str(path.resolve()),
        "commit": git("rev-parse", "HEAD"),
        "exact_tag": git("describe", "--tags", "--exact-match", "HEAD"),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Compare Current ANM2/FBX artifacts with isolated 0.5.0 artifacts."
    )
    parser.add_argument("--current-anm2", type=Path, required=True)
    parser.add_argument("--legacy-anm2", type=Path, required=True)
    parser.add_argument("--current-fbx", type=Path)
    parser.add_argument("--legacy-fbx", type=Path)
    parser.add_argument("--legacy-worktree", type=Path, required=True)
    parser.add_argument("--blender", type=Path)
    parser.add_argument(
        "--bone",
        action="append",
        default=[],
        help="Bone whose imported rest/first/final heads should be compared.",
    )
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)

    identity = _worktree_identity(args.legacy_worktree)
    if identity["exact_tag"] not in {"0.5.0", "v0.5.0"}:
        parser.error(
            f"--legacy-worktree must be checked out at tag 0.5.0; got "
            f"{identity['exact_tag']!r}"
        )
    report: dict[str, Any] = {
        "schema_version": 1,
        "legacy_worktree": identity,
        "fbx_to_anm2": compare_anm2_artifacts(
            args.current_anm2, args.legacy_anm2
        ),
    }
    if bool(args.current_fbx) != bool(args.legacy_fbx):
        parser.error("--current-fbx and --legacy-fbx must be supplied together")
    if args.current_fbx and args.legacy_fbx:
        blender = args.blender or discover_blender()
        if blender is None:
            parser.error("Blender is required for FBX rest/pose comparison")
        bones = args.bone or ["pelvis", "spine", "l_hand", "r_hand"]
        report["anm2_to_fbx"] = {
            "current": {
                "path": str(args.current_fbx.resolve()),
                "sha256": _sha256(args.current_fbx),
                "snapshot": _blender_snapshots(
                    blender, args.current_fbx, bones
                ),
            },
            "legacy_5_0": {
                "path": str(args.legacy_fbx.resolve()),
                "sha256": _sha256(args.legacy_fbx),
                "snapshot": _blender_snapshots(
                    blender, args.legacy_fbx, bones
                ),
            },
            "expected_anchor_contract": {
                "current": "first_sample",
                "legacy_5_0": "final_sample",
            },
        }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
