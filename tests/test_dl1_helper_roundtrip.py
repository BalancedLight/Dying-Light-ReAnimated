from __future__ import annotations

import copy
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest

from dlanm2_gui.anm2_fbx import (
    MOTION_HELPER_DESCRIPTOR,
    decode_anm2_animation,
    reconstruct_native_scene,
)
from dlanm2_gui.anm2_writer import build_payload_from_values
from dlanm2_gui.blender_fbx import FbxExportResult, export_anm2_to_fbx
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.chrome_rig_registry import (
    BUILTIN_MALE_RIG_REF,
    ChromeRigRegistry,
)
from dlanm2_gui.dl1_player_tpp import (
    DL1_PLAYER_TPP_HELPER_RIG_REF,
    PLAYER_1_TPP_HELPER_NAMES,
    PLAYER_1_TPP_MESH_ROOT_NAMES,
    build_dl1_player_tpp_helper_rig,
)
from dlanm2_gui.game_profiles import (
    DL1_GAME_ID,
    DL1_HELPER_RIG_REF,
    DL1_RIG_REF,
    GAME_PROFILES,
)
from dlanm2_gui.gui import MainWindow
from dlanm2_gui.oracle.smd_bind_pose import (
    anm2_cayley_vector_from_quaternion,
)
from dlanm2_gui.retarget_engines.legacy_exact_rig import (
    _Y_UP_TO_BLENDER,
    _rig_bind_globals,
    _row_differs_from_bind,
    _synthetic_tracks,
    _validate_armature_object_transform,
    _validate_exact_skeleton,
    _validate_native_display_rest,
    _validate_roundtrip_contract,
)
from dlanm2_gui.roundtrip_contract import (
    finalize_roundtrip_contract,
    resolve_native_roundtrip_metadata,
    roundtrip_sidecar_path,
    validate_roundtrip_contract_identity,
    write_roundtrip_sidecar,
)
from dlanm2_gui.target_package import validate_target_package


ROOT = Path(__file__).resolve().parents[1]
HELPER_RIG_PATH = ROOT / "reference" / "dl1" / "player_1_tpp_helpers.crig"


def _rig() -> ChromeRig:
    return ChromeRig.load(HELPER_RIG_PATH)


def _valid_contract(rig: ChromeRig) -> dict[str, object]:
    by_index = {bone.index: bone for bone in rig.bones}
    root = rig.bones[rig.root_index]
    refcamera = next(bone for bone in rig.bones if bone.name == "refcamera")
    descriptors = (
        MOTION_HELPER_DESCRIPTOR,
        root.descriptor,
        refcamera.descriptor,
    )
    contract: dict[str, object] = {
        "format": "dl-reanimated-native-roundtrip-contract",
        "schema_version": 1,
        "source_anm2_sha256": "0" * 64,
        "source_descriptors": list(descriptors),
        "rig_id": rig.rig_id,
        "rig_skeleton_hash": rig.skeleton_hash,
        "roundtrip_capable": True,
        "missing_source_descriptors": [],
        "expected_skeleton": [
            {
                "name": bone.name,
                "parent_name": (
                    None
                    if bone.parent_index < 0
                    else by_index[bone.parent_index].name
                ),
                "descriptor": bone.descriptor,
                "bind_translation": list(bone.bind_translation),
                "bind_rotation_wxyz": list(bone.bind_rotation_wxyz),
                "bind_scale": list(bone.bind_scale),
                "deform": bone.deform,
                "helper": bone.helper,
            }
            for bone in rig.bones
        ],
        "source_track_nodes": [
            {
                "descriptor": MOTION_HELPER_DESCRIPTOR,
                "node_name": "DLR_OffsetHelper_CCC3CDDF",
                "node_kind": "empty",
                "semantic": "motion_accumulator",
            },
            {
                "descriptor": root.descriptor,
                "node_name": root.name,
                "node_kind": "bone",
                "semantic": "",
            },
            {
                "descriptor": refcamera.descriptor,
                "node_name": refcamera.name,
                "node_kind": "bone",
                "semantic": "named_helper_bone",
            },
        ],
        "armature_object_name": "test_clip",
        "armature_object_transform_policy": "blender_identity_axis_export_v1",
    }
    return finalize_roundtrip_contract(contract)


