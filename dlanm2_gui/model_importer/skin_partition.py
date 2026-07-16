from __future__ import annotations

"""Deterministic Chrome source-MSH skin-palette partitioning.

Chrome stores global source-node indexes in each subset palette, but each
vertex stores an unsigned-byte *local* index into that palette.  Keeping those
two index spaces explicit prevents a large hierarchy from being confused with
the 256-entry per-subset limit.
"""

from dataclasses import dataclass
from typing import Any, Hashable, Iterable, Sequence
import math


MAX_SUBSET_PALETTE_ENTRIES = 256
MAX_SOURCE_LOD_VERTICES = 65_535


@dataclass(frozen=True, slots=True)
class GlobalSkinInfluence:
    """One normalized influence in the source-MSH physical-node index space."""

    global_node_index: int
    weight: float

    def __post_init__(self) -> None:
        if not 0 <= int(self.global_node_index) <= 0xFFFF:
            raise ValueError(
                "Skin influence global node index must fit the source-MSH uint16 "
                f"palette field; got {self.global_node_index}."
            )
        if not math.isfinite(float(self.weight)) or float(self.weight) < 0.0:
            raise ValueError("Skin influence weight must be finite and non-negative.")


@dataclass(frozen=True, slots=True)
class WeightedTriangle:
    """A triangle after final mapping/top-four normalization.

    ``vertex_keys`` are the complete emitted-vertex identities used for the
    vertex-count limit.  They may be omitted by callers that intentionally use
    conservative triangle-corner expansion; in that case every corner is
    treated as unique.
    """

    source_triangle_index: int
    material_index: int
    vertex_influences: tuple[
        tuple[GlobalSkinInfluence, ...],
        tuple[GlobalSkinInfluence, ...],
        tuple[GlobalSkinInfluence, ...],
    ]
    vertex_keys: tuple[Hashable, Hashable, Hashable] | tuple[()] = ()

    def __post_init__(self) -> None:
        if len(self.vertex_influences) != 3:
            raise ValueError("A weighted triangle must contain exactly three corners.")
        if self.vertex_keys and len(self.vertex_keys) != 3:
            raise ValueError("A weighted triangle must contain exactly three vertex keys.")
        for corner_index, rows in enumerate(self.vertex_influences):
            if not 1 <= len(rows) <= 4:
                raise ValueError(
                    f"Triangle {self.source_triangle_index} corner {corner_index} has "
                    f"{len(rows)} influences; Chrome requires one to four after normalization."
                )
            if sum(float(row.weight) for row in rows) <= 0.0:
                raise ValueError(
                    f"Triangle {self.source_triangle_index} corner {corner_index} has no "
                    "positive skin weight. Assign it to an intended deform bone before import."
                )

    @property
    def global_bone_set(self) -> frozenset[int]:
        result = frozenset(
            int(row.global_node_index)
            for corner in self.vertex_influences
            for row in corner
            if float(row.weight) > 0.0
        )
        if len(result) > 12:
            raise ValueError(
                f"Triangle {self.source_triangle_index} requires {len(result)} distinct "
                "bones after four-influence normalization; the mathematical maximum is 12. "
                "The source skin data is corrupt or was not normalized before partitioning."
            )
        return result


@dataclass(frozen=True, slots=True)
class PalettePartition:
    material_index: int
    partition_index: int
    triangle_indices: tuple[int, ...]
    global_palette: tuple[int, ...]
    unique_vertex_count: int

    @property
    def local_index_by_global(self) -> dict[int, int]:
        return {global_index: local_index for local_index, global_index in enumerate(self.global_palette)}

    def validate(self) -> None:
        if not self.triangle_indices:
            raise ValueError("A palette partition must contain at least one triangle.")
        if len(self.global_palette) > MAX_SUBSET_PALETTE_ENTRIES:
            raise ValueError(
                f"Subset palette has {len(self.global_palette)} entries; Chrome vertex local "
                "palette indexes are uint8 and permit at most 256 entries."
            )
        if len(set(self.global_palette)) != len(self.global_palette):
            raise ValueError("Subset palette contains duplicate global node indexes.")
        if tuple(sorted(self.global_palette)) != self.global_palette:
            raise ValueError("Subset palette must use deterministic ascending global-node order.")
        if not 1 <= self.unique_vertex_count <= MAX_SOURCE_LOD_VERTICES:
            raise ValueError(
                f"Subset partition has {self.unique_vertex_count} emitted vertices; source MSH "
                f"uint16 indexes permit at most {MAX_SOURCE_LOD_VERTICES}."
            )


