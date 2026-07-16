from __future__ import annotations

import io
import json
import zipfile

import numpy as np
import pytest

from dlanm2_gui.model_importer.crig import (
    build_crig_from_rig_contract_bytes,
    write_prebuilt_crig_payload,
)
from dlanm2_gui.model_importer.compiler_bridge import (
    ModelCompileError,
    preflight_model_compile,
)
from dlanm2_gui.model_importer.model_validation import validate_model_bind_skin
from dlanm2_gui.model_importer.msh_builder import _BuildVertex, _MeshChunk, _chunk_to_lod
from dlanm2_gui.model_importer.rig_contract import AuthoredRigContract
from dlanm2_gui.model_importer.skin_partition import (
    GlobalSkinInfluence,
    WeightedTriangle,
    partition_weighted_triangles,
    remap_global_influences_to_local,
    validate_local_palette_round_trip,
)
from dlanm2_gui.model_importer.vendor.chrome_mesh_tools.writer import (
    SourceLod,
    SourceMsh,
    SourceNode,
    SourceSkinVertex,
    SourceSubset,
)


def _matrix3x4(matrix: np.ndarray) -> tuple[float, ...]:
    return tuple(float(matrix[row, column]) for row in range(3) for column in range(4))


def _vertex(
    position: tuple[float, float, float],
    influences: list[tuple[int, float]],
    *,
    uv: tuple[float, float] = (0.0, 0.0),
    normal: tuple[float, float, float] = (0.0, 0.0, 1.0),
) -> _BuildVertex:
    point = np.asarray(position, dtype=float)
    return _BuildVertex(
        position=point,
        source_position=point.copy(),
        normal=np.asarray(normal, dtype=float),
        uv=uv,
        color=(255, 255, 255, 255),
        influences=influences,
    )


def _large_source() -> tuple[SourceMsh, AuthoredRigContract]:
    nodes: list[SourceNode] = []
    global_matrix = np.eye(4, dtype=float)
    for index in range(320):
        local = np.eye(4, dtype=float)
        if index:
            local[0, 3] = 0.01
            global_matrix = global_matrix @ local
        else:
            global_matrix = local.copy()
        nodes.append(
            SourceNode(
                name=f"bone_{index:03d}",
                node_type=8,
                parent_index=index - 1,
                local_matrix=_matrix3x4(local),
                reference_matrix=_matrix3x4(np.linalg.inv(global_matrix)),
            )
        )

    chunk = _MeshChunk(
        node_name="body_region",
        material_index=0,
        vertices=[
            _vertex((0.0, 0.0, 0.0), [(300, 0.75), (319, 0.25)]),
            _vertex((1.0, 0.0, 0.0), [(310, 1.0)]),
            _vertex((0.0, 1.0, 0.0), [(319, 1.0)]),
        ],
        bone_palette=(300, 310, 319),
        source_triangle_count=1,
        maximum_influences=2,
    )
    lod = _chunk_to_lod(chunk)
    nodes.append(SourceNode("body_region", node_type=2, lods=(lod,)))
    nodes.append(SourceNode("large_bounds", node_type=1, bounds=(0, 0, 0, 1, 1, 1)))
    source = SourceMsh(
        materials=("test.mat",),
        surface_names=("Flesh",),
        nodes=tuple(nodes),
    )
    contract = AuthoredRigContract.from_source_msh(
        source,
        source_fbx_sha256="a" * 64,
        source_model_name="large.fbx",
        authored_msh_resource_name="large",
        coordinate_contract={"resolved_orientation_policy": "none"},
    )
    return source, contract


def test_320_node_hierarchy_uses_subset_local_uint8_indexes() -> None:
    source, contract = _large_source()

    source.validate()
    payload = source.build()
    assert payload
    assert len(contract.nodes) == 322
    lod = source.nodes[320].lods[0]
    palette = lod.subsets[0].bone_palette
    assert palette == (300, 310, 319)
    assert max(index for row in lod.skin_vertices for index in row.bone_indices) == 2
    assert [palette[row.bone_indices[0]] for row in lod.skin_vertices] == [300, 310, 319]

    validation = validate_model_bind_skin(source, contract)
    assert validation["status"] == "pass"
    assert validation["palette_resolution_count"] == 4
    assert validation["maximum_bind_skin_error"] < 1.0e-10