def _fake_document(rig: ChromeRig, contract: dict[str, object]):
    armature_id = 10
    empty_id = 20
    guard_id = 30
    bone_ids = {bone.name: 1000 + bone.index for bone in rig.bones}
    model_names = {
        armature_id: "test_clip",
        empty_id: "DLR_OffsetHelper_CCC3CDDF",
        guard_id: str(contract["guard_name"]),
        **{object_id: name for name, object_id in bone_ids.items()},
    }
    model_subtypes = {
        armature_id: "Null",
        empty_id: "Null",
        guard_id: "Mesh",
        **{object_id: "LimbNode" for object_id in bone_ids.values()},
    }
    parents = {
        empty_id: [("OO", 0, [])],
        guard_id: [("OO", 0, [])],
    }
    parent_by_name: dict[str, str | None] = {}
    for bone in rig.bones:
        if bone.parent_index < 0:
            parents[bone_ids[bone.name]] = [("OO", armature_id, [])]
            parent_by_name[bone.name] = None
        else:
            parent = rig.bones[bone.parent_index].name
            parents[bone_ids[bone.name]] = [
                ("OO", bone_ids[parent], []),
            ]
            parent_by_name[bone.name] = parent
    scene = SimpleNamespace(
        model_names=model_names,
        model_subtypes=model_subtypes,
        model_ids=set(model_names),
        limb_ids=list(bone_ids.values()),
    )
    return SimpleNamespace(
        scene=scene,
        limb_models=dict(bone_ids),
        null_models={
            "test_clip": armature_id,
            "DLR_OffsetHelper_CCC3CDDF": empty_id,
        },
        parent_by_name=parent_by_name,
        parents=parents,
    )


def _write_static_payload(
    path: Path,
    rig: ChromeRig,
    descriptors: tuple[int, ...],
) -> None:
    by_descriptor = {
        descriptor: values
        for descriptor, values in zip(
            rig.descriptors,
            rig.bind_track_values(),
        )
    }
    values = [
        [list(by_descriptor[descriptor]) for descriptor in descriptors]
        for _ in range(2)
    ]
    header = replace(
        rig.make_header(frame_count=2),
        track_count=len(descriptors),
    )
    path.write_bytes(
        build_payload_from_values(
            header,
            descriptors,
            values,
            [[False] * 9 for _ in descriptors],
        )
    )


def test_helper_rig_is_deterministic_and_legacy_default_is_stable(
    tmp_path: Path,
) -> None:
    legacy = ChromeRig.load(ROOT / "reference" / "male_npc_infected.crig")
    generated = build_dl1_player_tpp_helper_rig(
        ROOT / "reference" / "player_1_tpp.smd",
        legacy,
        reference_anm2=(
            ROOT / "reference" / "infected_turn_90r.template.anm2"
        ),
    )
    bundled = _rig()

    assert generated.to_bytes() == HELPER_RIG_PATH.read_bytes()
    assert bundled.skeleton_hash == (
        "7eac5d67696034b6eca128e82fe999a58b1d73dc4a38fd696ea4f50e31cacfd3"
    )
    assert legacy.skeleton_hash == (
        "c82721e853715bfb176f7be21b13501fdf3d191c64195b9f335836aab1be5e1e"
    )
    assert len(bundled.bones) == 87
    assert sum(bone.deform for bone in bundled.bones) == 69
    assert tuple(bone.name for bone in bundled.bones if bone.helper) == (
        PLAYER_1_TPP_HELPER_NAMES
    )
    assert all(not bone.deform for bone in bundled.bones if bone.helper)
    assert not set(PLAYER_1_TPP_MESH_ROOT_NAMES) & {
        bone.name for bone in bundled.bones
    }
    assert len(bundled.descriptors) == 88
    package = validate_target_package(
        GAME_PROFILES[DL1_GAME_ID],
        ROOT,
        rig_ref=DL1_HELPER_RIG_REF,
    )
    assert package.status == "pass", package.errors
    assert package.smd_bone_count == 87
    assert package.crig_bone_count == 87

    profile = GAME_PROFILES[DL1_GAME_ID]
    assert profile.default_target_rig_ref == DL1_RIG_REF
    assert profile.compatible_builtin_rig_refs == (
        DL1_RIG_REF,
        DL1_HELPER_RIG_REF,
    )
    records = ChromeRigRegistry(tmp_path).records()
    assert any(
        row.rig_ref == DL1_PLAYER_TPP_HELPER_RIG_REF
        and row.builtin
        for row in records
    )


