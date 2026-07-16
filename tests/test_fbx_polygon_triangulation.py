from __future__ import annotations

import numpy as np
import pytest

from dlanm2_gui.model_importer.fbx_model import (
    FbxImportTolerance,
    FbxNode,
    FbxScene,
    _triangulate_polygon,
    _validate_polygon_for_fan,
)


def _check(points) -> None:
    _validate_polygon_for_fan(
        np.asarray(points, dtype=float),
        geometry_name="fixture_mesh",
        polygon_index=7,
    )


def test_planar_convex_quad_is_safe_for_deterministic_fan() -> None:
    _check(((0, 0, 0), (2, 0, 0), (2, 1, 0), (0, 1, 0)))


def _triangulate(points):
    return _triangulate_polygon(
        np.asarray(points, dtype=float),
        geometry_name="fixture_mesh",
        polygon_index=7,
    )


def test_source_triangle_order_is_preserved() -> None:
    result = _triangulate(((2, 0, 0), (0, 1, 0), (0, 0, 0)))

    assert result.method == "source_triangle"
    assert result.triangles == ((0, 1, 2),)


def test_concave_polygon_uses_deterministic_ear_clipping() -> None:
    points = ((0, 0, 0), (2, 0, 0), (2, 2, 0), (1, 1, 0), (0, 2, 0))

    first = _triangulate(points)
    second = _triangulate(points)

    assert first.method == "projected_ear_clipping"
    assert first.triangles == second.triangles
    assert len(first.triangles) == 3
    assert "deterministic projected ear clipping" in first.warnings[0]


def test_nonplanar_quad_is_repaired_and_warned() -> None:
    result = _triangulate(
        ((0, 0, 0), (1, 0, 0), (1, 1, 0.2), (0, 1, 0))
    )

    assert result.method in {"quad_diagonal_02", "quad_diagonal_13"}
    assert len(result.triangles) == 2
    assert result.maximum_plane_deviation > 0.0
    assert any("non-planar face" in warning for warning in result.warnings)


def test_both_quad_diagonal_decisions_are_repeatable() -> None:
    diagonal_02 = ((0, 0, 0), (2, 0, 0), (2, 1, 0), (0, 1, 0))
    diagonal_13 = ((0, 0, 0), (1, 0, 0), (1, 1, 0.1), (0, 1, 0))

    assert _triangulate(diagonal_02).method == "quad_diagonal_02"
    first = _triangulate(diagonal_13)
    second = _triangulate(diagonal_13)
    assert first.method == "quad_diagonal_13"
    assert first.triangles == second.triangles


def test_repeated_ngon_corner_is_rejected() -> None:
    with pytest.raises(ValueError, match="repeats non-adjacent corner"):
        _check(((0, 0, 0), (1, 0, 0), (1, 1, 0), (1, 0, 0), (0, 1, 0)))


def test_irreparably_self_intersecting_polygon_is_blocked() -> None:
    with pytest.raises(ValueError, match="self-intersecting"):
        _triangulate(((0, 0, 0), (2, 2, 0), (0, 2, 0), (2, 0, 0)))


def test_invalid_control_point_index_blocks_requested_model_geometry() -> None:
    geometry = FbxNode(
        "Geometry",
        [1, "BrokenMesh", "Mesh"],
        [
            FbxNode(
                "Vertices",
                [[0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0]],
                [],
                0,
                0,
            ),
            FbxNode("PolygonVertexIndex", [[0, 1, -100]], [], 0, 0),
        ],
        0,
        0,
    )
    scene = object.__new__(FbxScene)
    scene.object_by_id = {1: geometry}

    with pytest.raises(ValueError, match="control point 99 outside 0..2"):
        scene._read_geometries()


def test_strict_model_diagnostics_can_block_nonplanar_recovery() -> None:
    geometry = FbxNode(
        "Geometry",
        [1, "AuditMesh", "Mesh"],
        [
            FbxNode(
                "Vertices",
                [[0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0, 0.2, 0.0, 1.0, 0.0]],
                [],
                0,
                0,
            ),
            FbxNode("PolygonVertexIndex", [[0, 1, 2, -4]], [], 0, 0),
        ],
        0,
        0,
    )
    scene = object.__new__(FbxScene)
    scene.object_by_id = {1: geometry}
    scene.import_tolerance = FbxImportTolerance.STRICT_DIAGNOSTICS.value
    scene.geometry_findings = []

    with pytest.raises(ValueError, match="requires tolerant triangulation"):
        scene._read_geometries()
