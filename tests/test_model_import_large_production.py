from __future__ import annotations

from collections import defaultdict
from pathlib import Path

import numpy as np

from dlanm2_gui.model_importer.fbx_model import (
    FbxCluster,
    FbxGeometry,
    FbxLayerElement,
    FbxNode,
    FbxScene,
    FbxTriangle,
    FbxTriangleCorner,
)
from dlanm2_gui.model_importer.msh_builder import (
    ModelBuildOptions,
    build_source_from_fbx,
)
from dlanm2_gui.model_importer.vendor.chrome_mesh_tools.msh import MshFile
from dlanm2_gui.model_importer.vendor.chrome_mesh_tools.source_contract import (
    audit_source_msh_bytes_for_compiler,
)


def _fbx_property(name: str, *values: float | int) -> FbxNode:
    return FbxNode("P", [name, name, "", "A", *values], [], 0, 0)


def _fbx_model_node(
    object_id: int,
    name: str,
    subtype: str,
    *,
    translation: tuple[float, float, float] = (0.0, 0.0, 0.0),
) -> FbxNode:
    properties = FbxNode(
        "Properties70",
        [],
        [
            _fbx_property("Lcl Translation", *translation),
            _fbx_property("Lcl Rotation", 0.0, 0.0, 0.0),
            _fbx_property("Lcl Scaling", 1.0, 1.0, 1.0),
            _fbx_property("RotationOrder", 0),
        ],
        0,
        0,
    )
    return FbxNode(
        "Model",
        [object_id, f"Model::{name}", subtype],
        [properties],
        0,
        0,
    )


def _scene_with_chain_skeleton(
    path: Path,
    *,
    bone_count: int,
    mesh_names: tuple[str, ...],
) -> tuple[FbxScene, tuple[int, ...], tuple[int, ...]]:
    """Build the same in-memory FbxScene consumed by the production importer."""

    path.write_bytes(b"offline synthetic production-path FBX scene\n")
    bone_ids = tuple(1_000 + index for index in range(bone_count))
    mesh_ids = tuple(10_000 + index for index in range(len(mesh_names)))
    bone_nodes = tuple(
        _fbx_model_node(
            object_id,
            f"rig_bone_{index:03d}",
            "LimbNode",
            translation=(0.0, 0.0, 0.0) if index == 0 else (0.01, 0.0, 0.0),
        )
        for index, object_id in enumerate(bone_ids)
    )
    mesh_nodes = tuple(
        _fbx_model_node(object_id, name, "Mesh")
        for object_id, name in zip(mesh_ids, mesh_names)
    )
    model_nodes = (*bone_nodes, *mesh_nodes)
    parents = {
        bone_ids[index]: [("OO", bone_ids[index - 1], [])]
        for index in range(1, len(bone_ids))
    }
    children = {
        bone_ids[index]: [("OO", bone_ids[index + 1], [])]
        for index in range(len(bone_ids) - 1)
    }
    scene = FbxScene(
        path=path,
        version=7400,
        top={"Objects": FbxNode("Objects", [], list(model_nodes), 0, 0)},
        object_by_id={int(node.properties[0]): node for node in model_nodes},
        parents=parents,
        children=children,
        model_ids=tuple((*bone_ids, *mesh_ids)),
        limb_ids=bone_ids,
        model_names={
            **{
                object_id: f"rig_bone_{index:03d}"
                for index, object_id in enumerate(bone_ids)
            },
            **dict(zip(mesh_ids, mesh_names)),
        },
        model_subtypes={
            **{object_id: "LimbNode" for object_id in bone_ids},
            **{object_id: "Mesh" for object_id in mesh_ids},
        },
        material_names={},
        bind_pose_matrices={},
        geometries=(),
        animation_stacks=(),
        blend_shape_names=(),
        axis_settings={},
        meters_per_unit=1.0,
    )
    native_globals = scene.model_global_matrices(bone_ids)
    scene.bind_pose_matrices = {
        object_id: native_globals[object_id].copy() for object_id in bone_ids
    }
    return scene, bone_ids, mesh_ids


