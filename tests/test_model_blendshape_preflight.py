from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.fbx_core import FbxDocument
from dlanm2_gui.fbx_preflight import preflight_fbx
from dlanm2_gui.model_importer.fbx_model import (
    BLENDSHAPE_IDENTITY_NOOP,
    BLENDSHAPE_MALFORMED,
    BLENDSHAPE_REAL_ANIMATED,
    BLENDSHAPE_REAL_STATIC,
    FbxGeometry,
    FbxNode,
    FbxScene,
    FbxTriangle,
    FbxTriangleCorner,
)
from dlanm2_gui.model_importer.msh_builder import (
    ModelBuildOptions,
    build_source_from_fbx,
)


def _property(name: str, *values: float | int) -> FbxNode:
    return FbxNode("P", [name, name, "", "A", *values], [], 0, 0)


def _root_node() -> FbxNode:
    properties = FbxNode(
        "Properties70",
        [],
        [
            _property("Lcl Translation", 0.0, 0.0, 0.0),
            _property("Lcl Rotation", 0.0, 0.0, 0.0),
            _property("Lcl Scaling", 1.0, 1.0, 1.0),
        ],
        0,
        0,
    )
    return FbxNode("Model", [1, "Model::root", "LimbNode"], [properties], 0, 0)


def _array_node(name: str, values: list[float] | list[int]) -> FbxNode:
    return FbxNode(name, [values], [], 0, 0)


