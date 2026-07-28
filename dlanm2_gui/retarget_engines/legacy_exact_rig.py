"""Direct same-skeleton FBX to Chrome Rig ANM2 export."""

from __future__ import annotations

import base64
from dataclasses import replace
import math
from pathlib import Path
import re
from typing import Any

import numpy as np

from ..anm2_components import decode_samples
from ..anm2_writer import build_payload_from_values
from ..chrome_rig import ChromeRig
from ..chrome_rig_builder import decompose_local_matrix
from ..fbx_core import (
    FBX_TICKS_PER_SECOND,
    FbxDocument,
    _properties70,
)
from ..oracle.smd_bind_pose import anm2_cayley_vector_from_quaternion
from ..oracle.smd_bind_pose import quaternion_wxyz_from_anm2_cayley
from ..roundtrip_contract import (
    ROUNDTRIP_GUARD_PREFIX,
    embedded_native_metadata,
    resolve_native_roundtrip_metadata,
    validate_roundtrip_contract_identity,
)
from .base import RetargetBuild
from .output_validation import (
    DECODED_COMPONENT_ERROR_LIMIT,
    validate_decoded_component_error,
)


ExactRigBuild = RetargetBuild


_SYNTHETIC_TRACK = re.compile(r"^DLR_(?:OffsetHelper_|Track_)([0-9A-Fa-f]{8})$")
_MOTION_HELPER_DESCRIPTOR = 0xCCC3CDDF
_NATIVE_FBX_COMPONENT_NOISE_TOLERANCE = 1.0e-7
_APPEND_TRANSLATION_TOLERANCE_M = 2.0e-5
_APPEND_SCALE_TOLERANCE = 2.0e-5
_APPEND_ROTATION_TOLERANCE_DEGREES = 0.01
_Y_UP_TO_BLENDER = np.asarray(
    ((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)),
    dtype=float,
)


def _is_dlr_native_export(document: FbxDocument) -> bool:
    for object_id in getattr(document, "null_models", {}).values():
        node = document.object_by_id.get(object_id)
        if node is not None and (_properties70(node).get("dlr_native_anm2_export") or [0])[0]:
            return True
    return False


def _dlr_native_metadata(document: FbxDocument) -> dict[str, Any]:
    return embedded_native_metadata(document)


def _bind_local_matrix(bone) -> np.ndarray:
    w, x, y, z = map(float, bone.bind_rotation_wxyz)
    rotation = np.asarray(
        [
            [1 - 2 * (y*y + z*z), 2 * (x*y - z*w), 2 * (x*z + y*w)],
            [2 * (x*y + z*w), 1 - 2 * (x*x + z*z), 2 * (y*z - x*w)],
            [2 * (x*z - y*w), 2 * (y*z + x*w), 1 - 2 * (x*x + y*y)],
        ],
        dtype=float,
    )
    result = np.eye(4, dtype=float)
    result[:3, :3] = rotation @ np.diag(np.asarray(bone.bind_scale, dtype=float))
    result[:3, 3] = np.asarray(bone.bind_translation, dtype=float)
    return result


def _row_matrix(row: dict[str, Any]) -> np.ndarray:
    class _Row:
        bind_translation = tuple(row["translation"])
        bind_rotation_wxyz = tuple(row["rotation_wxyz"])
        bind_scale = tuple(row["scale"])
    return _bind_local_matrix(_Row())


def _native_sparse_helper_to_game_local(
    matrix: np.ndarray,
    *,
    meters_per_unit: float,
) -> np.ndarray:
    """Undo Blender FBX Empty axis/unit baking used by sparse native exports.

    Blender's FBX writer serializes an Empty with translations and scale in
    centimeters and appends the inverse scene-axis basis to its local rotation.
    LimbNodes take a different FBX armature path and are handled through the
    stored display-basis corrections, so this conversion is intentionally
    limited to schema-v2 synthetic helper objects.
    """

    result = np.asarray(matrix, dtype=float).copy()
    unit = float(meters_per_unit)
    if not math.isfinite(unit) or unit <= 0.0:
        raise ValueError("Native sparse helper has an invalid FBX unit scale")
    result[:3, :3] *= unit
    result[:3, 3] *= unit
    return result @ _Y_UP_TO_BLENDER


def _rig_bind_globals(rig: ChromeRig) -> list[np.ndarray]:
    local = [_bind_local_matrix(bone) for bone in rig.bones]
    result: list[np.ndarray | None] = [None] * len(rig.bones)
    def resolve(index: int) -> np.ndarray:
        if result[index] is not None:
            return result[index]  # type: ignore[return-value]
        parent = rig.bones[index].parent_index
        result[index] = resolve(parent) @ local[index] if parent >= 0 else local[index]
        return result[index]  # type: ignore[return-value]
    return [resolve(index) for index in range(len(rig.bones))]


def _synthetic_tracks(document: FbxDocument) -> dict[int, str]:
    result: dict[int, str] = {}
    scene = getattr(document, "scene", None)
    model_names = (
        list(scene.model_names.values())
        if scene is not None and hasattr(scene, "model_names")
        else [
            *document.limb_models,
            *getattr(document, "null_models", {}),
        ]
    )
    for name in model_names:
        match = _SYNTHETIC_TRACK.match(name)
        if match:
            descriptor = int(match.group(1), 16)
            previous = result.get(descriptor)
            if previous is not None:
                raise ValueError(
                    "FBX contains duplicate DLR track nodes for descriptor "
                    f"0x{descriptor:08X}: {previous!r} and {name!r}"
                )
            result[descriptor] = name
    return result


def _model_id(document: FbxDocument, name: str) -> int:
    if name in document.limb_models:
        return document.limb_models[name]
    null_models = getattr(document, "null_models", {})
    if name in null_models:
        return null_models[name]
    raise KeyError(name)


