from __future__ import annotations

import json
from pathlib import Path
import struct
from types import SimpleNamespace

import numpy as np
import pytest

from dlanm2_gui.fbx_core import (
    FBX_TICKS_PER_SECOND,
    FbxDocument,
)
from dlanm2_gui.fbx_preflight import preflight_fbx
from dlanm2_gui.chrome_rig_builder import build_chrome_rig_from_fbx
from dlanm2_gui.model_importer.fbx_model import (
    FbxNode,
    FbxScene,
    ROTATION_ORDERS,
    _euler_matrix,
)
from dlanm2_gui.model_importer.msh_builder import (
    _author_chrome_bone_frames,
    _canonical_bind_globals_meters,
    _matrix_units_to_meters,
)
from dlanm2_gui.retarget_engines.mapped_rig import SourceGlobalNormalization


def _property(name: str, *values: float | int) -> FbxNode:
    return FbxNode("P", [name, name, "", "A", *values], [], 0, 0)


def _model_node(
    object_id: int,
    name: str,
    subtype: str,
    *,
    translation: tuple[float, float, float] = (0.0, 0.0, 0.0),
    rotation: tuple[float, float, float] = (0.0, 0.0, 0.0),
    scale: tuple[float, float, float] = (1.0, 1.0, 1.0),
    rotation_order: int = 0,
) -> FbxNode:
    properties = FbxNode(
        "Properties70",
        [],
        [
            _property("Lcl Translation", *translation),
            _property("Lcl Rotation", *rotation),
            _property("Lcl Scaling", *scale),
            _property("RotationOrder", rotation_order),
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


def _scene(
    path: Path,
    nodes: tuple[FbxNode, ...],
    *,
    parents: dict[int, list[tuple[str, int, list[object]]]] | None = None,
    children: dict[int, list[tuple[str, int, list[object]]]] | None = None,
    axis_settings: dict[str, int | float | None] | None = None,
) -> FbxScene:
    object_by_id = {int(node.properties[0]): node for node in nodes}
    model_ids = tuple(object_by_id)
    limb_ids = tuple(
        object_id
        for object_id, node in object_by_id.items()
        if str(node.properties[2]) == "LimbNode"
    )
    return FbxScene(
        path=path,
        version=7400,
        top={"Objects": FbxNode("Objects", [], list(nodes), 0, 0)},
        object_by_id=object_by_id,
        parents=parents or {},
        children=children or {},
        model_ids=model_ids,
        limb_ids=limb_ids,
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
        axis_settings=axis_settings
        or {
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


@pytest.mark.parametrize("rotation_order", range(6))
def test_model_and_animation_share_all_six_euler_orders(
    tmp_path: Path,
    rotation_order: int,
) -> None:
    path = tmp_path / f"order_{rotation_order}.fbx"
    path.write_bytes(b"synthetic")
    angles = (17.0, -31.0, 43.0)
    scene = _scene(
        path,
        (
            _model_node(
                1,
                "root",
                "LimbNode",
                rotation=angles,
                rotation_order=rotation_order,
            ),
        ),
    )
    document = FbxDocument.from_scene(scene)

    model_local = scene.model_local_matrix(1)
    animation_local = document.local_matrix(1, tick=0, use_animation=False)
    expected = _euler_matrix(angles, ROTATION_ORDERS[rotation_order])

    np.testing.assert_allclose(model_local, expected, atol=1.0e-12)
    np.testing.assert_allclose(animation_local, model_local, atol=1.0e-12)


def test_transform_contract_is_serializable_and_reports_wrapper_normalization(
    tmp_path: Path,
) -> None:
    path = tmp_path / "wrapper.fbx"
    path.write_bytes(b"synthetic")
    wrapper = _model_node(1, "Armature", "Null", scale=(100.0, 100.0, 100.0))
    root = _model_node(2, "root", "LimbNode", translation=(0.0, 1.0, 0.0))
    scene = _scene(
        path,
        (wrapper, root),
        parents={2: [("OO", 1, [])]},
        children={1: [("OO", 2, [])]},
    )

    document = FbxDocument.from_scene(scene)
    report = document.transform_contract.to_dict()

    assert report["format"] == "dl-reanimated-fbx-transform-contract-v2"
    assert report["legacy_format_compatibility"] == (
        "dl-reanimated-fbx-transform-contract-v1"
    )
    assert report["unit_conversion_count"] == 1
    assert report["axis_conversion_count"] == 0
    assert report["wrapper_models"] == ("Armature",)
    assert report["wrapper_scale_normalization"]["Armature"][
        "normalization_factor"
    ] == pytest.approx(0.01)
    assert report["bind_source_by_bone"] == {"root": "ModelTransformsFallback"}
    assert report["roots"] == ("root",)
    assert report["non_bone_ancestors"] == ("Armature",)
    json.dumps(report)


def test_reflected_common_wrapper_is_removed_before_bone_classification(
    tmp_path: Path,
) -> None:
    path = tmp_path / "reflected_wrapper.fbx"
    path.write_bytes(b"synthetic")
    wrapper = _model_node(
        1,
        "Armature",
        "Null",
        rotation=(90.0, 0.0, 0.0),
        scale=(-100.0, 100.0, 100.0),
    )
    root = _model_node(2, "root", "LimbNode", translation=(0.0, 2.0, 0.0))
    child = _model_node(3, "child", "LimbNode", translation=(1.0, 0.0, 0.0))
    scene = _scene(
        path,
        (wrapper, root, child),
        parents={2: [("OO", 1, [])], 3: [("OO", 2, [])]},
        children={1: [("OO", 2, [])], 2: [("OO", 3, [])]},
    )
    document = FbxDocument.from_scene(scene, purpose="animation")
    contract = document.transform_contract

    assert contract.common_wrapper_models == ("Armature",)
    assert contract.common_wrapper_is_static
    assert contract.common_wrapper_is_uniform
    assert contract.common_wrapper_is_reflected
    assert contract.canonicalized_wrapper_reflection
    assert contract.local_reflected_bones == ()
    assert contract.singular_or_nonfinite_nodes == ()
    assert contract.canonical_transform_validation["negative_determinants"] == 0

    raw = scene.model_global_matrices()
    expected_root = np.linalg.inv(raw[1]) @ raw[2]
    expected_child = np.linalg.inv(raw[1]) @ raw[3]
    canonical = document.global_matrices(tick=0)
    # This focused assertion fixes the repository's column-vector convention:
    # inverse(wrapper) is left-multiplied, not right-multiplied.
    np.testing.assert_allclose(canonical["root"], expected_root, atol=1.0e-10)
    np.testing.assert_allclose(canonical["child"], expected_child, atol=1.0e-10)
    assert np.linalg.det(canonical["root"][:3, :3]) > 0.0
    assert np.linalg.det(canonical["child"][:3, :3]) > 0.0

    preflight = preflight_fbx(path, purpose="animation", document=document)
    assert not preflight.import_blocking
    finding = next(
        row
        for row in preflight.findings
        if row.code == "common_wrapper_reflection_canonicalized"
    )
    assert finding.outcome == "automatically_repaired"
    assert finding.severity == "informational"
    assert finding.group == "repaired"


def test_true_local_reflection_is_advisory_for_animation_and_strict_for_model(
    tmp_path: Path,
) -> None:
    path = tmp_path / "local_reflection.fbx"
    path.write_bytes(b"synthetic")
    scene = _scene(
        path,
        (_model_node(1, "reflected_root", "LimbNode", scale=(-1.0, 1.0, 1.0)),),
    )
    animation_document = FbxDocument.from_scene(scene, purpose="animation")
    model_document = FbxDocument.from_scene(scene, purpose="model")

    assert animation_document.transform_contract.common_wrapper_models == ()
    assert animation_document.transform_contract.local_reflected_bones == (
        "reflected_root",
    )
    animation = preflight_fbx(
        path,
        purpose="animation",
        document=animation_document,
    )
    assert not animation.import_blocking
    assert animation.readiness_level == "advisory"
    animation.require_buildable()

    model = preflight_fbx(path, purpose="model", document=model_document)
    assert model.import_blocking
    assert any(
        row.code == "reflected_or_negative_bone_scale"
        for row in model.findings
    )


def test_animated_determinant_sign_change_without_sampled_zero_is_advisory(
    tmp_path: Path,
) -> None:
    path = tmp_path / "sign_change.fbx"
    path.write_bytes(b"synthetic")
    scene = _scene(path, (_model_node(1, "root", "LimbNode"),))
    step = FBX_TICKS_PER_SECOND // 30
    curve_node = FbxNode("AnimationCurveNode", [20, "Scale", ""], [], 0, 0)
    curve = FbxNode(
        "AnimationCurve",
        [30, "ScaleX", ""],
        [
            FbxNode("KeyTime", [[0, step]], [], 0, 0),
            FbxNode("KeyValueFloat", [[1.0, -1.0]], [], 0, 0),
        ],
        0,
        0,
    )
    stack_properties = FbxNode(
        "Properties70",
        [],
        [_property("LocalStart", 0), _property("LocalStop", step)],
        0,
        0,
    )
    stack = FbxNode(
        "AnimationStack", [40, "AnimStack::Take", ""], [stack_properties], 0, 0
    )
    layer = FbxNode("AnimationLayer", [100, "AnimLayer::Layer", ""], [], 0, 0)
    scene.top["Objects"].children.extend((stack, layer, curve_node, curve))
    scene.object_by_id.update({20: curve_node, 30: curve, 40: stack, 100: layer})
    scene.children[40] = [("OO", 100, [])]
    scene.children[100] = [("OO", 20, [])]
    scene.children[20] = [("OP", 30, ["d|X"])]
    scene.parents[20] = [("OP", 1, ["Lcl Scaling"])]
    document = FbxDocument.from_scene(scene, purpose="animation")

    assert document.transform_contract.animated_determinant_sign_change_bones == (
        "root",
    )
    assert document.transform_contract.singular_or_nonfinite_nodes == ()
    report = preflight_fbx(path, purpose="animation", document=document)
    finding = next(
        row
        for row in report.findings
        if row.code == "animated_scale_sign_change"
    )
    assert finding.severity == "warning"
    assert finding.can_continue
    assert report.readiness_level == "advisory"
    report.require_buildable()


def test_non_uniform_armature_wrapper_has_named_actionable_preflight_finding(
    tmp_path: Path,
) -> None:
    path = tmp_path / "non_uniform_wrapper.fbx"
    path.write_bytes(b"synthetic")
    wrapper = _model_node(1, "Armature", "Null", scale=(100.0, 80.0, 100.0))
    root = _model_node(2, "root", "LimbNode", translation=(0.0, 1.0, 0.0))
    scene = _scene(
        path,
        (wrapper, root),
        parents={2: [("OO", 1, [])]},
        children={1: [("OO", 2, [])]},
    )
    document = FbxDocument.from_scene(scene)

    report = preflight_fbx(path, purpose="model", document=document)
    finding = next(
        row for row in report.findings if row.code == "non_uniform_scene_wrapper"
    )
    assert finding.severity == "warning"
    assert "Armature" in finding.detected
    assert "100.0" in finding.detected and "80.0" in finding.detected
    assert "Apply/freeze" in finding.action


def test_independent_uniform_wrappers_use_per_bone_normalization_factors(
    tmp_path: Path,
) -> None:
    path = tmp_path / "two_wrappers.fbx"
    path.write_bytes(b"synthetic")
    wrapper_a = _model_node(1, "ArmatureA", "Null", scale=(100.0, 100.0, 100.0))
    root_a = _model_node(2, "root_a", "LimbNode", translation=(0.0, 100.0, 0.0))
    wrapper_b = _model_node(3, "ArmatureB", "Null", scale=(10.0, 10.0, 10.0))
    root_b = _model_node(4, "root_b", "LimbNode", translation=(0.0, 10.0, 0.0))
    scene = _scene(
        path,
        (wrapper_a, root_a, wrapper_b, root_b),
        parents={
            2: [("OO", 1, [])],
            4: [("OO", 3, [])],
        },
        children={
            1: [("OO", 2, [])],
            3: [("OO", 4, [])],
        },
    )
    document = FbxDocument.from_scene(scene)

    assert document.wrapper_scale_normalization_factor("root_a") == pytest.approx(0.01)
    assert document.wrapper_scale_normalization_factor("root_b") == pytest.approx(0.1)
    animation_bind = {
        name: document.normalized_matrix_to_target_space(
            name, document.bind_global_matrices[name]
        )
        for name in ("root_a", "root_b")
    }
    model_bind = _canonical_bind_globals_meters(scene, (2, 4), "auto")
    np.testing.assert_allclose(animation_bind["root_a"], model_bind[2], atol=1.0e-12)
    np.testing.assert_allclose(animation_bind["root_b"], model_bind[4], atol=1.0e-12)
    assert animation_bind["root_a"][1, 3] == pytest.approx(100.0)
    assert animation_bind["root_b"][1, 3] == pytest.approx(1.0)


def test_model_and_animation_wrapper_scale_axis_normalization_reach_same_bind(
    tmp_path: Path,
) -> None:
    path = tmp_path / "blender_wrapper.fbx"
    path.write_bytes(b"synthetic")
    wrapper = _model_node(
        1,
        "Armature",
        "Null",
        rotation=(-90.0, 0.0, 0.0),
        scale=(100.0, 100.0, 100.0),
    )
    root = _model_node(
        2,
        "root",
        "LimbNode",
        translation=(0.0, 100.0, 25.0),
    )
    scene = _scene(
        path,
        (wrapper, root),
        parents={2: [("OO", 1, [])]},
        children={1: [("OO", 2, [])]},
    )
    document = FbxDocument.from_scene(scene)

    model_global = scene.to_chrome_global_matrix(
        _matrix_units_to_meters(scene.bone_globals((2,))[2], scene.meters_per_unit),
        "auto",
    )
    model_authored, _report = _author_chrome_bone_frames(
        [model_global],
        [-1],
        ["root"],
        deform_indices=frozenset({0}),
    )
    wrapper_normalizer = document._scene_scale_normalizer(2)
    wrapper_scale_factor = float(
        np.mean(np.linalg.norm(wrapper_normalizer[:3, :3], axis=0))
    )
    animation_normalizer = SourceGlobalNormalization(
        meters_per_unit=document.meters_per_unit,
        convert_y_up_to_dying_light=False,
        wrapper_scale_normalization_factor=wrapper_scale_factor,
        wrapper_axis_conversion=True,
    )
    animation_bind = animation_normalizer.apply(
        document.bind_global_matrices["root"]
    )

    np.testing.assert_allclose(model_authored[0], animation_bind, atol=1.0e-8)
    assert animation_normalizer.to_report()["unit_conversion_count"] == 1
    assert animation_normalizer.to_report()["axis_conversion_count"] == 1


def test_auto_axis_policy_accepts_signed_orthonormal_permutations(tmp_path: Path) -> None:
    path = tmp_path / "z_up.fbx"
    path.write_bytes(b"synthetic")
    scene = _scene(
        path,
        (_model_node(1, "root", "LimbNode"),),
        axis_settings={
            "UpAxis": 2,
            "UpAxisSign": 1,
            "CoordAxis": 0,
            "CoordAxisSign": 1,
            "FrontAxis": 1,
            "FrontAxisSign": -1,
            "UnitScaleFactor": 1.0,
            "OriginalUnitScaleFactor": 1.0,
        },
    )

    expected = np.asarray(
        ((1.0, 0.0, 0.0, 0.0),
         (0.0, 0.0, 1.0, 0.0),
         (0.0, -1.0, 0.0, 0.0),
         (0.0, 0.0, 0.0, 1.0)),
        dtype=float,
    )

    assert scene.resolved_orientation_policy("auto") == "fbx_global_settings"
    np.testing.assert_allclose(
        scene.coordinate_conversion_matrix("auto"), expected, atol=1.0e-12
    )


def test_non_limb_model_between_joints_preserves_animation_parent(tmp_path: Path) -> None:
    path = tmp_path / "intermediate_null.fbx"
    path.write_bytes(b"synthetic")
    root = _model_node(1, "root", "LimbNode")
    helper = _model_node(2, "axis_helper", "Null", rotation=(0.0, 0.0, 90.0))
    child = _model_node(3, "child", "LimbNode", translation=(1.0, 0.0, 0.0))
    scene = _scene(
        path,
        (root, helper, child),
        parents={2: [("OO", 1, [])], 3: [("OO", 2, [])]},
        children={1: [("OO", 2, [])], 2: [("OO", 3, [])]},
    )
    document = FbxDocument.from_scene(scene)

    assert scene.nearest_limb_parent_id(3) == 1
    assert scene.limb_children_ids(1) == (3,)
    assert scene.depth_first_model_ids(1, limb_only=True) == (1, 3)
    assert document.parent_by_name == {"root": None, "child": "root"}
    assert document.transform_contract.wrapper_models == ()


def test_model_preflight_allows_static_prop_but_animation_still_blocks(
    tmp_path: Path,
) -> None:
    source = tmp_path / "static.fbx"
    document = SimpleNamespace(
        limb_models={},
        animation_stacks=(),
        meters_per_unit=0.01,
        normalized_name_collisions=(),
        bind_global_matrices={},
        parent_by_name={},
        scene=SimpleNamespace(geometries=(), limb_ids=(), model_names={}),
    )

    model = preflight_fbx(source, purpose="model", document=document)
    animation = preflight_fbx(source, purpose="animation", document=document)

    assert not model.blocking
    assert any(row.code == "static_model_without_armature" for row in model.findings)
    assert animation.blocking
    assert any(row.code == "no_usable_skeleton" for row in animation.findings)


def test_animation_without_skeleton_accepts_supported_scalar_mimic_domain(
    tmp_path: Path,
) -> None:
    source = tmp_path / "mimic_only.fbx"
    document = SimpleNamespace(
        limb_models={},
        animation_stacks=(),
        meters_per_unit=0.01,
        normalized_name_collisions=(),
        bind_global_matrices={},
        parent_by_name={},
        scene=SimpleNamespace(
            geometries=(),
            limb_ids=(),
            model_names={},
            blend_shape_names=("Smile",),
            blend_shapes=(),
        ),
    )

    report = preflight_fbx(source, purpose="animation", document=document)

    assert not report.import_blocking
    assert report.inventory["supported_scalar_mimic_domain"] is True
    assert any(
        row.code == "scalar_mimic_animation_domain"
        for row in report.findings
    )


def test_crig_builder_uses_resolved_bind_and_bind_changes_rig_identity(
    tmp_path: Path,
) -> None:
    def document_with_bind(translation: float) -> SimpleNamespace:
        bind = np.eye(4, dtype=float)
        bind[0, 3] = translation
        frame_zero = np.eye(4, dtype=float)
        frame_zero[0, 3] = 999.0
        return SimpleNamespace(
            limb_models={"root": 1},
            parent_by_name={"root": None},
            meters_per_unit=0.01,
            scene=None,
            bind_local_matrices={"root": bind},
            _local_matrix=lambda *_args, **_kwargs: frame_zero.copy(),
        )

    first = build_chrome_rig_from_fbx(
        tmp_path / "first.fbx",
        document_factory=lambda _path: document_with_bind(12.0),
    )
    second = build_chrome_rig_from_fbx(
        tmp_path / "second.fbx",
        document_factory=lambda _path: document_with_bind(34.0),
    )

    assert first.bones[0].bind_translation == pytest.approx((0.12, 0.0, 0.0))
    assert second.bones[0].bind_translation == pytest.approx((0.34, 0.0, 0.0))
    assert first.rig_id != second.rig_id


def test_negative_bone_scale_is_an_actionable_blocking_preflight(
    tmp_path: Path,
) -> None:
    path = tmp_path / "reflected_bone.fbx"
    path.write_bytes(b"synthetic")
    scene = _scene(
        path,
        (_model_node(1, "reflected_root", "LimbNode", scale=(-1.0, 1.0, 1.0)),),
    )
    document = FbxDocument.from_scene(scene)

    report = preflight_fbx(path, purpose="model", document=document)

    assert report.blocking
    finding = next(
        row for row in report.findings if row.code == "reflected_or_negative_bone_scale"
    )
    assert "reflected_root" in finding.detected
    assert "Apply/freeze" in finding.action


def test_model_blendshapes_block_before_silent_base_mesh_output(
    tmp_path: Path,
) -> None:
    path = tmp_path / "morphed_model.fbx"
    path.write_bytes(b"synthetic")
    scene = _scene(path, (_model_node(1, "root", "LimbNode"),))
    scene.blend_shape_names = ("Smile", "BrowRaise")
    document = FbxDocument.from_scene(scene)

    report = preflight_fbx(path, purpose="model", document=document)

    assert report.blocking
    finding = next(
        row for row in report.findings
        if row.code == "unsupported_model_blend_shapes"
    )
    assert "Smile" in finding.detected
    assert "Bake" in finding.action
    assert "Exact Rig" in finding.action


def test_unsupported_binary_fbx_version_fails_with_reexport_action(
    tmp_path: Path,
) -> None:
    path = tmp_path / "future_version.fbx"
    path.write_bytes(b"Kaydara FBX Binary  \x00\x1a\x00" + struct.pack("<I", 9900))

    with pytest.raises(ValueError, match="unsupported binary FBX version 9900.*Re-export"):
        FbxScene.from_path(path)