def _skinned_geometry(
    scene: FbxScene,
    *,
    geometry_id: int,
    mesh_id: int,
    mesh_name: str,
    bone_ids: tuple[int, ...],
    control_points: list[tuple[float, float, float]],
    control_point_bones: list[int],
    material_slots: list[int],
) -> FbxGeometry:
    assert len(control_points) == len(control_point_bones)
    assert len(control_points) % 3 == 0
    assert len(material_slots) == len(control_points) // 3

    polygons: list[tuple[FbxTriangleCorner, ...]] = []
    triangles: list[FbxTriangle] = []
    for polygon_index in range(len(control_points) // 3):
        start = polygon_index * 3
        corners = tuple(
            FbxTriangleCorner(start + corner, start + corner)
            for corner in range(3)
        )
        polygons.append(corners)
        triangles.append(FbxTriangle(polygon_index, corners))

    indexes_by_bone: dict[int, list[int]] = defaultdict(list)
    for control_point_index, physical_bone_index in enumerate(control_point_bones):
        indexes_by_bone[physical_bone_index].append(control_point_index)
    clusters = tuple(
        FbxCluster(
            object_id=geometry_id * 1_000 + physical_bone_index,
            name=f"skin_rig_bone_{physical_bone_index:03d}",
            bone_id=bone_ids[physical_bone_index],
            bone_name=f"rig_bone_{physical_bone_index:03d}",
            indexes=tuple(indexes),
            weights=tuple(1.0 for _ in indexes),
            transform=np.eye(4, dtype=float),
            transform_link=scene.bind_pose_matrices[
                bone_ids[physical_bone_index]
            ].copy(),
        )
        for physical_bone_index, indexes in sorted(indexes_by_bone.items())
    )
    material_names = (
        f"{mesh_name}_skin_a",
        f"{mesh_name}_skin_b",
    )
    return FbxGeometry(
        object_id=geometry_id,
        name=f"{mesh_name}_geometry",
        model_id=mesh_id,
        model_name=mesh_name,
        control_points=np.asarray(control_points, dtype=float),
        polygons=tuple(polygons),
        triangles=tuple(triangles),
        layers={
            "LayerElementMaterial": [
                FbxLayerElement(
                    kind="LayerElementMaterial",
                    index=0,
                    name="materials",
                    mapping="ByPolygon",
                    reference="Direct",
                    direct=list(material_slots),
                    indices=[],
                    tuple_size=1,
                )
            ]
        },
        material_ids=(geometry_id * 2, geometry_id * 2 + 1),
        material_names=material_names,
        clusters=clusters,
        mesh_bind_global=np.eye(4, dtype=float),
        geometric_transform=np.eye(4, dtype=float),
    )


def _large_exact_scene(
    path: Path,
) -> tuple[FbxScene, dict[tuple[float, float, float], int]]:
    bone_count = 320
    batches = [
        tuple(range(start, min(start + 30, bone_count)))
        for start in range(0, bone_count, 30)
    ]
    mesh_names = tuple(f"skin_region_{index:02d}" for index in range(len(batches)))
    scene, bone_ids, mesh_ids = _scene_with_chain_skeleton(
        path,
        bone_count=bone_count,
        mesh_names=mesh_names,
    )

    expected_global_by_position: dict[tuple[float, float, float], int] = {}
    geometries: list[FbxGeometry] = []
    global_triangle_index = 0
    for region_index, physical_bones in enumerate(batches):
        control_point_bones = list(physical_bones)
        while len(control_point_bones) % 3:
            control_point_bones.append(control_point_bones[-1])
        triangle_count = len(control_point_bones) // 3
        material_zero_triangles = 5 if triangle_count == 10 else 3
        material_slots = [0] * material_zero_triangles + [1] * (
            triangle_count - material_zero_triangles
        )
        control_points: list[tuple[float, float, float]] = []
        for _triangle_index in range(triangle_count):
            base = float(global_triangle_index * 4)
            points = (
                (base, 0.0, 0.0),
                (base + 1.0, 0.0, 0.0),
                (base, 1.0, 0.0),
            )
            control_points.extend(points)
            global_triangle_index += 1
        for position, physical_bone_index in zip(
            control_points, control_point_bones
        ):
            expected_global_by_position[position] = physical_bone_index
        geometries.append(
            _skinned_geometry(
                scene,
                geometry_id=20_000 + region_index,
                mesh_id=mesh_ids[region_index],
                mesh_name=mesh_names[region_index],
                bone_ids=bone_ids,
                control_points=control_points,
                control_point_bones=control_point_bones,
                material_slots=material_slots,
            )
        )
    scene.geometries = tuple(geometries)
    scene.mesh_bind_source_by_geometry = {
        geometry.name: "synthetic authoritative identity mesh bind"
        for geometry in geometries
    }
    return scene, expected_global_by_position


def _vertex_boundary_scene(path: Path) -> FbxScene:
    scene, bone_ids, mesh_ids = _scene_with_chain_skeleton(
        path,
        bone_count=1,
        mesh_names=("uint16_vertex_boundary",),
    )
    triangle_count = 65_535 // 3 + 1
    polygons: list[tuple[FbxTriangleCorner, ...]] = []
    triangles: list[FbxTriangle] = []
    uv_direct: list[float] = []
    for polygon_index in range(triangle_count):
        corners = tuple(
            FbxTriangleCorner(corner, polygon_index * 3 + corner)
            for corner in range(3)
        )
        polygons.append(corners)
        triangles.append(FbxTriangle(polygon_index, corners))
        offset = float(polygon_index * 2)
        uv_direct.extend((offset, 0.0, offset + 1.0, 0.0, offset, 1.0))

    geometry = FbxGeometry(
        object_id=30_000,
        name="uint16_vertex_boundary_geometry",
        model_id=mesh_ids[0],
        model_name="uint16_vertex_boundary",
        control_points=np.asarray(
            ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0)),
            dtype=float,
        ),
        polygons=tuple(polygons),
        triangles=tuple(triangles),
        layers={
            "LayerElementUV": [
                FbxLayerElement(
                    kind="LayerElementUV",
                    index=0,
                    name="unique_uv_per_corner",
                    mapping="ByPolygonVertex",
                    reference="Direct",
                    direct=uv_direct,
                    indices=[],
                    tuple_size=2,
                )
            ]
        },
        material_ids=(),
        material_names=(),
        clusters=(
            FbxCluster(
                object_id=30_001,
                name="root_skin",
                bone_id=bone_ids[0],
                bone_name="rig_bone_000",
                indexes=(0, 1, 2),
                weights=(1.0, 1.0, 1.0),
                transform=np.eye(4, dtype=float),
                transform_link=scene.bind_pose_matrices[bone_ids[0]].copy(),
            ),
        ),
        mesh_bind_global=np.eye(4, dtype=float),
        geometric_transform=np.eye(4, dtype=float),
    )
    scene.geometries = (geometry,)
    scene.mesh_bind_source_by_geometry = {
        geometry.name: "synthetic authoritative identity mesh bind"
    }
    return scene


