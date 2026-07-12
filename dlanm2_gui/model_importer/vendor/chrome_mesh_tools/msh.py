from __future__ import annotations

import json
import math
import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable

from .binary import BinaryReadError, Reader
from .chunks import Chunk, MshFormatError, parse_chunk_tree

MSH_MAGIC = 0x0048534D  # b"MSH\0" interpreted as little-endian uint32

CHUNK_NAMES: dict[int, str] = {
    MSH_MAGIC: "MSH_ROOT",
    0x0001: "NODE_V1_CHROME5",
    0x0002: "NODE_V2_LEGACY_MATRIX",
    0x0003: "NODE_V3_CHROME6",
    0x0100: "LOD",
    0x0101: "POSITIONS_0",
    0x0171: "POSITIONS_1",
    0x0102: "NORMALS_0",
    0x0181: "NORMALS_1",
    0x0103: "TANGENTS_0",
    0x0191: "TANGENTS_1",
    0x0195: "BITANGENTS_0",
    0x0196: "BITANGENTS_1",
    0x0104: "MORPH_TARGETS",
    0x0110: "COLORS_0",
    0x0111: "COLORS_1",
    0x0112: "COLORS_2",
    0x0113: "COLORS_3",
    0x0120: "UVS_0",
    0x0121: "UVS_1",
    0x0122: "UVS_2",
    0x0123: "UVS_3",
    0x0130: "SKIN_WEIGHTS_FULL4",
    0x0131: "SKIN_WEIGHTS_COMPACT",
    0x0140: "INDICES_U16",
    0x0150: "SUBSETS_U16_RANGES",
    0x0151: "SUBSETS_U32_RANGES",
    0x0160: "VERTEX_FORMATS",
    0x0500: "MATERIAL_NAMES",
    0x0601: "COLLISION_TREE_0",
    0x0602: "COLLISION_TREE_1",
    0x0603: "COLLISION_TREE_2",
    0x0700: "SURFACE_NAMES",
    0x0800: "MESH_AUX_0",
    0x0801: "MESH_AUX_1",
}


def chunk_name(chunk_id: int) -> str:
    return CHUNK_NAMES.get(chunk_id, f"UNKNOWN_0x{chunk_id:04X}")


def _fixed_cstr(raw: bytes) -> str:
    return raw.split(b"\0", 1)[0].decode("utf-8", errors="replace")


def _float_list(data: bytes, offset: int, count: int) -> tuple[float, ...]:
    if offset + 4 * count > len(data):
        raise MshFormatError(
            f"need {count} floats at payload offset 0x{offset:X}, payload size is 0x{len(data):X}"
        )
    return struct.unpack_from(f"<{count}f", data, offset)


def _old_matrix44_to_matrix34(raw: bytes, offset: int) -> tuple[float, ...]:
    """Apply the exact Chrome 6 legacy-node conversion used by sub_18074B6C0.

    The old v1 node stores a 4x4 matrix. Chrome 6 selects/reorders twelve
    dwords into its 0x30-byte Matrix3x4 representation.
    """

    values = struct.unpack_from("<16f", raw, offset)
    order = (0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14)
    return tuple(values[index] for index in order)


