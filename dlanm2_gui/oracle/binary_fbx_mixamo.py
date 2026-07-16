"""Compatibility imports for the former oracle-owned FBX evaluator.

The production implementation lives in :mod:`dlanm2_gui.fbx_core`.  Keep the
historical private names available so older extensions and fixtures continue
to load while normal application modules migrate to the public API.
"""

from __future__ import annotations

from ..fbx_core import (
    AnimationStack,
    FBX_TICKS_PER_SECOND,
    FbxAnimationStack,
    FbxDocument,
    FbxNode,
    _axis_rotation,
    _child_value,
    _clean_name,
    _decompose_basis,
    _euler_matrix,
    _properties70,
    _sample_curve,
)

_FbxDocument = FbxDocument

__all__ = [
    "AnimationStack",
    "FBX_TICKS_PER_SECOND",
    "FbxAnimationStack",
    "FbxDocument",
    "FbxNode",
    "_FbxDocument",
    "_axis_rotation",
    "_child_value",
    "_clean_name",
    "_decompose_basis",
    "_euler_matrix",
    "_properties70",
    "_sample_curve",
]
