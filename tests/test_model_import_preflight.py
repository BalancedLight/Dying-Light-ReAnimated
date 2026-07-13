from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.chrome_rig import Anm2WriterProfile, ChromeRig
from dlanm2_gui.model_importer.fbx_model import (
    FBX_Y_UP_TO_DYING_LIGHT,
    FbxScene,
)
from dlanm2_gui.model_importer.msh_builder import (
    _BuildVertex,
    _MeshChunk,
    _bind_topology_preflight,
    _build_dying_light_humanoid,
    _companions,
    _compute_bone_local_bounds,
    _dying_light_humanoid_target_profile,
    _model_bounds_carrier,
    _validate_bind_topology_preflight,
    ModelBuildOptions,
)
from dlanm2_gui.model_importer.vendor.chrome_mesh_tools.smd import SmdFile
from dlanm2_gui.model_importer.vendor.chrome_mesh_tools.msh import MshFile
from dlanm2_gui.model_importer.vendor.chrome_mesh_tools.writer import (
    MSH_NODE_FLAG_ANIMATED,
    SourceMsh,
    SourceNode,
)
from dlanm2_gui.retarget_engines.mapped_rig import _target_uses_dying_light_basis


def test_auto_orientation_respects_declared_y_up_scene_and_keeps_manual_override() -> None:
    """Do not apply a second -90 degree conversion to an evaluated FBX bind.

    Blender's FBX export commonly records the axis conversion on the Model
    bind while GlobalSettings already declares the resulting scene Y-up.  Auto
    must accept that evaluated scene basis.  The legacy quarter-turn remains a
    deliberate manual override for assets authored to that older contract.
    """

    scene = object.__new__(FbxScene)
    scene.axis_settings = {
        "UpAxis": 1,
        "UpAxisSign": 1,
        "CoordAxis": 0,
        "CoordAxisSign": 1,
        "FrontAxis": 2,
        "FrontAxisSign": 1,
    }

    automatic = scene.coordinate_conversion_matrix("auto")
    manual = scene.coordinate_conversion_matrix("fbx_y_up_to_dying_light")

    assert np.allclose(automatic, np.eye(4), rtol=0.0, atol=1.0e-12)
    assert np.allclose(manual, FBX_Y_UP_TO_DYING_LIGHT, rtol=0.0, atol=1.0e-12)
    assert not np.allclose(automatic, manual, rtol=0.0, atol=1.0e-12)


def test_resolved_model_axis_conversion_overrides_legacy_crig_basis_heuristics() -> None:
    legacy_metadata = {
        "builder": "dl_reanimated_binary_fbx_v2",
        "model_axis_conversion": "auto",
    }
    writer = Anm2WriterProfile(
        coordinate_convention="dying_light_model_space",
    )

    # Old .crig files have no resolved field, so their established convention
    # remains compatible with the pre-fix mapped-retarget path.
    legacy = ChromeRig(
        "legacy",
        "Legacy",
        "Tests",
        (),
        0,
        writer_profile=writer,
        extensions=dict(legacy_metadata),
    )
    assert _target_uses_dying_light_basis(legacy) is True

    # New model imports record what Auto actually resolved to.  This explicit
    # result is authoritative over the legacy builder/convention heuristics.
    evaluated_y_up = ChromeRig(
        "evaluated",
        "Evaluated Y-up",
        "Tests",
        (),
        0,
        writer_profile=writer,
        extensions={**legacy_metadata, "resolved_model_axis_conversion": "none"},
    )
    assert _target_uses_dying_light_basis(evaluated_y_up) is False

    explicit_legacy_basis = ChromeRig(
        "explicit",
        "Explicit legacy basis",
        "Tests",
        (),
        0,
        writer_profile=Anm2WriterProfile(),
        extensions={"resolved_model_axis_conversion": "fbx_y_up_to_dying_light"},
    )
    assert _target_uses_dying_light_basis(explicit_legacy_basis) is True


def _vertex(source: tuple[float, float, float], output: tuple[float, float, float]) -> _BuildVertex:
    return _BuildVertex(
        source_position=np.asarray(source, dtype=float),
        position=np.asarray(output, dtype=float),
        normal=np.asarray((0.0, 0.0, 1.0), dtype=float),
        uv=(0.0, 0.0),
        color=(255, 255, 255, 255),
        influences=[(0, 1.0)],
    )


