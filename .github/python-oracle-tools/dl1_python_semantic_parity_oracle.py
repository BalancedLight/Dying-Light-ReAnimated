from __future__ import annotations

import argparse
import base64
import hashlib
import json
import math
import sys
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

import numpy as np


FORMAT = "dl-reanimated-python-csharp-semantic-parity-oracle-v1"
MAX_ORACLE_BYTES = 2 * 1024 * 1024
MOTION_ACCUMULATOR_DESCRIPTOR = 0xCCC3CDDF


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def _matrix_payload(matrix: np.ndarray) -> list[list[float]]:
    value = np.asarray(matrix, dtype=float)
    if value.shape != (4, 4) or not np.isfinite(value).all():
        raise ValueError("Parity matrix must be a finite 4x4 value.")
    return [[float(component) for component in row] for row in value]


def _quaternion_xyzw(axis: Sequence[float], degrees: float) -> list[float]:
    vector = np.asarray(tuple(float(value) for value in axis), dtype=float)
    length = float(np.linalg.norm(vector))
    if vector.shape != (3,) or not math.isfinite(length) or length <= 1.0e-12:
        raise ValueError("Quaternion axis must be a finite non-zero vector.")
    vector /= length
    half = math.radians(float(degrees)) * 0.5
    scale = math.sin(half)
    return [
        float(vector[0] * scale),
        float(vector[1] * scale),
        float(vector[2] * scale),
        float(math.cos(half)),
    ]


def _quaternion_multiply_xyzw(
    left: Sequence[float],
    right: Sequence[float],
) -> list[float]:
    lx, ly, lz, lw = (float(value) for value in left)
    rx, ry, rz, rw = (float(value) for value in right)
    result = np.asarray(
        (
            (lw * rx) + (lx * rw) + (ly * rz) - (lz * ry),
            (lw * ry) - (lx * rz) + (ly * rw) + (lz * rx),
            (lw * rz) + (lx * ry) - (ly * rx) + (lz * rw),
            (lw * rw) - (lx * rx) - (ly * ry) - (lz * rz),
        ),
        dtype=float,
    )
    result /= np.linalg.norm(result)
    return [float(value) for value in result]


def _trs(
    translation: Sequence[float],
    rotation_xyzw: Sequence[float] | None = None,
    scale: Sequence[float] = (1.0, 1.0, 1.0),
) -> dict[str, list[float]]:
    return {
        "translation": [float(value) for value in translation],
        "rotationXyzw": [
            float(value)
            for value in (
                rotation_xyzw
                if rotation_xyzw is not None
                else (0.0, 0.0, 0.0, 1.0)
            )
        ],
        "scale": [float(value) for value in scale],
    }


def _trs_matrix(transform: Mapping[str, Sequence[float]]) -> np.ndarray:
    from dlanm2_gui.retarget_engines.mapped_rig import compose_local_matrix

    quaternion = transform["rotationXyzw"]
    return compose_local_matrix(
        list(transform["translation"]),
        [quaternion[3], quaternion[0], quaternion[1], quaternion[2]],
        list(transform["scale"]),
    )


def _property(name: str, *values: float | int):
    from dlanm2_gui.model_importer.fbx_model import FbxNode

    return FbxNode("P", [name, name, "", "A", *values], [], 0, 0)


def _model_node(
    object_id: int,
    name: str,
    subtype: str,
    properties: Mapping[str, Sequence[float] | int],
):
    from dlanm2_gui.model_importer.fbx_model import FbxNode

    property_nodes = []
    for property_name, raw in properties.items():
        values = [raw] if isinstance(raw, int) else list(raw)
        property_nodes.append(_property(property_name, *values))
    properties70 = FbxNode("Properties70", [], property_nodes, 0, 0)
    return FbxNode(
        "Model",
        [object_id, f"Model::{name}", subtype],
        [properties70],
        0,
        0,
    )


def _fbx_scene(
    nodes: Sequence[Any],
    *,
    parents: dict[int, list[tuple[str, int, list[object]]]] | None = None,
    children: dict[int, list[tuple[str, int, list[object]]]] | None = None,
):
    from dlanm2_gui.model_importer.fbx_model import FbxNode, FbxScene

    object_by_id = {int(node.properties[0]): node for node in nodes}
    return FbxScene(
        path=Path("synthetic_parity.fbx"),
        version=7400,
        top={"Objects": FbxNode("Objects", [], list(nodes), 0, 0)},
        object_by_id=object_by_id,
        parents=parents or {},
        children=children or {},
        model_ids=tuple(object_by_id),
        limb_ids=tuple(
            object_id
            for object_id, node in object_by_id.items()
            if str(node.properties[2]) == "LimbNode"
        ),
        model_names={
            object_id: str(node.properties[1]).split("::", 1)[-1]
            for object_id, node in object_by_id.items()
        },
        model_subtypes={
            object_id: str(node.properties[2])
            for object_id, node in object_by_id.items()
        },
        material_names={},
        bind_pose_matrices={},
        geometries=(),
        animation_stacks=(),
        blend_shape_names=(),
        axis_settings={
            "UpAxis": 1,
            "UpAxisSign": 1,
            "CoordAxis": 0,
            "CoordAxisSign": 1,
            "FrontAxis": 2,
            "FrontAxisSign": 1,
            "UnitScaleFactor": 1.0,
            "OriginalUnitScaleFactor": 1.0,
        },
        meters_per_unit=0.01,
    )