def _validate_exact_skeleton(
    rig: ChromeRig,
    document: FbxDocument,
    *,
    meters_per_unit: float,
    native_roundtrip: bool = False,
    strict_hierarchy: bool = False,
) -> dict[str, Any]:
    helpers = set(_synthetic_tracks(document).values())
    scene = getattr(document, "scene", None)
    source_bone_names = (
        [
            scene.model_names[object_id]
            for object_id in scene.limb_ids
        ]
        if scene is not None
        else list(document.limb_models)
    )
    duplicate_bones = sorted(
        name
        for name in set(source_bone_names)
        if source_bone_names.count(name) > 1
    )
    source_names = set(source_bone_names) - helpers
    target_names = {bone.name for bone in rig.bones}
    missing = sorted(target_names - source_names)
    extra = sorted(source_names - target_names)
    errors: list[str] = []
    if duplicate_bones:
        errors.append(
            "duplicated bones: " + ", ".join(duplicate_bones[:12])
        )
    if missing:
        errors.append("missing target bones: " + ", ".join(missing[:12]))
    if extra:
        errors.append("source has extra bones: " + ", ".join(extra[:12]))
    by_index = {bone.index: bone for bone in rig.bones}
    for bone in rig.bones:
        expected_parent = (
            None if bone.parent_index < 0 else by_index[bone.parent_index].name
        )
        actual_parent = document.parent_by_name.get(bone.name)
        if actual_parent in helpers:
            actual_parent = None
        display_twist_parent = None
        if bone.parent_index >= 0 and "twist" in bone.name.lower():
            original_parent = by_index[bone.parent_index]
            if original_parent.parent_index >= 0:
                display_twist_parent = by_index[original_parent.parent_index].name
        if not strict_hierarchy and actual_parent == display_twist_parent:
            # ANM2 -> FBX moves zero-length twist nodes to the grandparent only
            # for Blender display purposes, preventing the visible upper-arm
            # or thigh bone from being shortened by half on FBX import.
            continue
        if actual_parent != expected_parent:
            errors.append(
                f"parent mismatch for {bone.name!r}: expected {expected_parent!r}, "
                f"found {actual_parent!r}"
            )
    if errors:
        raise ValueError("Exact-rig skeleton mismatch:\n- " + "\n- ".join(errors))
    if native_roundtrip or _is_dlr_native_export(document):
        # Native ANM2 -> FBX deliberately replaces Chrome's internal bone axes
        # with Blender-friendly display axes. Animation recovery below removes
        # that fixed display basis before rebuilding ANM2, so a direct local
        # bind comparison here would report every bone as a false mismatch.
        return {
            "max_translation_meters": 0.0,
            "max_rotation_degrees": 0.0,
            "max_scale_component": 0.0,
            "default_pose_mismatches": [],
            "default_pose_mismatch_count": 0,
            "status": "compatible",
        }
    maximum_translation = 0.0
    maximum_rotation_degrees = 0.0
    maximum_scale = 0.0
    mismatches: list[dict[str, Any]] = []
    for bone in rig.bones:
        local = document._local_matrix(
            document.limb_models[bone.name], tick=0, use_animation=False
        )
        translation, quaternion, scale = decompose_local_matrix(local)
        translation_delta = float(
            np.linalg.norm(
                translation * meters_per_unit
                - np.asarray(bone.bind_translation, dtype=float)
            )
        )
        quaternion_dot = abs(
            float(np.dot(quaternion, np.asarray(bone.bind_rotation_wxyz, dtype=float)))
        )
        rotation_delta = math.degrees(
            2.0 * math.acos(max(-1.0, min(1.0, quaternion_dot)))
        )
        scale_delta = float(
            np.max(np.abs(scale - np.asarray(bone.bind_scale, dtype=float)))
        )
        maximum_translation = max(maximum_translation, translation_delta)
        maximum_rotation_degrees = max(maximum_rotation_degrees, rotation_delta)
        maximum_scale = max(maximum_scale, scale_delta)
        components = []
        if translation_delta > 1.0e-4:
            components.append("translation")
        if rotation_delta > 0.1:
            components.append("rotation")
        if scale_delta > 1.0e-4:
            components.append("scale")
        if components:
            mismatches.append(
                {
                    "bone": bone.name,
                    "components": components,
                    "translation_delta_meters": translation_delta,
                    "rotation_delta_degrees": rotation_delta,
                    "scale_component_delta": scale_delta,
                }
            )
    return {
        "max_translation_meters": maximum_translation,
        "max_rotation_degrees": maximum_rotation_degrees,
        "max_scale_component": maximum_scale,
        "default_pose_mismatches": mismatches,
        "default_pose_mismatch_count": len(mismatches),
        "status": "warning" if mismatches else "compatible",
    }


def _compatibility_warnings(compatibility: dict[str, Any]) -> list[str]:
    mismatches = list(compatibility.get("default_pose_mismatches", []))
    if not mismatches:
        return []
    worst = max(mismatches, key=lambda row: float(row["rotation_delta_degrees"]))
    return [
        "Exact-rig default pose differs from the .crig for "
        f"{len(mismatches)} bone(s); exporting anyway. Largest rotation mismatch: "
        f"{worst['bone']!r} (translation {worst['translation_delta_meters']:.6g} m, "
        f"rotation {worst['rotation_delta_degrees']:.6g} degrees, "
        f"scale {worst['scale_component_delta']:.6g})."
    ]