def test_named_helpers_are_armature_bones_and_unknown_tracks_are_empties(
    tmp_path: Path,
) -> None:
    rig = _rig()
    source = tmp_path / "all_helper_tracks.anm2"
    _write_static_payload(source, rig, tuple(rig.descriptors))
    animation = decode_anm2_animation(source)
    scene = reconstruct_native_scene(
        animation,
        rig,
        unknown_track_policy="helpers",
    )

    refcamera = next(row for row in scene.bones if row.name == "refcamera")
    offset = next(
        row
        for row in scene.bones
        if row.descriptor == MOTION_HELPER_DESCRIPTOR
    )
    assert refcamera.node_kind == "bone"
    assert refcamera.helper
    assert not refcamera.deform
    assert offset.node_kind == "empty"
    assert offset.helper


def test_legacy_dl1_export_does_not_enable_helper_roundtrip_contract(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    legacy = ChromeRig.load(ROOT / "reference" / "male_npc_infected.crig")
    source = tmp_path / "legacy_static.anm2"
    _write_static_payload(source, legacy, tuple(legacy.descriptors))
    captured = []

    def fake_blender(scene, output_path, **_kwargs):
        captured.append(scene)
        return FbxExportResult(
            str(Path(output_path).resolve()),
            scene.frame_count,
            scene.fps,
            sum(bone.node_kind == "bone" for bone in scene.bones),
            tuple(scene.warnings),
            "fake blender",
        )

    monkeypatch.setattr(
        "dlanm2_gui.blender_fbx.run_blender_export",
        fake_blender,
    )
    result = export_anm2_to_fbx(
        source,
        legacy,
        tmp_path / "legacy_static.fbx",
        unknown_track_policy="helpers",
    )
    assert captured
    assert captured[0].roundtrip_contract == {}
    assert result.roundtrip_metadata_path == ""
    assert not roundtrip_sidecar_path(tmp_path / "legacy_static.fbx").exists()


def test_helper_rig_marks_sidecar_and_drop_unknown_modes_one_way(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    rig = _rig()
    source = tmp_path / "helper_unknown.anm2"
    _write_static_payload(source, rig, tuple(rig.descriptors))
    captured = []

    def fake_blender(scene, output_path, **_kwargs):
        captured.append(scene)
        return FbxExportResult(
            str(Path(output_path).resolve()),
            scene.frame_count,
            scene.fps,
            sum(bone.node_kind == "bone" for bone in scene.bones),
            tuple(scene.warnings),
            "fake blender",
        )

    monkeypatch.setattr(
        "dlanm2_gui.blender_fbx.run_blender_export",
        fake_blender,
    )
    for policy in ("sidecar", "drop"):
        export_anm2_to_fbx(
            source,
            rig,
            tmp_path / f"{policy}.fbx",
            unknown_track_policy=policy,
        )
        contract = captured[-1].roundtrip_contract
        assert contract["roundtrip_capable"] is False
        assert contract["missing_source_descriptors"] == [
            MOTION_HELPER_DESCRIPTOR
        ]
        assert any("one-way export" in warning for warning in captured[-1].warnings)

    export_anm2_to_fbx(
        source,
        rig,
        tmp_path / "helpers.fbx",
        unknown_track_policy="helpers",
    )
    assert captured[-1].roundtrip_contract["roundtrip_capable"] is True
    assert captured[-1].roundtrip_contract["missing_source_descriptors"] == []


def test_dl1_auto_detection_requires_helper_specific_evidence(
    tmp_path: Path,
) -> None:
    legacy = ChromeRig.load(ROOT / "reference" / "male_npc_infected.crig")
    helper = _rig()
    legacy_path = tmp_path / "legacy_only.anm2"
    helper_path = tmp_path / "helper_evidence.anm2"
    _write_static_payload(
        legacy_path,
        helper,
        (legacy.bones[legacy.root_index].descriptor,),
    )
    refcamera = next(bone for bone in helper.bones if bone.name == "refcamera")
    assert refcamera.descriptor not in legacy.descriptors
    _write_static_payload(helper_path, helper, (refcamera.descriptor,))

    window = object.__new__(MainWindow)
    window.resource_root = ROOT
    window.rig_registry = ChromeRigRegistry(tmp_path / "installed")
    window._rig_paths_by_ref = {
        DL1_HELPER_RIG_REF: str(HELPER_RIG_PATH),
    }

    assert window._reverse_detect_rig(legacy_path)[0] == BUILTIN_MALE_RIG_REF
    assert window._reverse_detect_rig(helper_path)[0] == DL1_HELPER_RIG_REF


def test_contract_sidecar_fallback_agreement_and_stale_identity(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from dlanm2_gui import roundtrip_contract as service

    contract = _valid_contract(_rig())
    metadata = {
        "version": 5,
        "roundtrip_contract": contract,
    }
    fbx = tmp_path / "clip.fbx"
    fbx.write_bytes(b"fixture")
    sidecar = write_roundtrip_sidecar(fbx, metadata)
    assert sidecar == roundtrip_sidecar_path(fbx).resolve()

    monkeypatch.setattr(service, "embedded_native_metadata", lambda _doc: {})
    resolved, source = resolve_native_roundtrip_metadata(fbx, object())
    assert resolved == metadata
    assert source == "sidecar"

    monkeypatch.setattr(
        service,
        "embedded_native_metadata",
        lambda _doc: copy.deepcopy(metadata),
    )
    resolved, source = resolve_native_roundtrip_metadata(fbx, object())
    assert resolved == metadata
    assert source == "embedded_and_sidecar"

    conflicting = copy.deepcopy(metadata)
    conflicting["version"] = 4
    monkeypatch.setattr(
        service,
        "embedded_native_metadata",
        lambda _doc: conflicting,
    )
    with pytest.raises(ValueError, match="do not agree"):
        resolve_native_roundtrip_metadata(fbx, object())

    stale = copy.deepcopy(contract)
    stale["source_frame_count"] = 99
    with pytest.raises(ValueError, match="stale or malformed"):
        validate_roundtrip_contract_identity(stale)


def test_contract_rejects_structural_edits_and_unexpected_nodes() -> None:
    rig = _rig()
    contract = _valid_contract(rig)
    document = _fake_document(rig, contract)
    tracks = _synthetic_tracks(document)
    assert _validate_roundtrip_contract(
        contract,
        rig,
        tracks,
        document,
    ) == tuple(contract["source_descriptors"])
    with pytest.raises(ValueError, match="different rig"):
        _validate_roundtrip_contract(
            contract,
            ChromeRig.load(ROOT / "reference" / "male_npc_infected.crig"),
            tracks,
            document,
        )
    _validate_exact_skeleton(
        rig,
        document,
        meters_per_unit=0.01,
        native_roundtrip=True,
        strict_hierarchy=True,
    )
    ordinary = copy.deepcopy(document)
    ordinary.scene.model_names[40_000] = "ArtistReferenceMesh"
    ordinary.scene.model_subtypes[40_000] = "Mesh"
    ordinary.scene.model_ids.add(40_000)
    _validate_roundtrip_contract(
        contract,
        rig,
        _synthetic_tracks(ordinary),
        ordinary,
    )
    _validate_exact_skeleton(
        rig,
        ordinary,
        meters_per_unit=0.01,
        native_roundtrip=True,
        strict_hierarchy=True,
    )

    reparented = copy.deepcopy(document)
    empty_id = reparented.null_models["DLR_OffsetHelper_CCC3CDDF"]
    reparented.parents[empty_id] = [
        ("OO", reparented.null_models["test_clip"], []),
    ]
    with pytest.raises(ValueError, match="reparented"):
        _validate_roundtrip_contract(
            contract,
            rig,
            _synthetic_tracks(reparented),
            reparented,
        )

    deleted_empty = copy.deepcopy(document)
    deleted_id = deleted_empty.null_models.pop(
        "DLR_OffsetHelper_CCC3CDDF"
    )
    deleted_empty.scene.model_names.pop(deleted_id)
    deleted_empty.scene.model_subtypes.pop(deleted_id)
    deleted_empty.scene.model_ids.remove(deleted_id)
    with pytest.raises(ValueError, match="missing or renamed"):
        _validate_roundtrip_contract(
            contract,
            rig,
            _synthetic_tracks(deleted_empty),
            deleted_empty,
        )

    duplicated_empty = copy.deepcopy(document)
    duplicated_empty.scene.model_names[45_000] = (
        "DLR_OffsetHelper_CCC3CDDF"
    )
    duplicated_empty.scene.model_subtypes[45_000] = "Null"
    duplicated_empty.scene.model_ids.add(45_000)
    with pytest.raises(ValueError, match="duplicate DLR track"):
        _synthetic_tracks(duplicated_empty)

    stale_guard = copy.deepcopy(document)
    guard_id = next(
        object_id
        for object_id, name in stale_guard.scene.model_names.items()
        if name.startswith("DLR_RoundTripGuard_")
    )
    stale_guard.scene.model_names[guard_id] += "_stale"
    with pytest.raises(ValueError, match="bind guard"):
        _validate_roundtrip_contract(
            contract,
            rig,
            _synthetic_tracks(stale_guard),
            stale_guard,
        )

    renamed = copy.deepcopy(document)
    renamed.limb_models.pop("refcamera")
    renamed.scene.limb_ids.remove(
        next(
            object_id
            for object_id, name in renamed.scene.model_names.items()
            if name == "refcamera"
        )
    )
    with pytest.raises(ValueError, match="missing target bones"):
        _validate_exact_skeleton(
            rig,
            renamed,
            meters_per_unit=0.01,
            native_roundtrip=True,
            strict_hierarchy=True,
        )

    duplicated = copy.deepcopy(document)
    duplicate_id = 50_000
    duplicated.scene.model_names[duplicate_id] = "refcamera"
    duplicated.scene.model_subtypes[duplicate_id] = "LimbNode"
    duplicated.scene.model_ids.add(duplicate_id)
    duplicated.scene.limb_ids.append(duplicate_id)
    with pytest.raises(ValueError, match="duplicated bones"):
        _validate_exact_skeleton(
            rig,
            duplicated,
            meters_per_unit=0.01,
            native_roundtrip=True,
            strict_hierarchy=True,
        )

    reparented_bone = copy.deepcopy(document)
    reparented_bone.parent_by_name["refcamera"] = "bip01"
    with pytest.raises(ValueError, match="parent mismatch"):
        _validate_exact_skeleton(
            rig,
            reparented_bone,
            meters_per_unit=0.01,
            native_roundtrip=True,
            strict_hierarchy=True,
        )

    unexpected = copy.deepcopy(document)
    unexpected_id = 60_000
    unexpected.scene.model_names[unexpected_id] = "unexpected_bone"
    unexpected.scene.model_subtypes[unexpected_id] = "LimbNode"
    unexpected.scene.model_ids.add(unexpected_id)
    unexpected.scene.limb_ids.append(unexpected_id)
    with pytest.raises(ValueError, match="source has extra bones"):
        _validate_exact_skeleton(
            rig,
            unexpected,
            meters_per_unit=0.01,
            native_roundtrip=True,
            strict_hierarchy=True,
        )


def test_rest_pose_and_armature_object_transform_are_strict() -> None:
    rig = _rig()
    corrections = {bone.name: np.eye(4) for bone in rig.bones}
    game_bind = _rig_bind_globals(rig)
    expected = {
        bone.name: (
            _Y_UP_TO_BLENDER
            @ game_bind[bone.index]
            @ np.linalg.inv(_Y_UP_TO_BLENDER)
        )
        for bone in rig.bones
    }
    stock_bind = {
        name: np.linalg.inv(_Y_UP_TO_BLENDER) @ matrix
        for name, matrix in expected.items()
    }
    rest_document = SimpleNamespace(
        bind_global_matrices=stock_bind,
        null_models={},
    )
    _validate_native_display_rest(rig, rest_document, corrections)
    edited_bind = {name: matrix.copy() for name, matrix in stock_bind.items()}
    edited_bind["refcamera"][0, 3] += 0.01
    with pytest.raises(ValueError, match="rest pose was structurally edited"):
        _validate_native_display_rest(
            rig,
            SimpleNamespace(
                bind_global_matrices=edited_bind,
                null_models={},
            ),
            corrections,
        )

    contract = _valid_contract(rig)
    document = _fake_document(rig, contract)
    expected_rotation = np.asarray(
        ((1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, -1.0, 0.0)),
        dtype=float,
    )
    armature_matrix = np.eye(4)
    armature_matrix[:3, :3] = expected_rotation * 100.0
    document._local_matrix = lambda *_args, **_kwargs: armature_matrix
    _validate_armature_object_transform(
        contract,
        rig,
        document,
        [0, 1],
        meters_per_unit=0.01,
    )
    moved = armature_matrix.copy()
    moved[0, 3] = 1.0
    document._local_matrix = lambda *_args, **_kwargs: moved
    with pytest.raises(ValueError, match="armature object transform was edited"):
        _validate_armature_object_transform(
            contract,
            rig,
            document,
            [0, 1],
            meters_per_unit=0.01,
        )


def test_append_thresholds_treat_quaternion_equivalent_rows_as_noops() -> None:
    bone = next(bone for bone in _rig().bones if bone.name == "refcamera")
    equivalent_rotation = anm2_cayley_vector_from_quaternion(
        -np.asarray(bone.bind_rotation_wxyz)
    )
    row = [
        *map(float, equivalent_rotation),
        *bone.bind_translation,
        *bone.bind_scale,
    ]
    assert not _row_differs_from_bind(row, bone)

    row[3] += 1.9e-5
    assert not _row_differs_from_bind(row, bone)
    row[3] += 0.2e-5
    assert _row_differs_from_bind(row, bone)

    row = [
        *map(float, equivalent_rotation),
        *bone.bind_translation,
        *bone.bind_scale,
    ]
    row[6] += 1.9e-5
    assert not _row_differs_from_bind(row, bone)
    row[6] += 0.2e-5
    assert _row_differs_from_bind(row, bone)

    def multiply(left: np.ndarray, right: np.ndarray) -> np.ndarray:
        w, x, y, z = left
        rw, rx, ry, rz = right
        return np.asarray(
            (
                w * rw - x * rx - y * ry - z * rz,
                w * rx + x * rw + y * rz - z * ry,
                w * ry - x * rz + y * rw + z * rx,
                w * rz + x * ry - y * rx + z * rw,
            )
        )

    for degrees, expected in ((0.009, False), (0.011, True)):
        radians = np.radians(degrees)
        delta = np.asarray(
            (np.cos(radians / 2.0), np.sin(radians / 2.0), 0.0, 0.0)
        )
        rotated = multiply(delta, np.asarray(bone.bind_rotation_wxyz))
        rotation = anm2_cayley_vector_from_quaternion(rotated)
        row = [
            *map(float, rotation),
            *bone.bind_translation,
            *bone.bind_scale,
        ]
        assert _row_differs_from_bind(row, bone) is expected
