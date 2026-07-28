"""Blender-side acceptance helper for a deterministic refcamera pose edit.

Run with Blender, not Python:

    blender --background --factory-startup --python tools/blender_offset_refcamera.py \
        -- --input source.fbx --output edited.fbx --distance 0.1
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import sys

import bpy
from mathutils import Matrix


def _arguments(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--distance", type=float, default=0.1)
    parser.add_argument("--bone", default="refcamera")
    parser.add_argument("--export-custom-props", action="store_true")
    parser.add_argument("--disable-prepost-rot", action="store_true")
    parser.add_argument("--bake-space-transform", action="store_true")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = _arguments(argv)
    bpy.ops.import_scene.fbx(
        filepath=str(args.input.resolve()),
        use_custom_props=True,
        use_prepost_rot=not args.disable_prepost_rot,
    )
    armatures = [
        obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE"
    ]
    if len(armatures) != 1:
        raise ValueError(
            f"Expected one imported armature, found {len(armatures)}"
        )
    armature = armatures[0]
    pose_bone = armature.pose.bones.get(args.bone)
    empty = (
        bpy.context.scene.objects.get(args.bone)
        if pose_bone is None
        else None
    )
    if pose_bone is None and (empty is None or empty.type != "EMPTY"):
        raise ValueError(
            f"Imported FBX has no {args.bone!r} pose bone or Empty"
        )
    action = (
        armature.animation_data.action
        if armature.animation_data is not None
        else None
    )
    if action is None:
        raise ValueError("Imported armature has no active animation action")

    scene = bpy.context.scene
    action_start, action_end = (float(value) for value in action.frame_range)
    frame_start = int(math.ceil(action_start))
    frame_end = int(math.floor(action_end))
    scene.frame_start = frame_start
    scene.frame_end = frame_end
    frames = list(range(frame_start, frame_end + 1))
    if not frames:
        raise ValueError("Imported FBX has no animation frames")
    sample_frames = sorted({frames[0], frames[len(frames) // 2], frames[-1]})
    before: dict[int, list[float]] = {}
    after: dict[int, list[float]] = {}
    for frame in frames:
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        if frame in sample_frames:
            before[frame] = [
                float(value)
                for value in (
                    pose_bone.matrix.translation
                    if pose_bone is not None
                    else empty.matrix_world.translation
                )
            ]
        # FBX uses the camera convention -Z forward, so post-multiplying a
        # +Z translation moves the helper backward in its current local basis.
        if pose_bone is not None:
            pose_bone.matrix = pose_bone.matrix @ Matrix.Translation(
                (0.0, 0.0, float(args.distance))
            )
            pose_bone.rotation_mode = "QUATERNION"
            target = pose_bone
        else:
            empty.matrix_world = empty.matrix_world @ Matrix.Translation(
                (0.0, 0.0, float(args.distance))
            )
            empty.rotation_mode = "QUATERNION"
            target = empty
        target.keyframe_insert(
            "location",
            frame=frame,
            group=args.bone,
        )
        target.keyframe_insert(
            "rotation_quaternion",
            frame=frame,
            group=args.bone,
        )
        target.keyframe_insert(
            "scale",
            frame=frame,
            group=args.bone,
        )
        bpy.context.view_layer.update()
        if frame in sample_frames:
            after[frame] = [
                float(value)
                for value in (
                    pose_bone.matrix.translation
                    if pose_bone is not None
                    else empty.matrix_world.translation
                )
            ]

    bpy.ops.object.mode_set(mode="OBJECT") if armature.mode != "OBJECT" else None
    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    for obj in scene.objects:
        if obj.type == "EMPTY" and obj.name.startswith("DLR_"):
            obj.select_set(True)
        if obj.type == "MESH" and obj.name.startswith("DLR_RoundTripGuard_"):
            obj.select_set(True)
    bpy.context.view_layer.objects.active = armature

    args.output.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=str(args.output.resolve()),
        use_selection=True,
        object_types={"ARMATURE", "EMPTY", "MESH"},
        use_mesh_modifiers=False,
        add_leaf_bones=False,
        bake_anim=True,
        bake_anim_use_all_bones=False,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=False,
        bake_anim_force_startend_keying=False,
        bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,
        axis_forward="-Z",
        axis_up="Y",
        apply_unit_scale=True,
        bake_space_transform=bool(args.bake_space_transform),
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        use_custom_props=bool(args.export_custom_props),
    )
    print(
        "DLR_REFCAMERA_EDIT:"
        + json.dumps(
            {
                "input": str(args.input.resolve()),
                "output": str(args.output.resolve()),
                "distance_m": float(args.distance),
                "local_axis": "+Z",
                "bone": args.bone,
                "node_kind": "bone" if pose_bone is not None else "empty",
                "frame_start": frames[0],
                "frame_end": frames[-1],
                "sample_before": before,
                "sample_after": after,
                "export_custom_props": bool(args.export_custom_props),
                "import_prepost_rot": not args.disable_prepost_rot,
                "bake_space_transform": bool(args.bake_space_transform),
            },
            separators=(",", ":"),
        ),
        flush=True,
    )
    return 0


if __name__ == "__main__":
    separator = sys.argv.index("--") if "--" in sys.argv else len(sys.argv)
    raise SystemExit(main(sys.argv[separator + 1 :]))