def _validate_roundtrip_contract(
    contract: dict[str, Any],
    rig: ChromeRig,
    synthetic_tracks: dict[int, str],
    document: FbxDocument,
) -> tuple[int, ...]:
    if contract.get("format") != "dl-reanimated-native-roundtrip-contract":
        raise ValueError("FBX round-trip contract has an unsupported format")
    if int(contract.get("schema_version", 0) or 0) != 1:
        raise ValueError("FBX round-trip contract has an unsupported schema")
    validate_roundtrip_contract_identity(contract)
    source_hash = str(contract.get("source_anm2_sha256", "") or "")
    if not re.fullmatch(r"[0-9A-Fa-f]{64}", source_hash):
        raise ValueError("FBX round-trip contract has no valid source ANM2 hash")
    if str(contract.get("rig_id", "")) != rig.rig_id:
        raise ValueError(
            "FBX round-trip contract targets a different rig: "
            f"{contract.get('rig_id')!r} instead of {rig.rig_id!r}"
        )
    if str(contract.get("rig_skeleton_hash", "")) != rig.skeleton_hash:
        raise ValueError(
            "FBX round-trip contract skeleton hash does not match the selected rig"
        )
    if not bool(contract.get("roundtrip_capable", False)):
        missing = [
            f"0x{int(value):08X}"
            for value in contract.get("missing_source_descriptors", ())
        ]
        suffix = f": {', '.join(missing[:12])}" if missing else ""
        raise ValueError(
            "This FBX was exported as one-way-only and cannot preserve every "
            f"source ANM2 descriptor{suffix}"
        )

    descriptors = tuple(
        int(value) for value in contract.get("source_descriptors", ())
    )
    if not descriptors or len(set(descriptors)) != len(descriptors):
        raise ValueError(
            "FBX round-trip contract source descriptor order is empty or duplicated"
        )

    by_index = {bone.index: bone for bone in rig.bones}
    expected_skeleton = contract.get("expected_skeleton")
    if not isinstance(expected_skeleton, list):
        raise ValueError("FBX round-trip contract has no expected skeleton")
    if len(expected_skeleton) != len(rig.bones):
        raise ValueError(
            "FBX round-trip contract bone count does not match the selected rig"
        )
    for bone, row in zip(rig.bones, expected_skeleton):
        if not isinstance(row, dict):
            raise ValueError("FBX round-trip skeleton row must be an object")
        expected_parent = (
            None
            if bone.parent_index < 0
            else by_index[bone.parent_index].name
        )
        if (
            str(row.get("name", "")) != bone.name
            or row.get("parent_name") != expected_parent
            or int(row.get("descriptor", -1)) != bone.descriptor
            or bool(row.get("deform", True)) != bone.deform
            or bool(row.get("helper", False)) != bone.helper
        ):
            raise ValueError(
                f"FBX round-trip skeleton contract differs at bone {bone.name!r}"
            )
        contract_values = np.asarray(
            [
                *row.get("bind_translation", ()),
                *row.get("bind_rotation_wxyz", ()),
                *row.get("bind_scale", ()),
            ],
            dtype=float,
        )
        rig_values = np.asarray(
            [
                *bone.bind_translation,
                *bone.bind_rotation_wxyz,
                *bone.bind_scale,
            ],
            dtype=float,
        )
        if (
            contract_values.shape != rig_values.shape
            or not np.isfinite(contract_values).all()
            or float(np.max(np.abs(contract_values - rig_values))) > 1.0e-9
        ):
            raise ValueError(
                f"FBX round-trip bind contract differs at bone {bone.name!r}"
            )

    track_rows = contract.get("source_track_nodes")
    if not isinstance(track_rows, list):
        raise ValueError("FBX round-trip contract has no source track-node map")
    mapped: dict[int, tuple[str, str]] = {}
    mapped_names: set[str] = set()
    for row in track_rows:
        if not isinstance(row, dict):
            raise ValueError("FBX round-trip track-node row must be an object")
        descriptor = int(row.get("descriptor", -1))
        name = str(row.get("node_name", ""))
        kind = str(row.get("node_kind", ""))
        if (
            descriptor in mapped
            or not name
            or name in mapped_names
            or kind not in {"bone", "empty"}
        ):
            raise ValueError(
                "FBX round-trip track-node map contains a duplicate or invalid row"
            )
        mapped[descriptor] = (name, kind)
        mapped_names.add(name)

    bone_by_descriptor = {bone.descriptor: bone for bone in rig.bones}
    for descriptor in descriptors:
        node = mapped.get(descriptor)
        if node is None:
            raise ValueError(
                "FBX round-trip contract has no node for source descriptor "
                f"0x{descriptor:08X}"
            )
        bone = bone_by_descriptor.get(descriptor)
        if bone is not None:
            if node != (bone.name, "bone"):
                raise ValueError(
                    "FBX round-trip contract maps bone descriptor "
                    f"0x{descriptor:08X} to the wrong node"
                )
        elif synthetic_tracks.get(descriptor) != node[0] or node[1] != "empty":
            raise ValueError(
                "FBX is missing or renamed the DLR Empty for source descriptor "
                f"0x{descriptor:08X}"
            )

    expected_external = {
        descriptor
        for descriptor in descriptors
        if descriptor not in bone_by_descriptor
    }
    unexpected_external = sorted(set(synthetic_tracks) - expected_external)
    if unexpected_external:
        raise ValueError(
            "FBX contains unexpected DLR track nodes: "
            + ", ".join(f"0x{value:08X}" for value in unexpected_external[:12])
        )

    guard_name = str(contract.get("guard_name", "") or "")
    scene = getattr(document, "scene", None)
    all_model_names = (
        list(scene.model_names.values())
        if scene is not None
        else [
            *document.limb_models,
            *getattr(document, "null_models", {}),
        ]
    )
    guard_names = [
        name
        for name in all_model_names
        if name.startswith(ROUNDTRIP_GUARD_PREFIX)
    ]
    if guard_names != [guard_name]:
        raise ValueError(
            "FBX round-trip bind guard is missing, duplicated, renamed, or "
            "belongs to stale metadata"
        )
    if scene is not None:
        guard_ids = [
            object_id
            for object_id, name in scene.model_names.items()
            if name == guard_name
        ]
        if (
            len(guard_ids) != 1
            or scene.model_subtypes.get(guard_ids[0]) != "Mesh"
        ):
            raise ValueError("FBX round-trip bind guard is not the expected mesh")

    for descriptor in expected_external:
        name = synthetic_tracks[descriptor]
        object_id = getattr(document, "null_models", {}).get(name)
        if object_id is None:
            raise ValueError(
                f"DLR track node {name!r} is not an independent Empty"
            )
        model_parents = [
            parent_id
            for relation, parent_id, _properties in document.parents.get(
                object_id,
                (),
            )
            if relation == "OO"
            and parent_id
            and (
                scene is None
                or parent_id in getattr(scene, "model_ids", ())
            )
        ]
        if model_parents:
            raise ValueError(f"DLR track Empty {name!r} was reparented")
    return descriptors