def _fbx_transform_payload() -> dict[str, Any]:
    from dlanm2_gui.fbx_core import _sample_curve

    angles = [17.0, -31.0, 43.0]
    euler_orders: list[dict[str, Any]] = []
    for order in range(6):
        properties: dict[str, Sequence[float] | int] = {
            "Lcl Translation": (0.0, 0.0, 0.0),
            "Lcl Rotation": angles,
            "Lcl Scaling": (1.0, 1.0, 1.0),
            "RotationOrder": order,
        }
        scene = _fbx_scene((_model_node(1, "root", "LimbNode", properties),))
        euler_orders.append(
            {
                "order": order,
                "matrix": _matrix_payload(scene.model_local_matrix(1)),
            }
        )

    pivot_properties: dict[str, Sequence[float] | int] = {
        "Lcl Translation": (1.25, -2.5, 3.75),
        "Lcl Rotation": (25.0, -40.0, 70.0),
        "Lcl Scaling": (1.2, 0.8, 1.5),
        "PreRotation": (5.0, 10.0, -15.0),
        "PostRotation": (-20.0, 5.0, 12.0),
        "RotationOffset": (0.5, -0.25, 0.75),
        "RotationPivot": (2.0, -1.0, 0.5),
        "ScalingOffset": (-0.4, 0.3, 0.2),
        "ScalingPivot": (0.6, 0.7, -0.8),
        "RotationOrder": 4,
    }
    pivot_scene = _fbx_scene(
        (_model_node(1, "pivoted", "LimbNode", pivot_properties),)
    )

    hierarchy_models = [
        {
            "objectId": 1,
            "name": "parent",
            "subtype": "LimbNode",
            "parentObjectId": None,
            "properties": {
                "Lcl Translation": [2.0, -1.0, 0.5],
                "Lcl Rotation": [10.0, 20.0, -30.0],
                "Lcl Scaling": [1.0, 1.0, 1.0],
                "RotationOrder": 2,
            },
        },
        {
            "objectId": 2,
            "name": "child",
            "subtype": "LimbNode",
            "parentObjectId": 1,
            "properties": {
                "Lcl Translation": [0.25, 1.5, -0.75],
                "Lcl Rotation": [-15.0, 5.0, 45.0],
                "Lcl Scaling": [0.9, 1.1, 1.0],
                "RotationOrder": 5,
            },
        },
    ]
    hierarchy_nodes = [
        _model_node(
            int(model["objectId"]),
            str(model["name"]),
            str(model["subtype"]),
            model["properties"],
        )
        for model in hierarchy_models
    ]
    hierarchy_scene = _fbx_scene(
        hierarchy_nodes,
        parents={2: [("OO", 1, [])]},
        children={1: [("OO", 2, [])]},
    )
    hierarchy_globals = hierarchy_scene.model_global_matrices()

    curve_times = [-10, 0, 10, 25]
    curve_values = [3.0, 5.0, -2.0, 8.0]
    sample_ticks = [-20, -10, -5, 0, 5, 10, 17, 25, 30]
    return {
        "eulerAnglesDegrees": angles,
        "eulerOrders": euler_orders,
        "pivotCase": {
            "properties": pivot_properties,
            "matrix": _matrix_payload(pivot_scene.model_local_matrix(1)),
        },
        "hierarchyCase": {
            "models": hierarchy_models,
            "globalMatrices": {
                str(object_id): _matrix_payload(matrix)
                for object_id, matrix in hierarchy_globals.items()
            },
        },
        "curveCase": {
            "keyTimes": curve_times,
            "keyValues": curve_values,
            "sampleTicks": sample_ticks,
            "samples": [
                _sample_curve((curve_times, curve_values), tick, -999.0)
                for tick in sample_ticks
            ],
        },
    }


def _rig_globals(
    bones: Sequence[Mapping[str, Any]],
    transform_key: str,
) -> list[np.ndarray]:
    globals_: list[np.ndarray] = []
    for bone in bones:
        local = _trs_matrix(bone[transform_key])
        parent = int(bone["parentIndex"])
        globals_.append(local if parent < 0 else globals_[parent] @ local)
    return globals_