@dataclass(frozen=True)
class VertexFormatInfo:
    position_format: int
    position_bias_or_scale: tuple[float, float, float, float]
    position_stride: int
    normal_format: int
    normal_scale: float
    normal_stride: int
    tangent_format: int
    tangent_scale: float
    tangent_stride: int
    uv_format: int
    uv_scale: float
    uv_stride: int
    raw_words: tuple[int, ...]

    @classmethod
    def parse(cls, payload: bytes) -> "VertexFormatInfo":
        if len(payload) != 0x3C:
            raise MshFormatError(
                f"0x160 vertex-format payload must be 0x3C bytes, got 0x{len(payload):X}"
            )
        words = struct.unpack("<15I", payload)
        return cls(
            position_format=words[0],
            position_bias_or_scale=struct.unpack_from("<4f", payload, 4),
            position_stride=words[5],
            normal_format=words[6],
            normal_scale=struct.unpack_from("<f", payload, 28)[0],
            normal_stride=words[8],
            tangent_format=words[9],
            tangent_scale=struct.unpack_from("<f", payload, 40)[0],
            tangent_stride=words[11],
            uv_format=words[12],
            uv_scale=struct.unpack_from("<f", payload, 52)[0],
            uv_stride=words[14],
            raw_words=words,
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "position": {
                "format": self.position_format,
                "bias_or_scale": list(self.position_bias_or_scale),
                "stride": self.position_stride,
            },
            "normal": {
                "format": self.normal_format,
                "scale": self.normal_scale,
                "stride": self.normal_stride,
            },
            "tangent": {
                "format": self.tangent_format,
                "scale": self.tangent_scale,
                "stride": self.tangent_stride,
            },
            "uv": {
                "format": self.uv_format,
                "scale": self.uv_scale,
                "stride": self.uv_stride,
            },
            "raw_words": [f"0x{x:08X}" for x in self.raw_words],
        }


@dataclass(frozen=True)
class SkinVertex:
    bone_indices: tuple[int, ...]
    weights_i16: tuple[int, ...]

    @property
    def weights(self) -> tuple[float, ...]:
        # Engine uses signed 15-bit full scale 0x7FFF in the source loader.
        return tuple(weight / 32767.0 for weight in self.weights_i16)

    def to_dict(self) -> dict[str, Any]:
        return {
            "bone_indices": list(self.bone_indices),
            "weights_i16": list(self.weights_i16),
            "weights_normalized": list(self.weights),
            "weight_sum": sum(self.weights),
        }


@dataclass(frozen=True)
class SurfaceSubset:
    material_index: int
    first_index: int
    index_count: int
    bone_palette: tuple[int, ...]
    source_chunk_id: int

    @property
    def material_or_surface_index(self) -> int:
        """Backward-compatible alias from the initial neutral field name."""

        return self.material_index

    def to_dict(self) -> dict[str, Any]:
        return {
            "material_index": self.material_index,
            "first_index": self.first_index,
            "index_count": self.index_count,
            "bone_palette": list(self.bone_palette),
            "bone_count": len(self.bone_palette),
            "source_chunk": f"0x{self.source_chunk_id:04X}",
        }


@dataclass(frozen=True)
class MorphTarget:
    name: str
    header_hex: str
    position_deltas: tuple[tuple[float, float, float], ...]

    def to_dict(self, preview: int = 4) -> dict[str, Any]:
        return {
            "name": self.name,
            "header_hex": self.header_hex,
            "delta_count": len(self.position_deltas),
            "delta_preview": [list(v) for v in self.position_deltas[:preview]],
        }


