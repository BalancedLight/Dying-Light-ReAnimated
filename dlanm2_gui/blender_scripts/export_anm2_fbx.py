"""Executed inside Blender; consumes a DL ReAnimated JSON export job."""

from __future__ import annotations

import argparse
import base64
import json
from pathlib import Path
import sys
import traceback
import zlib

import bpy
from mathutils import Matrix, Quaternion, Vector


Y_UP_TO_BLENDER = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))


def trs(row):
    translation = Matrix.Translation(Vector(row["translation"]))
    rotation = Quaternion(row["rotation_wxyz"]).to_matrix().to_4x4()
    scale = Matrix.Diagonal((*row["scale"], 1.0))
    return translation @ rotation @ scale


def convert(matrix):
    return Y_UP_TO_BLENDER @ matrix @ Y_UP_TO_BLENDER.inverted()


def global_matrices(locals_, bones):
    result = [None] * len(bones)
    def resolve(index):
        if result[index] is not None:
            return result[index]
        parent = int(bones[index]["parent_index"])
        result[index] = resolve(parent) @ locals_[index] if parent >= 0 else locals_[index]
        return result[index]
    return [resolve(index) for index in range(len(bones))]


def topological_indices(bones):
    result = []
    visited = set()
    def visit(index):
        if index in visited:
            return
        parent = int(bones[index]["parent_index"])
        if parent >= 0:
            visit(parent)
        visited.add(index)
        result.append(index)
    for index in range(len(bones)):
        visit(index)
    return result


def child_indices(bones):
    children = [[] for _ in bones]
    for index, row in enumerate(bones):
        parent = int(row["parent_index"])
        if parent >= 0:
            children[parent].append(index)
    return children


def descendant_depth(index, children):
    if not children[index]:
        return 0
    return 1 + max(descendant_depth(child, children) for child in children[index])


def display_child(index, bones, heads, children):
    """Choose a useful joint for a Blender display bone's tail.

    Chrome rigs commonly contain zero-length twist/helper nodes between the
    visible joints. Walk through coincident nodes and prefer the continuing
    chain over a terminal helper. This changes only Blender's bone display;
    the exact bind and animated matrices remain independent of the tail.
    """
    origin = heads[index]
    candidates = []
    pending = list(children[index])
    visited = set()
    while pending:
        child = pending.pop(0)
        if child in visited:
            continue
        visited.add(child)
        vector = heads[child] - origin
        if vector.length <= 1.0e-5:
            pending.extend(children[child])
            continue
        name = str(bones[child]["name"]).lower()
        helper_penalty = int(
            bool(bones[child].get("helper", False))
            or "holder" in name
            or "twist" in name
        )
        candidates.append((helper_penalty, -descendant_depth(child, children), vector.length, child))
    return min(candidates)[3] if candidates else None