def _bind_basis_retarget_payload() -> dict[str, Any]:
    source_bones = [
        {
            "name": "source_root",
            "parentIndex": -1,
            "kind": "Root",
            "bind": _trs(
                (0.5, 1.0, -0.25),
                _quaternion_xyzw((1.0, 0.0, 0.0), 10.0),
            ),
            "animated": _trs(
                (1.25, 1.2, -1.5),
                _quaternion_multiply_xyzw(
                    _quaternion_xyzw((0.0, 1.0, 0.0), 35.0),
                    _quaternion_xyzw((1.0, 0.0, 0.0), 10.0),
                ),
            ),
        },
        {
            "name": "source_head",
            "parentIndex": 0,
            "kind": "Helper",
            "bind": _trs(
                (0.0, 1.4, 0.1),
                _quaternion_xyzw((0.0, 0.0, 1.0), -5.0),
            ),
            "animated": _trs(
                (0.1, 1.45, 0.2),
                _quaternion_xyzw((0.0, 0.0, 1.0), 25.0),
            ),
        },
    ]
    target_bones = [
        {
            "name": "Bip01",
            "parentIndex": -1,
            "kind": "Root",
            "bind": _trs(
                (-0.25, 0.8, 0.5),
                _quaternion_xyzw((0.0, 1.0, 0.0), -15.0),
            ),
        },
        {
            "name": "RefCamera",
            "parentIndex": 0,
            "kind": "Camera",
            "bind": _trs(
                (0.15, 1.65, -0.1),
                _quaternion_xyzw((1.0, 0.0, 0.0), 5.0),
            ),
        },
        {
            "name": "EyeCamera",
            "parentIndex": 0,
            "kind": "Camera",
            "bind": _trs(
                (0.0, 1.55, 0.05),
                _quaternion_xyzw((1.0, 0.0, 0.0), 3.0),
            ),
        },
    ]
    mappings = [
        {"sourceBoneIndex": 0, "targetBoneIndex": 0},
        {"sourceBoneIndex": 1, "targetBoneIndex": 1},
    ]
    source_bind_globals = _rig_globals(source_bones, "bind")
    source_animated_globals = _rig_globals(source_bones, "animated")
    target_bind_globals = _rig_globals(target_bones, "bind")

    target_globals: list[np.ndarray] = []
    mapping_by_target = {
        int(row["targetBoneIndex"]): int(row["sourceBoneIndex"])
        for row in mappings
    }
    target_locals: list[np.ndarray] = []
    for target_index, target_bone in enumerate(target_bones):
        source_index = mapping_by_target.get(target_index)
        if source_index is not None:
            desired_global = (
                source_animated_globals[source_index]
                @ np.linalg.inv(source_bind_globals[source_index])
                @ target_bind_globals[target_index]
            )
            parent = int(target_bone["parentIndex"])
            desired_local = (
                desired_global
                if parent < 0
                else np.linalg.inv(target_globals[parent]) @ desired_global
            )
        else:
            desired_local = _trs_matrix(target_bone["bind"])
            parent = int(target_bone["parentIndex"])
            desired_global = (
                desired_local
                if parent < 0
                else target_globals[parent] @ desired_local
            )
        target_locals.append(desired_local)
        target_globals.append(desired_global)

    return {
        "sourceBones": source_bones,
        "targetBones": target_bones,
        "mappings": mappings,
        "reviewedTargetBindBoneIndices": [2],
        "expectedLocalMatrices": [
            _matrix_payload(matrix) for matrix in target_locals
        ],
        "expectedGlobalMatrices": [
            _matrix_payload(matrix) for matrix in target_globals
        ],
        "trackOwnership": [
            {
                "targetBoneIndex": index,
                "targetBone": target_bones[index]["name"],
                "source": (
                    "evaluated"
                    if index in mapping_by_target
                    else "target_bind"
                ),
                "sourceBoneIndex": mapping_by_target.get(index),
            }
            for index in range(len(target_bones))
        ],
    }


