from pathlib import Path

import numpy as np

from dlanm2_gui.model_importer.fbx_model import (
    FbxCluster,
    FbxNode,
    FbxScene,
    _axis_rotation,
)


def _model_node_with_x_rotation(degrees: float) -> FbxNode:
    rotation = FbxNode(
        "P",
        ["Lcl Rotation", "Lcl Rotation", "", "A", degrees, 0.0, 0.0],
        [],
        0,
        0,
    )
    properties = FbxNode("Properties70", [], [rotation], 0, 0)
    return FbxNode("Model", [1, "Model::Mesh", "Mesh"], [properties], 0, 0)


def _scene(*, bind_pose: np.ndarray | None, model_rotation: float = -90.0) -> FbxScene:
    return FbxScene(
        path=Path("synthetic.fbx"),
        version=7400,
        top={},
        object_by_id={1: _model_node_with_x_rotation(model_rotation)},
        parents={},
        children={},
        model_ids=(1,),
        limb_ids=(),
        model_names={1: "Mesh"},
        model_subtypes={1: "Mesh"},
        material_names={},
        bind_pose_matrices=({1: bind_pose} if bind_pose is not None else {}),
        geometries=(),
        animation_stacks=(),
        blend_shape_names=(),
        axis_settings={},
        meters_per_unit=0.01,
    )


def _cluster(
    *,
    transform: np.ndarray | None,
    transform_link: np.ndarray | None,
    associate: np.ndarray | None,
) -> FbxCluster:
    return FbxCluster(
        object_id=10,
        name="Bone",
        bone_id=2,
        bone_name="Bone",
        indexes=(0,),
        weights=(1.0,),
        transform=transform,
        transform_link=transform_link,
        transform_associate_model=associate,
    )


def test_mesh_bind_uses_model_pose_not_first_cluster_transform() -> None:
    mesh_bind = _axis_rotation("X", -90.0)
    scene = _scene(bind_pose=mesh_bind)
    cluster = _cluster(
        transform=np.eye(4),
        transform_link=mesh_bind,
        associate=mesh_bind,
    )

    resolved = scene._resolve_geometry_mesh_bind(
        geometry_name="Body",
        model_id=1,
        clusters=(cluster,),
    )

    np.testing.assert_allclose(resolved, mesh_bind, atol=1.0e-12)
    assert not np.allclose(resolved, cluster.transform)
    assert not any("mesh-bind sources disagree" in warning for warning in scene.warnings)


def test_mesh_bind_reconstructs_from_transform_link_and_transform() -> None:
    mesh_bind = _axis_rotation("X", -90.0)
    scene = _scene(bind_pose=None)
    cluster = _cluster(
        transform=np.eye(4),
        transform_link=mesh_bind,
        associate=None,
    )

    resolved = scene._resolve_geometry_mesh_bind(
        geometry_name="Body",
        model_id=None,
        clusters=(cluster,),
    )

    np.testing.assert_allclose(resolved, mesh_bind, atol=1.0e-12)


def test_cluster_consensus_outvotes_a_mismatched_model_bind_pose() -> None:
    mesh_bind = _axis_rotation("X", -90.0)
    bad_pose = mesh_bind.copy()
    bad_pose[0, 3] = 100.0
    scene = _scene(bind_pose=bad_pose)
    cluster = _cluster(
        transform=np.eye(4),
        transform_link=mesh_bind,
        associate=mesh_bind,
    )

    resolved = scene._resolve_geometry_mesh_bind(
        geometry_name="Body",
        model_id=1,
        clusters=(cluster,),
    )

    np.testing.assert_allclose(resolved, mesh_bind, atol=1.0e-12)
    assert any(
        "using TransformAssociateModel" in warning
        and "mesh Model BindPose" in warning
        for warning in scene.warnings
    )


def test_fbx_cluster_constructor_remains_backward_compatible() -> None:
    cluster = FbxCluster(10, "Bone", 2, "Bone", (0,), (1.0,), None, None)
    assert cluster.transform_associate_model is None
