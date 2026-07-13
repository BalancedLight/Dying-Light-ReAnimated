from __future__ import annotations

import struct

import pytest

from dlanm2_gui.model_importer.vendor.chrome_mesh_tools.compact_mesh import (
    COMPACT_ENTITY_BOUNDS,
    COMPACT_ENTITY_CHILD_COUNT,
    COMPACT_ENTITY_FLAGS,
    COMPACT_ENTITY_LOCAL_MATRIX,
    COMPACT_ENTITY_LOD_COUNT,
    COMPACT_ENTITY_NAME_PTR,
    COMPACT_ENTITY_PARENT_INDEX,
    COMPACT_ENTITY_REFERENCE_MATRIX,
    COMPACT_ENTITY_STRIDE,
    COMPACT_ENTITY_TYPE,
    COMPACT_HEADER_ENTITY_COUNT,
    COMPACT_HEADER_ENTITY_TABLE_PTR,
    COMPACT_HEADER_ROOT_COUNT,
    parse_compact_mesh_payload,
)


def _translation(x: float, y: float, z: float) -> tuple[float, ...]:
    return (
        1.0, 0.0, 0.0, x,
        0.0, 1.0, 0.0, y,
        0.0, 0.0, 1.0, z,
    )


def _compact_payload() -> bytes:
    table_offset = 0xB0
    names = (b"root\0", b"child\0")
    names_offset = table_offset + 2 * COMPACT_ENTITY_STRIDE
    payload = bytearray(names_offset + sum(len(name) for name in names))
    struct.pack_into("<Q", payload, COMPACT_HEADER_ENTITY_TABLE_PTR, table_offset + 1)
    struct.pack_into("<I", payload, COMPACT_HEADER_ENTITY_COUNT, 2)
    struct.pack_into("<I", payload, COMPACT_HEADER_ROOT_COUNT, 1)

    rows = (
        {
            "local": _translation(1.0, 2.0, 3.0),
            "reference": _translation(-1.0, -2.0, -3.0),
            "bounds": (0.0, 0.0, 0.0, 1.0, 1.0, 1.0),
            "parent": -1,
            "children": 1,
        },
        {
            "local": _translation(0.0, 4.0, 0.0),
            "reference": _translation(-1.0, -6.0, -3.0),
            "bounds": (1.0, 0.0, 0.0, 0.5, 0.5, 0.5),
            "parent": 0,
            "children": 0,
        },
    )
    cursor = names_offset
    for index, (row, name) in enumerate(zip(rows, names)):
        base = table_offset + index * COMPACT_ENTITY_STRIDE
        struct.pack_into("<12f", payload, base + COMPACT_ENTITY_LOCAL_MATRIX, *row["local"])
        struct.pack_into(
            "<12f", payload, base + COMPACT_ENTITY_REFERENCE_MATRIX, *row["reference"]
        )
        struct.pack_into("<6f", payload, base + COMPACT_ENTITY_BOUNDS, *row["bounds"])
        struct.pack_into("<Q", payload, base + COMPACT_ENTITY_NAME_PTR, cursor + 1)
        struct.pack_into("<I", payload, base + COMPACT_ENTITY_FLAGS, 0x4301)
        struct.pack_into("<h", payload, base + COMPACT_ENTITY_PARENT_INDEX, row["parent"])
        payload[base + COMPACT_ENTITY_TYPE] = 8
        payload[base + COMPACT_ENTITY_CHILD_COUNT] = row["children"]
        payload[base + COMPACT_ENTITY_LOD_COUNT] = 0
        payload[cursor:cursor + len(name)] = name
        cursor += len(name)
    return bytes(payload)


def test_compact_parser_reports_local_reference_and_reconstructed_global_matrices() -> None:
    compact = parse_compact_mesh_payload(_compact_payload())
    report = compact.to_dict()

    assert compact.entities[0].local_matrix == pytest.approx(_translation(1.0, 2.0, 3.0))
    assert compact.entities[1].reference_matrix == pytest.approx(
        _translation(-1.0, -6.0, -3.0)
    )
    assert report["entities"][0]["global_translation_xyz"] == pytest.approx(
        [1.0, 2.0, 3.0]
    )
    assert report["entities"][1]["global_translation_xyz"] == pytest.approx(
        [1.0, 6.0, 3.0]
    )
    assert report["entities"][1]["global_reference_identity_max_abs_error"] == pytest.approx(0.0)


def test_compact_bone_bounds_are_aggregated_in_global_bind_space() -> None:
    report = parse_compact_mesh_payload(_compact_payload()).to_dict()
    bounds = report["bone_bounds_global_aggregate"]

    assert bounds["bone_count"] == 2
    assert bounds["contributing_bone_count"] == 2
    assert bounds["collapsed_bone_count"] == 0
    assert bounds["minimum_xyz"] == pytest.approx([0.0, 1.0, 2.0])
    assert bounds["maximum_xyz"] == pytest.approx([2.5, 6.5, 4.0])
    assert bounds["center_xyz"] == pytest.approx([1.25, 3.75, 3.0])
    assert bounds["half_extents_xyz"] == pytest.approx([1.25, 2.75, 1.0])