def _single_triangle(output_scale: float) -> _MeshChunk:
    source = ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0))
    # A translation verifies that the metric measures topology, not placement.
    offset = np.asarray((20.0, -4.0, 7.0), dtype=float)
    output = [tuple(offset + output_scale * np.asarray(point)) for point in source]
    return _MeshChunk(
        node_name="SyntheticBody",
        material_index=0,
        vertices=[_vertex(point, transformed) for point, transformed in zip(source, output)],
    )


def test_bind_topology_preflight_accepts_coherent_retarget_scale_and_translation() -> None:
    report = _bind_topology_preflight((_single_triangle(1.5),))

    assert report["status"] == "pass"
    assert report["blocking_reasons"] == []
    assert report["edge_distortion_ratio"]["p95"] == pytest.approx(1.5)
    assert report["edge_distortion_ratio"]["p99"] == pytest.approx(1.5)
    _validate_bind_topology_preflight(report)


@pytest.mark.parametrize("output_scale", [12.0, 1.0 / 12.0])
def test_bind_topology_preflight_rejects_catastrophic_stretch_or_shrink(
    output_scale: float,
) -> None:
    """Both explosion and collapse-without-zero-length use the symmetric ratio."""

    report = _bind_topology_preflight((_single_triangle(output_scale),))

    assert report["status"] == "fail"
    assert report["edge_distortion_ratio"]["p95"] > 4.0
    assert report["edge_distortion_ratio"]["p99"] > 10.0
    assert any("severely distorts triangle topology" in row for row in report["blocking_reasons"])
    with pytest.raises(ValueError, match="bind/topology preflight failed.*axis conversion"):
        _validate_bind_topology_preflight(report)


def test_bind_topology_preflight_warns_for_review_band_without_blocking() -> None:
    report = _bind_topology_preflight((_single_triangle(3.0),))

    assert report["status"] == "warning"
    assert report["blocking_reasons"] == []
    assert report["edge_distortion_ratio"]["p95"] > 2.0
    assert report["edge_distortion_ratio"]["p99"] < 5.0
    _validate_bind_topology_preflight(report)


def test_bind_topology_preflight_rejects_more_than_one_percent_collapsed_edges() -> None:
    chunk = _single_triangle(1.0)
    chunk.vertices[1].position = chunk.vertices[0].position.copy()

    report = _bind_topology_preflight((chunk,))

    assert report["status"] == "fail"
    assert report["output_collapsed_edge_fraction"] > 0.01
    assert any("edges collapse" in row for row in report["blocking_reasons"])


def test_fitted_humanoid_bscr_accepts_tracks_retargeted_to_emitted_bind() -> None:
    _ascr, bscr = _companions(
        ("bip01", "pelvis", "l_forearm"),
        "anims_man_all.scr",
        fitted_humanoid=True,
    )

    assert bscr is not None
    assert 'SetBoneAnimTrans("bip01", POS | ROT | SCL, LOD_OFF);' in bscr
    assert 'SetBoneAnimTrans("pelvis", POS | ROT, LOD_OFF);' in bscr
    assert 'SetBoneAnimTrans("l_forearm", POS | ROT, LOD_OFF);' in bscr


def test_fitted_humanoid_rejects_direct_stock_animation_alias() -> None:
    with pytest.raises(ValueError, match="Direct anims_man_all\\.scr"):
        _build_dying_light_humanoid(
            object(),  # guard runs before FBX/SMD access
            ModelBuildOptions(
                resource_name="custom",
                mode="dying_light_humanoid",
                animation_script="anims_man_all.scr",
            ),
            set(),
        )


