from __future__ import annotations

import json
import math
import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Sequence

from .chunks import ChunkHeader
from .msh import MSH_MAGIC

IDENTITY_MATRIX3X4: tuple[float, ...] = (
    1.0,
    0.0,
    0.0,
    0.0,
    0.0,
    1.0,
    0.0,
    0.0,
    0.0,
    0.0,
    1.0,
    0.0,
)

# Source node v3 payload +0xC4 is ``msh_node.flags``. These names are
# recovered from the Dead Island linker/debug dump and Dying Light's editor
# compiler. Bit 0 is copied to the compact entity and gates the engine's
# animation-root/default-transform path; bit 1 requests collision-tree use.
MSH_NODE_FLAG_ANIMATED = 0x1
MSH_NODE_FLAG_COLLTREE = 0x2


def pack_chunk(
    chunk_id: int,
    payload: bytes = b"",
    children: Iterable[bytes] = (),
    *,
    version: int = 0,
) -> bytes:
    child_blob = b"".join(children)
    chunk_size = ChunkHeader.SIZE + len(payload) + len(child_blob)
    return struct.pack("<4I", chunk_id, version, chunk_size, len(payload)) + payload + child_blob


def _fixed_name(name: str, size: int = 64) -> bytes:
    encoded = name.encode("utf-8")
    if len(encoded) >= size:
        raise ValueError(f"name {name!r} is {len(encoded)} bytes; maximum is {size - 1}")
    return encoded + b"\0" * (size - len(encoded))


def _vec3_bytes(values: Sequence[Sequence[float]], label: str) -> bytes:
    out = bytearray()
    for index, value in enumerate(values):
        if len(value) != 3:
            raise ValueError(f"{label}[{index}] must contain exactly 3 floats")
        out += struct.pack("<3f", *map(float, value))
    return bytes(out)


def _vec2_bytes(values: Sequence[Sequence[float]], label: str) -> bytes:
    out = bytearray()
    for index, value in enumerate(values):
        if len(value) != 2:
            raise ValueError(f"{label}[{index}] must contain exactly 2 floats")
        out += struct.pack("<2f", *map(float, value))
    return bytes(out)


def _quantize_weights(weights: Sequence[float]) -> tuple[int, ...]:
    if not weights:
        raise ValueError("skin weights cannot be empty")
    clean = [max(0.0, float(value)) for value in weights]
    total = sum(clean)
    if total <= 0.0:
        raise ValueError("skin weights must contain at least one positive value")
    normalized = [value / total for value in clean]
    raw = [int(math.floor(value * 32767.0)) for value in normalized]
    remainder = 32767 - sum(raw)
    if remainder:
        largest = max(range(len(normalized)), key=normalized.__getitem__)
        raw[largest] += remainder
    return tuple(raw)


@dataclass(frozen=True)
class SourceSkinVertex:
    bone_indices: tuple[int, ...]
    weights: tuple[float, ...]

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "SourceSkinVertex":
        return cls(
            tuple(int(value) for value in data["bone_indices"]),
            tuple(float(value) for value in data["weights"]),
        )


@dataclass(frozen=True)
class SourceSubset:
    material_index: int
    first_index: int
    index_count: int
    bone_palette: tuple[int, ...] = ()

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "SourceSubset":
        return cls(
            int(data["material_index"]),
            int(data["first_index"]),
            int(data["index_count"]),
            tuple(int(value) for value in data.get("bone_palette", [])),
        )


@dataclass(frozen=True)
class SourceMorphTarget:
    name: str
    position_deltas: tuple[tuple[float, float, float], ...]

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "SourceMorphTarget":
        return cls(
            str(data["name"]),
            tuple(tuple(map(float, row)) for row in data["position_deltas"]),
        )