def _row_differs_from_bind(
    row: list[float],
    bone: Any,
) -> bool:
    translation_delta = float(
        np.max(
            np.abs(
                np.asarray(row[3:6], dtype=float)
                - np.asarray(bone.bind_translation, dtype=float)
            )
        )
    )
    scale_delta = float(
        np.max(
            np.abs(
                np.asarray(row[6:9], dtype=float)
                - np.asarray(bone.bind_scale, dtype=float)
            )
        )
    )
    quaternion = quaternion_wxyz_from_anm2_cayley(row[:3])
    bind_quaternion = np.asarray(bone.bind_rotation_wxyz, dtype=float)
    dot = abs(float(np.dot(quaternion, bind_quaternion)))
    rotation_delta = math.degrees(
        2.0 * math.acos(max(0.0, min(1.0, dot)))
    )
    return bool(
        translation_delta > _APPEND_TRANSLATION_TOLERANCE_M
        or scale_delta > _APPEND_SCALE_TOLERANCE
        or rotation_delta > _APPEND_ROTATION_TOLERANCE_DEGREES
    )


def _decode_rotation_branch_bits(
    contract: dict[str, Any],
    frame_count: int,
    *,
    field: str = "source_rotation_branch_bits",
) -> dict[int, np.ndarray]:
    encoded_rows = contract.get(field, {})
    if not isinstance(encoded_rows, dict):
        raise ValueError(
            "FBX round-trip rotation-branch inventory must contain an object"
        )
    if contract.get("source_rotation_branch_bit_order", "little") != "little":
        raise ValueError("FBX round-trip rotation branches use an unsupported bit order")
    result: dict[int, np.ndarray] = {}
    for descriptor_hex, encoded in encoded_rows.items():
        try:
            descriptor = int(str(descriptor_hex), 16)
            packed = np.frombuffer(
                base64.b64decode(str(encoded), validate=True),
                dtype=np.uint8,
            )
        except (ValueError, TypeError) as exc:
            raise ValueError(
                "FBX round-trip rotation-branch data is malformed"
            ) from exc
        bits = np.unpackbits(packed, bitorder="little")
        if len(bits) < frame_count:
            raise ValueError(
                "FBX round-trip rotation-branch data is shorter than the animation"
            )
        result[descriptor] = bits[:frame_count].astype(bool)
    return result


def _apply_recorded_cayley_branch(
    rotation: np.ndarray,
    *,
    far_branch: bool,
    positive_orientation: bool | None = None,
) -> np.ndarray:
    value = np.asarray(rotation, dtype=float)
    squared = float(np.dot(value, value))
    if squared <= 1.0e-12 and far_branch:
        raise ValueError(
            "FBX round-trip requests the far Cayley branch at the identity singularity"
        )
    alternate = -value / squared if squared > 1.0e-12 else value
    if (
        positive_orientation is not None
        and abs(squared - 1.0) <= 1.0e-3
    ):
        dominant = int(np.argmax(np.abs(value)))
        return (
            value
            if bool(value[dominant] >= 0.0) == positive_orientation
            else alternate
        )
    return alternate if far_branch else value


def _validate_native_display_rest(
    rig: ChromeRig,
    document: FbxDocument,
    display_basis_corrections: dict[str, np.ndarray],
) -> None:
    actual_globals = getattr(document, "bind_global_matrices", {})
    if len(actual_globals) < len(rig.bones):
        raise ValueError(
            "Exact native FBX has no complete authoritative bind pose. "
            "Include the DLR_RoundTripGuard mesh when exporting from Blender."
        )
    game_bind_globals = _rig_bind_globals(rig)
    errors: list[str] = []
    for bone, game_bind_global in zip(rig.bones, game_bind_globals):
        correction = display_basis_corrections.get(bone.name)
        actual = actual_globals.get(bone.name)
        if correction is None or actual is None:
            errors.append(f"{bone.name}: missing display-rest evidence")
            continue
        if not _is_dlr_native_export(document):
            # Without the native custom properties, FbxDocument cannot apply
            # its DLR armature-wrapper normalization. The stock Blender export
            # settings contribute exactly this fixed left-hand axis wrapper.
            actual = _Y_UP_TO_BLENDER @ actual
        expected = (
            _Y_UP_TO_BLENDER
            @ game_bind_global
            @ np.linalg.inv(_Y_UP_TO_BLENDER)
            @ correction
        )
        relative = np.linalg.inv(expected) @ actual
        translation, quaternion, scale = decompose_local_matrix(relative)
        translation_delta = float(np.linalg.norm(translation))
        rotation_delta = math.degrees(
            2.0
            * math.acos(
                max(0.0, min(1.0, abs(float(np.asarray(quaternion)[0]))))
            )
        )
        scale_delta = float(
            np.max(np.abs(np.asarray(scale, dtype=float) - 1.0))
        )
        if (
            translation_delta > _APPEND_TRANSLATION_TOLERANCE_M
            or rotation_delta > _APPEND_ROTATION_TOLERANCE_DEGREES
            or scale_delta > _APPEND_SCALE_TOLERANCE
        ):
            errors.append(
                f"{bone.name}: translation {translation_delta:.6g} m, "
                f"rotation {rotation_delta:.6g} degrees, scale {scale_delta:.6g}"
            )
    if errors:
        raise ValueError(
            "Exact native FBX rest pose was structurally edited:\n- "
            + "\n- ".join(errors[:12])
        )