def _helper_fanout_payload() -> dict[str, Any]:
    """Build a bounded Head -> body/RefCamera/EyeCamera policy oracle."""

    from dlanm2_gui.helper_retarget import (
        anm2_values_to_local_matrix,
        evaluate_helper_target_local,
        local_matrix_to_anm2_values,
        merge_helper_components,
    )

    source_bones = [
        {
            "name": "Root",
            "parentIndex": -1,
            "kind": "Root",
            "bind": _trs((0.2, 0.1, -0.3)),
        },
        {
            "name": "Head",
            "parentIndex": 0,
            "kind": "Deform",
            "bind": _trs(
                (0.0, 1.25, 0.05),
                _quaternion_xyzw((0.0, 1.0, 0.0), 4.0),
            ),
        },
    ]
    source_frames = [
        [bone["bind"] for bone in source_bones],
        [
            _trs(
                (0.55, 0.2, -0.75),
                _quaternion_xyzw((0.0, 1.0, 0.0), 23.0),
            ),
            _trs(
                (0.12, 1.38, -0.08),
                _quaternion_xyzw((0.0, 0.0, 1.0), 31.0),
            ),
        ],
    ]
    target_bones = [
        {
            "name": "Bip01",
            "parentIndex": -1,
            "kind": "Root",
            "bind": _trs(
                (-0.15, 0.4, 0.25),
                _quaternion_xyzw((0.0, 1.0, 0.0), -9.0),
            ),
        },
        {
            "name": "Head",
            "parentIndex": 0,
            "kind": "Deform",
            "bind": _trs(
                (0.0, 1.65, -0.04),
                _quaternion_xyzw((1.0, 0.0, 0.0), 7.0),
            ),
        },
        {
            "name": "RefCamera",
            "parentIndex": 1,
            "kind": "Camera",
            "bind": _trs(
                (0.18, 0.22, -0.16),
                _quaternion_xyzw((0.0, 1.0, 0.0), 14.0),
                (0.8, 0.8, 0.8),
            ),
        },
        {
            "name": "EyeCamera",
            "parentIndex": 1,
            "kind": "Camera",
            "bind": _trs(
                (-0.06, 0.31, 0.11),
                _quaternion_xyzw((1.0, 0.0, 0.0), -12.0),
                (1.3, 1.3, 1.3),
            ),
        },
        {
            "name": "UnmappedSocket",
            "parentIndex": 1,
            "kind": "Helper",
            "bind": _trs(
                (0.45, -0.1, 0.3),
                _quaternion_xyzw((0.0, 0.0, 1.0), 6.0),
            ),
        },
    ]
    mappings = [
        {
            "sourceBoneIndex": 0,
            "targetBoneIndex": 0,
            "mappingKind": "Bone",
            "transferPolicy": "GlobalBindBasis",
            "componentPolicy": "FullTransform",
        },
        {
            "sourceBoneIndex": 1,
            "targetBoneIndex": 1,
            "mappingKind": "Bone",
            "transferPolicy": "GlobalBindBasis",
            "componentPolicy": "FullTransform",
        },
        {
            "sourceBoneIndex": 1,
            "targetBoneIndex": 2,
            "mappingKind": "HelperOverride",
            "transferPolicy": "RestRelative",
            "componentPolicy": "Translation",
        },
        {
            "sourceBoneIndex": 1,
            "targetBoneIndex": 3,
            "mappingKind": "HelperOverride",
            "transferPolicy": "RestRelative",
            "componentPolicy": "RotationTranslation",
        },
    ]

    source_bind_globals = _rig_globals(source_bones, "bind")
    target_bind_globals = _rig_globals(target_bones, "bind")
    target_bind_locals = [
        _trs_matrix(bone["bind"]) for bone in target_bones
    ]
    base_by_target = {
        row["targetBoneIndex"]: row
        for row in mappings
        if row["mappingKind"] == "Bone"
    }
    helper_by_target = {
        row["targetBoneIndex"]: row
        for row in mappings
        if row["mappingKind"] == "HelperOverride"
    }

    expected_frames: list[dict[str, Any]] = []
    for frame_index, source_local_payloads in enumerate(source_frames):
        source_locals = [
            _trs_matrix(transform) for transform in source_local_payloads
        ]
        source_globals: list[np.ndarray] = []
        for source_index, source_bone in enumerate(source_bones):
            parent = int(source_bone["parentIndex"])
            source_globals.append(
                source_locals[source_index]
                if parent < 0
                else source_globals[parent] @ source_locals[source_index]
            )

        target_locals = list(target_bind_locals)
        target_globals: list[np.ndarray] = []
        for target_index, target_bone in enumerate(target_bones):
            parent = int(target_bone["parentIndex"])
            row = base_by_target.get(target_index)
            if row is not None:
                source_index = int(row["sourceBoneIndex"])
                desired_global = (
                    source_globals[source_index]
                    @ np.linalg.inv(source_bind_globals[source_index])
                    @ target_bind_globals[target_index]
                )
                target_locals[target_index] = (
                    desired_global
                    if parent < 0
                    else np.linalg.inv(target_globals[parent])
                    @ desired_global
                )
            target_globals.append(
                target_locals[target_index]
                if parent < 0
                else target_globals[parent] @ target_locals[target_index]
            )

        # Helper overrides run after the complete body solve. Rebuilding
        # globals in hierarchy order propagates helper-parent motion without
        # changing an unmapped helper's bind local.
        target_globals = []
        for target_index, target_bone in enumerate(target_bones):
            parent = int(target_bone["parentIndex"])
            row = helper_by_target.get(target_index)
            if row is not None:
                source_index = int(row["sourceBoneIndex"])
                candidate = evaluate_helper_target_local(
                    target_bind_locals[target_index],
                    _trs_matrix(source_bones[source_index]["bind"]),
                    source_locals[source_index],
                    "rest_relative",
                )
                merged = merge_helper_components(
                    local_matrix_to_anm2_values(
                        target_bind_locals[target_index]
                    ),
                    local_matrix_to_anm2_values(candidate),
                    (
                        "translation"
                        if row["componentPolicy"] == "Translation"
                        else "rotation_translation"
                    ),
                )
                target_locals[target_index] = (
                    anm2_values_to_local_matrix(merged)
                )
            target_globals.append(
                target_locals[target_index]
                if parent < 0
                else target_globals[parent] @ target_locals[target_index]
            )

        expected_frames.append(
            {
                "frame": frame_index,
                "sourceLocals": source_local_payloads,
                "expectedLocalMatrices": [
                    _matrix_payload(matrix) for matrix in target_locals
                ],
                "expectedGlobalMatrices": [
                    _matrix_payload(matrix) for matrix in target_globals
                ],
            }
        )

    return {
        "sourceBones": source_bones,
        "targetBones": target_bones,
        "mappings": mappings,
        "reviewedTargetBindBoneIndices": [4],
        "frames": expected_frames,
    }


