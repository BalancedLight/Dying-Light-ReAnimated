"""Executed inside Blender; consumes a sparse DL ReAnimated JSON/NPZ job."""

from __future__ import annotations

import argparse
import base64
import json
import math
from pathlib import Path
import sys
import traceback
import zlib

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector


Y_UP_TO_BLENDER = Matrix(
    ((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1))
)


def report(stage, current, total):
    print(f"DLR_PROGRESS:{stage}|{int(current)}|{int(total)}", flush=True)


def trs_values(translation, rotation_wxyz, scale):
    return (
        Matrix.Translation(Vector(translation))
        @ Quaternion(rotation_wxyz).to_matrix().to_4x4()
        @ Matrix.Diagonal((*scale, 1.0))
    )


def convert(matrix):
    return Y_UP_TO_BLENDER @ matrix @ Y_UP_TO_BLENDER.inverted()


def proper_rotation(matrix):
    quaternion = matrix.to_quaternion()
    quaternion.normalize()
    return quaternion.to_matrix()


def quaternion_error_degrees(actual, expected):
    left = [float(value) for value in actual]
    right = [float(value) for value in expected]
    left_norm = math.sqrt(sum(value * value for value in left))
    right_norm = math.sqrt(sum(value * value for value in right))
    if left_norm <= 1.0e-15 or right_norm <= 1.0e-15:
        raise ValueError("Cannot compare a singular quaternion")
    dot = abs(
        sum(a * b for a, b in zip(left, right)) / (left_norm * right_norm)
    )
    dot = min(1.0, max(0.0, dot))
    return math.degrees(2.0 * math.acos(dot))


def heading_error_degrees(actual, expected, axis):
    left = actual.copy()
    right = expected.copy()
    left.normalize()
    right.normalize()
    relative = left @ right.inverted()
    relative.normalize()
    direction = axis.normalized()
    vector = Vector((relative.x, relative.y, relative.z))
    projected = direction * vector.dot(direction)
    length = projected.length
    if length <= 1.0e-15:
        return 0.0
    return math.degrees(2.0 * math.atan2(length, abs(float(relative.w))))


def global_matrices(locals_, bones):
    result = [None] * len(bones)
    visiting = set()

    def resolve(index):
        if result[index] is not None:
            return result[index]
        if index in visiting:
            raise ValueError(f"Bone hierarchy cycle at index {index}")
        visiting.add(index)
        parent = int(bones[index]["parent_index"])
        result[index] = (
            resolve(parent) @ locals_[index] if parent >= 0 else locals_[index]
        )
        visiting.remove(index)
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
        candidates.append(
            (helper_penalty, -descendant_depth(child, children), vector.length, child)
        )
    return min(candidates)[3] if candidates else None


def create_action_target(name, owner):
    """Return an Action and its version-neutral FCurve collection."""

    action = bpy.data.actions.new(name)
    owner.animation_data_create()
    legacy = getattr(action, "fcurves", None)
    if legacy is not None:
        owner.animation_data.action = action
        return action, legacy
    slot = action.slots.new("OBJECT", owner.name)
    layer = action.layers.new("DL ReAnimated")
    strip = layer.strips.new(type="KEYFRAME")
    channelbag = strip.channelbags.new(slot)
    owner.animation_data.action = action
    owner.animation_data.action_slot = slot
    return action, channelbag.fcurves


def install_bulk_curve(collection, data_path, array_index, group_name, frames, values):
    try:
        curve = collection.new(
            data_path=data_path, index=array_index, group_name=group_name
        )
    except TypeError:  # Blender 4.3 and earlier legacy Action API.
        curve = collection.new(data_path, index=array_index, action_group=group_name)
    count = len(frames)
    curve.keyframe_points.add(count)
    coordinates = np.empty((count, 2), dtype=np.float64)
    coordinates[:, 0] = frames
    coordinates[:, 1] = values
    curve.keyframe_points.foreach_set("co", coordinates.ravel())
    curve.keyframe_points.foreach_set(
        "interpolation", np.ones(count, dtype=np.int32)
    )
    curve.update()
    return curve