def _position_key(position: tuple[float, float, float]) -> tuple[float, float, float]:
    return tuple(round(float(value), 8) for value in position)


def test_exact_fbx_build_accepts_320_weighted_nodes_with_local_palettes(
    tmp_path: Path,
) -> None:
    scene, expected_global_by_position = _large_exact_scene(
        tmp_path / "large_exact_scene.fbx"
    )
    options = ModelBuildOptions(
        "large_exact_production",
        mode="exact_rig",
        material_mode="preserve_slots",
        orientation_policy="none",
    )

    first = build_source_from_fbx(scene, options)
    second = build_source_from_fbx(
        scene,
        ModelBuildOptions(
            "large_exact_production",
            mode="exact_rig",
            material_mode="preserve_slots",
            orientation_policy="none",
        ),
    )

    first.source.validate()
    assert first.authored_rig_contract is not None
    contract_validation = first.authored_rig_contract.validate()
    assert contract_validation["status"] == "pass"
    assert contract_validation["node_count"] == len(first.source.nodes) == 343
    assert first.report["total_hierarchy_node_count"] == 343
    assert first.report["animation_entity_prefix_length"] == 320
    assert first.report["bone_count"] == 320
    assert first.report["helper_count"] == 0
    assert first.report["geometry_node_count"] == 22

    partition_report = first.report["skin_partitions"]
    assert partition_report["partition_count"] == 22
    assert partition_report["maximum_local_palette_size"] == 15
    assert all(
        0 < row["palette_size"] < 32
        for row in partition_report["partitions"]
    )
    assert all(
        row["global_nodes"] == sorted(row["global_nodes"])
        for row in partition_report["partitions"]
    )
    assert len({row["material_index"] for row in partition_report["partitions"]}) == 22

    resolved_global_nodes: set[int] = set()
    resolved_high_global_node = False
    emitted_vertex_count = 0
    for node in first.source.nodes:
        if node.node_type != 2:
            continue
        assert len(node.lods) == 1
        lod = node.lods[0]
        assert len(lod.subsets) == 1
        subset = lod.subsets[0]
        assert 0 < len(subset.bone_palette) < 32
        for vertex_index, skin in enumerate(lod.skin_vertices):
            expected_global = expected_global_by_position[
                _position_key(lod.positions[vertex_index])
            ]
            assert len(skin.bone_indices) == 1
            local_index = int(skin.bone_indices[0])
            assert 0 <= local_index <= 0xFF
            assert local_index < len(subset.bone_palette)
            resolved_global = int(subset.bone_palette[local_index])
            assert resolved_global == expected_global
            resolved_global_nodes.add(resolved_global)
            resolved_high_global_node |= resolved_global > 0xFF
            emitted_vertex_count += 1

    assert emitted_vertex_count == len(expected_global_by_position) == 321
    assert resolved_global_nodes == set(range(320))
    assert resolved_high_global_node is True
    assert first.report["model_bind_cpu_skin_validation"]["status"] == "pass"
    assert first.report["model_bind_cpu_skin_validation"][
        "palette_resolution_count"
    ] == 321

    first_payload = first.source.build()
    second_payload = second.source.build()
    assert first_payload == second_payload
    assert first.report["skin_partitions"] == second.report["skin_partitions"]
    assert first.report["total_hierarchy_node_count"] == second.report[
        "total_hierarchy_node_count"
    ]
    assert first.authored_rig_contract.to_dict() == second.authored_rig_contract.to_dict()

    # Prove the serialized uint8 fields, not only the in-memory SourceMsh rows,
    # resolve through their current subset palette to the authored global node.
    parsed = MshFile.parse(first_payload, "large_exact_production.msh")
    serialized_resolved_global_nodes: set[int] = set()
    serialized_vertex_count = 0
    for node in parsed.nodes:
        if node.node_type != 2:
            continue
        lod = node.lods[0]
        subset = lod.subsets[0]
        for vertex_index, skin in enumerate(lod.skin_vertices):
            expected_global = expected_global_by_position[
                _position_key(lod.positions[vertex_index])
            ]
            local_index = int(skin.bone_indices[0])
            assert local_index < len(subset.bone_palette)
            resolved_global = int(subset.bone_palette[local_index])
            assert resolved_global == expected_global
            serialized_resolved_global_nodes.add(resolved_global)
            serialized_vertex_count += 1
    assert serialized_vertex_count == 321
    assert serialized_resolved_global_nodes == set(range(320))

    audit = audit_source_msh_bytes_for_compiler(
        first_payload,
        "large_exact_production.msh",
    )
    assert audit["ready"] is True, audit["errors"]
    assert audit["node_count"] == 343
    assert audit["node_type_counts"]["BONE"] == 320
    assert audit["node_type_counts"]["MESH_VBLEND"] == 22