def action_fcurves(action):
    """Yield curves from both legacy and Blender 4.4+/5 layered Actions."""
    legacy = getattr(action, "fcurves", None)
    if legacy is not None:
        yield from legacy
        return
    for layer in action.layers:
        for strip in layer.strips:
            for channelbag in getattr(strip, "channelbags", ()):
                yield from channelbag.fcurves


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--job", required=True)
    args = parser.parse_args(argv)
    job = json.loads(Path(args.job).read_text(encoding="utf-8"))
    if job.get("format") != "dl-reanimated-blender-fbx-job":
        raise ValueError("Unsupported DL ReAnimated Blender job")
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    scene = bpy.context.scene
    scene.name = job["name"]
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    scene.render.fps = int(job["fps"])
    scene.frame_start = 0
    scene.frame_end = len(job["frames"]) - 1

    bones = job["bones"]
    armature_indices = [
        index for index, row in enumerate(bones) if not row.get("helper", False)
    ]
    helper_indices = [
        index for index, row in enumerate(bones) if row.get("helper", False)
    ]
    bind_local = [
        trs({"translation": row["bind_translation"], "rotation_wxyz": row["bind_rotation_wxyz"], "scale": row["bind_scale"]})
        for row in bones
    ]
    bind_global = [convert(value) for value in global_matrices(bind_local, bones)]
    armature_data = bpy.data.armatures.new(job["name"] + "_Armature")
    armature = bpy.data.objects.new(job["name"], armature_data)
    armature["dlr_native_anm2_export"] = 1
    armature["dlr_scene_unit_meters"] = 1.0
    bpy.context.collection.objects.link(armature)
    bpy.context.view_layer.objects.active = armature
    armature.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    edit_bones = {}
    bind_heads = [matrix.translation.copy() for matrix in bind_global]
    children = child_indices(bones)
    bind_tails = {}
    for index in armature_indices:
        row = bones[index]
        head = bind_heads[index]
        child_index = None if row.get("helper", False) else display_child(
            index, bones, bind_heads, children
        )
        if child_index is not None:
            # Point the display bone at a real downstream joint. Bone tails do
            # not encode Chrome transforms, but this makes the exported rig
            # readable and keeps ordinary chains visually connected.
            tail = bind_heads[child_index].copy()
        else:
            parent = int(row["parent_index"])
            if row.get("helper", False):
                tail = head + bind_global[index].to_3x3().col[1].normalized() * 0.03
            elif parent >= 0:
                direction = head - bind_heads[parent]
                if direction.length > 1.0e-5:
                    tail = head + direction.normalized() * max(direction.length * 0.4, 0.01)
                else:
                    tail = head + bind_global[index].to_3x3().col[1].normalized() * 0.03
            else:
                tail = head + bind_global[index].to_3x3().col[1].normalized() * 0.05
        if (tail - head).length < 0.001:
            tail = head + Vector((0.0, 0.01, 0.0))
        bind_tails[index] = tail
    for index in armature_indices:
        row = bones[index]
        bone = armature_data.edit_bones.new(row["name"])
        bone.head = bind_heads[index]
        bone.tail = bind_tails[index]
        roll_reference = bind_global[index].to_3x3().col[2].normalized()
        bone_direction = (bone.tail - bone.head).normalized()
        if abs(roll_reference.dot(bone_direction)) > 0.98:
            roll_reference = bind_global[index].to_3x3().col[0].normalized()
        bone.align_roll(roll_reference)
        bone.use_deform = bool(row.get("deform", True))
        edit_bones[index] = bone
    for index in armature_indices:
        row = bones[index]
        parent = int(row["parent_index"])
        name = str(row["name"]).lower()
        # Zero-length upper-arm/thigh twist nodes share their parent's head.
        # Leaving them as direct children makes Blender average the parent's
        # visible tail between the zero-length twist and the real elbow/knee,
        # shortening major limb bones by exactly one half. Move only the
        # display parent to the grandparent; keyed armature-space matrices keep
        # the twist transform itself unchanged.
        if (
            parent in edit_bones
            and "twist" in name
            and (bind_heads[index] - bind_heads[parent]).length <= 1.0e-5
        ):
            grandparent = int(bones[parent]["parent_index"])
            if grandparent in edit_bones:
                parent = grandparent
        if parent in edit_bones:
            edit_bones[index].parent = edit_bones[parent]
            # Geometrically touching heads/tails are enough for a readable
            # hierarchy. FBX importers can shorten parent bones when Blender's
            # connected flag is exported across branching/zero-length chains.
            edit_bones[index].use_connect = False
    bpy.ops.object.mode_set(mode="POSE")
    for index in armature_indices:
        row = bones[index]
        data_bone = armature.data.bones[row["name"]]
        data_bone["dlr_descriptor"] = "" if row.get("descriptor") is None else f"0x{int(row['descriptor']):08X}"
        data_bone["dlr_helper"] = bool(row.get("helper", False))
    action = bpy.data.actions.new(job["name"])
    armature.animation_data_create()
    armature.animation_data.action = action
    # Blender 4.4+/5 creates the layered Action slot/channel bag lazily on the
    # first keyframe insertion. That initialization can reset the pose and key
    # the rest transform instead of ANM2 frame 0. Seed the channels first; the
    # real frame-1 insertion below replaces these keys at the same time.
    scene.frame_set(0)
    for index in armature_indices:
        row = bones[index]
        pose_bone = armature.pose.bones[row["name"]]
        pose_bone.rotation_mode = "QUATERNION"
        pose_bone.keyframe_insert("location", frame=0, group=row["name"])
        pose_bone.keyframe_insert("rotation_quaternion", frame=0, group=row["name"])
        pose_bone.keyframe_insert("scale", frame=0, group=row["name"])
    for fcurve in action_fcurves(action):
        while fcurve.keyframe_points:
            fcurve.keyframe_points.remove(fcurve.keyframe_points[0], fast=True)
        fcurve.update()
    previous_quaternions = {}
    evaluation_order = [index for index in topological_indices(bones) if index in edit_bones]
    # Chrome bind rotations describe the engine's transform basis, which is
    # not necessarily aimed down the visible bone. Blender, however, displays
    # every bone along its local Y axis. Preserve the fixed difference between
    # those bases so animated heads retain the exact ANM2 joint transforms
    # while bone tails continue to point toward their anatomical children.
    display_basis_corrections = {}
    for index in armature_indices:
        row = bones[index]
        display_rest_global = armature.data.bones[row["name"]].matrix_local.copy()
        display_basis_corrections[index] = (
            bind_global[index].inverted_safe() @ display_rest_global
        )
    native_metadata = {
        "version": 1,
        "display_basis_corrections": {
            bones[index]["name"]: [
                float(value)
                for matrix_row in display_basis_corrections[index]
                for value in matrix_row
            ]
            for index in armature_indices
        },
        "helper_tracks": {
            f"{int(bones[index]['descriptor']):08X}": [
                frame[index] for frame in job["frames"]
            ]
            for index in helper_indices
            if bones[index].get("descriptor") is not None
        },
    }
    armature["dlr_native_metadata_zlib_b64"] = base64.b64encode(
        zlib.compress(json.dumps(native_metadata, separators=(",", ":")).encode("utf-8"), 9)
    ).decode("ascii")
    for frame_index, frame_rows in enumerate(job["frames"]):
        blender_frame = frame_index
        scene.frame_set(blender_frame)
        animated_local = [convert(trs(row)) for row in frame_rows]
        animated_global = global_matrices(animated_local, bones)
        # ANM2 rows are absolute parent-local transforms. Assign their exact
        # armature-space matrices in hierarchy order. Updating after each bone
        # is intentional: Blender 5 evaluates pose parents lazily, and a batch
        # matrix assignment can otherwise convert children against stale parent
        # state (correct rotations but badly displaced joints).
        for index in evaluation_order:
            row = bones[index]
            pose_bone = armature.pose.bones[row["name"]]
            pose_bone.rotation_mode = "QUATERNION"
            pose_bone.matrix = animated_global[index] @ display_basis_corrections[index]
            bpy.context.view_layer.update()
        for index in evaluation_order:
            row = bones[index]
            pose_bone = armature.pose.bones[row["name"]]
            quaternion = pose_bone.rotation_quaternion.copy()
            previous = previous_quaternions.get(row["name"])
            if previous is not None and quaternion.dot(previous) < 0:
                quaternion.negate()
                pose_bone.rotation_quaternion = quaternion
            previous_quaternions[row["name"]] = quaternion.copy()
            pose_bone.keyframe_insert("location", frame=blender_frame, group=row["name"])
            pose_bone.keyframe_insert("rotation_quaternion", frame=blender_frame, group=row["name"])
            pose_bone.keyframe_insert("scale", frame=blender_frame, group=row["name"])
    bpy.ops.object.mode_set(mode="OBJECT")
    for fcurve in action_fcurves(action):
        for key in fcurve.keyframe_points:
            key.interpolation = "LINEAR"
    helper_objects = []
    for index in helper_indices:
        row = bones[index]
        helper = bpy.data.objects.new(row["name"], None)
        helper.empty_display_type = "ARROWS"
        helper.empty_display_size = 0.05
        helper["dlr_descriptor"] = (
            "" if row.get("descriptor") is None else f"0x{int(row['descriptor']):08X}"
        )
        helper["dlr_helper"] = True
        bpy.context.collection.objects.link(helper)
        helper.animation_data_create()
        helper_action = bpy.data.actions.new(f"{job['name']}__{row['name']}")
        helper.animation_data.action = helper_action
        helper.rotation_mode = "QUATERNION"
        # Seed layered Action channels before writing the actual frames.
        helper.keyframe_insert("location", frame=0)
        helper.keyframe_insert("rotation_quaternion", frame=0)
        helper.keyframe_insert("scale", frame=0)
        for fcurve in action_fcurves(helper_action):
            while fcurve.keyframe_points:
                fcurve.keyframe_points.remove(fcurve.keyframe_points[0], fast=True)
            fcurve.update()
        previous = None
        for frame_index, frame_rows in enumerate(job["frames"]):
            helper.matrix_world = convert(trs(frame_rows[index]))
            quaternion = helper.rotation_quaternion.copy()
            if previous is not None and quaternion.dot(previous) < 0:
                quaternion.negate()
                helper.rotation_quaternion = quaternion
            previous = quaternion.copy()
            helper.keyframe_insert("location", frame=frame_index)
            helper.keyframe_insert("rotation_quaternion", frame=frame_index)
            helper.keyframe_insert("scale", frame=frame_index)
        for fcurve in action_fcurves(helper_action):
            for key in fcurve.keyframe_points:
                key.interpolation = "LINEAR"
        helper_objects.append(helper)
    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    for helper in helper_objects:
        helper.select_set(True)
    bpy.context.view_layer.objects.active = armature
    output = Path(job["output_path"])
    output.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=str(output), use_selection=True, object_types={"ARMATURE", "EMPTY"},
        use_mesh_modifiers=False, add_leaf_bones=False, bake_anim=True,
        bake_anim_use_all_bones=True, bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=False, bake_anim_force_startend_keying=True,
        bake_anim_step=1.0, bake_anim_simplify_factor=0.0,
        axis_forward="-Z", axis_up="Y", apply_unit_scale=True,
        primary_bone_axis="Y", secondary_bone_axis="X", use_custom_props=True,
    )
    print(f"DLR_EXPORT_COMPLETE:{output}")


if __name__ == "__main__":
    separator = sys.argv.index("--") if "--" in sys.argv else len(sys.argv) - 1
    try:
        main(sys.argv[separator + 1:])
    except BaseException:
        traceback.print_exc()
        raise SystemExit(1)