@dataclass
class MshLod:
    chunk: Chunk
    vertex_count: int
    index_count: int
    surface_count: int
    morph_target_count: int
    vertex_format: VertexFormatInfo | None = None
    streams: dict[int, bytes] = field(default_factory=dict)
    positions: tuple[tuple[float, float, float], ...] = ()
    normals: tuple[tuple[float, float, float], ...] = ()
    tangents: tuple[tuple[float, float, float], ...] = ()
    bitangents: tuple[tuple[float, float, float], ...] = ()
    uvs: tuple[tuple[float, float], ...] = ()
    indices: tuple[int, ...] = ()
    skin_vertices: tuple[SkinVertex, ...] = ()
    skin_influences_per_vertex: int | None = None
    subsets: tuple[SurfaceSubset, ...] = ()
    morph_targets: tuple[MorphTarget, ...] = ()
    warnings: list[str] = field(default_factory=list)

    @classmethod
    def parse(cls, chunk: Chunk) -> "MshLod":
        if chunk.chunk_id != 0x100:
            raise MshFormatError(f"expected LOD chunk 0x100, got 0x{chunk.chunk_id:X}")
        if len(chunk.payload) != 0x10:
            raise MshFormatError(
                f"LOD at 0x{chunk.offset:X} must have 16-byte payload, got {len(chunk.payload)}"
            )
        vertex_count, index_count, surface_count, morph_count = struct.unpack(
            "<4I", chunk.payload
        )
        lod = cls(chunk, vertex_count, index_count, surface_count, morph_count)
        for child in chunk.children:
            if child.chunk_id == 0x160:
                lod.vertex_format = VertexFormatInfo.parse(child.payload)
            else:
                lod.streams[child.chunk_id] = child.payload
        lod._decode_known_streams()
        return lod

    def _decode_vec3_stream(
        self, chunk_id: int, expected_stride: int | None, label: str
    ) -> tuple[tuple[float, float, float], ...]:
        raw = self.streams.get(chunk_id)
        if raw is None:
            return ()
        stride = expected_stride or 12
        if stride != 12:
            self.warnings.append(
                f"{label}: format stride {stride} is not decoded; raw stream retained"
            )
            return ()
        expected = self.vertex_count * stride
        if len(raw) != expected:
            self.warnings.append(
                f"{label}: expected {expected} bytes ({self.vertex_count} * {stride}), got {len(raw)}"
            )
            return ()
        return tuple(struct.iter_unpack("<3f", raw))

    def _decode_known_streams(self) -> None:
        vf = self.vertex_format
        self.positions = self._decode_vec3_stream(
            0x101, vf.position_stride if vf else 12, "positions"
        )
        self.normals = self._decode_vec3_stream(
            0x102, vf.normal_stride if vf else 12, "normals"
        )
        self.tangents = self._decode_vec3_stream(
            0x103, vf.tangent_stride if vf else 12, "tangents"
        )
        self.bitangents = self._decode_vec3_stream(
            0x195, vf.tangent_stride if vf else 12, "bitangents"
        )

        uv_raw = self.streams.get(0x120)
        if uv_raw is not None:
            stride = vf.uv_stride if vf else 8
            if stride == 8 and len(uv_raw) == self.vertex_count * 8:
                self.uvs = tuple(struct.iter_unpack("<2f", uv_raw))
            else:
                self.warnings.append(
                    f"uvs: expected {self.vertex_count * stride} bytes with stride {stride}, got {len(uv_raw)}"
                )

        index_raw = self.streams.get(0x140)
        if index_raw is not None:
            expected = self.index_count * 2
            if len(index_raw) == expected:
                self.indices = struct.unpack(f"<{self.index_count}H", index_raw)
            else:
                self.warnings.append(
                    f"indices: expected {expected} bytes, got {len(index_raw)}"
                )

        try:
            self._decode_skin_weights()
        except (BinaryReadError, struct.error, MshFormatError) as exc:
            self.warnings.append(f"skin weights: {exc}")

        try:
            self._decode_subsets()
        except (BinaryReadError, struct.error, MshFormatError) as exc:
            self.warnings.append(f"subsets: {exc}")

        try:
            self._decode_morph_targets()
        except (BinaryReadError, struct.error, MshFormatError) as exc:
            self.warnings.append(f"morph targets: {exc}")

    def _decode_skin_weights(self) -> None:
        if 0x131 in self.streams:
            reader = Reader(self.streams[0x131], label="MSH chunk 0x131")
            influences = reader.u8("influences per vertex")
            if influences not in (1, 2, 3):
                raise MshFormatError(
                    f"compact skin stream reports unsupported influence count {influences}"
                )
            rows: list[SkinVertex] = []
            for vertex_index in range(self.vertex_count):
                indices = tuple(
                    reader.u8(f"vertex {vertex_index} bone index {i}")
                    for i in range(influences)
                )
                if influences == 1:
                    weights = (0x7FFF,)
                else:
                    weights = tuple(
                        reader.i16(f"vertex {vertex_index} weight {i}")
                        for i in range(influences)
                    )
                rows.append(SkinVertex(indices, weights))
            reader.ensure_eof(allow_zero_padding=False)
            self.skin_influences_per_vertex = influences
            self.skin_vertices = tuple(rows)
            return

        if 0x130 in self.streams:
            raw = self.streams[0x130]
            expected = self.vertex_count * 12
            if len(raw) != expected:
                raise MshFormatError(
                    f"full4 skin stream expected {expected} bytes, got {len(raw)}"
                )
            rows = []
            for offset in range(0, len(raw), 12):
                indices = struct.unpack_from("<4B", raw, offset)
                weights = struct.unpack_from("<4h", raw, offset + 4)
                rows.append(SkinVertex(indices, weights))
            self.skin_influences_per_vertex = 4
            self.skin_vertices = tuple(rows)

    def _decode_subsets(self) -> None:
        chunk_id = 0x151 if 0x151 in self.streams else 0x150 if 0x150 in self.streams else None
        if chunk_id is None:
            return
        reader = Reader(self.streams[chunk_id], label=f"MSH chunk 0x{chunk_id:03X}")
        subsets: list[SurfaceSubset] = []
        for subset_index in range(self.surface_count):
            material_index = reader.u16(f"subset {subset_index} material/surface index")
            if chunk_id == 0x151:
                first_index = reader.u32(f"subset {subset_index} first index")
                index_count = reader.u32(f"subset {subset_index} index count")
            else:
                first_index = reader.u16(f"subset {subset_index} first index")
                index_count = reader.u16(f"subset {subset_index} index count")
            bone_count = reader.u16(f"subset {subset_index} bone count")
            palette = tuple(
                reader.u16(f"subset {subset_index} palette bone {i}")
                for i in range(bone_count)
            )
            subsets.append(
                SurfaceSubset(
                    material_index, first_index, index_count, palette, chunk_id
                )
            )
        reader.ensure_eof(allow_zero_padding=False)
        self.subsets = tuple(subsets)

    def _decode_morph_targets(self) -> None:
        raw = self.streams.get(0x104)
        if raw is None:
            return
        reader = Reader(raw, label="MSH chunk 0x104")
        targets: list[MorphTarget] = []
        for target_index in range(self.morph_target_count):
            header = reader.read(0x40, f"morph target {target_index} header")
            name = _fixed_cstr(header)
            delta_raw = reader.read(
                self.vertex_count * 12, f"morph target {target_index} position deltas"
            )
            deltas = tuple(struct.iter_unpack("<3f", delta_raw))
            targets.append(MorphTarget(name, header.hex(), deltas))
        reader.ensure_eof(allow_zero_padding=False)
        self.morph_targets = tuple(targets)

    def validate(self) -> list[str]:
        issues = list(self.warnings)
        if self.vertex_count and not self.positions:
            issues.append("vertex positions were not decoded")
        if self.index_count and len(self.indices) != self.index_count:
            issues.append(
                f"decoded index count {len(self.indices)} does not match header {self.index_count}"
            )
        if self.surface_count and len(self.subsets) != self.surface_count:
            issues.append(
                f"decoded subset count {len(self.subsets)} does not match header {self.surface_count}"
            )
        for subset_index, subset in enumerate(self.subsets):
            if subset.first_index + subset.index_count > self.index_count:
                issues.append(
                    f"subset {subset_index} index range {subset.first_index}+{subset.index_count} "
                    f"exceeds {self.index_count}"
                )
            if subset.index_count % 3:
                issues.append(
                    f"subset {subset_index} index count {subset.index_count} is not divisible by 3"
                )
            if self.skin_vertices and not subset.bone_palette:
                issues.append(f"subset {subset_index} has skinned vertices but no local bone palette")
        if self.skin_vertices:
            for vertex_index, vertex in enumerate(self.skin_vertices):
                if any(weight < 0 for weight in vertex.weights_i16):
                    issues.append(f"vertex {vertex_index} has negative source skin weight")
                    break
                total = sum(vertex.weights_i16)
                if abs(total - 0x7FFF) > 4:
                    issues.append(
                        f"vertex {vertex_index} skin weights sum to {total}, expected about 32767"
                    )
                    break
        return issues

    def to_dict(self, *, preview_vertices: int = 4) -> dict[str, Any]:
        stream_info = []
        for child in self.chunk.children:
            stream_info.append(
                {
                    "id": f"0x{child.chunk_id:04X}",
                    "name": chunk_name(child.chunk_id),
                    "offset": child.offset,
                    "data_size": child.header.data_size,
                    "chunk_size": child.header.chunk_size,
                }
            )
        return {
            "offset": self.chunk.offset,
            "vertex_count": self.vertex_count,
            "index_count": self.index_count,
            "triangle_count": self.index_count // 3,
            "surface_count": self.surface_count,
            "morph_target_count": self.morph_target_count,
            "vertex_format": self.vertex_format.to_dict() if self.vertex_format else None,
            "streams": stream_info,
            "positions_preview": [list(v) for v in self.positions[:preview_vertices]],
            "indices_preview": list(self.indices[: preview_vertices * 3]),
            "skin": {
                "present": bool(self.skin_vertices),
                "influences_per_vertex": self.skin_influences_per_vertex,
                "vertex_preview": [
                    row.to_dict() for row in self.skin_vertices[:preview_vertices]
                ],
            },
            "subsets": [subset.to_dict() for subset in self.subsets],
            "morph_targets": [target.to_dict() for target in self.morph_targets],
            "validation_issues": self.validate(),
        }


