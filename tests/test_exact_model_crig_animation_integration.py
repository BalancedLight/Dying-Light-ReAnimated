from __future__ import annotations

import math
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest

from dlanm2_gui.anm2_components import decode_samples
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.fbx_core import FbxDocument
from dlanm2_gui.model_importer.crig import build_crig_from_rig_contract_bytes
from dlanm2_gui.model_importer.fbx_model import (
    FbxCluster,
    FbxGeometry,
    FbxNode,
    FbxScene,
    FbxTriangle,
    FbxTriangleCorner,
)
from dlanm2_gui.model_importer.model_validation import validate_model_bind_skin
from dlanm2_gui.model_importer.msh_builder import (
    ModelBuildOptions,
    build_source_from_fbx,
)
from dlanm2_gui.model_importer.rig_contract import AuthoredRigContract
from dlanm2_gui.model_importer.vendor.chrome_mesh_tools.writer import (
    SourceLod,
    SourceMsh,
    SourceNode,
    SourceSkinVertex,
    SourceSubset,
)
from dlanm2_gui.retarget_engines.exact_rig import build_exact_rig_anm2
from dlanm2_gui.retarget_engines.mapped_rig import (
    reconstruct_target_globals,
    target_bind_local_matrix,
)


def _matrix3x4(matrix: np.ndarray) -> tuple[float, ...]:
    return tuple(
        float(matrix[row, column])
        for row in range(3)
        for column in range(4)
    )


def _rotation(axis: str, degrees: float) -> np.ndarray:
    angle = math.radians(degrees)
    cosine = math.cos(angle)
    sine = math.sin(angle)
    result = np.eye(4, dtype=float)
    if axis == "x":
        result[1:3, 1:3] = ((cosine, -sine), (sine, cosine))
    elif axis == "y":
        result[0, 0] = result[2, 2] = cosine
        result[0, 2] = sine
        result[2, 0] = -sine
    elif axis == "z":
        result[0:2, 0:2] = ((cosine, -sine), (sine, cosine))
    else:  # pragma: no cover - fixture programming error
        raise AssertionError(axis)
    return result


def _local(
    translation: tuple[float, float, float],
    *,
    axis: str = "z",
    degrees: float = 0.0,
) -> np.ndarray:
    result = _rotation(axis, degrees)
    result[:3, 3] = translation
    return result


def _fbx_property(name: str, *values: float | int) -> FbxNode:
    return FbxNode("P", [name, name, "", "A", *values], [], 0, 0)