def test_generated_crig_uses_the_complete_authored_contract_bind() -> None:
    _source, contract = _large_source()

    payload, report = build_crig_from_rig_contract_bytes(contract, name="large")
    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        skeleton = json.loads(archive.read("skeleton.json"))
        manifest = json.loads(archive.read("manifest.json"))

    assert len(skeleton["bones"]) == 320
    assert skeleton["bones"][319]["parent_index"] == 318
    assert skeleton["bones"][319]["bind_translation"] == pytest.approx([0.01, 0.0, 0.0])
    assert manifest["extensions"]["authored_bind_hash"] == contract.bind_hash
    assert manifest["extensions"]["authored_rig_contract_id"] == contract.contract_id
    assert report["maximum_crig_local_roundtrip_error"] < 1.0e-10
    assert report["in_memory_validation"] == {
        "status": "pass",
        "chrome_rig_package": "pass",
        "anm2_writer_capacity": "pass",
    }


def test_contract_identity_changes_for_renamed_same_bind_and_loads_legacy_id() -> None:
    _source, original = _large_source()
    renamed_source, _unused_contract = _large_source()
    renamed_source.nodes[319].name = "renamed_terminal_bone"
    renamed = AuthoredRigContract.from_source_msh(
        renamed_source,
        source_fbx_sha256="c" * 64,
        source_model_name="renamed.fbx",
        authored_msh_resource_name="renamed",
        coordinate_contract={"resolved_orientation_policy": "none"},
    )

    assert renamed.bind_hash == original.bind_hash
    assert renamed.skeleton_hash != original.skeleton_hash
    assert renamed.descriptor_hash != original.descriptor_hash
    assert renamed.contract_id != original.contract_id

    legacy_payload = renamed.to_dict()
    legacy_payload["contract_id"] = f"authored:{renamed.bind_hash[:24]}"
    legacy_payload.pop("bind_hash")
    legacy_payload.pop("skeleton_hash")
    legacy_payload.pop("descriptor_hash")
    loaded = AuthoredRigContract.from_dict(legacy_payload)

    assert loaded.contract_id == legacy_payload["contract_id"]
    assert loaded.bind_hash == renamed.bind_hash
    assert loaded.skeleton_hash == renamed.skeleton_hash
    assert loaded.descriptor_hash == renamed.descriptor_hash
    assert loaded.validate()["contract_identity_scheme"] == "legacy_bind_only_v1"


def test_compile_preflight_rejects_renamed_same_bind_stale_crig(tmp_path) -> None:
    _original_source, original = _large_source()
    renamed_source, _unused_contract = _large_source()
    renamed_source.nodes[319].name = "renamed_terminal_bone"
    renamed = AuthoredRigContract.from_source_msh(
        renamed_source,
        source_fbx_sha256="d" * 64,
        source_model_name="renamed.fbx",
        authored_msh_resource_name="renamed",
        coordinate_contract={"resolved_orientation_policy": "none"},
    )
    legacy_id = f"authored:{original.bind_hash[:24]}"
    original_payload = original.to_dict()
    original_payload["contract_id"] = legacy_id
    legacy_original = AuthoredRigContract.from_dict(original_payload)
    renamed_payload = renamed.to_dict()
    renamed_payload["contract_id"] = legacy_id
    legacy_renamed = AuthoredRigContract.from_dict(renamed_payload)

    crig_payload, crig_report = build_crig_from_rig_contract_bytes(
        legacy_original,
        name="original_names",
    )
    stale_crig, _write_report = write_prebuilt_crig_payload(
        crig_payload,
        crig_report,
        tmp_path / "original_names.crig",
    )
    source_path = tmp_path / "renamed.msh"
    source_path.write_bytes(renamed_source.build())
    report = {
        "resource_name": "renamed",
        "effective_mode": "exact_rig",
        "authored_rig_contract": legacy_renamed.to_dict(),
        # The report selection claims the current legacy identity, while its
        # path still points at the same-bind CRIG generated before the rename.
        "generated_crig": {
            "path": str(stale_crig),
            "bind_hash": legacy_renamed.bind_hash,
            "skeleton_hash": legacy_renamed.skeleton_hash,
            "descriptor_hash": legacy_renamed.descriptor_hash,
            "contract_id": legacy_id,
        },
    }

    with pytest.raises(ModelCompileError, match="stale authored skeleton identity"):
        preflight_model_compile(source_msh=source_path, source_report=report)