def _scene_with_shape(
    path: Path,
    *,
    name: str = "Placeholder",
    indexes: tuple[int, ...] = (0,),
    positions: tuple[float, ...] = (0.0, 0.0, 0.0),
    normals: tuple[float, ...] = (),
    default_weight: float = 0.0,
    curve_times: tuple[int, ...] = (0,),
    curve_values: tuple[float, ...] = (0.0,),
    include_shape: bool = True,
) -> FbxScene:
    path.write_bytes(b"synthetic model fbx")
    root = _root_node()
    base = FbxNode("Geometry", [10, "Geometry::Body", "Mesh"], [], 0, 0)
    blend = FbxNode(
        "Deformer",
        [20, "Deformer::Body", "BlendShape"],
        [FbxNode("Version", [100], [], 0, 0)],
        0,
        0,
    )
    channel = FbxNode(
        "Deformer",
        [30, f"SubDeformer::{name}", "BlendShapeChannel"],
        [
            FbxNode("Version", [100], [], 0, 0),
            FbxNode("DeformPercent", [default_weight], [], 0, 0),
            _array_node("FullWeights", [100.0]),
        ],
        0,
        0,
    )
    shape = FbxNode(
        "Geometry",
        [40, f"Geometry::{name}", "Shape"],
        [
            FbxNode("Version", [100], [], 0, 0),
            _array_node("Indexes", list(indexes)),
            _array_node("Vertices", list(positions)),
            _array_node("Normals", list(normals)),
        ],
        0,
        0,
    )
    curve_node = FbxNode(
        "AnimationCurveNode",
        [50, "AnimCurveNode::DeformPercent", ""],
        [],
        0,
        0,
    )
    curve = FbxNode(
        "AnimationCurve",
        [60, "AnimCurve::DeformPercent", ""],
        [
            _array_node("KeyTime", list(curve_times)),
            _array_node("KeyValueFloat", list(curve_values)),
        ],
        0,
        0,
    )
    nodes = [root, base, blend, channel, curve_node, curve]
    if include_shape:
        nodes.append(shape)

    connections = [
        ("OO", 20, 10, []),
        ("OO", 30, 20, []),
        ("OP", 50, 30, ["DeformPercent"]),
        ("OP", 60, 50, ["d|DeformPercent"]),
    ]
    if include_shape:
        connections.append(("OO", 40, 30, []))
    parents: dict[int, list[tuple[str, int, list[object]]]] = {}
    children: dict[int, list[tuple[str, int, list[object]]]] = {}
    for kind, child_id, parent_id, rest in connections:
        parents.setdefault(child_id, []).append((kind, parent_id, rest))
        children.setdefault(parent_id, []).append((kind, child_id, rest))

    corners = tuple(
        FbxTriangleCorner(control_point_index=index, polygon_vertex_index=index)
        for index in range(3)
    )
    geometry = FbxGeometry(
        object_id=10,
        name="Body",
        model_id=None,
        model_name="Body",
        control_points=np.asarray(
            ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0)),
            dtype=float,
        ),
        polygons=(corners,),
        triangles=(FbxTriangle(0, corners),),
        layers={},
        material_ids=(),
        material_names=(),
        clusters=(),
        mesh_bind_global=np.eye(4, dtype=float),
        geometric_transform=np.eye(4, dtype=float),
    )
    object_by_id = {int(node.properties[0]): node for node in nodes}
    scene = FbxScene(
        path=path,
        version=7400,
        top={"Objects": FbxNode("Objects", [], nodes, 0, 0)},
        object_by_id=object_by_id,
        parents=parents,
        children=children,
        model_ids=(1,),
        limb_ids=(1,),
        model_names={1: "root"},
        model_subtypes={1: "LimbNode"},
        material_names={},
        bind_pose_matrices={},
        geometries=(geometry,),
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
    scene.blend_shapes = scene._read_blend_shapes()
    scene.blend_shape_names = scene._read_blend_shape_names()
    geometry.blend_shapes = tuple(
        row for row in scene.blend_shapes if row.base_geometry_id == geometry.object_id
    )
    geometry.blend_shape_names = tuple(row.channel_name for row in geometry.blend_shapes)
    return scene


def _preflight(scene: FbxScene, *, morph_support: bool = False):
    document = FbxDocument.from_scene(scene)
    return preflight_fbx(
        scene.path,
        purpose="model",
        document=document,
        model_morph_support_enabled=morph_support,
    )


def test_sparse_zero_shape_is_allowed_and_reported(tmp_path: Path) -> None:
    scene = _scene_with_shape(tmp_path / "sparse_zero.fbx")

    report = _preflight(scene)
    target = scene.blend_shapes[0]

    assert target.classification == BLENDSHAPE_IDENTITY_NOOP
    assert target.control_point_indexes == (0,)
    assert target.position_deltas == ((0.0, 0.0, 0.0),)
    assert target.animation_curve_times == (0,)
    assert target.animation_curve_values == (0.0,)
    assert not report.blocking
    finding = next(
        row
        for row in report.findings
        if row.code == "ignored_identity_model_blend_shape"
    )
    assert finding.detected == (
        "Ignored identity blendshape Placeholder: the target contains no position "
        "deformation and its weight remains zero."
    )


def test_zero_position_with_small_normal_noise_is_allowed(tmp_path: Path) -> None:
    scene = _scene_with_shape(
        tmp_path / "normal_noise.fbx",
        normals=(1.1e-6, 0.0, 0.0),
    )

    report = _preflight(scene)
    target = scene.blend_shapes[0]

    assert target.classification == BLENDSHAPE_IDENTITY_NOOP
    assert target.maximum_position_delta == 0.0
    assert target.maximum_normal_delta == pytest.approx(1.1e-6)
    assert not report.blocking


def test_nonzero_position_is_real_and_respects_morph_capability(tmp_path: Path) -> None:
    scene = _scene_with_shape(
        tmp_path / "real_position.fbx",
        positions=(0.001, 0.0, 0.0),
    )

    unsupported = _preflight(scene)
    supported = _preflight(scene, morph_support=True)

    assert scene.blend_shapes[0].classification == BLENDSHAPE_REAL_STATIC
    assert unsupported.blocking
    assert any(
        row.code == "unsupported_model_blend_shapes"
        for row in unsupported.findings
    )
    assert not supported.blocking
    assert any(
        row.code == "supported_model_blend_shapes"
        for row in supported.findings
    )


def test_changing_nonzero_curve_is_not_identity(tmp_path: Path) -> None:
    scene = _scene_with_shape(
        tmp_path / "animated_weight.fbx",
        curve_times=(0, 46_186_158_000),
        curve_values=(0.0, 25.0),
    )

    report = _preflight(scene)
    target = scene.blend_shapes[0]

    assert target.classification == BLENDSHAPE_REAL_ANIMATED
    assert target.curve_changes
    assert report.blocking
    assert report.inventory["ignored_identity_blendshapes"] == []


def test_noop_target_with_unrelated_name_is_allowed(tmp_path: Path) -> None:
    scene = _scene_with_shape(tmp_path / "different_name.fbx", name="UnusedSmileSlot")

    report = _preflight(scene)

    assert scene.blend_shapes[0].classification == BLENDSHAPE_IDENTITY_NOOP
    assert not report.blocking
    assert report.inventory["ignored_identity_blendshapes"][0]["name"] == (
        "UnusedSmileSlot"
    )


def test_identity_like_name_with_real_deformation_is_not_ignored(
    tmp_path: Path,
) -> None:
    scene = _scene_with_shape(
        tmp_path / "real_named_placeholder.fbx",
        name="V_None",
        positions=(0.01, 0.0, 0.0),
    )

    report = _preflight(scene)

    assert scene.blend_shapes[0].classification == BLENDSHAPE_REAL_STATIC
    assert report.blocking
    assert report.inventory["ignored_identity_blendshapes"] == []
    assert "V_None (real_static_morph)" in next(
        row.detected
        for row in report.findings
        if row.code == "unsupported_model_blend_shapes"
    )


def test_malformed_sparse_index_is_blocked_with_exact_context(
    tmp_path: Path,
) -> None:
    scene = _scene_with_shape(
        tmp_path / "bad_sparse_index.fbx",
        name="BrokenTarget",
        indexes=(99,),
    )

    report = _preflight(scene)
    target = scene.blend_shapes[0]

    assert target.classification == BLENDSHAPE_MALFORMED
    assert report.blocking
    finding = next(
        row for row in report.findings if row.code == "malformed_model_blend_shape"
    )
    assert "BrokenTarget" in finding.detected
    assert "Body" in finding.detected
    assert "Indexes[0]" in finding.detected
    assert "99" in finding.detected
    assert "0..2" in finding.detected


def test_identity_target_leaves_non_morph_model_payload_unchanged(
    tmp_path: Path,
) -> None:
    plain_scene = _scene_with_shape(
        tmp_path / "plain.fbx",
        include_shape=False,
    )
    # Remove the intentionally malformed orphan channel as well, yielding a
    # genuinely non-morph scene with otherwise identical geometry.
    plain_scene.blend_shapes = ()
    plain_scene.blend_shape_names = ()
    identity_scene = _scene_with_shape(
        tmp_path / "identity.fbx",
        name="UnusedPlaceholder",
        normals=(1.1e-6, 0.0, 0.0),
    )
    options = ModelBuildOptions(resource_name="same_model", mode="static")

    plain = build_source_from_fbx(plain_scene, options)
    identity = build_source_from_fbx(
        identity_scene,
        ModelBuildOptions(resource_name="same_model", mode="static"),
    )

    assert plain.source.build() == identity.source.build()
    assert plain.report["ignored_identity_blendshapes"] == []
    assert identity.report["ignored_identity_blendshapes"] == [
        {
            "name": "UnusedPlaceholder",
            "geometry": "Body",
            "maximum_position_delta": 0.0,
            "maximum_normal_delta": pytest.approx(1.1e-6),
            "default_weight": 0.0,
            "curve_key_count": 1,
            "reason": (
                "the target contains no position deformation and its weight remains zero"
            ),
        }
    ]