@dataclass
class MshNode:
    chunk: Chunk
    source_node_version: int
    node_type: int
    name: str
    parent_index: int
    stored_child_count: int
    lod_count: int
    local_matrix: tuple[float, ...]
    reference_matrix: tuple[float, ...]
    bounds: tuple[float, ...]
    tail_words: tuple[int, ...]
    lods: tuple[MshLod, ...]

    @classmethod
    def parse(cls, chunk: Chunk) -> "MshNode":
        payload = chunk.payload
        if chunk.chunk_id == 1:
            if len(payload) != 0xF0:
                raise MshFormatError(
                    f"Chrome 5 node chunk at 0x{chunk.offset:X} must be 0xF0 bytes, got 0x{len(payload):X}"
                )
            node_type = struct.unpack_from("<I", payload, 0)[0]
            name = _fixed_cstr(payload[4:68])
            parent_index, child_count = struct.unpack_from("<hH", payload, 68)
            lod_count = struct.unpack_from("<I", payload, 72)[0]
            local_matrix = _old_matrix44_to_matrix34(payload, 76)
            reference_matrix = _old_matrix44_to_matrix34(payload, 140)
            bounds = _float_list(payload, 204, 6)
            tail_words = struct.unpack_from("<3I", payload, 228)
            version = 1
        elif chunk.chunk_id in (2, 3):
            if len(payload) != 0xD0:
                raise MshFormatError(
                    f"Chrome 6 node chunk at 0x{chunk.offset:X} must be 0xD0 bytes, got 0x{len(payload):X}"
                )
            node_type = struct.unpack_from("<I", payload, 0)[0]
            name = _fixed_cstr(payload[4:68])
            parent_index, child_count = struct.unpack_from("<hH", payload, 68)
            lod_count = struct.unpack_from("<I", payload, 72)[0]
            local_matrix = _float_list(payload, 76, 12)
            reference_matrix = _float_list(payload, 124, 12)
            bounds = _float_list(payload, 172, 6)
            tail_words = struct.unpack_from("<3I", payload, 196)
            version = chunk.chunk_id
        else:
            raise MshFormatError(f"not a recognized node chunk: 0x{chunk.chunk_id:X}")

        lods = tuple(MshLod.parse(child) for child in chunk.children if child.chunk_id == 0x100)
        if len(lods) != lod_count:
            # Keep parsing useful even when a damaged file has a bad count.
            pass
        return cls(
            chunk,
            version,
            node_type,
            name,
            parent_index,
            child_count,
            lod_count,
            local_matrix,
            reference_matrix,
            bounds,
            tail_words,
            lods,
        )

    @property
    def is_root(self) -> bool:
        return self.parent_index < 0

    def to_dict(self, index: int) -> dict[str, Any]:
        return {
            "index": index,
            "offset": self.chunk.offset,
            "source_node_version": self.source_node_version,
            "node_type": self.node_type,
            "name": self.name,
            "parent_index": self.parent_index,
            "stored_child_count": self.stored_child_count,
            "stored_descendant_count": self.stored_child_count,
            "lod_count": self.lod_count,
            "local_matrix3x4": list(self.local_matrix),
            "reference_matrix3x4": list(self.reference_matrix),
            # The six floats are written by the Chrome source saver as center XYZ
            # followed by half-extents XYZ. Preserve the older research key as an
            # alias so existing machine-readable reports remain consumable.
            "bounds_center_half_extents": list(self.bounds),
            "bounds_center_span_candidate": list(self.bounds),
            "tail_words_unknown": [f"0x{x:08X}" for x in self.tail_words],
            "lods": [lod.to_dict() for lod in self.lods],
        }


