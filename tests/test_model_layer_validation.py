from __future__ import annotations

from types import SimpleNamespace

import numpy as np
import pytest

from dlanm2_gui.model_importer.fbx_model import (
    FbxCluster,
    FbxGeometry,
    FbxLayerElement,
    FbxTriangle,
    FbxTriangleCorner,
)
from dlanm2_gui.model_importer.msh_builder import (
    ModelBuildOptions,
    _geometry_chunks,
)


def _geometry(
    layer: FbxLayerElement | None,
    *,
    clusters: tuple[FbxCluster, ...] = (),
    material_ids: tuple[int, ...] = (),
) -> FbxGeometry:
    corners = tuple(FbxTriangleCorner(index, index) for index in range(3))
    return FbxGeometry(
        object_id=1,
        name="Body",
        model_id=None,
        model_name="Body",
        control_points=np.asarray(
            ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0)),
            dtype=float,
        ),
        polygons=(corners,),
        triangles=(FbxTriangle(0, corners),),
        layers=({layer.kind: [layer]} if layer is not None else {}),
        material_ids=material_ids,
        material_names=tuple(f"material_{index}" for index in range(len(material_ids))),
        clusters=clusters,
        mesh_bind_global=np.eye(4, dtype=float),
        geometric_transform=np.eye(4, dtype=float),
    )


def _build(geometry: FbxGeometry, *, material_mode: str = "test"):
    scene = SimpleNamespace(
        meters_per_unit=1.0,
        coordinate_conversion_matrix=lambda _policy: np.eye(4, dtype=float),
    )
    return _geometry_chunks(
        scene,
        geometry,
        options=ModelBuildOptions("body", mode="static", material_mode=material_mode),
        material_lookup={(1, 0): 0},
        bone_local_by_id=None,
        target_by_source_bone=None,
        transfer_by_source_bone=None,
        fallback_bone_local_index=None,
    )


def test_invalid_present_normal_layer_index_is_actionable() -> None:
    layer = FbxLayerElement(
        "LayerElementNormal",
        0,
        "Normals",
        "ByPolygonVertex",
        "IndexToDirect",
        [0.0, 0.0, 1.0],
        [0, 0],
        3,
    )

    with pytest.raises(ValueError, match="Body.*polygon 0.*LayerElementNormal.*recalculate normals"):
        _build(_geometry(layer))


def test_invalid_present_uv_layer_index_is_actionable() -> None:
    layer = FbxLayerElement(
        "LayerElementUV",
        0,
        "UVMap",
        "ByPolygonVertex",
        "IndexToDirect",
        [0.0, 0.0],
        [0, 0],
        2,
    )

    with pytest.raises(ValueError, match="Body.*polygon 0.*LayerElementUV.*Repair the UV"):
        _build(_geometry(layer))


def test_extreme_degenerate_authored_uv_blocks_tangent_fallback_with_provenance() -> None:
    layer = FbxLayerElement(
        "LayerElementUV",
        0,
        "UVMap",
        "ByPolygonVertex",
        "Direct",
        [0.0, 0.0, 1.0e-13, 0.0, 0.0, 1.0e-13],
        [],
        2,
    )

    with pytest.raises(
        ValueError,
        match=(
            "Body.*mesh node.*tangent rebuild.*rebuilt_missing_source.*1 of 1 "
            ".*source polygon 0.*Repair/unwrap UV0"
        ),
    ):
        _build(_geometry(layer))


def test_degenerate_uv_is_allowed_when_valid_authored_tangents_are_imported() -> None:
    uv_layer = FbxLayerElement(
        "LayerElementUV",
        0,
        "UVMap",
        "ByPolygonVertex",
        "Direct",
        [0.0, 0.0] * 3,
        [],
        2,
    )
    tangent_layer = FbxLayerElement(
        "LayerElementTangent",
        0,
        "Tangents",
        "ByPolygonVertex",
        "Direct",
        [1.0, 0.0, 0.0] * 3,
        [],
        3,
    )
    geometry = _geometry(uv_layer)
    geometry.layers[tangent_layer.kind] = [tangent_layer]

    chunks = _build(geometry)

    assert chunks[0].tangent_policy == "imported"


def test_invalid_authored_tangents_report_rebuild_provenance_on_degenerate_uv() -> None:
    uv_layer = FbxLayerElement(
        "LayerElementUV",
        0,
        "UVMap",
        "ByPolygonVertex",
        "Direct",
        [0.0, 0.0] * 3,
        [],
        2,
    )
    tangent_layer = FbxLayerElement(
        "LayerElementTangent",
        0,
        "Tangents",
        "ByPolygonVertex",
        "Direct",
        [0.0, 0.0, 0.0] * 3,
        [],
        3,
    )
    geometry = _geometry(uv_layer)
    geometry.layers[tangent_layer.kind] = [tangent_layer]

    with pytest.raises(ValueError, match="rebuilt_invalid_source.*source polygon 0"):
        _build(geometry)


def test_invalid_present_material_slot_is_actionable() -> None:
    layer = FbxLayerElement(
        "LayerElementMaterial",
        0,
        "Materials",
        "ByPolygon",
        "Direct",
        [2],
        [],
        1,
    )

    with pytest.raises(ValueError, match="Body.*material slot 2.*outside"):
        _build(
            _geometry(layer, material_ids=(10,)),
            material_mode="preserve_slots",
        )


@pytest.mark.parametrize(
    ("index", "weight", "message"),
    (
        (99, 1.0, "control point 99"),
        (-1, 1.0, "control point -1"),
        (0, float("nan"), "invalid weight"),
        (0, float("inf"), "invalid weight"),
        (0, -0.25, "invalid weight"),
    ),
)
def test_invalid_skin_cluster_rows_are_rejected(
    index: int,
    weight: float,
    message: str,
) -> None:
    cluster = FbxCluster(
        object_id=7,
        name="BadSkin",
        bone_id=1,
        bone_name="root",
        indexes=(index,),
        weights=(weight,),
        transform=None,
        transform_link=None,
    )

    with pytest.raises(ValueError, match=message):
        _build(_geometry(None, clusters=(cluster,)))
