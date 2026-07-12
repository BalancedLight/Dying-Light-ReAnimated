from __future__ import annotations

import math
from typing import Iterable, Sequence

Matrix3x4 = tuple[float, ...]
Matrix4x4 = tuple[tuple[float, float, float, float], ...]
Vec3 = tuple[float, float, float]

IDENTITY_MATRIX4X4: Matrix4x4 = (
    (1.0, 0.0, 0.0, 0.0),
    (0.0, 1.0, 0.0, 0.0),
    (0.0, 0.0, 1.0, 0.0),
    (0.0, 0.0, 0.0, 1.0),
)


def matrix4_multiply(left: Matrix4x4, right: Matrix4x4) -> Matrix4x4:
    return tuple(
        tuple(
            sum(left[row][k] * right[k][column] for k in range(4))
            for column in range(4)
        )
        for row in range(4)
    )  # type: ignore[return-value]


def matrix3x4_from_matrix4(matrix: Matrix4x4) -> Matrix3x4:
    return tuple(matrix[row][column] for row in range(3) for column in range(4))


def matrix4_from_matrix3x4(values: Sequence[float]) -> Matrix4x4:
    if len(values) != 12:
        raise ValueError("Matrix3x4 needs 12 values")
    return (
        (float(values[0]), float(values[1]), float(values[2]), float(values[3])),
        (float(values[4]), float(values[5]), float(values[6]), float(values[7])),
        (float(values[8]), float(values[9]), float(values[10]), float(values[11])),
        (0.0, 0.0, 0.0, 1.0),
    )


def matrix4_transform_point(matrix: Matrix4x4, point: Sequence[float]) -> Vec3:
    if len(point) != 3:
        raise ValueError("point needs three values")
    x, y, z = map(float, point)
    return (
        matrix[0][0] * x + matrix[0][1] * y + matrix[0][2] * z + matrix[0][3],
        matrix[1][0] * x + matrix[1][1] * y + matrix[1][2] * z + matrix[1][3],
        matrix[2][0] * x + matrix[2][1] * y + matrix[2][2] * z + matrix[2][3],
    )


def matrix4_inverse_affine(matrix: Matrix4x4) -> Matrix4x4:
    """Invert an affine 4x4 matrix without third-party dependencies."""

    # General inverse of the upper-left 3x3, so this remains useful if scale
    # appears in a future donor rather than silently assuming a rigid matrix.
    a, b, c = matrix[0][0], matrix[0][1], matrix[0][2]
    d, e, f = matrix[1][0], matrix[1][1], matrix[1][2]
    g, h, i = matrix[2][0], matrix[2][1], matrix[2][2]
    determinant = (
        a * (e * i - f * h)
        - b * (d * i - f * g)
        + c * (d * h - e * g)
    )
    if not math.isfinite(determinant) or abs(determinant) < 1.0e-12:
        raise ValueError("affine matrix has a singular 3x3 basis")
    inv_det = 1.0 / determinant
    basis_inverse = (
        (
            (e * i - f * h) * inv_det,
            (c * h - b * i) * inv_det,
            (b * f - c * e) * inv_det,
        ),
        (
            (f * g - d * i) * inv_det,
            (a * i - c * g) * inv_det,
            (c * d - a * f) * inv_det,
        ),
        (
            (d * h - e * g) * inv_det,
            (b * g - a * h) * inv_det,
            (a * e - b * d) * inv_det,
        ),
    )
    translation = (matrix[0][3], matrix[1][3], matrix[2][3])
    inverse_translation = tuple(
        -sum(basis_inverse[row][column] * translation[column] for column in range(3))
        for row in range(3)
    )
    return (
        (*basis_inverse[0], inverse_translation[0]),
        (*basis_inverse[1], inverse_translation[1]),
        (*basis_inverse[2], inverse_translation[2]),
        (0.0, 0.0, 0.0, 1.0),
    )


def smd_local_matrix(
    position: Sequence[float], rotation_radians: Sequence[float]
) -> Matrix4x4:
    """Build the local transform used by the supplied Dying Light SMD exports.

    Valve SMD stores XYZ Euler components. For the column-vector convention
    used by Chrome's row-major Matrix3x4, the verified intrinsic XYZ
    composition is Rz * Ry * Rx. This was checked against all 106 global joint
    positions in the supplied player_1_tpp ASCII/SMD pair.
    """

    if len(position) != 3 or len(rotation_radians) != 3:
        raise ValueError("SMD position and rotation both need three values")
    tx, ty, tz = map(float, position)
    rx, ry, rz = map(float, rotation_radians)
    cx, sx = math.cos(rx), math.sin(rx)
    cy, sy = math.cos(ry), math.sin(ry)
    cz, sz = math.cos(rz), math.sin(rz)

    # Expanded Rz @ Ry @ Rx.
    return (
        (
            cz * cy,
            cz * sy * sx - sz * cx,
            cz * sy * cx + sz * sx,
            tx,
        ),
        (
            sz * cy,
            sz * sy * sx + cz * cx,
            sz * sy * cx - cz * sx,
            ty,
        ),
        (-sy, cy * sx, cy * cx, tz),
        (0.0, 0.0, 0.0, 1.0),
    )


def add(left: Sequence[float], right: Sequence[float]) -> Vec3:
    return tuple(float(left[i]) + float(right[i]) for i in range(3))  # type: ignore[return-value]


def subtract(left: Sequence[float], right: Sequence[float]) -> Vec3:
    return tuple(float(left[i]) - float(right[i]) for i in range(3))  # type: ignore[return-value]


def scale(vector: Sequence[float], value: float) -> Vec3:
    return tuple(float(component) * float(value) for component in vector)  # type: ignore[return-value]


def dot(left: Sequence[float], right: Sequence[float]) -> float:
    return sum(float(left[i]) * float(right[i]) for i in range(3))


def cross(left: Sequence[float], right: Sequence[float]) -> Vec3:
    ax, ay, az = map(float, left)
    bx, by, bz = map(float, right)
    return (ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx)


def length(vector: Sequence[float]) -> float:
    return math.sqrt(dot(vector, vector))


def normalize(vector: Sequence[float]) -> Vec3:
    magnitude = length(vector)
    if magnitude <= 1.0e-12:
        raise ValueError("cannot normalize a zero-length vector")
    return scale(vector, 1.0 / magnitude)


def midpoint(left: Sequence[float], right: Sequence[float]) -> Vec3:
    return scale(add(left, right), 0.5)


def max_abs_matrix_delta(left: Sequence[float], right: Sequence[float]) -> float:
    if len(left) != len(right):
        raise ValueError("matrix/value sequences must have the same length")
    return max((abs(float(a) - float(b)) for a, b in zip(left, right)), default=0.0)