def update_dependency_graph():
    """Evaluate the scene from either frame-selection audit site."""

    bpy.context.view_layer.update()


def component_maps(arrays):
    return {
        "location": {
            int(bone): column
            for column, bone in enumerate(arrays["location_bone_indices"])
        },
        "rotation": {
            int(bone): column
            for column, bone in enumerate(arrays["rotation_bone_indices"])
        },
        "scale": {
            int(bone): column
            for column, bone in enumerate(arrays["scale_bone_indices"])
        },
    }


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--job", required=True)
    args = parser.parse_args(argv)
    job_path = Path(args.job)
    job = json.loads(job_path.read_text(encoding="utf-8-sig"))
    if (
        job.get("format") != "dl-reanimated-blender-fbx-job"
        or int(job.get("schema_version", 0)) != 2
    ):
        raise ValueError("Unsupported sparse DL ReAnimated Blender job")
    array_path = Path(job["arrays_path"])
    if not array_path.is_absolute():
        array_path = job_path.parent / array_path
    with np.load(array_path, allow_pickle=False) as loaded:
        arrays = {name: loaded[name] for name in loaded.files}
    frames = np.asarray(arrays["frames"], dtype=np.float64)
    frame_count = len(frames)
    maps = component_maps(arrays)

    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    scene = bpy.context.scene
    scene.name = job["name"]
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    output_fps = float(job["fbx_output_fps"] if "fbx_output_fps" in job else job["fps"])
    if not math.isfinite(output_fps) or output_fps <= 0.0:
        raise ValueError("FBX output FPS must be finite and positive")
    fps_numerator = max(1, int(round(output_fps)))
    scene.render.fps = fps_numerator
    scene.render.fps_base = fps_numerator / output_fps
    scene.frame_start = int(job["frame_start"])
    scene.frame_end = int(job["frame_end"])

    bones = job["bones"]
    motion_accumulator = dict(job.get("motion_accumulator", {}) or {})
    armature_indices = [
        index for index, row in enumerate(bones) if not row.get("helper", False)
    ]
    helper_indices = [
        index for index, row in enumerate(bones) if row.get("helper", False)
    ]
    report("Creating armature", 0, len(armature_indices))
    bind_local = [
        trs_values(
            row["bind_translation"],
            row["bind_rotation_wxyz"],
            row["bind_scale"],
        )
        for row in bones
    ]
    bind_global = [convert(value) for value in global_matrices(bind_local, bones)]
    armature_data = bpy.data.armatures.new(job["name"] + "_Armature")
    armature = bpy.data.objects.new(job["name"], armature_data)
    armature["dlr_native_anm2_export"] = 1
    armature["dlr_scene_unit_meters"] = 1.0
    if motion_accumulator:
        armature["dlr_motion_accumulator_detected"] = bool(
            motion_accumulator.get("present", False)
        )
        armature["dlr_motion_accumulator_baked"] = bool(
            motion_accumulator.get("baked", False)
        )
        armature["dlr_motion_accumulator_root"] = str(
            motion_accumulator.get("root_name", "")
        )
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
        child_index = display_child(index, bones, bind_heads, children)
        if child_index is not None:
            # The edit bone's Y axis is Blender's visible bone direction.
            # Chrome rigs do not require that axis to point at a child pivot,
            # but doing so makes the exported armature readable and editable.
            tail = bind_heads[child_index].copy()
        else:
            parent = int(row["parent_index"])
            if parent >= 0:
                direction = head - bind_heads[parent]
                if direction.length > 1.0e-5:
                    tail = head + direction.normalized() * max(
                        direction.length * 0.4, 0.01
                    )
                else:
                    tail = (
                        head
                        + proper_rotation(bind_global[index]).col[1].normalized()
                        * 0.03
                    )
            else:
                tail = (
                    head
                    + proper_rotation(bind_global[index]).col[1].normalized()
                    * 0.05
                )
        if (tail - head).length < 0.001:
            tail = head + Vector((0.0, 0.01, 0.0))
        bind_tails[index] = tail
    for completed, index in enumerate(armature_indices, start=1):
        row = bones[index]
        bone = armature_data.edit_bones.new(row["name"])
        bone.head = bind_heads[index]
        bone.tail = bind_tails[index]
        native_rotation = proper_rotation(bind_global[index])
        roll_reference = native_rotation.col[2].normalized()
        bone_direction = (bone.tail - bone.head).normalized()
        if abs(roll_reference.dot(bone_direction)) > 0.98:
            roll_reference = native_rotation.col[0].normalized()
        bone.align_roll(roll_reference)
        bone.use_deform = bool(row.get("deform", True))
        edit_bones[index] = bone
        if completed % 32 == 0 or completed == len(armature_indices):
            report("Creating armature", completed, len(armature_indices))
    for index in armature_indices:
        row = bones[index]
        parent = int(row["parent_index"])
        if parent in edit_bones:
            edit_bones[index].parent = edit_bones[parent]
        edit_bones[index].use_connect = False
    bpy.ops.object.mode_set(mode="POSE")
    for index in armature_indices:
        row = bones[index]
        data_bone = armature.data.bones[row["name"]]
        data_bone["dlr_descriptor"] = (
            ""
            if row.get("descriptor") is None
            else f"0x{int(row['descriptor']):08X}"
        )
        data_bone["dlr_helper"] = False

    display_basis_corrections = {}
    display_rest_globals = {}
    display_parent_indices = {}
    native_rest_basis_errors = {}
    edge_case_rest_bones = []
    index_by_name = {row["name"]: index for index, row in enumerate(bones)}
    for index in armature_indices:
        row = bones[index]
        display_rest_global = armature.data.bones[row["name"]].matrix_local.copy()
        display_rest_globals[index] = display_rest_global
        display_parent = armature.data.bones[row["name"]].parent
        display_parent_indices[index] = (
            index_by_name[display_parent.name] if display_parent is not None else -1
        )
        rotation_error = quaternion_error_degrees(
            display_rest_global.to_quaternion(), bind_global[index].to_quaternion()
        )
        translation_error = (
            display_rest_global.translation - bind_global[index].translation
        ).length
        native_rest_basis_errors[row["name"]] = {
            "rotation_degrees": float(rotation_error),
            "translation_m": float(translation_error),
            "status": "display_delta" if rotation_error > 0.01 else "aligned",
        }
        if rotation_error > 0.01:
            edge_case_rest_bones.append(row["name"])
        # The visible Blender rest basis intentionally differs from Chrome's
        # native basis whenever a child pivot is off-axis. Preserve that fixed
        # delta so the FBX->ANM2 importer can recover game-space globals.
        display_basis_corrections[index] = (
            bind_global[index].inverted_safe() @ display_rest_global
        )
    native_metadata = {
        "version": 4,
        "basis_mode": "child_pivot_display_v1",
        "sparse_summary": job["sparse_summary"],
        "display_basis_corrections": {
            bones[index]["name"]: [
                float(value)
                for matrix_row in display_basis_corrections[index]
                for value in matrix_row
            ]
            for index in armature_indices
        },
        "native_rest_basis_errors": native_rest_basis_errors,
        "native_rest_basis_max_rotation_degrees": max(
            (
                row["rotation_degrees"]
                for row in native_rest_basis_errors.values()
            ),
            default=0.0,
        ),
        "native_rest_basis_edge_bones": edge_case_rest_bones,
        "helper_descriptors": [
            f"{int(bones[index]['descriptor']):08X}"
            for index in helper_indices
            if bones[index].get("descriptor") is not None
        ],
        "motion_accumulator": motion_accumulator,
    }
    active_armature = sorted(
        (
            set(maps["location"])
            | set(maps["rotation"])
            | set(maps["scale"])
        )
        & set(armature_indices)
    )
    order = [
        index for index in topological_indices(bones) if index in active_armature
    ]
    sampled_location = {
        index: np.empty((frame_count, 3), dtype=np.float64)
        for index in maps["location"]
        if index in edit_bones
    }
    sampled_rotation = {
        index: np.empty((frame_count, 4), dtype=np.float64)
        for index in maps["rotation"]
        if index in edit_bones
    }
    sampled_scale = {
        index: np.empty((frame_count, 3), dtype=np.float64)
        for index in maps["scale"]
        if index in edit_bones
    }
    previous_quaternions = {}
    active_all = set(maps["location"]) | set(maps["rotation"]) | set(maps["scale"])
    primary_root_index = job.get("primary_root_index")
    if (
        primary_root_index is None
        or int(primary_root_index) not in armature_indices
    ):
        armature_set = set(armature_indices)
        primary_root_index = next(
            (
                index
                for index in armature_indices
                if int(bones[index]["parent_index"]) not in armature_set
            ),
            armature_indices[0],
        )
    primary_root_index = int(primary_root_index)
    expected_root_globals = []
    report("Installing animation curves", 0, frame_count)
    for frame_index in range(frame_count):
        animated_local = list(bind_local)
        for index in active_all:
            row = bones[index]
            translation = (
                arrays["locations"][frame_index, maps["location"][index]]
                if index in maps["location"]
                else row["bind_translation"]
            )
            rotation = (
                arrays["rotations_wxyz"][frame_index, maps["rotation"][index]]
                if index in maps["rotation"]
                else row["bind_rotation_wxyz"]
            )
            scale = (
                arrays["scales"][frame_index, maps["scale"][index]]
                if index in maps["scale"]
                else row["bind_scale"]
            )
            animated_local[index] = trs_values(translation, rotation, scale)
        animated_global = [
            convert(value) for value in global_matrices(animated_local, bones)
        ]
        desired_pose_globals = {
            index: animated_global[index] @ display_basis_corrections[index]
            for index in armature_indices
        }
        expected_root_globals.append(
            desired_pose_globals[primary_root_index].copy()
        )
        sampled_basis = {}
        for index in order:
            row = bones[index]
            pose_bone = armature.pose.bones[row["name"]]
            pose_bone.rotation_mode = "QUATERNION"
            display_parent = display_parent_indices[index]
            if display_parent >= 0:
                rest_relative = (
                    display_rest_globals[display_parent].inverted_safe()
                    @ display_rest_globals[index]
                )
                pose_relative = (
                    desired_pose_globals[display_parent].inverted_safe()
                    @ desired_pose_globals[index]
                )
                basis = rest_relative.inverted_safe() @ pose_relative
            else:
                basis = (
                    display_rest_globals[index].inverted_safe()
                    @ desired_pose_globals[index]
                )
            sampled_basis[index] = basis
        for index in active_armature:
            location_value, quaternion, scale_value = sampled_basis[index].decompose()
            if index in sampled_location:
                sampled_location[index][frame_index] = tuple(location_value)
            if index in sampled_rotation:
                previous = previous_quaternions.get(index)
                if previous is not None and quaternion.dot(previous) < 0.0:
                    quaternion.negate()
                previous_quaternions[index] = quaternion.copy()
                sampled_rotation[index][frame_index] = tuple(quaternion)
            if index in sampled_scale:
                sampled_scale[index][frame_index] = tuple(scale_value)
        if (frame_index + 1) % 32 == 0 or frame_index + 1 == frame_count:
            report("Installing animation curves", frame_index + 1, frame_count)

    if active_armature:
        _action, curves = create_action_target(job["name"], armature)
        for index, table in sampled_location.items():
            path = f'pose.bones["{bones[index]["name"]}"].location'
            for component in range(3):
                install_bulk_curve(
                    curves,
                    path,
                    component,
                    bones[index]["name"],
                    frames,
                    table[:, component],
                )
        for index, table in sampled_rotation.items():
            path = f'pose.bones["{bones[index]["name"]}"].rotation_quaternion'
            for component in range(4):
                install_bulk_curve(
                    curves,
                    path,
                    component,
                    bones[index]["name"],
                    frames,
                    table[:, component],
                )
        for index, table in sampled_scale.items():
            path = f'pose.bones["{bones[index]["name"]}"].scale'
            for component in range(3):
                install_bulk_curve(
                    curves,
                    path,
                    component,
                    bones[index]["name"],
                    frames,
                    table[:, component],
                )

    target_up = Vector(job.get("target_up_axis", (0.0, 1.0, 0.0)))
    blender_up = Y_UP_TO_BLENDER.to_3x3() @ target_up
    max_angular_error = 0.0
    max_heading_error = 0.0
    max_translation_error = 0.0
    root_pose = armature.pose.bones[bones[primary_root_index]["name"]]
    report("Auditing root parity", 0, frame_count)
    for frame_index, expected in enumerate(expected_root_globals):
        scene.frame_set(int(round(float(frames[frame_index]))))
        # Exactly one dependency evaluation per frame, never one per bone.
        update_dependency_graph()
        actual = root_pose.matrix.copy()
        angular_error = quaternion_error_degrees(
            actual.to_quaternion(), expected.to_quaternion()
        )
        heading_error = heading_error_degrees(
            actual.to_quaternion(), expected.to_quaternion(), blender_up
        )
        translation_error = (actual.translation - expected.translation).length
        max_angular_error = max(max_angular_error, angular_error)
        max_heading_error = max(max_heading_error, heading_error)
        max_translation_error = max(max_translation_error, translation_error)
        if (frame_index + 1) % 32 == 0 or frame_index + 1 == frame_count:
            report("Auditing root parity", frame_index + 1, frame_count)
    root_parity = {
        "root_index": primary_root_index,
        "root_name": bones[primary_root_index]["name"],
        "frame_count": frame_count,
        "max_angular_error_degrees": float(max_angular_error),
        "max_heading_error_degrees": float(max_heading_error),
        "max_translation_error_m": float(max_translation_error),
        "angular_tolerance_degrees": 0.05,
        "heading_tolerance_degrees": 0.05,
        "translation_tolerance_m": 1.0e-5,
        "native_rest_basis_max_rotation_degrees": float(
            native_metadata["native_rest_basis_max_rotation_degrees"]
        ),
        "native_rest_basis_edge_bone_count": len(edge_case_rest_bones),
    }
    native_metadata["root_parity"] = root_parity
    armature["dlr_native_metadata_zlib_b64"] = base64.b64encode(
        zlib.compress(
            json.dumps(native_metadata, separators=(",", ":")).encode("utf-8"), 9
        )
    ).decode("ascii")
    print(
        "DLR_ROOT_PARITY:" + json.dumps(root_parity, separators=(",", ":")),
        flush=True,
    )
    if (
        max_angular_error > 0.05
        or max_heading_error > 0.05
        or max_translation_error > 1.0e-5
    ):
        raise ValueError(
            "Native root parity failed: "
            f"angular={max_angular_error:.9f} degrees, "
            f"heading={max_heading_error:.9f} degrees, "
            f"translation={max_translation_error:.12g} m"
        )

    bpy.ops.object.mode_set(mode="OBJECT")
    helper_objects = []
    for index in helper_indices:
        row = bones[index]
        helper = bpy.data.objects.new(row["name"], None)
        helper.empty_display_type = "ARROWS"
        helper.empty_display_size = 0.05
        helper["dlr_descriptor"] = (
            ""
            if row.get("descriptor") is None
            else f"0x{int(row['descriptor']):08X}"
        )
        helper["dlr_helper"] = True
        semantic = str(row.get("semantic", "") or "")
        if semantic:
            helper["dlr_semantic"] = semantic
        if semantic == "motion_accumulator":
            helper["dlr_baked_into_root"] = str(
                motion_accumulator.get("root_name", "")
            )
            helper["dlr_motion_accumulator_baked"] = bool(
                motion_accumulator.get("baked", False)
            )
        bpy.context.collection.objects.link(helper)
        helper.rotation_mode = "QUATERNION"
        component_tables = {}
        helper_location = np.empty((frame_count, 3), dtype=np.float64)
        helper_rotation = np.empty((frame_count, 4), dtype=np.float64)
        helper_scale = np.empty((frame_count, 3), dtype=np.float64)
        previous_helper_rotation = None
        if (
            index in maps["location"]
            or index in maps["rotation"]
            or index in maps["scale"]
        ):
            for frame_index in range(frame_count):
                translation = (
                    arrays["locations"][frame_index, maps["location"][index]]
                    if index in maps["location"]
                    else row["bind_translation"]
                )
                rotation = (
                    arrays["rotations_wxyz"][frame_index, maps["rotation"][index]]
                    if index in maps["rotation"]
                    else row["bind_rotation_wxyz"]
                )
                scale = (
                    arrays["scales"][frame_index, maps["scale"][index]]
                    if index in maps["scale"]
                    else row["bind_scale"]
                )
                converted = convert(trs_values(translation, rotation, scale))
                location_value, rotation_value, scale_value = converted.decompose()
                if (
                    previous_helper_rotation is not None
                    and rotation_value.dot(previous_helper_rotation) < 0.0
                ):
                    rotation_value.negate()
                previous_helper_rotation = rotation_value.copy()
                helper_location[frame_index] = tuple(location_value)
                helper_rotation[frame_index] = tuple(rotation_value)
                helper_scale[frame_index] = tuple(scale_value)
        if index in maps["location"]:
            component_tables["location"] = helper_location
        if index in maps["rotation"]:
            component_tables["rotation_quaternion"] = helper_rotation
        if index in maps["scale"]:
            component_tables["scale"] = helper_scale
        if component_tables:
            _helper_action, helper_curves = create_action_target(
                f"{job['name']}__{row['name']}", helper
            )
            widths = {"location": 3, "rotation_quaternion": 4, "scale": 3}
            for data_path, table in component_tables.items():
                for component in range(widths[data_path]):
                    install_bulk_curve(
                        helper_curves,
                        data_path,
                        component,
                        row["name"],
                        frames,
                        table[:, component],
                    )
        helper_objects.append(helper)

    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    for helper in helper_objects:
        helper.select_set(True)
    bpy.context.view_layer.objects.active = armature

    # The FBX exporter serializes a LimbNode's static transform from the
    # currently evaluated pose.  The root-parity audit above necessarily ends
    # on the last sample, which would otherwise rebase the visible armature at
    # that final pose.  Anchor the editable FBX rest skeleton at the exact
    # first sample instead; the baked action remains unchanged.
    first_sample = float(frames[0])
    first_frame = math.floor(first_sample)
    scene.frame_set(first_frame, subframe=first_sample - first_frame)
    update_dependency_graph()

    output = Path(job["output_path"])
    output.parent.mkdir(parents=True, exist_ok=True)
    report("Writing FBX", 0, 1)
    bpy.ops.export_scene.fbx(
        filepath=str(output),
        use_selection=True,
        object_types={"ARMATURE", "EMPTY"},
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
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        use_custom_props=True,
    )
    report("Writing FBX", 1, 1)
    print(f"DLR_EXPORT_COMPLETE:{output}", flush=True)


if __name__ == "__main__":
    separator = sys.argv.index("--") if "--" in sys.argv else len(sys.argv) - 1
    try:
        main(sys.argv[separator + 1 :])
    except BaseException:
        traceback.print_exc()
        raise SystemExit(1)