def _root_policy_payload() -> dict[str, Any]:
    from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
    from dlanm2_gui.helper_retarget import local_matrix_to_anm2_values
    from dlanm2_gui.retarget_engines.mapped_rig import compose_local_matrix
    from dlanm2_gui.root_heading import apply_target_root_policy
    from dlanm2_gui.trackmap import dl_name_hash

    tilt = _quaternion_xyzw((1.0, 0.0, 0.0), 20.0)
    root_bind = _trs((0.0, 1.0, 0.0), tilt)
    input_transforms = [
        root_bind,
        _trs(
            (1.0, 1.5, -2.0),
            _quaternion_multiply_xyzw(
                _quaternion_xyzw((0.0, 1.0, 0.0), 45.0),
                tilt,
            ),
        ),
        _trs(
            (2.0, 0.75, -3.0),
            _quaternion_multiply_xyzw(
                _quaternion_xyzw((0.0, 1.0, 0.0), 90.0),
                tilt,
            ),
        ),
    ]
    root_descriptor = dl_name_hash("Bip01")
    root = ChromeRigBone(
        0,
        "Bip01",
        -1,
        root_descriptor,
        tuple(root_bind["translation"]),
        (
            root_bind["rotationXyzw"][3],
            root_bind["rotationXyzw"][0],
            root_bind["rotationXyzw"][1],
            root_bind["rotationXyzw"][2],
        ),
        tuple(root_bind["scale"]),
    )
    rig = ChromeRig(
        "parity:root-policy",
        "Parity root policy",
        "test",
        (root,),
        0,
        extra_track_descriptors=(MOTION_ACCUMULATOR_DESCRIPTOR,),
        track_descriptors=(root_descriptor, MOTION_ACCUMULATOR_DESCRIPTOR),
        extensions={"world_up_axis": [0.0, 1.0, 0.0]},
    )
    identity_row = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0]
    input_rows: list[list[list[float]]] = []
    for transform in input_transforms:
        quaternion = transform["rotationXyzw"]
        matrix = compose_local_matrix(
            list(transform["translation"]),
            [quaternion[3], quaternion[0], quaternion[1], quaternion[2]],
            list(transform["scale"]),
        )
        input_rows.append(
            [local_matrix_to_anm2_values(matrix), list(identity_row)]
        )

    modes: list[dict[str, Any]] = []
    for legacy in ("inplace", "bip01", "motion"):
        values = [
            [list(track) for track in frame]
            for frame in input_rows
        ]
        report = apply_target_root_policy(values, rig, "Bip01", legacy)
        modes.append(
            {
                "legacyPolicy": legacy,
                "resolvedMotionMode": report.resolved_motion_mode,
                "resolvedHeadingMode": report.resolved_heading_mode,
                "translationOwner": report.translation_owner,
                "headingOwner": report.heading_owner,
                "rootValues": [frame[0] for frame in values],
                "motionAccumulatorValues": [frame[1] for frame in values],
                "report": {
                    "sourceHeadingDegrees": report.source_heading_degrees,
                    "skeletalRootHeadingDegrees":
                        report.skeletal_root_heading_degrees,
                    "motionHeadingDegrees": report.motion_heading_degrees,
                    "sourcePlanarDisplacement":
                        list(report.source_planar_displacement),
                    "skeletalRootPlanarDisplacement":
                        list(report.skeletal_root_planar_displacement),
                    "motionPlanarDisplacement":
                        list(report.motion_planar_displacement),
                },
            }
        )

    return {
        "rootDescriptor": f"0x{root_descriptor:08X}",
        "motionAccumulatorDescriptor":
            f"0x{MOTION_ACCUMULATOR_DESCRIPTOR:08X}",
        "rootBind": root_bind,
        "inputRootTransforms": input_transforms,
        "modes": modes,
    }


