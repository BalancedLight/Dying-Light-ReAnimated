from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .math3d import (
    IDENTITY_MATRIX4X4,
    Matrix4x4,
    matrix3x4_from_matrix4,
    matrix4_inverse_affine,
    matrix4_multiply,
    smd_local_matrix,
)
from .smd import SmdFile


@dataclass(frozen=True)
class SmdBindMatrices:
    local: dict[int, Matrix4x4]
    global_bind: dict[int, Matrix4x4]
    inverse_global_bind: dict[int, Matrix4x4]

    def matrix3x4(self, node_index: int, mode: str) -> tuple[float, ...]:
        if mode == "identity":
            matrix = IDENTITY_MATRIX4X4
        elif mode == "local":
            matrix = self.local[node_index]
        elif mode == "global":
            matrix = self.global_bind[node_index]
        elif mode == "inverse_global":
            matrix = self.inverse_global_bind[node_index]
        else:
            raise ValueError(
                f"unknown reference matrix mode {mode!r}; "
                "expected identity, local, global, or inverse_global"
            )
        return matrix3x4_from_matrix4(matrix)


def build_smd_bind_matrices(smd: SmdFile, *, time: int = 0) -> SmdBindMatrices:
    poses = smd.poses_by_time.get(time)
    if poses is None:
        raise ValueError(f"SMD has no skeleton pose at time {time}")
    by_index = {node.index: node for node in smd.nodes}
    if len(by_index) != len(smd.nodes):
        raise ValueError("SMD contains duplicate node indexes")

    local: dict[int, Matrix4x4] = {}
    for node in smd.nodes:
        pose = poses.get(node.index)
        if pose is None:
            raise ValueError(f"SMD bind pose is missing node {node.index} {node.name!r}")
        local[node.index] = smd_local_matrix(pose.position, pose.rotation_radians)

    global_bind: dict[int, Matrix4x4] = {}
    visiting: set[int] = set()

    def resolve(index: int) -> Matrix4x4:
        if index in global_bind:
            return global_bind[index]
        if index in visiting:
            raise ValueError(f"SMD hierarchy cycle at node {index}")
        node = by_index.get(index)
        if node is None:
            raise ValueError(f"SMD hierarchy references missing node {index}")
        visiting.add(index)
        matrix = local[index]
        if node.parent_index >= 0:
            matrix = matrix4_multiply(resolve(node.parent_index), matrix)
        visiting.remove(index)
        global_bind[index] = matrix
        return matrix

    for node in smd.nodes:
        resolve(node.index)
    inverse = {
        index: matrix4_inverse_affine(matrix)
        for index, matrix in global_bind.items()
    }
    return SmdBindMatrices(local, global_bind, inverse)


def validate_smd_depth_first_order(smd: SmdFile) -> None:
    index_to_position = {node.index: position for position, node in enumerate(smd.nodes)}
    if len(index_to_position) != len(smd.nodes):
        raise ValueError("SMD contains duplicate node indexes")
    children: dict[int, list[int]] = {node.index: [] for node in smd.nodes}
    roots: list[int] = []
    for node in smd.nodes:
        if node.parent_index < 0:
            roots.append(node.index)
            continue
        if node.parent_index not in children:
            raise ValueError(
                f"SMD node {node.index} {node.name!r} references missing parent {node.parent_index}"
            )
        if index_to_position[node.parent_index] >= index_to_position[node.index]:
            raise ValueError(
                f"SMD parent {node.parent_index} must precede child {node.index}"
            )
        children[node.parent_index].append(node.index)

    traversal: list[int] = []

    def visit(index: int) -> None:
        traversal.append(index)
        for child in children[index]:
            visit(child)

    for root in roots:
        visit(root)
    physical = [node.index for node in smd.nodes]
    if traversal != physical:
        raise ValueError(
            "SMD nodes are not in Chrome depth-first pre-order; "
            f"traversal={traversal}, physical={physical}"
        )


def parse_noesis_ascii_skeleton(path: str | Path) -> dict[str, dict[str, Any]]:
    """Read only the skeleton prefix from a Noesis .ascii model export."""

    lines = Path(path).read_text(encoding="utf-8", errors="replace").splitlines()
    if not lines:
        raise ValueError("ASCII model file is empty")
    try:
        count = int(lines[0].strip())
    except ValueError as exc:
        raise ValueError("ASCII model does not start with a bone count") from exc
    cursor = 1
    result: dict[str, dict[str, Any]] = {}
    for index in range(count):
        if cursor + 2 >= len(lines):
            raise ValueError(f"ASCII model ended in skeleton row {index}")
        name = lines[cursor].strip()
        parent_index = int(lines[cursor + 1].strip())
        position_values = tuple(float(value) for value in lines[cursor + 2].split())
        if len(position_values) != 3:
            raise ValueError(f"ASCII skeleton node {name!r} does not have xyz")
        result[name] = {
            "index": index,
            "parent_index": parent_index,
            "global_position": position_values,
        }
        cursor += 3
    return result


def verify_smd_global_positions(
    smd: SmdFile, ascii_path: str | Path, *, time: int = 0
) -> dict[str, Any]:
    bind = build_smd_bind_matrices(smd, time=time)
    expected = parse_noesis_ascii_skeleton(ascii_path)
    rows: list[dict[str, Any]] = []
    for node in smd.nodes:
        target = expected.get(node.name)
        if target is None:
            continue
        actual = (
            bind.global_bind[node.index][0][3],
            bind.global_bind[node.index][1][3],
            bind.global_bind[node.index][2][3],
        )
        wanted = tuple(target["global_position"])
        error = sum((actual[i] - wanted[i]) ** 2 for i in range(3)) ** 0.5
        rows.append(
            {
                "node_index": node.index,
                "name": node.name,
                "actual": list(actual),
                "expected": list(wanted),
                "position_error": error,
            }
        )
    maximum = max((row["position_error"] for row in rows), default=0.0)
    mean = (
        sum(row["position_error"] for row in rows) / len(rows)
        if rows
        else 0.0
    )
    return {
        "smd": smd.source_path,
        "ascii": str(ascii_path),
        "time": time,
        "matched_bones": len(rows),
        "smd_bones": len(smd.nodes),
        "ascii_bones": len(expected),
        "max_position_error": maximum,
        "mean_position_error": mean,
        "verified_euler_composition": "Rz*Ry*Rx (intrinsic XYZ)",
        "pass_at_1e-5": bool(rows) and maximum <= 1.0e-5,
        "worst_rows": sorted(
            rows, key=lambda row: row["position_error"], reverse=True
        )[:12],
    }