def _fbx_model_node(
    object_id: int,
    name: str,
    subtype: str,
    *,
    translation: tuple[float, float, float] = (0.0, 0.0, 0.0),
    rotation: tuple[float, float, float] = (0.0, 0.0, 0.0),
) -> FbxNode:
    properties = FbxNode(
        "Properties70",
        [],
        [
            _fbx_property("Lcl Translation", *translation),
            _fbx_property("Lcl Rotation", *rotation),
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


def _signed_z_up_scene(path: Path) -> FbxScene:
    """A skinned exact-model scene with a non-Y-up signed axis basis."""

    root = _fbx_model_node(
        1,
        "root",
        "LimbNode",
        translation=(10.0, 20.0, 30.0),
        rotation=(3.0, 5.0, -7.0),
    )
    child = _fbx_model_node(
        2,
        "joint",
        "LimbNode",
        translation=(0.0, 50.0, 10.0),
        rotation=(-6.0, 11.0, 4.0),
    )
    mesh_model = _fbx_model_node(3, "body", "Mesh")
    nodes = (root, child, mesh_model)
    scene = FbxScene(
        path=path,
        version=7400,
        top={"Objects": FbxNode("Objects", [], list(nodes), 0, 0)},
        object_by_id={int(node.properties[0]): node for node in nodes},
        parents={2: [("OO", 1, [])]},
        children={1: [("OO", 2, [])]},
        model_ids=(1, 2, 3),
        limb_ids=(1, 2),
        model_names={1: "root", 2: "joint", 3: "body"},
        model_subtypes={1: "LimbNode", 2: "LimbNode", 3: "Mesh"},
        material_names={},
        bind_pose_matrices={},
        geometries=(),
        animation_stacks=(),
        blend_shape_names=(),
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
        meters_per_unit=0.01,
    )
    native_bind = scene.model_global_matrices((1, 2))
    scene.bind_pose_matrices = {
        object_id: matrix.copy() for object_id, matrix in native_bind.items()
    }
    corners = tuple(FbxTriangleCorner(index, index) for index in range(3))
    clusters = (
        FbxCluster(
            201,
            "root_skin",
            1,
            "root",
            (0,),
            (1.0,),
            np.eye(4, dtype=float),
            native_bind[1].copy(),
        ),
        FbxCluster(
            202,
            "joint_skin",
            2,
            "joint",
            (1, 2),
            (1.0, 1.0),
            np.eye(4, dtype=float),
            native_bind[2].copy(),
        ),
    )
    scene.geometries = (
        FbxGeometry(
            object_id=100,
            name="triangle",
            model_id=3,
            model_name="body",
            control_points=np.asarray(
                ((0.0, 0.0, 0.0), (25.0, 0.0, 0.0), (0.0, 25.0, 0.0)),
                dtype=float,
            ),
            polygons=(corners,),
            triangles=(FbxTriangle(0, corners),),
            layers={},
            material_ids=(),
            material_names=(),
            clusters=clusters,
            mesh_bind_global=np.eye(4, dtype=float),
            geometric_transform=np.eye(4, dtype=float),
        ),
    )
    scene.mesh_bind_source_by_geometry = {"triangle": "synthetic identity bind"}
    return scene


def _exact_model_source() -> tuple[SourceMsh, AuthoredRigContract]:
    """Create the exact source-MSH shape emitted by the model importer.

    Animation entities form the leading depth-first prefix.  Skin bytes address
    the subset-local palette while each authored reference matrix remains the
    known-good inverse-global bind.
    """

    locals_by_name = {
        "root": _local((0.0, 0.0, 0.0)),
        "pelvis": _local((0.0, 0.9, 0.0), axis="z", degrees=5.0),
        "spine": _local((0.0, 0.45, 0.0), axis="x", degrees=-8.0),
        "hand_r": _local((0.35, 0.3, 0.05), axis="y", degrees=12.0),
    }
    names = tuple(locals_by_name)
    parent_indexes = (-1, 0, 1, 2)
    globals_by_name: dict[str, np.ndarray] = {}
    animation_nodes: list[SourceNode] = []
    for index, name in enumerate(names):
        parent_index = parent_indexes[index]
        local_matrix = locals_by_name[name]
        global_matrix = (
            globals_by_name[names[parent_index]] @ local_matrix
            if parent_index >= 0
            else local_matrix.copy()
        )
        globals_by_name[name] = global_matrix
        animation_nodes.append(
            SourceNode(
                name,
                node_type=8,
                parent_index=parent_index,
                local_matrix=_matrix3x4(local_matrix),
                reference_matrix=_matrix3x4(np.linalg.inv(global_matrix)),
            )
        )

    mesh = SourceLod(
        positions=((0.0, 0.9, 0.0), (0.25, 1.4, 0.0), (0.55, 1.65, 0.05)),
        indices=(0, 1, 2),
        subsets=(SourceSubset(0, 0, 3, (1, 2, 3)),),
        skin_vertices=(
            SourceSkinVertex((0,), (1.0,)),
            SourceSkinVertex((1,), (1.0,)),
            SourceSkinVertex((2,), (1.0,)),
        ),
    )
    source = SourceMsh(
        materials=("integration_test.mat",),
        surface_names=("Flesh",),
        nodes=(
            *animation_nodes,
            SourceNode("body", node_type=2, lods=(mesh,)),
            SourceNode("bounds", node_type=1, bounds=(0.25, 1.25, 0.0, 1.0, 1.0, 1.0)),
        ),
    )
    contract = AuthoredRigContract.from_source_msh(
        source,
        source_fbx_sha256="e" * 64,
        source_model_name="authored_exact_model.fbx",
        authored_msh_resource_name="authored_exact_model",
        coordinate_contract={"resolved_orientation_policy": "none"},
    )
    return source, contract


class _ContractAnimationDocument:
    """Small canonical-document fixture backed by an authored MSH contract."""

    _EXTRA_PARENTS = {
        "face_jaw": "spine",
        "cloth_cape": "spine",
        "accessory_chain": "hand_r",
        "camera_anchor": "root",
        "weapon_socket": "hand_r",
        "helper_marker": "root",
    }

    def __init__(
        self,
        contract: AuthoredRigContract,
        *,
        include_source_superset: bool,
    ) -> None:
        target_rows = contract.animation_nodes
        self.meters_per_unit = 1.0
        self.bind_source = "synthetic authoritative SourceMsh contract bind"
        self.bind_coverage = {
            "total": len(target_rows),
            "authoritative": len(target_rows),
            "fallback": 0,
        }
        self.selected_animation_stack = None
        self.normalized_name_collisions: tuple[tuple[str, ...], ...] = ()
        self.null_models: dict[str, int] = {}

        self.parent_by_name: dict[str, str | None] = {}
        self.bind_local_matrices: dict[str, np.ndarray] = {}
        self.bind_global_matrices: dict[str, np.ndarray] = {}
        for row in target_rows:
            self.bind_local_matrices[row.name] = np.asarray(
                (
                    (*row.local_matrix3x4[0:4],),
                    (*row.local_matrix3x4[4:8],),
                    (*row.local_matrix3x4[8:12],),
                    (0.0, 0.0, 0.0, 1.0),
                ),
                dtype=float,
            )
            self.bind_global_matrices[row.name] = np.asarray(
                row.global_matrix4x4, dtype=float
            ).reshape((4, 4))
            parent = row.parent_physical_index
            self.parent_by_name[row.name] = (
                contract.nodes[parent].name if parent >= 0 else None
            )

        if include_source_superset:
            for index, (name, parent_name) in enumerate(
                self._EXTRA_PARENTS.items(), start=1
            ):
                self.parent_by_name[name] = parent_name
                self.bind_local_matrices[name] = _local(
                    (0.03 * index, 0.015 * index, -0.01 * index),
                    axis=("x", "y", "z")[index % 3],
                    degrees=float(index),
                )
                self.bind_global_matrices[name] = (
                    self.bind_global_matrices[parent_name]
                    @ self.bind_local_matrices[name]
                )

        names = tuple(self.parent_by_name)
        self.limb_models = {name: index + 1 for index, name in enumerate(names)}
        self._name_by_id = {object_id: name for name, object_id in self.limb_models.items()}

    def bind_diagnostics(self) -> dict[str, object]:
        return {
            "bind_source": self.bind_source,
            "bind_coverage": dict(self.bind_coverage),
            "conflicting_transform_links": [],
            "conflicting_pose_transform_links": [],
        }

    def frame_ticks(self, *, fps: int) -> list[int]:
        assert fps == 30
        return [0, 1]

    def _animated_locals(self, tick: int, use_animation: bool) -> dict[str, np.ndarray]:
        result = {
            name: matrix.copy() for name, matrix in self.bind_local_matrices.items()
        }
        if not use_animation or tick == 0:
            return result
        axes = ("z", "x", "y")
        for index, name in enumerate(result):
            result[name] = result[name] @ _rotation(
                axes[index % len(axes)], 6.0 + 2.0 * index
            )
        return result

    def skeletal_local_matrices(
        self,
        *,
        tick: int,
        use_animation: bool,
        globals_by_name: dict[str, np.ndarray] | None = None,
    ) -> dict[str, np.ndarray]:
        del globals_by_name
        return self._animated_locals(tick, use_animation)

    def global_matrices(
        self, *, tick: int, use_animation: bool
    ) -> dict[str, np.ndarray]:
        locals_by_name = self._animated_locals(tick, use_animation)
        result: dict[str, np.ndarray] = {}
        for name in self.limb_models:
            parent = self.parent_by_name[name]
            result[name] = (
                result[parent] @ locals_by_name[name]
                if parent is not None
                else locals_by_name[name].copy()
            )
        return result

    def _local_matrix(
        self, object_id: int, *, tick: int, use_animation: bool
    ) -> np.ndarray:
        return self._animated_locals(tick, use_animation)[self._name_by_id[object_id]]


def _generated_model_rig() -> tuple[SourceMsh, AuthoredRigContract, ChromeRig]:
    source, contract = _exact_model_source()
    payload, report = build_crig_from_rig_contract_bytes(
        contract, name="Authored exact model"
    )
    rig = ChromeRig.from_bytes(payload, source_name="authored_exact_model.crig")
    assert report["authored_rig_contract_id"] == contract.contract_id
    return source, contract, rig


def _rig_bind_globals(rig: ChromeRig) -> dict[str, np.ndarray]:
    result: dict[str, np.ndarray] = {}
    for bone in rig.bones:
        local_matrix = target_bind_local_matrix(bone)
        result[bone.name] = (
            result[rig.bones[bone.parent_index].name] @ local_matrix
            if bone.parent_index >= 0
            else local_matrix.copy()
        )
    return result


def test_exact_model_contract_crig_bind_and_same_rig_animation_are_continuous(
    tmp_path: Path,
) -> None:
    source, contract, rig = _generated_model_rig()

    # Exercise the binary source-MSH and CPU bind-skin validation that precede
    # model output.  The CRIG must reconstruct those exact authored globals.
    assert source.build()
    bind_validation = validate_model_bind_skin(source, contract)
    assert bind_validation["status"] == "pass"
    assert bind_validation["maximum_bind_skin_error"] < 1.0e-10
    rig_globals = _rig_bind_globals(rig)
    assert [bone.name for bone in rig.bones] == [
        row.name for row in contract.animation_nodes
    ]
    for bone, authored in zip(rig.bones, contract.animation_nodes):
        expected_parent = (
            contract.nodes[authored.parent_physical_index].name
            if authored.parent_physical_index >= 0
            else None
        )
        actual_parent = (
            rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None
        )
        assert actual_parent == expected_parent
        assert rig_globals[bone.name] == pytest.approx(
            np.asarray(authored.global_matrix4x4).reshape((4, 4)), abs=1.0e-10
        )
    assert rig.extensions["authored_bind_hash"] == contract.bind_hash
    assert rig.extensions["authored_rig_contract_id"] == contract.contract_id

    document = _ContractAnimationDocument(
        contract, include_source_superset=False
    )
    build = build_exact_rig_anm2(
        tmp_path / "same_rig_animation.fbx",
        rig,
        document=document,
    )

    assert build.report["retarget_mode"] == "exact"
    assert build.report["skeleton_classification"] == "exact_identity"
    assert build.report["required_missing_bones"] == []
    assert build.report["hierarchy_mismatches"] == []
    assert build.report["base_mapped_bone_count"] == len(rig.bones)
    assert build.report["maximum_bind_position_discrepancy"] < 1.0e-10
    assert build.report["maximum_bind_rotation_discrepancy_degrees"] < 1.0e-5
    assert set(build.report["moving_target_bones"]) == {
        bone.name for bone in rig.bones
    }
    assert {
        row["review_state"]
        for row in build.report["mapping_profile"]["pairs"]
    } == {"automatic_accepted"}
    assert build.report["hierarchy_safety"]["status"] == "pass"

    decoded = decode_samples(build.payload, [0.0, 1.0])
    assert decoded.track_count == len(rig.bones)
    for track_index, bone in enumerate(rig.bones):
        first = decoded.frames[0].tracks[track_index]
        second = decoded.frames[1].tracks[track_index]
        assert first[3:6] == pytest.approx(bone.bind_translation, abs=2.0e-5)
        assert first[6:9] == pytest.approx(bone.bind_scale, abs=2.0e-5)
        assert max(abs(second[index] - first[index]) for index in range(3)) > 1.0e-3


def test_source_superset_extras_do_not_break_any_required_target_track(
    tmp_path: Path,
) -> None:
    _source, contract, rig = _generated_model_rig()
    document = _ContractAnimationDocument(
        contract, include_source_superset=True
    )

    build = build_exact_rig_anm2(
        tmp_path / "source_superset_animation.fbx",
        rig,
        document=document,
    )

    expected_extras = set(_ContractAnimationDocument._EXTRA_PARENTS)
    required_targets = {
        bone.name for bone in rig.bones if bone.deform and not bone.helper
    }
    assert build.report["retarget_mode"] == "target_compatible_source_superset"
    assert build.report["skeleton_classification"] == (
        "target_compatible_source_superset"
    )
    assert set(build.report["extra_source_bones"]) == expected_extras
    assert build.report["required_missing_bones"] == []
    assert build.report["hierarchy_mismatches"] == []
    assert build.report["unmapped_target_bones"] == []
    assert required_targets <= set(build.report["moving_target_bones"])
    assert set(build.report["base_component_policies"].values()) == {"rotation"}
    assert build.report["preserves_target_non_root_translation_and_scale"] is True
    assert build.report["hierarchy_safety"]["status"] == "pass"

    decoded = decode_samples(build.payload, [0.0, 1.0])
    assert decoded.track_count == len(rig.descriptors)
    for track_index, bone in enumerate(rig.bones):
        first = decoded.frames[0].tracks[track_index]
        second = decoded.frames[1].tracks[track_index]
        assert first[3:6] == pytest.approx(bone.bind_translation, abs=2.0e-5)
        assert second[3:6] == pytest.approx(bone.bind_translation, abs=2.0e-5)
        assert first[6:9] == pytest.approx(bone.bind_scale, abs=2.0e-5)
        assert second[6:9] == pytest.approx(bone.bind_scale, abs=2.0e-5)
        assert max(abs(second[index] - first[index]) for index in range(3)) > 1.0e-3


def test_signed_non_y_up_scene_uses_one_model_and_animation_target_basis(
    tmp_path: Path,
) -> None:
    path = tmp_path / "signed_z_up_exact_model.fbx"
    path.write_bytes(b"synthetic signed-axis scene")
    scene = _signed_z_up_scene(path)
    basis = scene.global_settings_conversion_matrix()
    expected_basis = np.asarray(
        (
            (1.0, 0.0, 0.0, 0.0),
            (0.0, 0.0, 1.0, 0.0),
            (0.0, -1.0, 0.0, 0.0),
            (0.0, 0.0, 0.0, 1.0),
        ),
        dtype=float,
    )
    np.testing.assert_allclose(basis, expected_basis, atol=1.0e-12)
    assert scene.resolved_orientation_policy("auto") == "fbx_global_settings"

    model = build_source_from_fbx(
        scene,
        ModelBuildOptions(
            "signed_z_up_exact_model",
            mode="exact_rig",
            orientation_policy="auto",
        ),
    )
    contract = model.authored_rig_contract
    assert contract is not None
    assert model.report["coordinate_contract"]["resolved_orientation_policy"] == (
        "fbx_global_settings"
    )
    assert contract.coordinate_contract["resolved_orientation_policy"] == (
        "fbx_global_settings"
    )
    assert validate_model_bind_skin(model.source, contract)["status"] == "pass"
    payload, _crig_report = build_crig_from_rig_contract_bytes(
        contract, name="Signed Z-up exact model"
    )
    rig = ChromeRig.from_bytes(payload, source_name="signed_z_up_exact_model.crig")

    document = FbxDocument.from_scene(scene, orientation_policy="auto")
    native_bind_locals = {
        name: matrix.copy()
        for name, matrix in document.bind_local_matrices.items()
    }

    def native_locals(tick: int, use_animation: bool) -> dict[str, np.ndarray]:
        result = {
            name: matrix.copy() for name, matrix in native_bind_locals.items()
        }
        if use_animation and tick:
            result["root"] = result["root"] @ _rotation("z", 9.0)
            result["joint"] = result["joint"] @ _rotation("x", -12.0)
        return result

    def native_globals(tick: int, use_animation: bool) -> dict[str, np.ndarray]:
        locals_by_name = native_locals(tick, use_animation)
        return {
            "root": locals_by_name["root"],
            "joint": locals_by_name["root"] @ locals_by_name["joint"],
        }

    document.frame_ticks = lambda *, fps: [0, 1]  # type: ignore[method-assign]
    document.global_matrices = (  # type: ignore[method-assign]
        lambda *, tick, use_animation: native_globals(tick, use_animation)
    )
    document.skeletal_local_matrices = (  # type: ignore[method-assign]
        lambda *, tick, use_animation, globals_by_name=None: native_locals(
            tick, use_animation
        )
    )
    take = SimpleNamespace(name="Take")
    document.animation_stacks = (take,)
    document.selected_animation_stack = take
    document.curves = {
        (1, "Lcl Rotation", "Z"): ([0, 1], [0.0, 9.0]),
        (2, "Lcl Rotation", "X"): ([0, 1], [0.0, -12.0]),
    }

    build = build_exact_rig_anm2(
        path,
        rig,
        document=document,
    )

    assert build.report["retarget_mode"] == "exact"
    normalization = build.report["source_global_normalization"]
    assert normalization["axis_conversion"] == "fbx_global_settings"
    assert normalization["axis_conversion_count"] == 1
    assert normalization["axis_conversion_source"] == "canonical_document_basis"
    assert normalization["bind_and_animation_share_normalizer"] is True
    np.testing.assert_allclose(
        normalization["axis_conversion_matrix"], basis, atol=1.0e-12
    )

    # The exact model bind and both animation samples must land in the same
    # canonical target basis.  Comparing reconstructed output globals catches
    # a basis applied to only bind or only animation, and catches a double unit
    # or signed-axis conversion as soon as the joints rotate.
    decoded = decode_samples(build.payload, [0.0, 1.0])
    authored_globals = {
        row.name: np.asarray(row.global_matrix4x4, dtype=float).reshape((4, 4))
        for row in contract.animation_nodes
    }
    bind_basis_corrections = {
        bone.name: np.linalg.inv(
            document.normalized_matrix_to_target_space(
                bone.name, document.bind_global_matrices[bone.name]
            )
        )
        @ authored_globals[bone.name]
        for bone in rig.bones
    }
    for frame_index, tick in enumerate((0, 1)):
        actual_globals = reconstruct_target_globals(
            rig, decoded.frames[frame_index].tracks
        )
        raw_globals = native_globals(tick, True)
        for bone in rig.bones:
            expected_global = document.normalized_matrix_to_target_space(
                bone.name, raw_globals[bone.name]
            ) @ bind_basis_corrections[bone.name]
            np.testing.assert_allclose(
                actual_globals[bone.name], expected_global, atol=3.0e-5
            )
            if frame_index == 0:
                np.testing.assert_allclose(
                    actual_globals[bone.name],
                    authored_globals[bone.name],
                    atol=3.0e-5,
                )