def _mimic_payload() -> dict[str, Any]:
    from dlanm2_gui.anm2_components import decode_samples
    from dlanm2_gui.fbx_blendshapes import FbxBlendShapeCurve, FbxFacialScan
    from dlanm2_gui.mimic_builder import build_mimic_anm2
    from dlanm2_gui.mimic_profiles import (
        MimicMappingRow,
        MimicProfile,
        MimicTarget,
    )

    source_curves = [
        {
            "name": "jawOpen",
            "values": [0.0, 0.25, 0.5, 1.0],
        },
        {
            "name": "mouthOpen",
            "values": [0.0, 0.1, 0.2, 0.3],
        },
        {
            "name": "smileLeft",
            "values": [0.0, 0.2, -0.1, 0.4],
        },
    ]
    targets = [
        {
            "index": 0,
            "descriptor": 0x11111111,
            "name": "jaw",
            "label": "Jaw",
        },
        {
            "index": 1,
            "descriptor": 0x22222222,
            "name": "smile",
            "label": "Smile",
        },
    ]
    mappings = [
        {
            "sourceChannel": "jawOpen",
            "targetMorph": "jaw",
            "targetDescriptor": 0x11111111,
            "weight": 1.0,
        },
        {
            "sourceChannel": "mouthOpen",
            "targetMorph": "jaw",
            "targetDescriptor": 0x11111111,
            "weight": 0.5,
        },
        {
            "sourceChannel": "smileLeft",
            "targetMorph": "smile",
            "targetDescriptor": 0x22222222,
            "weight": 1.0,
        },
    ]
    scan = FbxFacialScan(
        source_path="synthetic_parity.fbx",
        animation_stack="Take 001",
        fps=30.0,
        frame_count=4,
        curves=tuple(
            FbxBlendShapeCurve(
                row["name"],
                index + 1,
                (row["name"],),
                tuple(row["values"]),
                min(row["values"]),
                max(row["values"]),
                True,
            )
            for index, row in enumerate(source_curves)
        ),
    )
    profile = MimicProfile(
        profile_id="parity:face",
        name="Parity face",
        targets=tuple(
            MimicTarget(
                row["index"],
                row["descriptor"],
                row["name"],
                row["label"],
            )
            for row in targets
        ),
    )
    build = build_mimic_anm2(
        scan,
        profile,
        mapping=[
            MimicMappingRow(
                row["sourceChannel"],
                row["targetDescriptor"],
                row["weight"],
            )
            for row in mappings
        ],
    )
    decoded = decode_samples(build.payload, [0.0, 1.0, 2.0, 3.0])
    return {
        "frameRate": {"numerator": 30, "denominator": 1},
        "frameCount": 4,
        "sourceCurves": source_curves,
        "targets": [
            {
                **row,
                "descriptorHex": f"0x{row['descriptor']:08X}",
            }
            for row in targets
        ],
        "mappings": [
            {
                **row,
                "targetDescriptorHex":
                    f"0x{row['targetDescriptor']:08X}",
            }
            for row in mappings
        ],
        "decodedTargetValues": [
            [float(track[3]) for track in frame.tracks]
            for frame in decoded.frames
        ],
        "payloadByteLength": len(build.payload),
        "payloadSha256": _sha256(build.payload),
        "consolidatedTargets": build.report["consolidated_targets"],
        "unmappedAnimatedShapes": build.report["unmapped_animated_shapes"],
    }


def _canonical_rpack_payload() -> dict[str, Any]:
    from dlanm2_gui.rp6l import (
        build_animation_library_rpack,
        extract_animation_library,
        parse_rp6l,
    )

    animations = {
        "a_clip": b"ANM2-A\x00\x01",
        "z_clip": b"ANM2-Z\xFE\xFF",
    }
    scripts = {
        "anims_man_all_DLC60": (b"HEADER-A\n", b"BODY-A\n"),
        "anims_player_dlc60": (b"HEADER-B\n", b"BODY-B\n"),
    }
    payload = build_animation_library_rpack(
        animation_resources=animations,
        animation_scripts=scripts,
    )
    parsed = parse_rp6l(payload)
    extracted = extract_animation_library(payload)
    if extracted.animations != animations or extracted.animation_scripts != scripts:
        raise AssertionError("Python canonical RP6L round trip changed payloads.")
    return {
        "animations": [
            {
                "name": name,
                "payloadBase64": base64.b64encode(data).decode("ascii"),
            }
            for name, data in animations.items()
        ],
        "animationScripts": [
            {
                "name": name,
                "headerBase64": base64.b64encode(sections[0]).decode("ascii"),
                "bodyBase64": base64.b64encode(sections[1]).decode("ascii"),
            }
            for name, sections in scripts.items()
        ],
        "containerBase64": base64.b64encode(payload).decode("ascii"),
        "byteLength": len(payload),
        "sha256": _sha256(payload),
        "manifest": {
            "version": parsed.version,
            "names": list(parsed.names),
            "chunkCount": len(parsed.chunks),
            "itemCount": len(parsed.items),
            "resources": [
                {
                    "name": parsed.names[resource.name_index],
                    "type": resource.resource_type,
                    "itemCount": resource.item_count,
                    "firstItemIndex": resource.first_item_index,
                }
                for resource in parsed.resources
            ],
        },
    }