def _validate_armature_object_transform(
    contract: dict[str, Any],
    rig: ChromeRig,
    document: FbxDocument,
    ticks: list[int],
    *,
    meters_per_unit: float,
) -> None:
    if (
        contract.get("armature_object_transform_policy")
        != "blender_identity_axis_export_v1"
    ):
        raise ValueError(
            "FBX round-trip contract has an unsupported armature transform policy"
        )
    scene = getattr(document, "scene", None)
    if scene is None:
        raise ValueError("FBX cannot validate its armature object transform")
    expected_name = str(contract.get("armature_object_name", "") or "")
    parent_ids: set[int] = set()
    for bone in rig.bones:
        if bone.parent_index >= 0:
            continue
        object_id = document.limb_models[bone.name]
        candidates = [
            parent_id
            for relation, parent_id, _properties in document.parents.get(
                object_id,
                (),
            )
            if relation == "OO"
            and parent_id
            and parent_id in scene.model_ids
        ]
        if len(candidates) != 1:
            raise ValueError(
                f"Root bone {bone.name!r} is not parented to one armature object"
            )
        parent_ids.add(candidates[0])
    if len(parent_ids) != 1:
        raise ValueError("FBX root bones do not share one armature object")
    armature_id = next(iter(parent_ids))
    if scene.model_names.get(armature_id) != expected_name:
        raise ValueError("FBX armature object was renamed")
    if scene.model_subtypes.get(armature_id) not in {"Null", "Root"}:
        raise ValueError("FBX armature object has an unexpected node type")

    expected_rotation = np.asarray(
        ((1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, -1.0, 0.0)),
        dtype=float,
    )
    expected_scale = 1.0 / meters_per_unit
    sample_ticks = sorted(set([0, *ticks]))
    for tick in sample_ticks:
        matrix = document._local_matrix(
            armature_id,
            tick=tick,
            use_animation=True,
        )
        translation_m = np.asarray(matrix[:3, 3], dtype=float) * meters_per_unit
        basis = np.asarray(matrix[:3, :3], dtype=float)
        scales = np.linalg.norm(basis, axis=0)
        if np.any(scales <= 1.0e-12):
            raise ValueError("FBX armature object transform is singular")
        rotation = basis @ np.diag(1.0 / scales)
        relative = expected_rotation.T @ rotation
        cosine = max(
            -1.0,
            min(1.0, (float(np.trace(relative)) - 1.0) / 2.0),
        )
        rotation_degrees = math.degrees(math.acos(cosine))
        scale_delta = float(
            np.max(np.abs(scales / expected_scale - 1.0))
        )
        shear_delta = float(
            np.max(np.abs(rotation.T @ rotation - np.eye(3)))
        )
        if (
            float(np.linalg.norm(translation_m))
            > _APPEND_TRANSLATION_TOLERANCE_M
            or rotation_degrees > _APPEND_ROTATION_TOLERANCE_DEGREES
            or scale_delta > _APPEND_SCALE_TOLERANCE
            or shear_delta > _APPEND_SCALE_TOLERANCE
        ):
            raise ValueError(
                "FBX armature object transform was edited; apply animation "
                "changes in Pose Mode instead"
            )


def _raw_native_limb_globals(
    document: FbxDocument,
    rig: ChromeRig,
    *,
    tick: int,
    use_animation: bool,
) -> dict[str, np.ndarray]:
    """Compose LimbNode locals without a re-exported Armature wrapper."""

    result: dict[str, np.ndarray] = {}

    def resolve(index: int) -> np.ndarray:
        bone = rig.bones[index]
        cached = result.get(bone.name)
        if cached is not None:
            return cached
        local = document._local_matrix(
            document.limb_models[bone.name],
            tick=tick,
            use_animation=use_animation,
        )
        if bone.parent_index >= 0:
            result[bone.name] = resolve(bone.parent_index) @ local
        else:
            result[bone.name] = local
        return result[bone.name]

    for index in range(len(rig.bones)):
        resolve(index)
    return result