@dataclass(frozen=True, slots=True)
class EmittedMeshPartition:
    """Serializable/report-friendly result for one emitted mesh node."""

    material_index: int
    partition_index: int
    triangle_count: int
    vertex_count: int
    global_palette: tuple[int, ...]
    maximum_influences: int
    dropped_weight_total: float = 0.0
    fallback_weight_total: float = 0.0
    maximum_quantization_error: float = 0.0

    def to_dict(self) -> dict[str, Any]:
        return {
            "material_index": self.material_index,
            "partition_index": self.partition_index,
            "triangle_count": self.triangle_count,
            "vertex_count": self.vertex_count,
            "palette_size": len(self.global_palette),
            "global_nodes": list(self.global_palette),
            "maximum_influences": self.maximum_influences,
            "dropped_weight_total": self.dropped_weight_total,
            "fallback_weight_total": self.fallback_weight_total,
            "maximum_quantization_error": self.maximum_quantization_error,
        }


def partition_weighted_triangles(
    triangles: Iterable[WeightedTriangle],
    *,
    maximum_palette_entries: int = MAX_SUBSET_PALETTE_ENTRIES,
    maximum_vertices: int = MAX_SOURCE_LOD_VERTICES,
) -> tuple[PalettePartition, ...]:
    """Partition triangles independently by material in stable input order.

    The algorithm is deliberately a stable greedy pass.  Reordering triangles
    could reduce the partition count, but would make source topology and reports
    harder to compare between builds.  The deterministic palette itself is
    always sorted by physical/global source-node index.
    """

    if not 1 <= int(maximum_palette_entries) <= MAX_SUBSET_PALETTE_ENTRIES:
        raise ValueError("maximum_palette_entries must be between 1 and 256.")
    if not 3 <= int(maximum_vertices) <= MAX_SOURCE_LOD_VERTICES:
        raise ValueError("maximum_vertices must be between 3 and 65535.")

    grouped: dict[int, list[WeightedTriangle]] = {}
    for triangle in triangles:
        grouped.setdefault(int(triangle.material_index), []).append(triangle)

    output: list[PalettePartition] = []
    for material_index in sorted(grouped):
        current: list[WeightedTriangle] = []
        current_palette: set[int] = set()
        current_keys: set[Hashable] = set()
        conservative_vertex_count = 0
        partition_index = 0

        def flush() -> None:
            nonlocal current, current_palette, current_keys
            nonlocal conservative_vertex_count, partition_index
            if not current:
                return
            row = PalettePartition(
                material_index=material_index,
                partition_index=partition_index,
                triangle_indices=tuple(value.source_triangle_index for value in current),
                global_palette=tuple(sorted(current_palette)),
                unique_vertex_count=(
                    len(current_keys) if current_keys else conservative_vertex_count
                ),
            )
            row.validate()
            output.append(row)
            partition_index += 1
            current = []
            current_palette = set()
            current_keys = set()
            conservative_vertex_count = 0

        for triangle in grouped[material_index]:
            triangle_bones = set(triangle.global_bone_set)
            if len(triangle_bones) > maximum_palette_entries:
                raise ValueError(
                    f"Triangle {triangle.source_triangle_index} in material {material_index} "
                    f"requires {len(triangle_bones)} palette entries by itself; reduce actual "
                    "influences and re-export. No valid influence was dropped."
                )
            candidate_palette = current_palette | triangle_bones
            if triangle.vertex_keys:
                candidate_keys = current_keys | set(triangle.vertex_keys)
                candidate_vertices = len(candidate_keys)
            else:
                candidate_keys = set()
                candidate_vertices = conservative_vertex_count + 3
            if current and (
                len(candidate_palette) > maximum_palette_entries
                or candidate_vertices > maximum_vertices
            ):
                flush()
                candidate_palette = triangle_bones
                if triangle.vertex_keys:
                    candidate_keys = set(triangle.vertex_keys)
                    candidate_vertices = len(candidate_keys)
                else:
                    candidate_keys = set()
                    candidate_vertices = 3
            if candidate_vertices > maximum_vertices:
                raise ValueError(
                    f"Triangle {triangle.source_triangle_index} cannot fit the source-MSH "
                    f"{maximum_vertices}-vertex partition limit."
                )
            current.append(triangle)
            current_palette = candidate_palette
            current_keys = candidate_keys
            conservative_vertex_count = candidate_vertices
        flush()
    return tuple(output)