@dataclass
class SourceLod:
    positions: tuple[tuple[float, float, float], ...]
    indices: tuple[int, ...]
    subsets: tuple[SourceSubset, ...]
    normals: tuple[tuple[float, float, float], ...] = ()
    tangents: tuple[tuple[float, float, float], ...] = ()
    bitangents: tuple[tuple[float, float, float], ...] = ()
    colors: tuple[tuple[int, int, int, int], ...] = ()
    uvs: tuple[tuple[float, float], ...] = ()
    skin_vertices: tuple[SourceSkinVertex, ...] = ()
    morph_targets: tuple[SourceMorphTarget, ...] = ()

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "SourceLod":
        def vec3(name: str) -> tuple[tuple[float, float, float], ...]:
            return tuple(tuple(map(float, row)) for row in data.get(name, []))

        return cls(
            positions=vec3("positions"),
            indices=tuple(int(value) for value in data["indices"]),
            subsets=tuple(SourceSubset.from_dict(row) for row in data["subsets"]),
            normals=vec3("normals"),
            tangents=vec3("tangents"),
            bitangents=vec3("bitangents"),
            colors=tuple(tuple(map(int, row)) for row in data.get("colors", [])),
            uvs=tuple(tuple(map(float, row)) for row in data.get("uvs", [])),
            skin_vertices=tuple(
                SourceSkinVertex.from_dict(row) for row in data.get("skin_vertices", [])
            ),
            morph_targets=tuple(
                SourceMorphTarget.from_dict(row) for row in data.get("morph_targets", [])
            ),
        )

    @property
    def vertex_count(self) -> int:
        return len(self.positions)

    def validate(self, *, material_count: int, node_count: int) -> None:
        if not self.positions:
            raise ValueError("LOD needs at least one position")
        if len(self.indices) % 3:
            raise ValueError("index count must be divisible by 3")
        if any(index < 0 or index >= self.vertex_count for index in self.indices):
            raise ValueError("index buffer references a vertex outside the LOD")
        if max(self.indices, default=0) > 0xFFFF:
            raise ValueError("source MSH 0x140 uses uint16 indices; vertex index exceeds 65535")
        for name, values in (
            ("normals", self.normals),
            ("tangents", self.tangents),
            ("bitangents", self.bitangents),
            ("colors", self.colors),
            ("uvs", self.uvs),
            ("skin_vertices", self.skin_vertices),
        ):
            if values and len(values) != self.vertex_count:
                raise ValueError(
                    f"{name} count {len(values)} does not match vertex count {self.vertex_count}"
                )
        if not self.subsets:
            raise ValueError("LOD needs at least one subset")
        for subset_index, subset in enumerate(self.subsets):
            if not 0 <= subset.material_index < material_count:
                raise ValueError(
                    f"subset {subset_index} material index {subset.material_index} outside 0..{material_count-1}"
                )
            if subset.first_index < 0 or subset.index_count < 0:
                raise ValueError(f"subset {subset_index} has a negative range")
            if subset.first_index + subset.index_count > len(self.indices):
                raise ValueError(f"subset {subset_index} exceeds the index buffer")
            if any(bone < 0 or bone >= node_count for bone in subset.bone_palette):
                raise ValueError(f"subset {subset_index} bone palette references an invalid node")
        for target in self.morph_targets:
            if len(target.position_deltas) != self.vertex_count:
                raise ValueError(
                    f"morph target {target.name!r} has {len(target.position_deltas)} deltas; expected {self.vertex_count}"
                )
        if self.skin_vertices:
            palette_sizes = {len(subset.bone_palette) for subset in self.subsets}
            if 0 in palette_sizes:
                raise ValueError("skinned LOD subsets must have local bone palettes")
            max_influences = max(len(row.bone_indices) for row in self.skin_vertices)
            if max_influences > 4:
                raise ValueError("source MSH supports at most four influences per vertex")
            if max_influences < 1:
                raise ValueError("skinned vertices need at least one influence")
            for vertex_index, row in enumerate(self.skin_vertices):
                if len(row.bone_indices) != len(row.weights):
                    raise ValueError(
                        f"skin vertex {vertex_index} has different index/weight counts"
                    )
                if not 1 <= len(row.bone_indices) <= max_influences:
                    raise ValueError(f"skin vertex {vertex_index} influence count is invalid")
                if any(not math.isfinite(weight) or weight < 0.0 for weight in row.weights):
                    raise ValueError(
                        f"skin vertex {vertex_index} weights must be finite and non-negative"
                    )
                if sum(row.weights) <= 0.0:
                    raise ValueError(
                        f"skin vertex {vertex_index} must have at least one positive weight"
                    )

            # Vertex bone indices are local to the palette of the subset that draws the
            # triangle. Validate against every subset that references each vertex rather
            # than only the largest palette in the LOD.
            subset_vertices: list[set[int]] = []
            for subset in self.subsets:
                referenced = {
                    self.indices[index]
                    for index in range(
                        subset.first_index, subset.first_index + subset.index_count
                    )
                }
                subset_vertices.append(referenced)
            for subset_index, (subset, referenced) in enumerate(
                zip(self.subsets, subset_vertices)
            ):
                palette_size = len(subset.bone_palette)
                for vertex_index in referenced:
                    row = self.skin_vertices[vertex_index]
                    if any(
                        local_index < 0 or local_index >= palette_size
                        for local_index in row.bone_indices
                    ):
                        raise ValueError(
                            f"skin vertex {vertex_index} uses a local bone index outside "
                            f"subset {subset_index} palette size {palette_size}"
                        )

    def _vertex_format_payload(self) -> bytes:
        # This is the editor saver's common uncompressed source layout:
        # float3 position/normal/tangent/bitangent and float2 UV.
        return struct.pack(
            "<I4fIIfIIfIIfI",
            2,
            0.0,
            0.0,
            0.0,
            1.0,
            12,
            2,
            1.0,
            12,
            2,
            1.0,
            12,
            3,
            1.0,
            8,
        )

    def _skin_chunk(self) -> bytes | None:
        if not self.skin_vertices:
            return None
        influence_count = max(len(row.bone_indices) for row in self.skin_vertices)
        padded: list[tuple[tuple[int, ...], tuple[int, ...]]] = []
        for row in self.skin_vertices:
            indices = list(row.bone_indices)
            weights = list(row.weights)
            while len(indices) < influence_count:
                indices.append(indices[-1])
                weights.append(0.0)
            if any(index > 0xFF for index in indices):
                raise ValueError("vertex local bone indices are uint8 in source MSH")
            padded.append((tuple(indices), _quantize_weights(weights)))

        if influence_count >= 4:
            payload = bytearray()
            for indices, weights in padded:
                payload += struct.pack("<4B4h", *indices[:4], *weights[:4])
            return pack_chunk(0x130, bytes(payload))

        payload = bytearray([influence_count])
        for indices, weights in padded:
            payload += bytes(indices[:influence_count])
            if influence_count > 1:
                payload += struct.pack(f"<{influence_count}h", *weights[:influence_count])
            elif weights[0] != 0x7FFF:
                raise ValueError("one-influence compact skin rows must have full weight")
        return pack_chunk(0x131, bytes(payload))

    def _subset_chunk(self) -> bytes:
        payload = bytearray()
        for subset in self.subsets:
            if subset.material_index > 0xFFFF or len(subset.bone_palette) > 0xFFFF:
                raise ValueError("subset field exceeds uint16")
            payload += struct.pack(
                "<HIIH",
                subset.material_index,
                subset.first_index,
                subset.index_count,
                len(subset.bone_palette),
            )
            if subset.bone_palette:
                payload += struct.pack(
                    f"<{len(subset.bone_palette)}H", *subset.bone_palette
                )
        return pack_chunk(0x151, bytes(payload))

    def _morph_chunk(self) -> bytes | None:
        if not self.morph_targets:
            return None
        payload = bytearray()
        for target in self.morph_targets:
            payload += _fixed_name(target.name, 64)
            payload += _vec3_bytes(target.position_deltas, f"morph {target.name}")
        return pack_chunk(0x104, bytes(payload))

    def build_chunk(self) -> bytes:
        children: list[bytes] = [pack_chunk(0x160, self._vertex_format_payload())]
        children.append(pack_chunk(0x101, _vec3_bytes(self.positions, "positions")))
        morph = self._morph_chunk()
        if morph:
            children.append(morph)
        if self.normals:
            children.append(pack_chunk(0x102, _vec3_bytes(self.normals, "normals")))
        if self.tangents:
            children.append(pack_chunk(0x103, _vec3_bytes(self.tangents, "tangents")))
        if self.bitangents:
            children.append(
                pack_chunk(0x195, _vec3_bytes(self.bitangents, "bitangents"))
            )
        if self.colors:
            payload = bytearray()
            for index, color in enumerate(self.colors):
                if len(color) != 4 or any(value < 0 or value > 255 for value in color):
                    raise ValueError(f"colors[{index}] must be four uint8 values")
                payload += bytes(color)
            children.append(pack_chunk(0x110, bytes(payload)))
        if self.uvs:
            children.append(pack_chunk(0x120, _vec2_bytes(self.uvs, "uvs")))
        skin = self._skin_chunk()
        if skin:
            children.append(skin)
        children.append(pack_chunk(0x140, struct.pack(f"<{len(self.indices)}H", *self.indices)))
        children.append(self._subset_chunk())
        payload = struct.pack(
            "<4I",
            self.vertex_count,
            len(self.indices),
            len(self.subsets),
            len(self.morph_targets),
        )
        return pack_chunk(0x100, payload, children)


