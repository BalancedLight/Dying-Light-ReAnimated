from __future__ import annotations

import numpy as np
import pytest

from dlanm2_gui.model_importer.fbx_model import _validate_polygon_for_fan


def _check(points) -> None:
    _validate_polygon_for_fan(
        np.asarray(points, dtype=float),
        geometry_name="fixture_mesh",
        polygon_index=7,
    )


def test_planar_convex_quad_is_safe_for_deterministic_fan() -> None:
    _check(((0, 0, 0), (2, 0, 0), (2, 1, 0), (0, 1, 0)))


def test_concave_polygon_is_rejected_with_triangulation_action() -> None:
    with pytest.raises(ValueError, match="concave.*Triangulate"):
        _check(((0, 0, 0), (2, 0, 0), (0.5, 0.5, 0), (2, 2, 0), (0, 2, 0)))


def test_nonplanar_quad_is_rejected_with_triangulation_action() -> None:
    with pytest.raises(ValueError, match="non-planar.*Triangulate"):
        _check(((0, 0, 0), (1, 0, 0), (1, 1, 0.2), (0, 1, 0)))


def test_repeated_ngon_corner_is_rejected() -> None:
    with pytest.raises(ValueError, match="repeats a corner"):
        _check(((0, 0, 0), (1, 0, 0), (1, 1, 0), (1, 0, 0), (0, 1, 0)))