def build_exact_rig_anm2(
    animation_fbx: str | Path,
    rig: ChromeRig,
    *,
    fps: float | None = None,
    animation_stack: str | None = None,
    document_factory: Any = FbxDocument,
    document: Any | None = None,
) -> ExactRigBuild:
    rig.validate().require_valid()
    sample_fps = float(
        rig.writer_profile.default_fps if fps is None else fps
    )
    if not math.isfinite(sample_fps) or sample_fps <= 0.0:
        raise ValueError("sample FPS must be finite and positive")
    if sample_fps > 1000.0:
        raise ValueError("Exact-rig sample FPS must not exceed 1000")
    source = Path(animation_fbx)
    document = document if document is not None else document_factory(source)
    selected_stack = getattr(document, "selected_animation_stack", None)
    selected_stack_name = str(getattr(selected_stack, "name", "") or "")
    if (
        animation_stack
        and selected_stack_name != animation_stack
    ) or (
        not animation_stack
        and len(getattr(document, "animation_stacks", ())) > 1
        and selected_stack is None
    ):
        document.select_animation_stack(animation_stack)
    source_meters = float(document.meters_per_unit)
    synthetic_tracks = _synthetic_tracks(document)
    motion_helper_name = synthetic_tracks.get(_MOTION_HELPER_DESCRIPTOR)
    native_marker = _is_dlr_native_export(document)
    native_metadata, roundtrip_metadata_source = (
        resolve_native_roundtrip_metadata(source, document)
    )
    native_dlr_export = bool(native_marker or native_metadata)
    roundtrip_contract = native_metadata.get("roundtrip_contract", {})
    if roundtrip_contract and not isinstance(roundtrip_contract, dict):
        raise ValueError("FBX native round-trip contract must contain an object")
    if (
        bool(rig.extensions.get("roundtrip_contract_required", False))
        and not roundtrip_contract
    ):
        raise ValueError(
            "The selected helper-capable rig requires native round-trip "
            "metadata. Keep or rename the adjacent "
            f"{source.name}.dlrroundtrip.json sidecar."
        )
    source_descriptors: tuple[int, ...] | None = None
    if roundtrip_contract:
        source_descriptors = _validate_roundtrip_contract(
            roundtrip_contract,
            rig,
            synthetic_tracks,
            document,
        )
    native_metadata_version = int(native_metadata.get("version", 0) or 0)
    native_helper_tracks = native_metadata.get("helper_tracks", {})
    bind_compatibility = _validate_exact_skeleton(
        rig,
        document,
        meters_per_unit=source_meters,
        native_roundtrip=bool(roundtrip_contract),
        strict_hierarchy=bool(roundtrip_contract),
    )
    display_basis_corrections: dict[str, np.ndarray] = {}
    authoritative_bind_pose = False
    if native_dlr_export:
        stored_corrections = native_metadata.get("display_basis_corrections", {})
        bind_diagnostics = (
            document.bind_diagnostics()
            if hasattr(document, "bind_diagnostics")
            else {}
        )
        authoritative_bind_pose = int(
            bind_diagnostics.get("bind_coverage", {}).get(
                "authoritative",
                0,
            )
            or 0
        ) == len(rig.bones)
        use_stored_corrections = bool(stored_corrections)
        if use_stored_corrections:
            display_basis_corrections = {
                name: np.asarray(values, dtype=float).reshape(4, 4)
                for name, values in stored_corrections.items()
            }
        else:
            document_bind_global = document.global_matrices(tick=0, use_animation=False)
            for bone, game_bind_global in zip(rig.bones, _rig_bind_globals(rig)):
                blender_bind_global = (
                    _Y_UP_TO_BLENDER @ game_bind_global @ np.linalg.inv(_Y_UP_TO_BLENDER)
                )
                display_basis_corrections[bone.name] = (
                    np.linalg.inv(blender_bind_global) @ document_bind_global[bone.name]
                )
        if roundtrip_contract and not authoritative_bind_pose:
            raise ValueError(
                "Exact native FBX has no complete authoritative bind pose. "
                "Include the DLR_RoundTripGuard mesh when exporting from Blender."
            )
        if roundtrip_contract and use_stored_corrections:
            _validate_native_display_rest(
                rig,
                document,
                display_basis_corrections,
            )
    if hasattr(document, "frame_ticks"):
        ticks = list(document.frame_ticks(fps=sample_fps))
    else:
        ticks = [
            int(round(frame * FBX_TICKS_PER_SECOND / sample_fps))
            for frame in range(max(1, int(document.frame_count(fps=sample_fps))))
        ]
    if len(ticks) == 1:
        ticks.append(ticks[0])
    frame_count = len(ticks)
    if roundtrip_contract:
        expected_frame_count = int(
            roundtrip_contract.get("fbx_frame_count", 0) or 0
        )
        expected_fps = float(
            roundtrip_contract.get("fbx_output_fps", 0.0) or 0.0
        )
        if frame_count != expected_frame_count:
            raise ValueError(
                "FBX round-trip frame count changed: "
                f"expected {expected_frame_count}, found {frame_count}"
            )
        if not math.isclose(
            sample_fps,
            expected_fps,
            rel_tol=0.0,
            abs_tol=1.0e-6,
        ):
            raise ValueError(
                "FBX round-trip sampling cadence changed: "
                f"expected {expected_fps:g} FPS, found {sample_fps:g} FPS"
            )
        _validate_armature_object_transform(
            roundtrip_contract,
            rig,
            document,
            ticks,
            meters_per_unit=source_meters,
        )
    rotation_branch_bits = (
        _decode_rotation_branch_bits(roundtrip_contract, frame_count)
        if roundtrip_contract
        else {}
    )
    rotation_orientation_bits = (
        _decode_rotation_branch_bits(
            roundtrip_contract,
            frame_count,
            field="source_rotation_orientation_bits",
        )
        if roundtrip_contract
        else {}
    )
    motion_contract = (
        roundtrip_contract.get("motion_accumulator", {})
        if roundtrip_contract
        else native_metadata.get("motion_accumulator", {})
    )
    if not isinstance(motion_contract, dict):
        raise ValueError("FBX motion-accumulator metadata must contain an object")
    motion_was_baked = bool(motion_contract.get("baked", False))
    motion_root_name = str(motion_contract.get("root_name", "") or "")
    if motion_was_baked and not motion_root_name:
        motion_root_name = str(
            roundtrip_contract.get("primary_root_name", "")
            if roundtrip_contract
            else rig.bones[rig.root_index].name
        )
    original_bake_samples = motion_contract.get("original_bake_samples", ())
    if motion_was_baked and original_bake_samples:
        if (
            not isinstance(original_bake_samples, list)
            or len(original_bake_samples) != frame_count
        ):
            raise ValueError(
                "FBX round-trip motion-bake samples do not match its frame count"
            )

    frame_rows: list[dict[int, list[float]]] = []
    for frame_index, tick in enumerate(ticks):
        rows_by_descriptor: dict[int, list[float]] = {}
        stored_motion_rows = native_helper_tracks.get(
            f"{_MOTION_HELPER_DESCRIPTOR:08X}", ()
        )
        if (
            not roundtrip_contract
            and native_dlr_export
            and frame_index < len(stored_motion_rows)
        ):
            current_motion_helper_local = _row_matrix(
                stored_motion_rows[frame_index]
            )
            current_motion_helper_is_game_space = True
        else:
            current_motion_helper_local = (
                document._local_matrix(
                    _model_id(document, motion_helper_name),
                    tick=tick,
                    use_animation=True,
                )
                if motion_helper_name is not None
                else None
            )
            current_motion_helper_is_game_space = False

        unbake_helper_game: np.ndarray | None = None
        if motion_was_baked:
            if original_bake_samples:
                sample = original_bake_samples[frame_index]
                if not isinstance(sample, dict):
                    raise ValueError(
                        "FBX round-trip motion-bake sample must be an object"
                    )
                unbake_helper_game = _row_matrix(sample)
            elif current_motion_helper_local is not None:
                unbake_helper_game = current_motion_helper_local
                if not current_motion_helper_is_game_space:
                    unbake_helper_game = (
                        _native_sparse_helper_to_game_local(
                            current_motion_helper_local,
                            meters_per_unit=source_meters,
                        )
                        if native_metadata_version >= 2
                        else (
                            np.linalg.inv(_Y_UP_TO_BLENDER)
                            @ current_motion_helper_local
                            @ _Y_UP_TO_BLENDER
                        )
                    )
            else:
                raise ValueError(
                    "FBX says its motion accumulator was baked, but no "
                    "original bake samples or helper node are available"
                )
        legacy_unbake_helper_game: np.ndarray | None = None
        if (
            not roundtrip_contract
            and current_motion_helper_local is not None
        ):
            legacy_unbake_helper_game = current_motion_helper_local
            if not current_motion_helper_is_game_space:
                legacy_unbake_helper_game = (
                    _native_sparse_helper_to_game_local(
                        current_motion_helper_local,
                        meters_per_unit=source_meters,
                    )
                    if native_metadata_version >= 2
                    else (
                        np.linalg.inv(_Y_UP_TO_BLENDER)
                        @ current_motion_helper_local
                        @ _Y_UP_TO_BLENDER
                    )
                )
        native_game_globals: dict[str, np.ndarray] = {}
        if native_dlr_export:
            display_globals = (
                _raw_native_limb_globals(
                    document,
                    rig,
                    tick=tick,
                    use_animation=True,
                )
                if roundtrip_contract
                else document.global_matrices(
                    tick=tick,
                    use_animation=True,
                )
            )
            for bone in rig.bones:
                blender_game_global = (
                    display_globals[bone.name]
                    @ np.linalg.inv(display_basis_corrections[bone.name])
                )
                native_game_globals[bone.name] = (
                    np.linalg.inv(_Y_UP_TO_BLENDER)
                    @ blender_game_global
                    @ _Y_UP_TO_BLENDER
                )
        for bone in rig.bones:
            if native_dlr_export:
                game_global = native_game_globals[bone.name]
                if bone.parent_index >= 0:
                    parent_global = native_game_globals[rig.bones[bone.parent_index].name]
                    local = np.linalg.inv(parent_global) @ game_global
                elif (
                    motion_was_baked
                    and bone.name == motion_root_name
                    and unbake_helper_game is not None
                ):
                    local = np.linalg.inv(unbake_helper_game) @ game_global
                elif legacy_unbake_helper_game is not None:
                    local = (
                        np.linalg.inv(legacy_unbake_helper_game) @ game_global
                    )
                else:
                    local = game_global
                translation_factor = 1.0
            else:
                object_id = document.limb_models[bone.name]
                local = document._local_matrix(
                    object_id, tick=tick, use_animation=True
                )
                translation_factor = source_meters
            translation, quaternion, scale = decompose_local_matrix(local)
            rotation = anm2_cayley_vector_from_quaternion(quaternion)
            branch_bits = rotation_branch_bits.get(bone.descriptor)
            if branch_bits is not None:
                orientation_bits = rotation_orientation_bits.get(
                    bone.descriptor
                )
                rotation = _apply_recorded_cayley_branch(
                    rotation,
                    far_branch=bool(branch_bits[frame_index]),
                    positive_orientation=(
                        bool(orientation_bits[frame_index])
                        if orientation_bits is not None
                        else None
                    ),
                )
            rows_by_descriptor[bone.descriptor] = [
                *map(float, rotation),
                *(float(v * translation_factor) for v in translation),
                *map(float, scale),
            ]
        for descriptor, name in synthetic_tracks.items():
            if source_descriptors is None and descriptor not in rig.descriptors:
                continue
            stored_rows = native_helper_tracks.get(f"{descriptor:08X}", ())
            if (
                not roundtrip_contract
                and native_dlr_export
                and frame_index < len(stored_rows)
            ):
                stored_row = stored_rows[frame_index]
                quaternion = np.asarray(stored_row["rotation_wxyz"], dtype=float)
                rotation = anm2_cayley_vector_from_quaternion(quaternion)
                rows_by_descriptor[descriptor] = [
                    *map(float, rotation),
                    *map(float, stored_row["translation"]),
                    *map(float, stored_row["scale"]),
                ]
                continue
            local = document._local_matrix(
                _model_id(document, name), tick=tick, use_animation=True
            )
            if native_dlr_export:
                local = (
                    _native_sparse_helper_to_game_local(
                        local,
                        meters_per_unit=source_meters,
                    )
                    if native_metadata_version >= 2
                    else (
                        np.linalg.inv(_Y_UP_TO_BLENDER)
                        @ local
                        @ _Y_UP_TO_BLENDER
                    )
                )
            translation, quaternion, scale = decompose_local_matrix(local)
            rotation = anm2_cayley_vector_from_quaternion(quaternion)
            branch_bits = rotation_branch_bits.get(descriptor)
            if branch_bits is not None:
                orientation_bits = rotation_orientation_bits.get(descriptor)
                rotation = _apply_recorded_cayley_branch(
                    rotation,
                    far_branch=bool(branch_bits[frame_index]),
                    positive_orientation=(
                        bool(orientation_bits[frame_index])
                        if orientation_bits is not None
                        else None
                    ),
                )
            rows_by_descriptor[descriptor] = [
                *map(float, rotation),
                *(
                    float(v if native_dlr_export else v * source_meters)
                    for v in translation
                ),
                *map(float, scale),
            ]
        frame_rows.append(rows_by_descriptor)

    appended_descriptors: list[int] = []
    if source_descriptors is not None:
        missing_rows = sorted(
            {
                descriptor
                for descriptor in source_descriptors
                if any(descriptor not in rows for rows in frame_rows)
            }
        )
        if missing_rows:
            raise ValueError(
                "FBX cannot resolve every original ANM2 descriptor: "
                + ", ".join(f"0x{value:08X}" for value in missing_rows[:12])
            )
        source_set = set(source_descriptors)
        bone_by_descriptor = {bone.descriptor: bone for bone in rig.bones}
        for descriptor in rig.descriptors:
            bone = bone_by_descriptor.get(descriptor)
            if bone is None or descriptor in source_set:
                continue
            if any(
                _row_differs_from_bind(rows[descriptor], bone)
                for rows in frame_rows
            ):
                appended_descriptors.append(descriptor)
        effective_descriptors = (
            *source_descriptors,
            *appended_descriptors,
        )
        values = [
            [rows[descriptor] for descriptor in effective_descriptors]
            for rows in frame_rows
        ]
    else:
        effective_descriptors = tuple(int(value) for value in rig.descriptors)
        identity = [
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            1.0,
            1.0,
            1.0,
        ]
        values = [
            [rows.get(descriptor, list(identity)) for descriptor in effective_descriptors]
            for rows in frame_rows
        ]
    # Blender's FBX curve evaluation introduces sub-micro component noise in
    # otherwise static native-export channels. Treating it as authored motion
    # makes a one-slot packed stream exceed ANM2's 64 KiB page limit for dense
    # DL2 rigs. Native export metadata is the narrow contract that authorizes
    # this tolerance; ordinary FBX imports retain the existing sensitivity.
    packed_variation_threshold = (
        _NATIVE_FBX_COMPONENT_NOISE_TOLERANCE if native_dlr_export else 1.0e-8
    )
    packed_flags: list[list[bool]] = []
    for track_index in range(len(effective_descriptors)):
        flags = []
        for component_index in range(9):
            curve = [frame[track_index][component_index] for frame in values]
            flags.append(max(curve) - min(curve) > packed_variation_threshold)
        if any(flags[6:9]):
            flags[6:9] = [True, True, True]
        packed_flags.append(flags)
    header = replace(
        rig.make_header(frame_count=frame_count),
        track_count=len(effective_descriptors),
    )
    payload = build_payload_from_values(
        header,
        effective_descriptors,
        values,
        packed_flags,
    )
    sample_frames = sorted({0, frame_count // 2, frame_count - 1})
    decoded = decode_samples(payload, [float(value) for value in sample_frames])
    maximum_error = validate_decoded_component_error(
        decoded,
        values,
        sample_frames,
        engine_name="ExactRigRetargetEngine",
    )
    names_by_descriptor = {bone.descriptor: bone.name for bone in rig.bones}
    moving_tracks = [
        names_by_descriptor.get(effective_descriptors[index], f"extra:{index}")
        for index, flags in enumerate(packed_flags)
        if any(flags)
    ]
    return ExactRigBuild(
        payload=payload,
        frame_count=frame_count,
        report={
            "retarget_mode": "exact",
            "engine": "ExactRigRetargetEngine",
            "source_fbx": str(source),
            "target_rig_id": rig.rig_id,
            "target_rig_name": rig.name,
            "target_skeleton_hash": rig.skeleton_hash,
            "packed_variation_threshold": packed_variation_threshold,
            "frame_count": frame_count,
            "fps": sample_fps,
            "track_count": len(effective_descriptors),
            "source_track_count": (
                len(source_descriptors)
                if source_descriptors is not None
                else len(rig.descriptors)
            ),
            "preserved_source_descriptors": (
                [f"0x{value:08X}" for value in source_descriptors]
                if source_descriptors is not None
                else []
            ),
            "appended_edited_descriptors": [
                f"0x{value:08X}" for value in appended_descriptors
            ],
            "appended_edited_tracks": [
                names_by_descriptor.get(value, f"0x{value:08X}")
                for value in appended_descriptors
            ],
            "roundtrip_metadata_source": roundtrip_metadata_source,
            "bone_count": len(rig.bones),
            "moving_tracks": moving_tracks,
            "sample_frames": sample_frames,
            "decoded_max_component_error": maximum_error,
            "decoded_component_error_tolerance": DECODED_COMPONENT_ERROR_LIMIT,
            "source_unit_meters": source_meters,
            "source_animation_stack": (
                document.selected_animation_stack.name
                if getattr(document, "selected_animation_stack", None)
                else ""
            ),
            "bind_compatibility": bind_compatibility,
            "warnings": _compatibility_warnings(bind_compatibility),
            "root_policy": (
                "native_recorded_motion_unbake"
                if motion_was_baked
                else "exact_local_transforms"
            ),
            "candidate_path": None,
        },
    )


__all__ = ["ExactRigBuild", "build_exact_rig_anm2"]