def test_model_bounds_carrier_matches_emitted_vertices_and_has_no_lod() -> None:
    vertices = []
    for point in ((-2.0, 1.0, -0.5), (4.0, 3.0, 1.5), (0.0, 2.0, 0.0)):
        value = np.asarray(point, dtype=float)
        vertices.append(
            _BuildVertex(
                position=value,
                source_position=value.copy(),
                normal=np.asarray((0.0, 0.0, 1.0), dtype=float),
                uv=(0.0, 0.0),
                color=(255, 255, 255, 255),
                influences=[(0, 1.0)],
            )
        )

    node, report = _model_bounds_carrier((_MeshChunk("body", 0, vertices),), "hero")

    assert node.name == "hero_bounds"
    assert node.node_type == 1
    assert node.lods == ()
    assert node.bounds == pytest.approx((1.0, 2.0, 0.5, 3.0, 1.0, 1.0))
    assert report["minimum_xyz"] == pytest.approx([-2.0, 1.0, -0.5])
    assert report["maximum_xyz"] == pytest.approx([4.0, 3.0, 1.5])


def test_bone_bounds_cover_weighted_geometry_and_unused_child_segment() -> None:
    root = np.eye(4, dtype=float)
    child = np.eye(4, dtype=float)
    child[1, 3] = 1.0
    chunk = _MeshChunk(
        "body",
        0,
        vertices=[
            _BuildVertex(
                position=np.asarray((0.2, 0.1, -0.1), dtype=float),
                source_position=np.asarray((0.2, 0.1, -0.1), dtype=float),
                normal=np.asarray((0.0, 0.0, 1.0), dtype=float),
                uv=(0.0, 0.0),
                color=(255, 255, 255, 255),
                influences=[(1, 1.0), (0, 2.0 / 32767.0)],
            )
        ],
    )

    bounds, report = _compute_bone_local_bounds(
        (chunk,),
        (root, child),
        (-1, 0),
        retention_weight_i16=2,
    )

    assert len(bounds) == 2
    assert report["nonzero_bound_count"] == 2
    assert report["weighted_vertex_bound_count"] == 1
    assert report["segment_fallback_count"] == 1
    assert report["aggregate_model_diagonal_m"] > 1.0
    assert min(bounds[0][3:]) >= 0.005
    assert min(bounds[1][3:]) >= 0.005


def test_stock_humanoid_profile_keeps_69_bones_and_18_helpers_only() -> None:
    smd = SmdFile.from_path(Path("reference/player_1_tpp.smd"))

    profile = _dying_light_humanoid_target_profile(smd.nodes)

    assert profile["animation_entity_count"] == 87
    assert profile["bone_count"] == 69
    assert profile["helper_count"] == 18
    assert "hspine" in profile["helper_names"]
    assert "sc_boots" in profile["omitted_mesh_root_names"]
    assert "watch" in profile["omitted_mesh_root_names"]


def test_segment_proxy_bounds_ignore_remote_weighted_vertices_and_extend_leaf_x() -> None:
    root = np.eye(4, dtype=float)
    child = np.eye(4, dtype=float)
    child[0, 3] = 0.25
    remote = _BuildVertex(
        position=np.asarray((20.0, -30.0, 40.0), dtype=float),
        source_position=np.asarray((20.0, -30.0, 40.0), dtype=float),
        normal=np.asarray((0.0, 0.0, 1.0), dtype=float),
        uv=(0.0, 0.0),
        color=(255, 255, 255, 255),
        influences=[(0, 1.0)],
    )

    bounds, report = _compute_bone_local_bounds(
        (_MeshChunk("body", 0, vertices=[remote]),),
        (root, child),
        (-1, 0),
        retention_weight_i16=2,
        segment_proxy=True,
    )

    assert report["policy"] == "bone-local bind-segment proxy"
    assert bounds[0][0] == pytest.approx(0.125)
    assert bounds[0][3] == pytest.approx(0.125)
    assert bounds[1][0] > 0.0
    assert bounds[1][0] == pytest.approx(bounds[1][3])
    assert max(abs(value) for value in bounds[0]) < 1.0


def test_source_node_animated_flag_roundtrips_in_v3_tail_word() -> None:
    source = SourceMsh(
        materials=("test.mat",),
        surface_names=("Flesh",),
        nodes=(
            SourceNode(
                "bone",
                node_type=8,
                tail_words=(MSH_NODE_FLAG_ANIMATED, 0, 0),
            ),
            SourceNode("static_mesh", node_type=1),
        ),
    )

    parsed = MshFile.parse(source.build())

    assert parsed.nodes[0].tail_words[0] & MSH_NODE_FLAG_ANIMATED
    assert parsed.nodes[1].tail_words[0] == 0
