"""Production validation for bundled SMD/CRIG/reference-ANM2 packages."""

from __future__ import annotations

from dataclasses import dataclass, field
import hashlib
from pathlib import Path
from typing import Any
import unicodedata

import numpy as np

from .chrome_rig import ChromeRig
from .chrome_rig_builder import decompose_local_matrix
from .dl2_anm2 import detect_anm2_format
from .oracle.smd_bind_pose import parse_smd_bind_pose, smd_local_matrices


BIND_TRANSLATION_TOLERANCE_METERS = 2.0e-6
BIND_ROTATION_TOLERANCE_DEGREES = 2.0e-4
BIND_SCALE_TOLERANCE = 2.0e-6


def _normalized_name(value: str) -> str:
    return unicodedata.normalize("NFKC", str(value)).casefold()


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


@dataclass(slots=True)
class TargetPackageCoherence:
    game_id: str
    status: str = "fail"
    smd_path: str = ""
    crig_path: str = ""
    reference_anm2_path: str = ""
    smd_exists: bool = False
    crig_exists: bool = False
    reference_anm2_exists: bool = False
    smd_bone_count: int = 0
    crig_bone_count: int = 0
    bone_names_match: bool = False
    parents_match: bool = False
    roots_match: bool = False
    bind_translation_match: bool = False
    bind_rotation_match: bool = False
    bind_scale_match: bool = False
    bind_pose_match: bool = False
    source_smd_filename_match: bool = False
    source_smd_hash_match: bool = False
    reference_anm2_filename_match: bool = False
    reference_anm2_hash_match: bool = False
    reference_anm2_format_match: bool = False
    game_id_match: bool = False
    primary_root_match: bool = False
    roots: list[str] = field(default_factory=list)
    bind_tolerance: dict[str, float] = field(default_factory=lambda: {
        "translation_meters": BIND_TRANSLATION_TOLERANCE_METERS,
        "rotation_degrees": BIND_ROTATION_TOLERANCE_DEGREES,
        "scale": BIND_SCALE_TOLERANCE,
    })
    maximum_bind_translation_delta_meters: float = 0.0
    maximum_bind_rotation_delta_degrees: float = 0.0
    maximum_bind_scale_delta: float = 0.0
    errors: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return {
            "status": self.status,
            "game_id": self.game_id,
            "smd_path": self.smd_path,
            "crig_path": self.crig_path,
            "reference_anm2_path": self.reference_anm2_path,
            "smd_exists": self.smd_exists,
            "crig_exists": self.crig_exists,
            "reference_anm2_exists": self.reference_anm2_exists,
            "smd_bone_count": self.smd_bone_count,
            "crig_bone_count": self.crig_bone_count,
            "bone_names_match": self.bone_names_match,
            "parents_match": self.parents_match,
            "roots_match": self.roots_match,
            "roots": list(self.roots),
            "bind_translation_match": self.bind_translation_match,
            "bind_rotation_match": self.bind_rotation_match,
            "bind_scale_match": self.bind_scale_match,
            "bind_pose_match": self.bind_pose_match,
            "bind_tolerance": dict(self.bind_tolerance),
            "maximum_bind_translation_delta_meters": self.maximum_bind_translation_delta_meters,
            "maximum_bind_rotation_delta_degrees": self.maximum_bind_rotation_delta_degrees,
            "maximum_bind_scale_delta": self.maximum_bind_scale_delta,
            "source_smd_filename_match": self.source_smd_filename_match,
            "source_smd_hash_match": self.source_smd_hash_match,
            "reference_anm2_filename_match": self.reference_anm2_filename_match,
            "reference_anm2_hash_match": self.reference_anm2_hash_match,
            "reference_anm2_format_match": self.reference_anm2_format_match,
            "game_id_match": self.game_id_match,
            "primary_root_match": self.primary_root_match,
            "errors": list(self.errors),
        }

    def require_valid(self, display_name: str) -> None:
        if self.status == "pass":
            return
        short_name = "DL2" if self.game_id == "dying_light_2" else display_name
        raise ValueError(
            f"The bundled {short_name} target SMD and CRIG describe different skeletons. "
            "Reinstall the application or regenerate the target package.\n- "
            + "\n- ".join(self.errors)
        )