def _animation_scr_parity_payload() -> dict[str, Any]:
    from dlanm2_gui.animation_scr import (
        ANIMATION_SCR_RECORD_SIZE,
        AnimationScrSequence,
        append_animation_scr_sequences,
        build_animation_scr_sections,
        parse_animation_scr_sections,
        patch_animation_scr_sequence_ranges,
    )

    source_sequences = (
        AnimationScrSequence(
            "Walk_B",
            "walk_b.anm2",
            0.0,
            29.0,
            30.0,
            enabled=1,
            blend=0.5,
        ),
        AnimationScrSequence(
            "Idle.Upper-01",
            "idle_upper.anm2",
            2.5,
            62.25,
            60.0,
            enabled=0,
            blend=0.25,
        ),
    )
    built = build_animation_scr_sections(source_sequences)
    patched = patch_animation_scr_sequence_ranges(
        built,
        {"WALK_B": (1.5, 42.5, 24.0)},
    )
    appended = append_animation_scr_sequences(
        patched,
        (
            AnimationScrSequence(
                "Attack_Z",
                "attack_z.anm2",
                0.0,
                12.0,
                30.0,
                blend=0.75,
            ),
            AnimationScrSequence(
                "attack_a",
                "attack_a.anm2",
                3.0,
                18.0,
                48.0,
                enabled=0,
                blend=-0.125,
            ),
        ),
    )

    def parsed_payload(sections: tuple[bytes, bytes]) -> dict[str, Any]:
        parsed = parse_animation_scr_sections(sections)
        return {
            "declaredSequenceCount": parsed.sequence_count,
            "nameTableOffset": parsed.name_table_offset,
            "sequences": [
                {
                    "name": sequence.name,
                    "nameOffset": sequence.name_offset,
                    "recordOffset": sequence.record_offset,
                    "enabled": sequence.enabled,
                    "blend": sequence.blend,
                    "framesPerSecond": sequence.fps,
                    "startFrame": sequence.start_frame,
                    "endFrame": sequence.end_frame,
                    "eventCount": sequence.event_count,
                }
                for sequence in parsed.sequences
            ],
        }

    def sections_payload(sections: tuple[bytes, bytes]) -> dict[str, Any]:
        section0, section1 = sections
        return {
            "section0Base64": base64.b64encode(section0).decode("ascii"),
            "section1Base64": base64.b64encode(section1).decode("ascii"),
            "section0Sha256": _sha256(section0),
            "section1Sha256": _sha256(section1),
            "parsed": parsed_payload(sections),
        }

    record_bytes = len(source_sequences) * ANIMATION_SCR_RECORD_SIZE
    auxiliary_section0 = (
        built[0][:record_bytes]
        + b"\xFE\xED\xFA\xCE\x00"
        + built[0][record_bytes:]
    )
    rejection_recipes: list[
        tuple[str, str, tuple[bytes, bytes], Any]
    ] = [
        (
            "section1-too-small",
            "parse",
            (built[0], built[1][:7]),
            lambda sections: parse_animation_scr_sections(sections),
        ),
        (
            "record-table-truncated",
            "parse",
            (built[0][: record_bytes - 1], built[1]),
            lambda sections: parse_animation_scr_sections(sections),
        ),
        (
            "name-table-missing",
            "parse",
            (built[0][:record_bytes], built[1]),
            lambda sections: parse_animation_scr_sections(sections),
        ),
        (
            "patch-missing-sequence",
            "patch",
            built,
            lambda sections: patch_animation_scr_sequence_ranges(
                sections,
                {"not_present": (0.0, 1.0, 30.0)},
            ),
        ),
        (
            "append-duplicate-sequence",
            "append",
            built,
            lambda sections: append_animation_scr_sequences(
                sections,
                (
                    AnimationScrSequence(
                        "WALK_B",
                        "walk_b.anm2",
                        0.0,
                        1.0,
                        30.0,
                    ),
                ),
            ),
        ),
        (
            "append-auxiliary-payload",
            "append",
            (auxiliary_section0, built[1]),
            lambda sections: append_animation_scr_sequences(
                sections,
                (
                    AnimationScrSequence(
                        "new_clip",
                        "new_clip.anm2",
                        0.0,
                        1.0,
                        30.0,
                    ),
                ),
            ),
        ),
    ]
    rejected_inputs: list[dict[str, Any]] = []
    for case_id, operation, sections, action in rejection_recipes:
        try:
            action(sections)
        except (ValueError, NotImplementedError) as exception:
            rejected_inputs.append(
                {
                    "id": case_id,
                    "operation": operation,
                    "section0Base64": base64.b64encode(
                        sections[0]
                    ).decode("ascii"),
                    "section1Base64": base64.b64encode(
                        sections[1]
                    ).decode("ascii"),
                    "pythonExceptionType": type(exception).__name__,
                    "pythonMessage": str(exception),
                }
            )
        else:
            raise AssertionError(
                f"Python AnimationScr unexpectedly accepted {case_id}."
            )

    invalid_magic = bytearray(built[0])
    invalid_magic[4:8] = b"\0\0\0\0"
    skipped_record = parse_animation_scr_sections(
        (bytes(invalid_magic), built[1])
    )
    if len(skipped_record.sequences) != len(source_sequences) - 1:
        raise AssertionError(
            "Python AnimationScr invalid-record skip behavior changed."
        )

    return {
        "recipe": (
            "ASCII no-event DL1 records; case-insensitive sort/lookup; "
            "exact build, range patch, append, invalid-record skip, and "
            "six bounded malformed-input controls"
        ),
        "recordSize": ANIMATION_SCR_RECORD_SIZE,
        "sourceSequences": [
            {
                "name": sequence.name,
                "anm2Name": sequence.anm2_name,
                "startFrame": sequence.start_frame,
                "endFrame": sequence.end_frame,
                "framesPerSecond": sequence.fps,
                "enabled": sequence.enabled,
                "blend": sequence.blend,
            }
            for sequence in source_sequences
        ],
        "built": sections_payload(built),
        "patched": sections_payload(patched),
        "appended": sections_payload(appended),
        "invalidMagicAcceptedWithSkippedRecord": {
            "section0Base64": base64.b64encode(
                bytes(invalid_magic)
            ).decode("ascii"),
            "section1Base64": base64.b64encode(
                built[1]
            ).decode("ascii"),
            "parsedSequenceNames": [
                sequence.name for sequence in skipped_record.sequences
            ],
        },
        "rejectedInputs": rejected_inputs,
    }