@dataclass
class SourceNode:
    name: str
    node_type: int = 1
    parent_index: int = -1
    local_matrix: tuple[float, ...] = IDENTITY_MATRIX3X4
    reference_matrix: tuple[float, ...] = IDENTITY_MATRIX3X4
    bounds: tuple[float, float, float, float, float, float] | None = None
    tail_words: tuple[int, int, int] = (0, 0, 0)
    lods: tuple[SourceLod, ...] = ()

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "SourceNode":
        return cls(
            name=str(data["name"]),
            node_type=int(data.get("node_type", 1)),
            parent_index=int(data.get("parent_index", -1)),
            local_matrix=tuple(map(float, data.get("local_matrix3x4", IDENTITY_MATRIX3X4))),
            reference_matrix=tuple(
                map(float, data.get("reference_matrix3x4", IDENTITY_MATRIX3X4))
            ),
            bounds=(
                tuple(map(float, data["bounds_center_half_extents"]))
                if "bounds_center_half_extents" in data
                else None
            ),
            tail_words=tuple(map(int, data.get("tail_words", (0, 0, 0)))),
            lods=tuple(SourceLod.from_dict(row) for row in data.get("lods", [])),
        )

    def computed_bounds(self) -> tuple[float, float, float, float, float, float]:
        if self.bounds is not None:
            if len(self.bounds) != 6:
                raise ValueError("node bounds must contain center xyz and half-extents xyz")
            return self.bounds
        positions = [value for lod in self.lods for value in lod.positions]
        if not positions:
            return (0.0, 0.0, 0.0, 0.0, 0.0, 0.0)
        mins = [min(row[axis] for row in positions) for axis in range(3)]
        maxs = [max(row[axis] for row in positions) for axis in range(3)]
        center = [(low + high) * 0.5 for low, high in zip(mins, maxs)]
        half = [(high - low) * 0.5 for low, high in zip(mins, maxs)]
        return tuple(center + half)  # type: ignore[return-value]

    def build_chunk(self, descendant_count: int) -> bytes:
        if len(self.local_matrix) != 12 or len(self.reference_matrix) != 12:
            raise ValueError("node matrices must contain 12 floats")
        if len(self.tail_words) != 3:
            raise ValueError("node tail_words must contain three uint32 values")
        if not -32768 <= self.parent_index <= 32767:
            raise ValueError("parent index must fit int16")
        if descendant_count > 0xFFFF:
            raise ValueError("descendant count must fit uint16")
        payload = bytearray(0xD0)
        struct.pack_into("<I", payload, 0, self.node_type)
        payload[4:68] = _fixed_name(self.name, 64)
        struct.pack_into("<hH", payload, 68, self.parent_index, descendant_count)
        struct.pack_into("<I", payload, 72, len(self.lods))
        struct.pack_into("<12f", payload, 76, *self.local_matrix)
        struct.pack_into("<12f", payload, 124, *self.reference_matrix)
        struct.pack_into("<6f", payload, 172, *self.computed_bounds())
        struct.pack_into("<3I", payload, 196, *self.tail_words)
        return pack_chunk(0x0003, bytes(payload), [lod.build_chunk() for lod in self.lods])