def test_generated_crig_rejects_anm2_writer_over_capacity_in_memory() -> None:
    identity = _matrix3x4(np.eye(4, dtype=float))
    source = SourceMsh(
        materials=("test.mat",),
        surface_names=(),
        nodes=tuple(
            SourceNode(
                name=f"capacity_bone_{index:04d}",
                node_type=8,
                parent_index=-1,
                local_matrix=identity,
                reference_matrix=identity,
            )
            for index in range(2048)
        ),
    )
    contract = AuthoredRigContract.from_source_msh(
        source,
        source_fbx_sha256="b" * 64,
        source_model_name="over_capacity.fbx",
        authored_msh_resource_name="over_capacity",
        coordinate_contract={"resolved_orientation_policy": "none"},
    )

    with pytest.raises(ValueError) as caught:
        build_crig_from_rig_contract_bytes(contract, name="over_capacity")

    message = str(caught.value)
    assert "failed full in-memory ChromeRig/ANM2 writer validation before output" in message
    assert "2048 animation tracks" in message
    assert "64 KiB ANM2 page size" in message
    assert "Reduce the number of BONE/HELPER animation entities" in message


def test_prebuilt_crig_writer_validates_before_creating_output(tmp_path) -> None:
    destination = tmp_path / "must_not_exist" / "invalid.crig"

    with pytest.raises(ValueError, match="failed full in-memory"):
        write_prebuilt_crig_payload(
            b"not a Chrome Rig",
            {"name": "invalid", "bone_count": 1},
            destination,
        )

    assert not destination.exists()
    assert not destination.parent.exists()


def test_partitioning_is_stable_per_material_and_flushes_at_256() -> None:
    triangles = []
    for index in range(260):
        influence = (GlobalSkinInfluence(index, 1.0),)
        triangles.append(
            WeightedTriangle(
                source_triangle_index=index,
                material_index=0,
                vertex_influences=(influence, influence, influence),
                vertex_keys=(
                    (index, 0),
                    (index, 1),
                    (index, 2),
                ),
            )
        )
    other = (GlobalSkinInfluence(300, 1.0),)
    triangles.append(
        WeightedTriangle(
            source_triangle_index=260,
            material_index=1,
            vertex_influences=(other, other, other),
            vertex_keys=((260, 0), (260, 1), (260, 2)),
        )
    )

    first = partition_weighted_triangles(triangles)
    second = partition_weighted_triangles(reversed(triangles))

    assert [len(row.global_palette) for row in first] == [256, 4, 1]
    assert first[0].global_palette == tuple(range(256))
    assert first[1].global_palette == tuple(range(256, 260))
    # Reversing source order changes triangle order but never palette ordering,
    # material isolation, or the <=256 invariant.
    assert all(tuple(sorted(row.global_palette)) == row.global_palette for row in second)
    assert all(len(row.global_palette) <= 256 for row in (*first, *second))
    assert {row.material_index for row in first} == {0, 1}


def test_global_to_local_round_trip_never_writes_global_node_number() -> None:
    global_rows = ((300, 0.25), (319, 0.75))
    palette = (300, 310, 319)

    local_rows = remap_global_influences_to_local(global_rows, palette)

    assert local_rows == ((0, 0.25), (2, 0.75))
    assert all(index <= 0xFF for index, _weight in local_rows)
    validate_local_palette_round_trip(local_rows, palette, global_rows)


