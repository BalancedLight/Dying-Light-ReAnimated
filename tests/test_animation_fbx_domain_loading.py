from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest

from dlanm2_gui.fbx_core import FbxDocument
from dlanm2_gui.fbx_preflight import preflight_fbx
from dlanm2_gui.model_importer.fbx_model import (
    FbxImportTolerance,
    FbxLoadOptions,
    FbxLoadPurpose,
)


CORPUS_ROOT = Path(r"F:\Fbx\AnimationTests")
CORPUS_EXPECTATIONS = (
    ("Standing Greeting.fbx", 154, 150),
    ("Taunt.fbx", 86, 159),
    ("Right Turn - Binary.fbx", 36, 159),
    ("Hip Hop Dancing.fbx", 135, 69),
    ("Thriller Part 1.fbx", 897, 159),
    ("Thriller Part 2.fbx", 567, 159),
    ("Thriller Part 3.fbx", 769, 159),
    ("Thriller Part 4.fbx", 1113, 159),
    ("Walk Strafe Left.fbx", 45, 69),
    ("Crouch To Stand.fbx", 78, 69),
    ("T-Pose.fbx", 2, 0),
)


def test_animation_load_options_never_request_model_topology() -> None:
    options = FbxLoadOptions.for_purpose(FbxLoadPurpose.ANIMATION)
    strict = FbxLoadOptions.for_purpose(
        FbxLoadPurpose.ANIMATION,
        tolerance=FbxImportTolerance.STRICT_DIAGNOSTICS,
    )

    assert options.load_skeleton
    assert options.load_animation
    assert options.load_bind_pose
    assert not options.load_geometry
    assert not options.load_skin
    assert not options.load_materials
    assert not options.load_blendshape_geometry
    assert options.tolerance == FbxImportTolerance.RECOMMENDED
    assert strict.tolerance == FbxImportTolerance.STRICT_DIAGNOSTICS
    assert not strict.load_geometry


def test_facial_animation_loads_channel_curves_without_polygon_topology() -> None:
    options = FbxLoadOptions.for_purpose(FbxLoadPurpose.ANIMATION_AND_FACIAL)

    assert options.load_blendshape_curves
    assert not options.load_blendshape_geometry
    assert not options.load_geometry


def _large_geometry_animation_document() -> SimpleNamespace:
    stack = SimpleNamespace(name="mixamo.com")
    scene = SimpleNamespace(
        loaded_domains=("skeleton", "animation", "bind_pose"),
        raw_geometry_inventory=(
            {
                "name": "DisplayMesh",
                "control_point_count": 24_000,
                "polygon_count": 20_500,
                "polygon_size_counts": {"4": 20_500},
                "inventory_error": "",
            },
        ),
        geometry_findings=(),
        geometries=(),
        model_names={1: "Hips"},
        limb_ids=(1,),
        model_parent_id=lambda _object_id: None,
        model_local_matrix=lambda _object_id: np.eye(4),
        blend_shape_names=(),
        blend_shapes=(),
    )
    return SimpleNamespace(
        scene=scene,
        limb_models={"Hips": 1},
        parent_by_name={"Hips": None},
        animation_stacks=(stack,),
        selected_animation_stack=stack,
        animation_stack_activity=lambda: (
            SimpleNamespace(
                name="mixamo.com",
                usable=True,
                changing=True,
                skeletal_channel_count=1,
                changing_skeletal_channel_count=1,
                to_dict=lambda: {
                    "name": "mixamo.com",
                    "skeletal_channel_count": 1,
                    "changing_skeletal_channel_count": 1,
                    "usable": True,
                },
            ),
        ),
        curves={(1, "Lcl Translation", "X"): ([0, 1], [0.0, 1.0])},
        transform_contract=None,
        normalized_name_collisions=(),
        bind_global_matrices={"Hips": np.eye(4)},
        bind_diagnostics=lambda: {"bind_coverage": {"authoritative": 1, "total": 1}},
        meters_per_unit=0.01,
    )


def test_animation_with_more_than_twenty_thousand_quads_is_buildable() -> None:
    report = preflight_fbx(
        "large-display-mesh.fbx",
        purpose="animation",
        document=_large_geometry_animation_document(),
    )

    assert not report.import_blocking
    assert report.inventory["raw_geometry_inventory"][0]["polygon_count"] == 20_500
    ignored = next(
        row for row in report.findings if row.code == "model_geometry_ignored_for_animation"
    )
    assert ignored.group == "ignored"
    assert "20500 quads" in ignored.detected


def test_geometry_inventory_error_is_visible_but_never_fbx_unreadable_for_animation() -> None:
    document = _large_geometry_animation_document()
    document.scene.raw_geometry_inventory[0]["inventory_error"] = (
        "polygon index array has an unsupported value"
    )

    report = preflight_fbx(
        "geometry-error-animation.fbx",
        purpose="animation",
        document=document,
    )

    assert not report.import_blocking
    assert not any(row.code == "fbx_unreadable" for row in report.findings)
    finding = next(
        row
        for row in report.findings
        if row.code == "model_geometry_error_ignored_for_animation"
    )
    assert finding.group == "ignored"
    assert "unsupported value" in finding.detected


@pytest.mark.skipif(
    not all((CORPUS_ROOT / name).is_file() for name, _frames, _changing in CORPUS_EXPECTATIONS),
    reason="external compatibility corpus is not installed",
)
@pytest.mark.parametrize("filename, expected_frames, expected_changing", CORPUS_EXPECTATIONS)
def test_uploaded_compatibility_corpus_uses_animation_domain_only(
    filename: str,
    expected_frames: int,
    expected_changing: int,
) -> None:
    document = FbxDocument(
        CORPUS_ROOT / filename,
        purpose=FbxLoadPurpose.ANIMATION,
    )

    assert document.scene.geometries == ()
    assert "geometry" not in document.scene.loaded_domains
    assert len(document.limb_models) == 65
    assert document.selected_animation_stack is not None
    assert document.selected_animation_stack.name == "mixamo.com"
    assert document.frame_count(fps=30) == expected_frames
    changing = sum(
        len(values) > 1 and max(values) - min(values) > 1.0e-8
        for _times, values in document.curves.values()
    )
    assert changing == expected_changing


@pytest.mark.skipif(
    not (CORPUS_ROOT / "Standing Greeting.fbx").is_file(),
    reason="external compatibility corpus is not installed",
)
def test_real_quad_heavy_file_preflight_reports_ignored_model_domain() -> None:
    path = CORPUS_ROOT / "Standing Greeting.fbx"
    report = preflight_fbx(path, purpose="animation")

    assert not report.import_blocking
    assert report.inventory["changing_skeletal_channel_count"] == 150
    assert any(
        row.code == "animation_stack_automatically_selected"
        and row.outcome == "automatically_repaired"
        for row in report.findings
    )
    ignored = next(
        row for row in report.findings if row.code == "model_geometry_ignored_for_animation"
    )
    assert "23770 quads" in ignored.detected


@pytest.mark.skipif(
    not (CORPUS_ROOT / "T-Pose.fbx").is_file(),
    reason="external compatibility corpus is not installed",
)
def test_static_t_pose_preflight_is_importable_as_rest_pose() -> None:
    report = preflight_fbx(CORPUS_ROOT / "T-Pose.fbx", purpose="animation")

    assert not report.import_blocking
    assert report.inventory["changing_skeletal_channel_count"] == 0
    assert any(row.code == "static_rest_pose_stack" for row in report.findings)
    assert not any(row.code == "no_changing_skeletal_channels" for row in report.findings)