@dataclass
class SourceMsh:
    materials: tuple[str, ...]
    surface_names: tuple[str, ...]
    nodes: tuple[SourceNode, ...]

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "SourceMsh":
        return cls(
            tuple(map(str, data.get("materials", []))),
            tuple(map(str, data.get("surface_names", []))),
            tuple(SourceNode.from_dict(row) for row in data.get("nodes", [])),
        )

    @classmethod
    def from_json(cls, path: str | Path) -> "SourceMsh":
        return cls.from_dict(json.loads(Path(path).read_text(encoding="utf-8")))

    def _descendant_counts(self) -> list[int]:
        count = len(self.nodes)
        children: list[list[int]] = [[] for _ in range(count)]
        for index, node in enumerate(self.nodes):
            if node.parent_index >= count:
                raise ValueError(f"node {index} parent {node.parent_index} is invalid")
            if node.parent_index >= 0:
                children[node.parent_index].append(index)
        visiting: set[int] = set()
        done: dict[int, int] = {}

        def count_descendants(index: int) -> int:
            if index in done:
                return done[index]
            if index in visiting:
                raise ValueError(f"node hierarchy cycle involving node {index}")
            visiting.add(index)
            value = sum(1 + count_descendants(child) for child in children[index])
            visiting.remove(index)
            done[index] = value
            return value

        return [count_descendants(index) for index in range(count)]

    def _validate_depth_first_order(self) -> None:
        """Require the node order emitted by Chrome's source saver.

        The CE6 saver writes a node and immediately recurses through that node's child
        linked list. The stored uint16 at node +0x46 is therefore a subtree descendant
        count, and nodes occur in depth-first pre-order. The loader can construct some
        non-canonical files, but preserving this order removes ambiguity in later editor
        compilation and in code that skips subtrees by descendant count.
        """

        count = len(self.nodes)
        children: list[list[int]] = [[] for _ in range(count)]
        roots: list[int] = []
        for index, node in enumerate(self.nodes):
            parent = node.parent_index
            if parent < -1:
                raise ValueError(f"node {index} parent {parent} is below the -1 root marker")
            if parent == -1:
                roots.append(index)
            else:
                if parent >= count:
                    raise ValueError(f"node {index} parent {parent} is invalid")
                if parent >= index:
                    raise ValueError(
                        f"node {index} parent {parent} must precede it in source MSH order"
                    )
                children[parent].append(index)

        expected: list[int] = []

        def visit(index: int) -> None:
            expected.append(index)
            for child in children[index]:
                visit(child)

        for root in roots:
            visit(root)
        if expected != list(range(count)):
            raise ValueError(
                "nodes are not in Chrome source-saver depth-first pre-order; "
                f"expected traversal {expected}, physical order is {list(range(count))}"
            )

    def validate(self) -> None:
        if not self.nodes:
            raise ValueError("source MSH needs at least one node")
        if not self.materials:
            raise ValueError("source MSH needs at least one material")
        self._validate_depth_first_order()
        descendant_counts = self._descendant_counts()
        del descendant_counts
        for node_index, node in enumerate(self.nodes):
            if not 0 <= node.node_type <= 0xFFFFFFFF:
                raise ValueError(f"node {node_index} type must fit uint32")
            if len(node.local_matrix) != 12 or len(node.reference_matrix) != 12:
                raise ValueError(f"node {node_index} matrices must contain 12 floats")
            if any(
                not math.isfinite(value)
                for value in (*node.local_matrix, *node.reference_matrix)
            ):
                raise ValueError(f"node {node_index} matrices contain non-finite values")
            if len(node.tail_words) != 3 or any(
                word < 0 or word > 0xFFFFFFFF for word in node.tail_words
            ):
                raise ValueError(
                    f"node {node_index} tail_words must contain three uint32 values"
                )
            for lod in node.lods:
                lod.validate(material_count=len(self.materials), node_count=len(self.nodes))
            if node.parent_index == node_index:
                raise ValueError(f"node {node_index} is its own parent")

    def build(self) -> bytes:
        self.validate()
        descendant_counts = self._descendant_counts()
        children: list[bytes] = []
        children.append(pack_chunk(0x500, b"".join(_fixed_name(x) for x in self.materials)))
        children.append(
            pack_chunk(0x700, b"".join(_fixed_name(x) for x in self.surface_names))
        )
        children.extend(
            node.build_chunk(descendant_counts[index])
            for index, node in enumerate(self.nodes)
        )
        root_payload = struct.pack(
            "<3I", len(self.nodes), len(self.materials), len(self.surface_names)
        )
        return pack_chunk(MSH_MAGIC, root_payload, children)

    def write(self, path: str | Path) -> None:
        Path(path).write_bytes(self.build())