def remap_global_influences_to_local(
    influences: Sequence[tuple[int, float] | GlobalSkinInfluence],
    global_palette: Sequence[int],
) -> tuple[tuple[int, float], ...]:
    """Convert physical/global node indexes into uint8 subset-local indexes."""

    palette = tuple(int(value) for value in global_palette)
    if len(palette) > MAX_SUBSET_PALETTE_ENTRIES:
        raise ValueError(
            f"Cannot remap through a {len(palette)}-entry subset palette; the local field is uint8."
        )
    if len(set(palette)) != len(palette):
        raise ValueError("Cannot remap through a palette with duplicate global node indexes.")
    local_by_global = {global_index: local_index for local_index, global_index in enumerate(palette)}
    result: list[tuple[int, float]] = []
    for row in influences:
        if isinstance(row, GlobalSkinInfluence):
            global_index, weight = row.global_node_index, row.weight
        else:
            global_index, weight = int(row[0]), float(row[1])
        if global_index not in local_by_global:
            raise ValueError(
                f"Global skin node {global_index} is absent from the emitted subset palette. "
                "This is an importer partitioning error; no output was written."
            )
        local_index = local_by_global[global_index]
        if not 0 <= local_index <= 0xFF:
            raise ValueError(
                f"Resolved local palette index {local_index} does not fit uint8 for global node "
                f"{global_index}."
            )
        result.append((local_index, float(weight)))
    return tuple(result)


def validate_local_palette_round_trip(
    local_influences: Sequence[tuple[int, float]],
    global_palette: Sequence[int],
    expected_global_influences: Sequence[tuple[int, float]],
    *,
    tolerance: float = 1.0e-9,
) -> None:
    """Prove that every emitted local byte resolves to its intended global node."""

    if len(local_influences) != len(expected_global_influences):
        raise ValueError("Local/global influence row lengths differ.")
    for row_index, ((local_index, local_weight), (global_index, global_weight)) in enumerate(
        zip(local_influences, expected_global_influences)
    ):
        if not 0 <= int(local_index) < len(global_palette):
            raise ValueError(
                f"Influence {row_index} local palette index {local_index} is outside "
                f"0..{len(global_palette) - 1}."
            )
        resolved = int(global_palette[int(local_index)])
        if resolved != int(global_index):
            raise ValueError(
                f"Influence {row_index} resolves local index {local_index} to global node "
                f"{resolved}, expected {global_index}."
            )
        if abs(float(local_weight) - float(global_weight)) > float(tolerance):
            raise ValueError(
                f"Influence {row_index} changed weight during palette remap: "
                f"{local_weight} versus {global_weight}."
            )


__all__ = [
    "EmittedMeshPartition",
    "GlobalSkinInfluence",
    "MAX_SOURCE_LOD_VERTICES",
    "MAX_SUBSET_PALETTE_ENTRIES",
    "PalettePartition",
    "WeightedTriangle",
    "partition_weighted_triangles",
    "remap_global_influences_to_local",
    "validate_local_palette_round_trip",
]