def validate_target_package(
    profile: Any,
    root: str | Path | None = None,
    *,
    smd_path: str | Path | None = None,
    crig_path: str | Path | None = None,
    reference_anm2_path: str | Path | None = None,
) -> TargetPackageCoherence:
    """Validate one profile's immutable target assets and embedded provenance."""

    base = Path(root) if root is not None else Path()
    smd = Path(smd_path) if smd_path is not None else base / profile.canonical_smd_relative_path
    crig = Path(crig_path) if crig_path is not None else base / profile.target_rig_relative_path
    reference = (
        Path(reference_anm2_path)
        if reference_anm2_path is not None
        else base / profile.reference_anm2_relative_path
    )
    result = TargetPackageCoherence(
        game_id=str(profile.game_id),
        smd_path=str(smd),
        crig_path=str(crig),
        reference_anm2_path=str(reference),
        smd_exists=smd.is_file(),
        crig_exists=crig.is_file(),
        reference_anm2_exists=reference.is_file(),
    )
    for label, exists, path in (
        ("canonical SMD", result.smd_exists, smd),
        ("target CRIG", result.crig_exists, crig),
        ("reference ANM2", result.reference_anm2_exists, reference),
    ):
        if not exists:
            result.errors.append(f"Missing {label}: {path}")
    if result.errors:
        return result

    try:
        pose = parse_smd_bind_pose(smd)
    except (OSError, ValueError) as exc:
        result.errors.append(f"Canonical SMD cannot be parsed: {exc}")
        return result
    try:
        rig = ChromeRig.load(crig)
    except (OSError, ValueError) as exc:
        result.errors.append(f"Target CRIG cannot be loaded: {exc}")
        return result

    result.smd_bone_count = len(pose.bones)
    result.crig_bone_count = len(rig.bones)
    if result.smd_bone_count != result.crig_bone_count:
        result.errors.append(
            f"Bone count differs: SMD has {result.smd_bone_count}, CRIG has {result.crig_bone_count}."
        )

    smd_names = {_normalized_name(bone.name): bone.name for bone in pose.bones}
    crig_names = {_normalized_name(bone.name): bone.name for bone in rig.bones}
    if len(smd_names) != len(pose.bones):
        result.errors.append("SMD bone names collide after NFKC/casefold normalization.")
    if len(crig_names) != len(rig.bones):
        result.errors.append("CRIG bone names collide after NFKC/casefold normalization.")
    result.bone_names_match = set(smd_names) == set(crig_names)
    if not result.bone_names_match:
        missing = sorted(set(crig_names) - set(smd_names))
        extra = sorted(set(smd_names) - set(crig_names))
        result.errors.append(
            "Normalized bone-name sets differ"
            + (f"; missing from SMD: {missing[:12]}" if missing else "")
            + (f"; extra in SMD: {extra[:12]}" if extra else "")
            + "."
        )

    smd_by_index = pose.by_index
    smd_parents = {
        _normalized_name(bone.name): (
            _normalized_name(smd_by_index[bone.parent_index].name)
            if bone.parent_index in smd_by_index
            else None
        )
        for bone in pose.bones
    }
    crig_parents = {
        _normalized_name(bone.name): (
            _normalized_name(rig.bones[bone.parent_index].name)
            if bone.parent_index >= 0
            else None
        )
        for bone in rig.bones
    }
    result.parents_match = smd_parents == crig_parents
    if not result.parents_match:
        mismatches = [
            name
            for name in sorted(set(smd_parents).intersection(crig_parents))
            if smd_parents[name] != crig_parents[name]
        ]
        result.errors.append(f"Parent maps differ for: {mismatches[:12]}.")

    smd_roots = sorted(
        (bone.name for bone in pose.bones if bone.parent_index < 0), key=str.casefold
    )
    crig_roots = sorted(
        (bone.name for bone in rig.bones if bone.parent_index < 0), key=str.casefold
    )
    result.roots = crig_roots
    result.roots_match = {
        _normalized_name(name) for name in smd_roots
    } == {_normalized_name(name) for name in crig_roots}
    if not result.roots_match:
        result.errors.append(f"Root inventory differs: SMD {smd_roots}, CRIG {crig_roots}.")

    smd_locals = smd_local_matrices(pose)
    smd_components: dict[str, tuple[np.ndarray, np.ndarray, np.ndarray]] = {}
    for bone in pose.bones:
        smd_components[_normalized_name(bone.name)] = decompose_local_matrix(
            smd_locals[bone.name]
        )
    translation_deltas: list[float] = []
    rotation_deltas: list[float] = []
    scale_deltas: list[float] = []
    for bone in rig.bones:
        components = smd_components.get(_normalized_name(bone.name))
        if components is None:
            continue
        smd_translation, smd_quaternion, smd_scale = components
        translation_deltas.append(
            float(np.max(np.abs(smd_translation - np.asarray(bone.bind_translation))))
        )
        dot = abs(float(np.dot(smd_quaternion, np.asarray(bone.bind_rotation_wxyz))))
        rotation_deltas.append(
            float(np.degrees(2.0 * np.arccos(np.clip(dot, -1.0, 1.0))))
        )
        scale_deltas.append(
            float(np.max(np.abs(smd_scale - np.asarray(bone.bind_scale))))
        )
    result.maximum_bind_translation_delta_meters = max(translation_deltas, default=0.0)
    result.maximum_bind_rotation_delta_degrees = max(rotation_deltas, default=0.0)
    result.maximum_bind_scale_delta = max(scale_deltas, default=0.0)
    compared_all = len(translation_deltas) == len(rig.bones) == len(pose.bones)
    result.bind_translation_match = compared_all and (
        result.maximum_bind_translation_delta_meters <= BIND_TRANSLATION_TOLERANCE_METERS
    )
    result.bind_rotation_match = compared_all and (
        result.maximum_bind_rotation_delta_degrees <= BIND_ROTATION_TOLERANCE_DEGREES
    )
    result.bind_scale_match = compared_all and (
        result.maximum_bind_scale_delta <= BIND_SCALE_TOLERANCE
    )
    result.bind_pose_match = (
        result.bind_translation_match
        and result.bind_rotation_match
        and result.bind_scale_match
    )
    if not result.bind_pose_match:
        result.errors.append(
            "Bind-local transforms differ beyond tolerance: "
            f"translation {result.maximum_bind_translation_delta_meters:.9g} m, "
            f"rotation {result.maximum_bind_rotation_delta_degrees:.9g} degrees, "
            f"scale {result.maximum_bind_scale_delta:.9g}."
        )

    embedded_smd_names = {
        Path(str(rig.source_model_name or "")).name,
        Path(str(rig.extensions.get("source_smd", "") or "")).name,
    }
    embedded_smd_names.discard("")
    result.source_smd_filename_match = bool(embedded_smd_names) and embedded_smd_names == {smd.name}
    if not result.source_smd_filename_match:
        result.errors.append(
            f"Embedded source SMD filename(s) {sorted(embedded_smd_names)} do not identify {smd.name!r}."
        )
    embedded_smd_hash = str(rig.extensions.get("source_smd_sha256", "") or "").upper()
    result.source_smd_hash_match = bool(embedded_smd_hash) and embedded_smd_hash == _sha256(smd)
    if not result.source_smd_hash_match:
        result.errors.append("Embedded source SMD SHA-256 does not match the canonical SMD bytes.")

    embedded_reference_name = Path(
        str(rig.extensions.get("source_reference_anm2", "") or "")
    ).name
    result.reference_anm2_filename_match = embedded_reference_name == reference.name
    if not result.reference_anm2_filename_match:
        result.errors.append(
            f"Embedded reference ANM2 filename {embedded_reference_name!r} does not identify {reference.name!r}."
        )
    embedded_reference_hash = str(
        rig.extensions.get("source_reference_anm2_sha256", "") or ""
    ).upper()
    result.reference_anm2_hash_match = (
        bool(embedded_reference_hash) and embedded_reference_hash == _sha256(reference)
    )
    if not result.reference_anm2_hash_match:
        result.errors.append("Embedded reference ANM2 SHA-256 does not match the reference file.")
    try:
        detected_format = detect_anm2_format(reference)
        expected_format = 42 if str(profile.game_id) == "dying_light_2" else 1
        result.reference_anm2_format_match = detected_format == expected_format
        if not result.reference_anm2_format_match:
            result.errors.append(
                f"Reference ANM2 format is {detected_format}, expected {expected_format}."
            )
    except ValueError as exc:
        result.errors.append(f"Reference ANM2 is unsupported: {exc}")

    result.game_id_match = str(rig.extensions.get("game_id", "") or "") == str(
        profile.game_id
    )
    if not result.game_id_match:
        result.errors.append("CRIG game_id does not match the GameProfile.")
    declared_primary = str(rig.extensions.get("primary_root", "") or "")
    actual_primary = rig.bones[rig.root_index].name
    result.primary_root_match = (
        _normalized_name(declared_primary) == _normalized_name(profile.primary_root)
        and _normalized_name(actual_primary) == _normalized_name(profile.primary_root)
        and _normalized_name(profile.primary_root)
        in {_normalized_name(name) for name in smd_roots}
    )
    if not result.primary_root_match:
        result.errors.append(
            f"Primary root mismatch: profile {profile.primary_root!r}, CRIG manifest "
            f"{declared_primary!r}, CRIG root_index {actual_primary!r}."
        )

    result.status = "pass" if not result.errors else "fail"
    return result


__all__ = [
    "BIND_ROTATION_TOLERANCE_DEGREES",
    "BIND_SCALE_TOLERANCE",
    "BIND_TRANSLATION_TOLERANCE_METERS",
    "TargetPackageCoherence",
    "validate_target_package",
]