def test_complete_vertex_key_preserves_uv_seams_and_deduplicates_exact_corners() -> None:
    shared = _vertex((0.0, 0.0, 0.0), [(300, 1.0)], uv=(0.0, 0.0))
    uv_seam = _vertex((0.0, 0.0, 0.0), [(300, 1.0)], uv=(1.0, 0.0))
    chunk = _MeshChunk(
        "seams",
        0,
        vertices=[
            shared,
            _vertex((1.0, 0.0, 0.0), [(300, 1.0)]),
            _vertex((0.0, 1.0, 0.0), [(300, 1.0)]),
            uv_seam,
            _vertex((0.0, 1.0, 0.0), [(300, 1.0)]),
            _vertex((-1.0, 0.0, 0.0), [(300, 1.0)]),
        ],
        bone_palette=(300,),
    )

    lod = _chunk_to_lod(chunk)

    assert len(lod.indices) == 6
    assert lod.vertex_count == 5
    assert lod.indices[0] != lod.indices[3]


def test_complete_vertex_key_preserves_hard_normal_seams() -> None:
    smooth = _vertex((0.0, 0.0, 0.0), [(300, 1.0)])
    hard = _vertex(
        (0.0, 0.0, 0.0),
        [(300, 1.0)],
        normal=(0.0, 1.0, 0.0),
    )
    chunk = _MeshChunk(
        "hard_normal_seam",
        0,
        vertices=[
            smooth,
            _vertex((1.0, 0.0, 0.0), [(300, 1.0)]),
            _vertex((0.0, 1.0, 0.0), [(300, 1.0)]),
            hard,
            _vertex((0.0, 1.0, 0.0), [(300, 1.0)]),
            _vertex((-1.0, 0.0, 0.0), [(300, 1.0)]),
        ],
        bone_palette=(300,),
    )

    lod = _chunk_to_lod(chunk)

    assert len(lod.indices) == 6
    assert lod.vertex_count == 5
    assert lod.indices[0] != lod.indices[3]


def test_partitioning_splits_before_the_unique_vertex_limit() -> None:
    influence = (GlobalSkinInfluence(7, 1.0),)
    triangles = (
        WeightedTriangle(
            source_triangle_index=0,
            material_index=0,
            vertex_influences=(influence, influence, influence),
            vertex_keys=((0, 0), (0, 1), (0, 2)),
        ),
        WeightedTriangle(
            source_triangle_index=1,
            material_index=0,
            vertex_influences=(influence, influence, influence),
            vertex_keys=((1, 0), (1, 1), (1, 2)),
        ),
    )

    partitions = partition_weighted_triangles(triangles, maximum_vertices=3)

    assert [row.triangle_indices for row in partitions] == [(0,), (1,)]
    assert [row.unique_vertex_count for row in partitions] == [3, 3]


def test_palette_overflow_is_rejected_before_serialization() -> None:
    lod = SourceLod(
        positions=((0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0)),
        indices=(0, 1, 2),
        skin_vertices=(
            SourceSkinVertex((0,), (1.0,)),
            SourceSkinVertex((0,), (1.0,)),
            SourceSkinVertex((0,), (1.0,)),
        ),
        subsets=(SourceSubset(0, 0, 3, tuple(range(257))),),
    )

    with pytest.raises(ValueError, match="at most 256"):
        lod.validate(material_count=1, node_count=300)


def test_large_source_compile_preflight_accepts_total_hierarchy_over_256(
    tmp_path,
) -> None:
    source, contract = _large_source()
    path = tmp_path / "large.msh"
    path.write_bytes(source.build())
    report = {
        "resource_name": "large",
        "effective_mode": "exact_rig",
        "authored_rig_contract": contract.to_dict(),
    }

    audit = preflight_model_compile(source_msh=path, source_report=report)

    assert audit["ready"] is True
    assert audit["node_count"] == 322


def test_compile_preflight_rejects_stale_generated_crig_bind_before_output(
    tmp_path,
) -> None:
    source, contract = _large_source()
    path = tmp_path / "large.msh"
    path.write_bytes(source.build())
    report = {
        "resource_name": "large",
        "effective_mode": "exact_rig",
        "authored_rig_contract": contract.to_dict(),
        "generated_crig": {
            "bind_hash": "stale-bind",
            "contract_id": contract.contract_id,
        },
    }

    with pytest.raises(ModelCompileError, match="stale generated CRIG"):
        preflight_model_compile(source_msh=path, source_report=report)