def build_oracle(_repository_root: Path) -> dict[str, Any]:
    return {
        "format": FORMAT,
        "scope": {
            "game": "Dying Light 1",
            "matrixAbsoluteTolerance": 1.0e-9,
            "rootValueAbsoluteTolerance": 1.0e-8,
            "mimicValueAbsoluteTolerance": 0.002,
            "maximumOracleBytes": MAX_ORACLE_BYTES,
            "notes": [
                "FBX coverage is limited to the listed Euler, pivot, hierarchy, and linear-curve recipes.",
                "Retarget coverage proves the shared global bind-basis correction and reviewed target-bind fallback, not every mapping heuristic.",
                "Helper coverage proves two-frame body plus RefCamera/EyeCamera fan-out with target-specific binds and component ownership.",
                "Root coverage proves the listed three DL1 ownership modes on a root-level synthetic control.",
                "Mimic coverage compares mapped and consolidated scalar values after each implementation encodes and decodes its own ANM2; bytes are not claimed identical.",
                "AnimationScr coverage compares exact no-event section bytes plus normalized parse, range patch, append, record skip, and six malformed-input decisions.",
                "RP6L coverage compares exact bytes only for the canonical sorted uncompressed animation-library recipe.",
                "No retail or proprietary game payload is embedded.",
            ],
        },
        "fbxTransformEvaluation": _fbx_transform_payload(),
        "bindBasisRetarget": _bind_basis_retarget_payload(),
        "helperFanoutRetarget": _helper_fanout_payload(),
        "rootHelperOwnership": _root_policy_payload(),
        "mimicScalars": _mimic_payload(),
        "animationScr": _animation_scr_parity_payload(),
        "canonicalAnimationRpack": _canonical_rpack_payload(),
    }


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Emit the bounded DL1 semantic Python parity oracle consumed by C# tests."
        )
    )
    parser.add_argument(
        "--repository-root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
    )
    parser.add_argument("--output", type=Path)
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    repository_root = args.repository_root.resolve()
    sys.path.insert(0, str(repository_root))
    payload = build_oracle(repository_root)
    text = json.dumps(
        payload,
        ensure_ascii=False,
        indent=2,
        sort_keys=True,
        allow_nan=False,
    ) + "\n"
    encoded = text.encode("utf-8")
    if len(encoded) > MAX_ORACLE_BYTES:
        raise ValueError(
            f"Semantic parity oracle is {len(encoded)} bytes; "
            f"maximum is {MAX_ORACLE_BYTES}."
        )
    if args.output is None:
        sys.stdout.write(text)
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text, encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
