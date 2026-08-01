"""Build a local DL1 retail-mesh FBX handoff inside Blender.

This helper is embedded in DL ReAnimated's C# executable.  It consumes only
bounded, temporary job data written by the C# export service.  Retail geometry
and textures are intentionally emitted only beside the user-selected FBX.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import struct
import sys
import traceback

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector


JOB_FORMAT = "dl-reanimated-csharp-blender-fbx-job"
JOB_SCHEMA = 1
CLIP_MAGIC = b"DLRANM1\x00"
Y_UP_TO_BLENDER = Matrix(
    ((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1))
)


def report(stage, current, total):
    print(
        f"DLR_PROGRESS:{stage}|{int(current)}|{int(total)}",
        flush=True,
    )


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
            resolve(parent) @ locals_[index]
            if parent >= 0
            else locals_[index]
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
    return 1 + max(
        descendant_depth(child, children)
        for child in children[index]
    )


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
            (
                helper_penalty,
                -descendant_depth(child, children),
                vector.length,
                child,
            )
        )
    return min(candidates)[3] if candidates else None


def create_action_target(name, owner):
    """Return an armature-owned Action and version-neutral FCurves."""

    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    owner.animation_data_create()
    legacy = getattr(action, "fcurves", None)
    if legacy is not None:
        owner.animation_data.action = action
        return action, legacy, None

    slot = action.slots.new("OBJECT", owner.name)
    layer = action.layers.new("DL ReAnimated")
    strip = layer.strips.new(type="KEYFRAME")
    channelbag = strip.channelbags.new(slot)
    owner.animation_data.action = action
    owner.animation_data.action_slot = slot
    return action, channelbag.fcurves, slot


def activate_action(owner, action, slot):
    owner.animation_data.action = action
    if slot is not None:
        owner.animation_data.action_slot = slot


def install_bulk_curve(
    collection,
    data_path,
    array_index,
    group_name,
    frames,
    values,
):
    try:
        curve = collection.new(
            data_path=data_path,
            index=array_index,
            group_name=group_name,
        )
    except TypeError:
        curve = collection.new(
            data_path,
            index=array_index,
            action_group=group_name,
        )
    count = len(frames)
    curve.keyframe_points.add(count)
    coordinates = np.empty((count, 2), dtype=np.float64)
    coordinates[:, 0] = frames
    coordinates[:, 1] = values
    curve.keyframe_points.foreach_set("co", coordinates.ravel())
    curve.keyframe_points.foreach_set(
        "interpolation",
        np.ones(count, dtype=np.int32),
    )
    curve.update()


def quaternion_error_degrees(actual, expected):
    left = [float(value) for value in actual]
    right = [float(value) for value in expected]
    left_norm = math.sqrt(sum(value * value for value in left))
    right_norm = math.sqrt(sum(value * value for value in right))
    if left_norm <= 1.0e-15 or right_norm <= 1.0e-15:
        raise ValueError("Cannot compare a singular quaternion")
    dot = abs(
        sum(a * b for a, b in zip(left, right))
        / (left_norm * right_norm)
    )
    dot = min(1.0, max(0.0, dot))
    return math.degrees(2.0 * math.acos(dot))


def read_clip(path, expected_frames, expected_bones):
    with Path(path).open("rb") as stream:
        if stream.read(8) != CLIP_MAGIC:
            raise ValueError(f"Invalid temporary clip payload: {path}")
        frame_count, bone_count = struct.unpack("<ii", stream.read(8))
        if frame_count != int(expected_frames):
            raise ValueError(f"Temporary clip frame count changed: {path}")
        if bone_count != int(expected_bones):
            raise ValueError(f"Temporary clip bone count changed: {path}")
        count = frame_count * bone_count * 10
        values = np.fromfile(stream, dtype="<f4", count=count)
        if len(values) != count or stream.read(1):
            raise ValueError(f"Temporary clip payload is truncated: {path}")
    return values.reshape((frame_count, bone_count, 10))


def read_mesh(row):
    vertex_count = int(row["vertex_count"])
    index_count = int(row["index_count"])
    stride = int(row["vertex_stride_floats"])
    if stride != 16 or index_count % 3:
        raise ValueError("Unsupported temporary retail mesh layout")
    with Path(row["binary_path"]).open("rb") as stream:
        vertices = np.fromfile(
            stream,
            dtype="<f4",
            count=vertex_count * stride,
        )
        indices = np.fromfile(
            stream,
            dtype="<u4",
            count=index_count,
        )
        if (
            len(vertices) != vertex_count * stride
            or len(indices) != index_count
            or stream.read(1)
        ):
            raise ValueError(
                f"Temporary retail mesh payload is truncated: "
                f"{row['binary_path']}"
            )
    return vertices.reshape((vertex_count, stride)), indices


def install_armature_only_bind_pose_export():
    """Emit armature edit-rest as BindPose, including an unskinned rig."""

    from io_scene_fbx import export_fbx_bin

    marker = "_dlr_armature_only_bind_pose_installed"
    if getattr(export_fbx_bin, marker, False):
        return

    original_data_from_scene = export_fbx_bin.fbx_data_from_scene
    original_animations_do = export_fbx_bin.fbx_animations_do
    original_skeleton_from_armature = (
        export_fbx_bin.fbx_skeleton_from_armature
    )
    original_armature_elements = export_fbx_bin.fbx_data_armature_elements
    original_object_elements = export_fbx_bin.fbx_data_object_elements
    original_object_tx = export_fbx_bin.ObjectWrapper.fbx_object_tx
    static_model_state = {"rest_bone": False}

    def data_from_scene_with_bind_pose(scene, depsgraph, settings):
        scene_data = original_data_from_scene(
            scene,
            depsgraph,
            settings,
        )
        unskinned_armatures = tuple(
            obj
            for obj in scene_data.objects
            if obj.is_object
            and obj.type == "ARMATURE"
            and not scene_data.data_deformers_skin.get(obj)
        )
        if not unskinned_armatures:
            return scene_data
        templates = dict(scene_data.templates)
        existing = templates.get(b"BindPose")
        existing_users = (
            int(existing.nbr_users)
            if existing is not None
            else 0
        )
        templates[b"BindPose"] = export_fbx_bin.fbx_template_def_pose(
            scene,
            settings,
            nbr_users=existing_users + len(unskinned_armatures),
        )
        return scene_data._replace(
            templates=templates,
            templates_users=(
                scene_data.templates_users
                + len(unskinned_armatures)
            ),
        )

    def animations_do_with_action_name(
        scene_data,
        ref_id,
        f_start,
        f_end,
        start_zero,
        objects=None,
        force_keep=False,
    ):
        animation = original_animations_do(
            scene_data,
            ref_id,
            f_start,
            f_end,
            start_zero,
            objects=objects,
            force_keep=force_keep,
        )
        if (
            animation is None
            or not isinstance(ref_id, tuple)
            or len(ref_id) != 2
            or not hasattr(ref_id[1], "name")
        ):
            return animation
        action_name = str(ref_id[1].name).encode()
        return (
            animation[0],
            animation[1],
            animation[2],
            action_name,
            animation[4],
            animation[5],
        )

    def skeleton_with_weighted_clusters(
        scene,
        settings,
        arm_obj,
        objects,
        data_meshes,
        data_bones,
        data_deformers_skin,
        data_empties,
        arm_parents,
    ):
        original_skeleton_from_armature(
            scene,
            settings,
            arm_obj,
            objects,
            data_meshes,
            data_bones,
            data_deformers_skin,
            data_empties,
            arm_parents,
        )
        deformed_meshes = data_deformers_skin.get(arm_obj)
        if not deformed_meshes:
            return
        for mesh, (skin_key, mesh_object, clusters) in tuple(
            deformed_meshes.items()
        ):
            weighted_group_names = {
                group.name
                for group in mesh_object.bdata.vertex_groups
            }
            weighted_clusters = {
                bone: cluster_key
                for bone, cluster_key in clusters.items()
                if bone.bdata.name in weighted_group_names
            }
            if not weighted_clusters:
                raise RuntimeError(
                    "A skinned Blender handoff mesh has no weighted "
                    f"armature clusters: {mesh_object.bdata.name}"
                )
            deformed_meshes[mesh] = (
                skin_key,
                mesh_object,
                weighted_clusters,
            )

    def armature_elements_with_bind_pose(root, arm_obj, scene_data):
        original_armature_elements(root, arm_obj, scene_data)
        if scene_data.data_deformers_skin.get(arm_obj):
            return
        bones = tuple(
            bone
            for bone in arm_obj.bones
            if bone in scene_data.objects
        )
        if not bones:
            return
        matrix_world = arm_obj.fbx_object_matrix(
            scene_data,
            global_space=True,
        )
        export_fbx_bin.fbx_data_bindpose_element(
            root,
            arm_obj,
            arm_obj.bdata.data,
            scene_data,
            arm_obj=arm_obj,
            mat_world_arm=matrix_world,
            bones=bones,
        )

    def object_tx_with_bind_default(
        wrapped,
        scene_data,
        rest=False,
        rot_euler_compat=None,
    ):
        if static_model_state["rest_bone"] and wrapped.is_bone:
            rest = True
        return original_object_tx(
            wrapped,
            scene_data,
            rest=rest,
            rot_euler_compat=rot_euler_compat,
        )

    def object_elements_with_bind_default(root, obj, scene_data):
        previous = static_model_state["rest_bone"]
        static_model_state["rest_bone"] = bool(obj.is_bone)
        try:
            return original_object_elements(root, obj, scene_data)
        finally:
            static_model_state["rest_bone"] = previous

    export_fbx_bin.fbx_data_from_scene = data_from_scene_with_bind_pose
    export_fbx_bin.fbx_animations_do = animations_do_with_action_name
    export_fbx_bin.fbx_skeleton_from_armature = (
        skeleton_with_weighted_clusters
    )
    export_fbx_bin.fbx_data_armature_elements = (
        armature_elements_with_bind_pose
    )
    export_fbx_bin.fbx_data_object_elements = (
        object_elements_with_bind_default
    )
    export_fbx_bin.ObjectWrapper.fbx_object_tx = (
        object_tx_with_bind_default
    )
    setattr(export_fbx_bin, marker, True)


def build_armature(job):
    bones = job["bones"]
    if not bones:
        raise ValueError("Retail rig contains no bones")
    names = [str(row["name"]) for row in bones]
    if len(set(names)) != len(names):
        raise ValueError("Retail rig contains duplicate bone names")

    bind_local = [
        trs_values(
            row["bind_translation"],
            row["bind_rotation_wxyz"],
            row["bind_scale"],
        )
        for row in bones
    ]
    bind_global = [
        convert(value)
        for value in global_matrices(bind_local, bones)
    ]
    bind_heads = [
        matrix.translation.copy()
        for matrix in bind_global
    ]
    children = child_indices(bones)
    armature_data = bpy.data.armatures.new("DL1_Retail_Armature")
    armature = bpy.data.objects.new(
        str(job["asset"]["resource_name"]),
        armature_data,
    )
    armature["dlr_basis_mode"] = "child_pivot_display_v1"
    armature["dlr_fidelity"] = str(job["fidelity"])
    armature["dlr_retail_asset_key"] = str(
        job["asset"]["stable_key"]
    )
    armature["dlr_retail_content_fingerprint"] = str(
        job["asset"]["content_fingerprint"]
    )
    armature["dlr_local_export_only"] = True
    bpy.context.collection.objects.link(armature)
    bpy.context.view_layer.objects.active = armature
    armature.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    edit_bones = {}
    report("Creating armature", 0, len(bones))
    for index, row in enumerate(bones):
        head = bind_heads[index]
        child_index = display_child(
            index,
            bones,
            bind_heads,
            children,
        )
        if child_index is not None:
            tail = bind_heads[child_index].copy()
        else:
            parent = int(row["parent_index"])
            if parent >= 0:
                direction = head - bind_heads[parent]
                if direction.length > 1.0e-5:
                    tail = head + direction.normalized() * max(
                        direction.length * 0.4,
                        0.01,
                    )
                else:
                    tail = (
                        head
                        + proper_rotation(bind_global[index])
                        .col[1]
                        .normalized()
                        * 0.03
                    )
            else:
                tail = (
                    head
                    + proper_rotation(bind_global[index])
                    .col[1]
                    .normalized()
                    * 0.05
                )
        if (tail - head).length < 0.001:
            tail = head + Vector((0.0, 0.01, 0.0))
        bone = armature_data.edit_bones.new(str(row["name"]))
        bone.head = head
        bone.tail = tail
        native_rotation = proper_rotation(bind_global[index])
        roll_reference = native_rotation.col[2].normalized()
        bone_direction = (bone.tail - bone.head).normalized()
        if abs(roll_reference.dot(bone_direction)) > 0.98:
            roll_reference = native_rotation.col[0].normalized()
        bone.align_roll(roll_reference)
        bone.use_deform = bool(row.get("deform", True))
        edit_bones[index] = bone
        if (index + 1) % 32 == 0 or index + 1 == len(bones):
            report("Creating armature", index + 1, len(bones))

    for index, row in enumerate(bones):
        parent = int(row["parent_index"])
        if parent >= 0:
            edit_bones[index].parent = edit_bones[parent]
        edit_bones[index].use_connect = False

    bpy.ops.object.mode_set(mode="POSE")
    display_rest_globals = {}
    display_parent_indices = {}
    display_basis_corrections = {}
    index_by_name = {
        str(row["name"]): index
        for index, row in enumerate(bones)
    }
    for index, row in enumerate(bones):
        name = str(row["name"])
        data_bone = armature.data.bones[name]
        descriptor = row.get("descriptor")
        data_bone["dlr_descriptor"] = (
            ""
            if descriptor is None
            else f"0x{int(descriptor):08X}"
        )
        data_bone["dlr_helper"] = bool(row.get("helper", False))
        data_bone["dlr_semantic"] = str(row.get("semantic", ""))
        display_rest = data_bone.matrix_local.copy()
        display_rest_globals[index] = display_rest
        display_parent = data_bone.parent
        display_parent_indices[index] = (
            index_by_name[display_parent.name]
            if display_parent is not None
            else -1
        )
        display_basis_corrections[index] = (
            bind_global[index].inverted_safe()
            @ display_rest
        )
        armature.pose.bones[name].rotation_mode = "QUATERNION"
    bpy.ops.object.mode_set(mode="OBJECT")
    return (
        armature,
        bind_local,
        bind_global,
        display_rest_globals,
        display_parent_indices,
        display_basis_corrections,
        bind_heads,
    )


def install_actions(
    job,
    armature,
    bind_local,
    display_rest_globals,
    display_parent_indices,
    display_basis_corrections,
):
    bones = job["bones"]
    order = topological_indices(bones)
    root_candidates = [
        index
        for index, row in enumerate(bones)
        if int(row["parent_index"]) < 0
        and bool(row.get("root", False))
    ]
    if len(root_candidates) != 1:
        raise ValueError(
            "Retail rig needs exactly one parentless Root bone; "
            f"found {len(root_candidates)}"
        )
    root_index = root_candidates[0]
    actions = []
    expected_roots = []
    total = sum(int(row["fbx_frame_count"]) for row in job["clips"])
    completed = 0
    report("Installing actions", 0, total)
    for clip in job["clips"]:
        values = read_clip(
            clip["binary_path"],
            clip["fbx_frame_count"],
            len(bones),
        )
        frame_count = values.shape[0]
        frames = np.arange(frame_count, dtype=np.float64)
        sampled_location = np.empty(
            (frame_count, len(bones), 3),
            dtype=np.float64,
        )
        sampled_rotation = np.empty(
            (frame_count, len(bones), 4),
            dtype=np.float64,
        )
        sampled_scale = np.empty(
            (frame_count, len(bones), 3),
            dtype=np.float64,
        )
        prior_rotations = [None] * len(bones)
        clip_expected_roots = []
        for frame_index in range(frame_count):
            animated_local = [
                trs_values(
                    values[frame_index, index, 0:3],
                    values[frame_index, index, 3:7],
                    values[frame_index, index, 7:10],
                )
                for index in range(len(bones))
            ]
            animated_global = [
                convert(value)
                for value in global_matrices(
                    animated_local,
                    bones,
                )
            ]
            desired_globals = [
                animated_global[index]
                @ display_basis_corrections[index]
                for index in range(len(bones))
            ]
            clip_expected_roots.append(
                desired_globals[root_index].copy()
            )
            for index in order:
                parent = display_parent_indices[index]
                if parent >= 0:
                    rest_relative = (
                        display_rest_globals[parent].inverted_safe()
                        @ display_rest_globals[index]
                    )
                    pose_relative = (
                        desired_globals[parent].inverted_safe()
                        @ desired_globals[index]
                    )
                    basis = (
                        rest_relative.inverted_safe()
                        @ pose_relative
                    )
                else:
                    basis = (
                        display_rest_globals[index].inverted_safe()
                        @ desired_globals[index]
                    )
                location, rotation, scale = basis.decompose()
                previous = prior_rotations[index]
                if previous is not None and rotation.dot(previous) < 0.0:
                    rotation.negate()
                prior_rotations[index] = rotation.copy()
                sampled_location[frame_index, index] = tuple(location)
                sampled_rotation[frame_index, index] = tuple(rotation)
                sampled_scale[frame_index, index] = tuple(scale)
            completed += 1
            if completed % 32 == 0 or completed == total:
                report("Installing actions", completed, total)
            del animated_local
            del animated_global
            del desired_globals

        action, curves, slot = create_action_target(
            str(clip["action_name"]),
            armature,
        )
        action["dlr_source_file"] = str(clip["source_file_name"])
        action["dlr_source_sha256"] = str(clip["source_sha256"])
        action["dlr_anm2_input_fps"] = float(
            clip["anm2_input_fps"]
        )
        action["dlr_fbx_output_fps"] = float(
            clip["fbx_output_fps"]
        )
        action["dlr_helper_tracks"] = json.dumps(
            clip["helper_tracks"],
            separators=(",", ":"),
        )
        action["dlr_motion_accumulator"] = json.dumps(
            clip["motion_accumulator"],
            separators=(",", ":"),
        )
        for index, row in enumerate(bones):
            name = str(row["name"])
            location_path = f'pose.bones["{name}"].location'
            rotation_path = (
                f'pose.bones["{name}"].rotation_quaternion'
            )
            scale_path = f'pose.bones["{name}"].scale'
            for component in range(3):
                install_bulk_curve(
                    curves,
                    location_path,
                    component,
                    name,
                    frames,
                    sampled_location[:, index, component],
                )
            for component in range(4):
                install_bulk_curve(
                    curves,
                    rotation_path,
                    component,
                    name,
                    frames,
                    sampled_rotation[:, index, component],
                )
            for component in range(3):
                install_bulk_curve(
                    curves,
                    scale_path,
                    component,
                    name,
                    frames,
                    sampled_scale[:, index, component],
                )
        actions.append((action, slot))
        expected_roots.append(clip_expected_roots)
        del values
        del frames
        del sampled_location
        del sampled_rotation
        del sampled_scale
        del prior_rotations

    if actions:
        activate_action(armature, actions[0][0], actions[0][1])
    return actions, expected_roots, root_index


def create_material(texture, image_by_key):
    material = bpy.data.materials.new(
        "DL1_BaseColor_" + str(texture["key"])[:24]
    )
    material.use_nodes = True
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    principled = nodes.get("Principled BSDF")
    image_node = nodes.new("ShaderNodeTexImage")
    image_node.image = image_by_key[str(texture["key"])]
    image_node.label = "Decoded DL1 base color only"
    links.new(
        image_node.outputs["Color"],
        principled.inputs["Base Color"],
    )
    if "Alpha" in image_node.outputs and "Alpha" in principled.inputs:
        links.new(
            image_node.outputs["Alpha"],
            principled.inputs["Alpha"],
        )
    material["dlr_base_color_only"] = True
    return material


def build_meshes(job, armature):
    bones = job["bones"]
    texture_by_key = {
        str(row["key"]): row
        for row in job["textures"]
    }
    image_by_key = {}
    for key, row in texture_by_key.items():
        image = bpy.data.images.load(
            str(Path(row["file_path"]).resolve()),
            check_existing=False,
        )
        image.name = "DL1_" + str(row["resource_name"])[:48]
        image["dlr_base_color_only"] = True
        image_by_key[key] = image
    material_by_key = {
        key: create_material(row, image_by_key)
        for key, row in texture_by_key.items()
    }

    objects = []
    report("Creating retail meshes", 0, len(job["meshes"]))
    conversion3 = Y_UP_TO_BLENDER.to_3x3()
    for mesh_index, row in enumerate(job["meshes"]):
        vertices, indices = read_mesh(row)
        positions = [
            tuple(conversion3 @ Vector(value))
            for value in vertices[:, 0:3]
        ]
        normals = [
            tuple((conversion3 @ Vector(value)).normalized())
            for value in vertices[:, 3:6]
        ]
        faces = [
            tuple(int(value) for value in indices[offset : offset + 3])
            for offset in range(0, len(indices), 3)
        ]
        mesh_data = bpy.data.meshes.new(str(row["name"]) + "_Mesh")
        mesh_data.from_pydata(positions, [], faces)
        mesh_data.update()
        del positions
        del faces
        del indices
        uv_layer = mesh_data.uv_layers.new(name="UVMap")
        for polygon in mesh_data.polygons:
            for loop_index in polygon.loop_indices:
                vertex_index = mesh_data.loops[loop_index].vertex_index
                uv_layer.data[loop_index].uv = (
                    float(vertices[vertex_index, 6]),
                    1.0 - float(vertices[vertex_index, 7]),
                )
        try:
            mesh_data.normals_split_custom_set_from_vertices(normals)
        except (AttributeError, RuntimeError, ValueError) as error:
            raise RuntimeError(
                "Failed to install decoded custom normals for retail "
                f"mesh '{row['name']}'. The handoff is aborted instead "
                "of silently exporting different shading."
            ) from error
        del normals

        mesh_object = bpy.data.objects.new(str(row["name"]), mesh_data)
        mesh_object["dlr_retail_mesh"] = True
        mesh_object["dlr_base_color_only"] = True
        local_to_world = Matrix(
            np.asarray(
                row["local_to_world"],
                dtype=np.float64,
            ).reshape((4, 4)).tolist()
        )
        mesh_object.matrix_world = convert(local_to_world)
        bpy.context.collection.objects.link(mesh_object)
        texture_key = row.get("texture_key")
        if texture_key is not None and str(texture_key) in material_by_key:
            mesh_data.materials.append(
                material_by_key[str(texture_key)]
            )

        if bool(row.get("skinned", False)):
            influences = []
            active_bone_indices = set()
            for vertex_index in range(len(vertices)):
                vertex_influences = {}
                for lane in range(4):
                    weight = float(vertices[vertex_index, 8 + lane])
                    raw_bone_index = float(
                        vertices[vertex_index, 12 + lane]
                    )
                    if weight <= 1.0e-8:
                        continue
                    bone_index = int(round(raw_bone_index))
                    if (
                        not math.isfinite(weight)
                        or not math.isfinite(raw_bone_index)
                        or abs(raw_bone_index - bone_index) > 1.0e-4
                        or bone_index < 0
                        or bone_index >= len(bones)
                    ):
                        raise ValueError(
                            "Decoded retail skin influence is invalid for "
                            f"mesh '{row['name']}', vertex {vertex_index}, "
                            f"lane {lane}: weight={weight}, "
                            f"bone_index={raw_bone_index}."
                        )
                    vertex_influences[bone_index] = (
                        vertex_influences.get(bone_index, 0.0) + weight
                    )
                    active_bone_indices.add(bone_index)
                if not vertex_influences:
                    raise ValueError(
                        "Decoded skinned retail mesh "
                        f"'{row['name']}' has no positive influence for "
                        f"vertex {vertex_index}."
                    )
                influences.append(vertex_influences)
            groups = {
                bone_index: mesh_object.vertex_groups.new(
                    name=str(bones[bone_index]["name"])
                )
                for bone_index in sorted(active_bone_indices)
            }
            for vertex_index, vertex_influences in enumerate(influences):
                for bone_index, weight in vertex_influences.items():
                    groups[bone_index].add(
                        [vertex_index],
                        weight,
                        "REPLACE",
                    )
            modifier = mesh_object.modifiers.new(
                "DL1 Armature",
                "ARMATURE",
            )
            modifier.object = armature
            del influences
            del groups
        del vertices
        objects.append(mesh_object)
        report(
            "Creating retail meshes",
            mesh_index + 1,
            len(job["meshes"]),
        )
    return objects


def create_roundtrip_guard(job, armature, bind_heads):
    bones = job["bones"]
    guard_mesh = bpy.data.meshes.new("DLR_BindPoseGuard_Mesh")
    guard_mesh.from_pydata(
        [tuple(head) for head in bind_heads],
        [],
        [],
    )
    guard_mesh.update()
    guard = bpy.data.objects.new("DLR_BindPoseGuard", guard_mesh)
    bpy.context.collection.objects.link(guard)
    guard.display_type = "WIRE"
    guard.hide_render = True
    guard["dlr_roundtrip_guard"] = (
        "armature_edit_rest_with_roundtrip_guard"
    )
    for index, row in enumerate(bones):
        group = guard.vertex_groups.new(name=str(row["name"]))
        group.add([index], 1.0, "REPLACE")
    modifier = guard.modifiers.new("DLR Bind Guard", "ARMATURE")
    modifier.object = armature
    return guard


def audit_root_parity(
    scene,
    armature,
    actions,
    expected_roots,
    root_index,
    bones,
):
    maximum_angular = 0.0
    maximum_translation = 0.0
    frame_total = sum(len(rows) for rows in expected_roots)
    completed = 0
    report("Auditing root parity", 0, frame_total)
    root_pose = armature.pose.bones[
        str(bones[root_index]["name"])
    ]
    for action_index, (action, slot) in enumerate(actions):
        activate_action(armature, action, slot)
        for frame_index, expected in enumerate(
            expected_roots[action_index]
        ):
            scene.frame_set(frame_index)
            bpy.context.view_layer.update()
            actual = root_pose.matrix.copy()
            maximum_angular = max(
                maximum_angular,
                quaternion_error_degrees(
                    actual.to_quaternion(),
                    expected.to_quaternion(),
                ),
            )
            maximum_translation = max(
                maximum_translation,
                (actual.translation - expected.translation).length,
            )
            completed += 1
            if completed % 32 == 0 or completed == frame_total:
                report(
                    "Auditing root parity",
                    completed,
                    frame_total,
                )
    parity = {
        "max_angular_error_degrees": float(maximum_angular),
        "max_translation_error_m": float(maximum_translation),
    }
    print(
        "DLR_ROOT_PARITY:"
        + json.dumps(parity, separators=(",", ":")),
        flush=True,
    )
    if maximum_angular > 0.05 or maximum_translation > 1.0e-5:
        raise ValueError(
            "Child-pivot animation parity exceeded tolerance: "
            f"{maximum_angular:.9f} degrees, "
            f"{maximum_translation:.12g} m"
        )
    if actions:
        activate_action(armature, actions[0][0], actions[0][1])
        scene.frame_set(0)


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--job", required=True)
    args = parser.parse_args(argv)
    job_path = Path(args.job)
    job = json.loads(job_path.read_text(encoding="utf-8-sig"))
    if (
        job.get("format") != JOB_FORMAT
        or int(job.get("schema_version", 0)) != JOB_SCHEMA
    ):
        raise ValueError("Unsupported DL ReAnimated C# Blender job")
    if not job.get("clips") or not job.get("meshes"):
        raise ValueError("The Blender handoff needs mesh and ANM2 data")

    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    scene = bpy.context.scene
    scene.name = "DL ReAnimated DL1 Handoff"
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    output_fps = float(job["fbx_output_fps"])
    if not math.isfinite(output_fps) or output_fps <= 0.0:
        raise ValueError("FBX output FPS must be finite and positive")
    fps_numerator = max(1, int(round(output_fps)))
    scene.render.fps = fps_numerator
    scene.render.fps_base = fps_numerator / output_fps
    scene.frame_start = 0
    scene.frame_end = max(
        int(row["fbx_frame_count"]) - 1
        for row in job["clips"]
    )

    (
        armature,
        bind_local,
        _bind_global,
        display_rest_globals,
        display_parent_indices,
        display_basis_corrections,
        bind_heads,
    ) = build_armature(job)
    actions, expected_roots, root_index = install_actions(
        job,
        armature,
        bind_local,
        display_rest_globals,
        display_parent_indices,
        display_basis_corrections,
    )
    mesh_objects = build_meshes(job, armature)
    guard = create_roundtrip_guard(job, armature, bind_heads)
    audit_root_parity(
        scene,
        armature,
        actions,
        expected_roots,
        root_index,
        job["bones"],
    )

    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    for mesh_object in mesh_objects:
        mesh_object.select_set(True)
    guard.select_set(True)
    bpy.context.view_layer.objects.active = armature
    install_armature_only_bind_pose_export()
    output = Path(job["output_path"])
    output.parent.mkdir(parents=True, exist_ok=True)
    report("Writing FBX", 0, 1)
    bpy.ops.export_scene.fbx(
        filepath=str(output),
        use_selection=True,
        object_types={"ARMATURE", "MESH"},
        use_mesh_modifiers=False,
        add_leaf_bones=False,
        bake_anim=True,
        bake_anim_use_all_bones=False,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=True,
        bake_anim_force_startend_keying=False,
        bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,
        axis_forward="-Z",
        axis_up="Y",
        apply_unit_scale=True,
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        use_custom_props=True,
        path_mode="RELATIVE",
        embed_textures=False,
    )
    report("Writing FBX", 1, 1)
    print(
        "DLR_ACTION_STACKS:"
        + json.dumps(
            [action.name for action, _slot in actions],
            separators=(",", ":"),
        ),
        flush=True,
    )
    print(
        "DLR_BIND_POSE:"
        + json.dumps(
            {
                "exported": True,
                "bone_count": len(job["bones"]),
            },
            separators=(",", ":"),
        ),
        flush=True,
    )
    print(f"DLR_EXPORT_COMPLETE:{output}", flush=True)


if __name__ == "__main__":
    separator = (
        sys.argv.index("--")
        if "--" in sys.argv
        else len(sys.argv) - 1
    )
    try:
        main(sys.argv[separator + 1 :])
    except BaseException:
        traceback.print_exc()
        raise SystemExit(1)