@dataclass
class MshFile:
    source_path: str | None
    raw_data: bytes
    root: Chunk
    material_count: int
    surface_name_count: int
    node_count: int
    materials: tuple[str, ...]
    surface_names: tuple[str, ...]
    nodes: tuple[MshNode, ...]
    warnings: list[str] = field(default_factory=list)

    @classmethod
    def parse(cls, data: bytes, source_path: str | None = None) -> "MshFile":
        root = parse_chunk_tree(data)
        if root.chunk_id != MSH_MAGIC:
            raise MshFormatError(
                f"root id is 0x{root.chunk_id:08X}, expected MSH magic 0x{MSH_MAGIC:08X}"
            )
        if len(root.payload) != 12:
            raise MshFormatError(
                f"MSH root payload must be 12 bytes, got {len(root.payload)}"
            )
        node_count, material_count, surface_count = struct.unpack("<3I", root.payload)
        material_chunks = [c for c in root.children if c.chunk_id == 0x500]
        surface_chunks = [c for c in root.children if c.chunk_id == 0x700]
        node_chunks = [c for c in root.children if c.chunk_id in (1, 2, 3)]

        warnings: list[str] = []
        materials: tuple[str, ...] = ()
        if material_chunks:
            materials = cls._decode_fixed_name_table(
                material_chunks[0], material_count, "material"
            )
        elif material_count:
            warnings.append("root declares materials but chunk 0x500 is absent")

        surface_names: tuple[str, ...] = ()
        if surface_chunks:
            surface_names = cls._decode_fixed_name_table(
                surface_chunks[0], surface_count, "surface"
            )
        elif surface_count:
            warnings.append("root declares surface names but chunk 0x700 is absent")

        nodes = tuple(MshNode.parse(chunk) for chunk in node_chunks)
        if len(nodes) != node_count:
            warnings.append(
                f"root node count is {node_count}, parsed {len(nodes)} node chunks"
            )
        if len(materials) != material_count:
            warnings.append(
                f"root material count is {material_count}, decoded {len(materials)}"
            )
        if len(surface_names) != surface_count:
            warnings.append(
                f"root surface-name count is {surface_count}, decoded {len(surface_names)}"
            )

        parsed = cls(
            source_path,
            data,
            root,
            material_count,
            surface_count,
            node_count,
            materials,
            surface_names,
            nodes,
            warnings,
        )
        parsed.warnings.extend(parsed.validate_hierarchy())
        return parsed

    @classmethod
    def from_path(cls, path: str | Path) -> "MshFile":
        p = Path(path)
        return cls.parse(p.read_bytes(), str(p))

    @staticmethod
    def _decode_fixed_name_table(
        chunk: Chunk, count: int, label: str
    ) -> tuple[str, ...]:
        expected = count * 64
        if len(chunk.payload) != expected:
            raise MshFormatError(
                f"{label} table chunk 0x{chunk.chunk_id:X} at 0x{chunk.offset:X}: "
                f"expected {expected} bytes, got {len(chunk.payload)}"
            )
        return tuple(
            _fixed_cstr(chunk.payload[index * 64 : (index + 1) * 64])
            for index in range(count)
        )

    def serialize(self) -> bytes:
        return self.root.serialize()

    def is_lossless_roundtrip(self) -> bool:
        return self.serialize() == self.raw_data

    @property
    def node_source_versions(self) -> tuple[int, ...]:
        return tuple(sorted({node.source_node_version for node in self.nodes}))

    @property
    def has_skinning(self) -> bool:
        return any(lod.skin_vertices for node in self.nodes for lod in node.lods)

    @property
    def has_morph_targets(self) -> bool:
        return any(lod.morph_targets for node in self.nodes for lod in node.lods)

    def validate_hierarchy(self) -> list[str]:
        issues: list[str] = []
        node_count = len(self.nodes)
        children: list[list[int]] = [[] for _ in range(node_count)]
        roots: list[int] = []

        for index, node in enumerate(self.nodes):
            parent = node.parent_index
            if parent < -1:
                issues.append(
                    f"node {index} {node.name!r} parent {parent} is below the -1 root marker"
                )
            elif parent == -1:
                roots.append(index)
            elif parent >= node_count:
                issues.append(
                    f"node {index} {node.name!r} parent {parent} is outside node table"
                )
            else:
                children[parent].append(index)
                if parent >= index:
                    issues.append(
                        f"node {index} {node.name!r} parent {parent} does not precede the node"
                    )

        visiting: set[int] = set()
        descendant_counts: dict[int, int] = {}

        def count_descendants(index: int) -> int:
            if index in descendant_counts:
                return descendant_counts[index]
            if index in visiting:
                issues.append(f"node hierarchy cycle involving node {index}")
                return 0
            visiting.add(index)
            total = sum(1 + count_descendants(child) for child in children[index])
            visiting.remove(index)
            descendant_counts[index] = total
            return total

        for index in range(node_count):
            count_descendants(index)

        expected_order: list[int] = []
        visited: set[int] = set()

        def visit(index: int) -> None:
            if index in visited:
                return
            visited.add(index)
            expected_order.append(index)
            for child in children[index]:
                visit(child)

        for root in roots:
            visit(root)
        # Append unreachable nodes only to keep the diagnostic deterministic.
        for index in range(node_count):
            if index not in visited:
                visit(index)
        if expected_order != list(range(node_count)):
            issues.append(
                "node table is not in source-saver depth-first pre-order: "
                f"traversal={expected_order}"
            )

        for index, node in enumerate(self.nodes):
            expected_descendants = descendant_counts.get(index, 0)
            if node.stored_child_count != expected_descendants:
                issues.append(
                    f"node {index} {node.name!r} stores descendant count "
                    f"{node.stored_child_count}, computed {expected_descendants}"
                )
            if node.lod_count != len(node.lods):
                issues.append(
                    f"node {index} {node.name!r} declares {node.lod_count} LODs, parsed {len(node.lods)}"
                )
        return issues

    def to_dict(self, *, include_chunk_tree: bool = True) -> dict[str, Any]:
        chunk_histogram: dict[str, int] = {}
        for chunk in self.root.walk():
            key = f"0x{chunk.chunk_id:04X} {chunk_name(chunk.chunk_id)}"
            chunk_histogram[key] = chunk_histogram.get(key, 0) + 1
        result: dict[str, Any] = {
            "source_path": self.source_path,
            "file_size": len(self.raw_data),
            "root_version": self.root.version,
            "counts": {
                "materials": self.material_count,
                "surface_names": self.surface_name_count,
                "nodes": self.node_count,
            },
            "materials": list(self.materials),
            "surface_names": list(self.surface_names),
            "node_source_versions": list(self.node_source_versions),
            "has_skinning": self.has_skinning,
            "has_morph_targets": self.has_morph_targets,
            "lossless_roundtrip": self.is_lossless_roundtrip(),
            "chunk_histogram": chunk_histogram,
            "nodes": [node.to_dict(index) for index, node in enumerate(self.nodes)],
            "warnings": list(self.warnings),
        }
        if include_chunk_tree:
            result["chunk_tree"] = self.root.to_tree_dict()
        return result

    def write_json(self, path: str | Path, *, include_chunk_tree: bool = True) -> None:
        Path(path).write_text(
            json.dumps(self.to_dict(include_chunk_tree=include_chunk_tree), indent=2),
            encoding="utf-8",
        )

    def export_obj(self, path: str | Path, *, node_index: int = 0, lod_index: int = 0) -> None:
        if node_index < 0 or node_index >= len(self.nodes):
            raise IndexError(f"node index {node_index} outside 0..{len(self.nodes)-1}")
        node = self.nodes[node_index]
        if lod_index < 0 or lod_index >= len(node.lods):
            raise IndexError(f"LOD index {lod_index} outside 0..{len(node.lods)-1}")
        lod = node.lods[lod_index]
        if not lod.positions or not lod.indices:
            raise MshFormatError("OBJ export requires decoded float3 positions and uint16 indices")
        lines = [
            "# Exported by chrome-mesh-tools",
            f"# source={self.source_path or '<memory>'}",
            f"o {node.name or 'mesh_node'}",
        ]
        for x, y, z in lod.positions:
            lines.append(f"v {x:.9g} {y:.9g} {z:.9g}")
        if lod.uvs:
            for u, v in lod.uvs:
                lines.append(f"vt {u:.9g} {v:.9g}")
        if lod.normals:
            for x, y, z in lod.normals:
                lines.append(f"vn {x:.9g} {y:.9g} {z:.9g}")

        def vertex_ref(index: int) -> str:
            obj_index = index + 1
            if lod.uvs and lod.normals:
                return f"{obj_index}/{obj_index}/{obj_index}"
            if lod.uvs:
                return f"{obj_index}/{obj_index}"
            if lod.normals:
                return f"{obj_index}//{obj_index}"
            return str(obj_index)

        if lod.subsets:
            for subset_index, subset in enumerate(lod.subsets):
                material_name = (
                    self.materials[subset.material_index]
                    if subset.material_index < len(self.materials)
                    else f"material_{subset.material_index}"
                )
                lines.append(f"g subset_{subset_index}")
                lines.append(f"usemtl {material_name}")
                span = lod.indices[
                    subset.first_index : subset.first_index + subset.index_count
                ]
                for i in range(0, len(span) - 2, 3):
                    lines.append(
                        f"f {vertex_ref(span[i])} {vertex_ref(span[i+1])} {vertex_ref(span[i+2])}"
                    )
        else:
            for i in range(0, len(lod.indices) - 2, 3):
                lines.append(
                    f"f {vertex_ref(lod.indices[i])} {vertex_ref(lod.indices[i+1])} {vertex_ref(lod.indices[i+2])}"
                )
        Path(path).write_text("\n".join(lines) + "\n", encoding="utf-8")
