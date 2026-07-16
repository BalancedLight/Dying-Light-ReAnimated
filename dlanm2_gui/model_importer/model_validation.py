from __future__ import annotations

"""Offline CPU validation connecting authored MSH skin data to its rig bind."""

from typing import Any, Sequence
import math

import numpy as np

from .rig_contract import AuthoredRigContract
from .vendor.chrome_mesh_tools.math3d import matrix4_from_matrix3x4
from .vendor.chrome_mesh_tools.writer import SourceMsh


def _quantized_weights(values: Sequence[float]) -> tuple[float, ...]:
    clean = [max(0.0, float(value)) for value in values]
    total = sum(clean)
    if total <= 0.0:
        raise ValueError("CPU bind validation encountered a skin row with no positive weight.")
    normalized = [value / total for value in clean]
    integers = [int(math.floor(value * 32767.0)) for value in normalized]
    remainder = 32767 - sum(integers)
    if remainder:
        integers[max(range(len(normalized)), key=normalized.__getitem__)] += remainder
    return tuple(value / 32767.0 for value in integers)


def validate_model_bind_skin(
    source: SourceMsh,
    contract: AuthoredRigContract,
    *,
    tolerance: float = 1.0e-5,
    maximum_samples_per_partition: int = 1024,
) -> dict[str, Any]:
    """Resolve every sampled local palette byte and CPU-skin at authored bind.

    This intentionally uses ``authored_global * authored_reference`` without
    changing the known-good inverse-global rule.  A correct source vertex must
    remain at its emitted position at bind even after weight quantization.
    """

    source.validate()
    contract_validation = contract.validate()
    if len(source.nodes) != len(contract.nodes):
        raise ValueError(
            f"MSH/rig contract node counts differ ({len(source.nodes)} versus "
            f"{len(contract.nodes)}); rebuild the model before generating output."
        )
    if maximum_samples_per_partition < 1:
        raise ValueError("maximum_samples_per_partition must be positive.")

    skin_matrices: list[np.ndarray] = []
    for row in contract.nodes:
        global_matrix = np.asarray(row.global_matrix4x4, dtype=float).reshape((4, 4))
        reference = np.asarray(
            matrix4_from_matrix3x4(row.inverse_global_reference3x4), dtype=float
        )
        skin_matrices.append(global_matrix @ reference)

    maximum_error = 0.0
    maximum_quantized_error = 0.0
    maximum_weight_sum_error = 0.0
    maximum_quantization_error = 0.0
    sampled_vertex_count = 0
    palette_resolution_count = 0
    worst_partition = ""
    worst_vertex = -1
    worst_influence: dict[str, Any] = {}
    partition_rows: list[dict[str, Any]] = []

    for node_index, node in enumerate(source.nodes):
        for lod_index, lod in enumerate(node.lods):
            if not lod.skin_vertices:
                continue
            for subset_index, subset in enumerate(lod.subsets):
                referenced = sorted(
                    {
                        int(lod.indices[index])
                        for index in range(
                            subset.first_index,
                            subset.first_index + subset.index_count,
                        )
                    }
                )
                stride = max(1, math.ceil(len(referenced) / maximum_samples_per_partition))
                sampled = referenced[::stride]
                partition_error = 0.0
                for vertex_index in sampled:
                    skin = lod.skin_vertices[vertex_index]
                    position = np.asarray((*lod.positions[vertex_index], 1.0), dtype=float)
                    raw_weights = [float(value) for value in skin.weights]
                    total = sum(raw_weights)
                    if total <= 0.0 or not math.isfinite(total):
                        raise ValueError(
                            f"Mesh {node.name!r} subset {subset_index} vertex {vertex_index} "
                            "has no finite positive skin weight. Assign it to an intended bone."
                        )
                    normalized = [value / total for value in raw_weights]
                    quantized = _quantized_weights(raw_weights)
                    maximum_weight_sum_error = max(
                        maximum_weight_sum_error, abs(1.0 - sum(normalized))
                    )
                    maximum_quantization_error = max(
                        maximum_quantization_error,
                        max(
                            abs(left - right)
                            for left, right in zip(normalized, quantized)
                        ),
                    )
                    skinned = np.zeros(4, dtype=float)
                    quantized_skinned = np.zeros(4, dtype=float)
                    for influence_index, (local_index, weight, quantized_weight) in enumerate(
                        zip(skin.bone_indices, normalized, quantized)
                    ):
                        if not 0 <= int(local_index) < len(subset.bone_palette):
                            raise ValueError(
                                f"Mesh {node.name!r} subset {subset_index} vertex {vertex_index} "
                                f"stores local palette index {local_index}, outside 0.."
                                f"{len(subset.bone_palette) - 1}. Rebuild; a global node index "
                                "must never be written directly to this uint8 field."
                            )
                        global_index = int(subset.bone_palette[int(local_index)])
                        if not 0 <= global_index < len(skin_matrices):
                            raise ValueError(
                                f"Mesh {node.name!r} subset {subset_index} local palette index "
                                f"{local_index} resolves to invalid global node {global_index}."
                            )
                        palette_resolution_count += 1
                        transformed = skin_matrices[global_index] @ position
                        skinned += float(weight) * transformed
                        quantized_skinned += float(quantized_weight) * transformed
                        identity_error = float(np.max(np.abs(transformed - position)))
                        if identity_error > maximum_error:
                            worst_influence = {
                                "local_palette_index": int(local_index),
                                "global_node_index": global_index,
                                "global_node_name": contract.nodes[global_index].name,
                                "identity_error": identity_error,
                                "influence_index": influence_index,
                            }
                    error = float(np.max(np.abs(skinned - position)))
                    quantized_error = float(
                        np.max(np.abs(quantized_skinned - position))
                    )
                    partition_error = max(partition_error, error, quantized_error)
                    if max(error, quantized_error) > max(
                        maximum_error, maximum_quantized_error
                    ):
                        worst_partition = f"node {node_index} lod {lod_index} subset {subset_index}"
                        worst_vertex = vertex_index
                    maximum_error = max(maximum_error, error)
                    maximum_quantized_error = max(
                        maximum_quantized_error, quantized_error
                    )
                    sampled_vertex_count += 1
                partition_rows.append(
                    {
                        "node_index": node_index,
                        "node_name": node.name,
                        "lod_index": lod_index,
                        "subset_index": subset_index,
                        "palette_size": len(subset.bone_palette),
                        "global_nodes": list(subset.bone_palette),
                        "referenced_vertex_count": len(referenced),
                        "sampled_vertex_count": len(sampled),
                        "maximum_bind_skin_error": partition_error,
                    }
                )

    blocking_error = max(maximum_error, maximum_quantized_error)
    if blocking_error > tolerance:
        raise ValueError(
            f"CPU bind-pose skin validation failed at {worst_partition}, vertex "
            f"{worst_vertex}: maximum error {blocking_error:.6g} exceeds tolerance "
            f"{tolerance:.6g}. Verify palette remapping and inverse-global references; "
            "no MSH/CRIG output should be installed."
        )
    return {
        "status": "pass",
        "maximum_bind_skin_error": maximum_error,
        "maximum_quantized_bind_skin_error": maximum_quantized_error,
        "maximum_weight_sum_error_before_quantization": maximum_weight_sum_error,
        "maximum_weight_quantization_error": maximum_quantization_error,
        "worst_partition": worst_partition,
        "worst_vertex": worst_vertex,
        "worst_influence": worst_influence,
        "sampled_vertex_count": sampled_vertex_count,
        "palette_resolution_count": palette_resolution_count,
        "tolerance": tolerance,
        "authored_rig_validation": contract_validation,
        "partitions": partition_rows,
    }


__all__ = ["validate_model_bind_skin"]