def test_exact_fbx_build_splits_at_the_65535_vertex_boundary(
    tmp_path: Path,
) -> None:
    scene = _vertex_boundary_scene(tmp_path / "vertex_boundary_scene.fbx")

    result = build_source_from_fbx(
        scene,
        ModelBuildOptions(
            "vertex_boundary_production",
            mode="exact_rig",
            max_vertices_per_mesh=65_535,
            orientation_policy="none",
        ),
    )

    mesh_nodes = [node for node in result.source.nodes if node.node_type == 2]
    assert len(mesh_nodes) == 2
    assert [node.lods[0].vertex_count for node in mesh_nodes] == [65_535, 3]
    assert [len(node.lods[0].indices) for node in mesh_nodes] == [65_535, 3]
    assert [max(node.lods[0].indices) for node in mesh_nodes] == [65_534, 2]
    assert all(
        node.lods[0].subsets[0].bone_palette == (0,)
        for node in mesh_nodes
    )
    assert all(
        skin.bone_indices == (0,)
        for node in mesh_nodes
        for skin in node.lods[0].skin_vertices
    )
    assert result.report["total_vertices"] == 65_538
    assert result.report["total_triangles"] == 21_846
    assert result.report["skin_partitions"]["partition_count"] == 2
    assert result.report["skin_partitions"][
        "maximum_emitted_vertex_count"
    ] == 65_535
    assert result.report["model_bind_cpu_skin_validation"]["status"] == "pass"

    result.source.validate()
    payload = result.source.build()
    audit = audit_source_msh_bytes_for_compiler(
        payload,
        "vertex_boundary_production.msh",
    )
    assert audit["ready"] is True, audit["errors"]
