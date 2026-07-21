"""Deterministically build the advanced and legacy bundled DL2 Chrome Rigs."""

from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import sys

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.chrome_rig_builder import decompose_local_matrix
from dlanm2_gui.dl2_anm2 import parse_dl2_header42
from dlanm2_gui.game_profiles import (
    DL2_ADVANCED_RIG_REF,
    DL2_LEGACY_RIG_REF,
)
from dlanm2_gui.oracle.smd_bind_pose import parse_smd_bind_pose, smd_local_matrices
from dlanm2_gui.root_mapping import dl_name_hash


REFERENCE_ANM2_RELATIVE = Path("reference/dl2/0_m_fpp_farjump.anm2")
EXPECTED_ADVANCED_SMD_SHA256 = (
    "D2FED6A5DA455147F85B8002671A23A6CD1E4890E8D50B62878C056457340904"
)
EXPECTED_REFERENCE_ANM2_SHA256 = (
    "9368914A4C59521BDD31FED064DF93A5D2D287E793FDC9447BE24ACD4A3FFF6D"
)
OFFSET_HELPER_DESCRIPTOR = 0xCCC3CDDF

NEWLY_RESOLVED_ADVANCED_NAMES = (
    "refcamera",
    "eyecamera",
    "l_leg_secanim_01",
    "l_leg_secanim_02",
    "l_leg_secanim_03",
    "l_leg_secanim_02_a",
    "l_leg_secanim_02_b",
    "l_leg_secanim_02_c",
    "l_leg_secanim_02_d",
    "r_leg_secanim_01",
    "r_leg_secanim_02",
    "r_leg_secanim_03",
    "r_leg_secanim_02_a",
    "r_leg_secanim_02_b",
    "r_leg_secanim_02_c",
    "r_leg_secanim_02_d",
)


@dataclass(frozen=True, slots=True)
class RigPreset:
    key: str
    rig_id: str
    name: str
    description: str
    smd_relative_path: Path
    output_relative_path: Path
    full_reference_inventory: bool


PRESETS = {
    "advanced": RigPreset(
        "advanced",
        DL2_ADVANCED_RIG_REF,
        "Dying Light 2 Player — Advanced",
        "Bundled 271-node DL2 player target built from the supplied advanced SMD.",
        Path("reference/dl2/player_skeleton.smd"),
        Path("reference/dl2/player_skeleton.crig"),
        True,
    ),
    "legacy": RigPreset(
        "legacy",
        DL2_LEGACY_RIG_REF,
        "Dying Light 2 Player — Shadow Caster [Legacy]",
        "Legacy bundled 81-node DL2 shadow-caster target retained for project compatibility.",
        Path("reference/dl2/player_shadow_caster.smd"),
        Path("reference/dl2/player_shadow_caster.crig"),
        False,
    ),
}


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def _semantic_category(name: str) -> str:
    lowered = name.casefold()
    if lowered.endswith("_bone"):
        return "facial"
    if "secanim" in lowered:
        return "secondary_animation"
    if lowered in {"refcamera", "eyecamera"}:
        return "camera"
    if lowered.startswith("player_collar_"):
        return "collar"
    if (
        lowered == "headend"
        or "_fx_" in lowered
        or lowered.endswith("holder")
    ):
        return "attachment"
    return "body"


def _is_helper(name: str, category: str, *, advanced: bool) -> bool:
    normal = "".join(ch for ch in name.casefold() if ch.isalnum())
    legacy_helper = any(
        token in normal
        for token in ("iktarget", "helper", "shadowcaster", "camera", "dummy")
    )
    return legacy_helper or (advanced and category in {"camera", "attachment"})


def _build_bones(
    smd_path: Path,
    *,
    advanced: bool,
) -> tuple[tuple[ChromeRigBone, ...], dict[str, int]]:
    pose = parse_smd_bind_pose(smd_path)
    local_matrices = smd_local_matrices(pose)
    source_rows = list(pose.bones)
    old_to_new = {int(row.index): index for index, row in enumerate(source_rows)}
    categories: Counter[str] = Counter()
    bones: list[ChromeRigBone] = []
    for index, row in enumerate(source_rows):
        translation, quaternion, scale = decompose_local_matrix(
            np.asarray(local_matrices[row.name], dtype=float)
        )
        name = str(row.name)
        category = _semantic_category(name)
        helper = _is_helper(name, category, advanced=advanced)
        categories[category] += 1
        tags = (
            ("dl2", category, "helper")
            if advanced and helper
            else ("dl2", category)
            if advanced
            else ("dl2", "helper")
            if helper
            else ("dl2",)
        )
        bones.append(
            ChromeRigBone(
                index=index,
                name=name,
                parent_index=(
                    old_to_new.get(int(row.parent_index), -1)
                    if int(row.parent_index) >= 0
                    else -1
                ),
                descriptor=dl_name_hash(name),
                bind_translation=tuple(float(value) for value in translation),
                bind_rotation_wxyz=tuple(float(value) for value in quaternion),
                bind_scale=tuple(float(value) for value in scale),
                deform=not helper,
                helper=helper,
                aliases=(),
                tags=tags,
            )
        )
    return tuple(bones), dict(sorted(categories.items()))


def _descriptor_collisions(
    bones: tuple[ChromeRigBone, ...],
) -> dict[int, tuple[str, ...]]:
    names: dict[int, list[str]] = {}
    for bone in bones:
        names.setdefault(int(bone.descriptor), []).append(bone.name)
    return {
        descriptor: tuple(rows)
        for descriptor, rows in names.items()
        if len(rows) > 1
    }


def _reference_descriptors(reference_path: Path) -> tuple[int, ...]:
    layout = parse_dl2_header42(reference_path)
    errors = tuple(getattr(layout, "validation_errors", ()))
    if errors:
        raise ValueError("Invalid DL2 reference ANM2:\n- " + "\n- ".join(errors))
    descriptors = tuple(int(value) for value in layout.descriptors)
    if len(descriptors) != 189:
        raise ValueError(
            f"Expected the supplied reference ANM2 to contain 189 descriptors, got {len(descriptors)}."
        )
    if len(set(descriptors)) != len(descriptors):
        raise ValueError("The supplied reference descriptor table contains duplicates.")
    return descriptors


def _track_inventory(
    preset: RigPreset,
    bones: tuple[ChromeRigBone, ...],
    reference_descriptors: tuple[int, ...],
) -> tuple[tuple[int, ...], tuple[int, ...], tuple[int, ...], tuple[int, ...]]:
    bone_descriptors = {int(bone.descriptor) for bone in bones}
    matched = tuple(
        descriptor for descriptor in reference_descriptors if descriptor in bone_descriptors
    )
    unmatched = tuple(
        descriptor for descriptor in reference_descriptors if descriptor not in bone_descriptors
    )
    if preset.full_reference_inventory:
        track_order = list(reference_descriptors)
    else:
        # Preserve the established legacy writer inventory: reference descriptors
        # that map to its 81-node topology plus the stock offset helper.  This
        # avoids changing old shadow-caster format-1 compatibility exports.
        track_order = [
            descriptor
            for descriptor in reference_descriptors
            if descriptor in bone_descriptors or descriptor == OFFSET_HELPER_DESCRIPTOR
        ]
    for bone in bones:
        descriptor = int(bone.descriptor)
        if descriptor not in track_order:
            track_order.append(descriptor)
    extras = tuple(
        descriptor for descriptor in track_order if descriptor not in bone_descriptors
    )
    return tuple(track_order), extras, matched, unmatched


def _newly_resolved_names(
    advanced_bones: tuple[ChromeRigBone, ...],
    legacy_bones: tuple[ChromeRigBone, ...],
    reference_descriptors: tuple[int, ...],
) -> tuple[str, ...]:
    advanced_by_descriptor = {
        int(bone.descriptor): bone.name for bone in advanced_bones
    }
    legacy_descriptors = {int(bone.descriptor) for bone in legacy_bones}
    return tuple(
        advanced_by_descriptor[descriptor]
        for descriptor in reference_descriptors
        if descriptor in advanced_by_descriptor and descriptor not in legacy_descriptors
    )


def build_reference_crig(
    preset_name: str,
    *,
    root: str | Path = ROOT,
) -> ChromeRig:
    """Build one preset in memory without mutating the repository."""

    if preset_name not in PRESETS:
        raise ValueError(f"Unknown DL2 rig preset {preset_name!r}; choose advanced or legacy.")
    base = Path(root)
    preset = PRESETS[preset_name]
    smd_path = base / preset.smd_relative_path
    reference_path = base / REFERENCE_ANM2_RELATIVE
    if preset_name == "advanced" and _sha256(smd_path) != EXPECTED_ADVANCED_SMD_SHA256:
        raise ValueError("Advanced player SMD SHA-256 does not match the supplied reference.")
    if _sha256(reference_path) != EXPECTED_REFERENCE_ANM2_SHA256:
        raise ValueError("DL2 far-jump ANM2 SHA-256 does not match the supplied reference.")

    advanced_bones, advanced_categories = _build_bones(
        base / PRESETS["advanced"].smd_relative_path,
        advanced=True,
    )
    legacy_bones, legacy_categories = _build_bones(
        base / PRESETS["legacy"].smd_relative_path,
        advanced=False,
    )
    bones = advanced_bones if preset_name == "advanced" else legacy_bones
    categories = advanced_categories if preset_name == "advanced" else legacy_categories
    collisions = _descriptor_collisions(bones)
    if collisions:
        rendered = ", ".join(
            f"0x{descriptor:08X}: {names!r}"
            for descriptor, names in sorted(collisions.items())
        )
        raise ValueError(f"SMD bone-name hash collisions: {rendered}")

    reference_descriptors = _reference_descriptors(reference_path)
    track_order, extras, matched, unmatched = _track_inventory(
        preset, bones, reference_descriptors
    )
    newly_resolved = _newly_resolved_names(
        advanced_bones, legacy_bones, reference_descriptors
    )
    expected_new_names = set(NEWLY_RESOLVED_ADVANCED_NAMES)
    if (
        set(newly_resolved) != expected_new_names
        or len(newly_resolved) != len(expected_new_names)
    ):
        raise ValueError(
            "Advanced/legacy descriptor delta does not match the audited 16-name "
            f"inventory: {newly_resolved!r}"
        )

    roots = [bone.name for bone in bones if bone.parent_index < 0]
    if preset_name == "advanced":
        if len(bones) != 271 or roots != ["pelvis"]:
            raise ValueError(
                f"Advanced SMD contract changed: {len(bones)} bones, roots {roots!r}."
            )
        if len(matched) != 92 or len(unmatched) != 97:
            raise ValueError(
                "Advanced reference mapping changed: "
                f"{len(matched)} matched, {len(unmatched)} unmatched."
            )
    elif len(bones) != 81 or set(roots) != {
        "pelvis",
        "l_iktarget",
        "r_iktarget",
        "player_shadowcaster",
    }:
        raise ValueError(f"Legacy SMD contract changed: {len(bones)} bones, roots {roots!r}.")

    template = ChromeRig.load(base / "reference/male_npc_infected.crig")
    legacy_names = {bone.name for bone in legacy_bones}
    added_categories = Counter(
        _semantic_category(bone.name)
        for bone in advanced_bones
        if bone.name not in legacy_names
    )
    matched_set = set(matched)
    extensions = {
        "game_id": "dying_light_2",
        "preset_status": "current_default" if preset_name == "advanced" else "legacy_compatible",
        "source_smd": smd_path.name,
        "source_smd_sha256": _sha256(smd_path),
        "source_reference_anm2": reference_path.name,
        "source_reference_anm2_sha256": _sha256(reference_path),
        "source_anm2_signature": 42,
        "source_anm2_header_version": 2,
        "source_anm2_container": "dl2_header_version_2",
        "reference_descriptor_count": len(reference_descriptors),
        "matched_reference_descriptor_count": len(matched),
        "unmatched_reference_descriptor_count": len(unmatched),
        "hash_collision_count": 0,
        "descriptor_inventory_policy": (
            "full_reference_then_unreferenced_smd_bones"
            if preset.full_reference_inventory
            else "legacy_resolved_reference_then_unreferenced_smd_bones"
        ),
        "reference_descriptor_order_preserved": True,
        "allow_source_superset": True,
        "bind_pose_policy": "fbx_authoritative_global_to_target_bind_global",
        "primary_root": "pelvis",
        "independent_roots": (
            []
            if preset_name == "advanced"
            else ["l_iktarget", "r_iktarget", "player_shadowcaster"]
        ),
        "finger_policy": "dl2_explicit_finger10_20_30_40_roots",
        "resolved_model_axis_conversion": "fbx_y_up_to_dying_light",
        "writer_compatibility": "format1_compatibility_experimental",
        "native_dl2_anm2_writer": False,
        "bone_category_counts": categories,
        "advanced_addition_category_counts": dict(sorted(added_categories.items())),
        "newly_resolved_reference_bones": list(NEWLY_RESOLVED_ADVANCED_NAMES),
        "newly_resolved_reference_bones_in_track_order": list(newly_resolved),
        "matched_facial_reference_bone_count": sum(
            1
            for bone in bones
            if bone.descriptor in matched_set and _semantic_category(bone.name) == "facial"
        ),
    }
    root_index = next(bone.index for bone in bones if bone.name == "pelvis")
    rig = ChromeRig(
        rig_id=preset.rig_id,
        name=preset.name,
        category="Humanoid",
        bones=bones,
        root_index=root_index,
        writer_profile=template.writer_profile,
        extra_track_descriptors=extras,
        track_descriptors=track_order,
        description=preset.description,
        author=template.author,
        license=template.license,
        source_model_name=smd_path.name,
        extensions=extensions,
    )
    rig.validate().require_valid()
    return rig


def _report(preset_name: str, rig: ChromeRig, output: Path, status: str) -> dict[str, object]:
    return {
        "preset": preset_name,
        "status": status,
        "output": str(output),
        "rig_id": rig.rig_id,
        "bone_count": len(rig.bones),
        "track_count": len(rig.descriptors),
        "extra_track_count": len(rig.extra_track_descriptors),
        "matched_reference_descriptor_count": rig.extensions[
            "matched_reference_descriptor_count"
        ],
        "unmatched_reference_descriptor_count": rig.extensions[
            "unmatched_reference_descriptor_count"
        ],
        "roots": [bone.name for bone in rig.bones if bone.parent_index < 0],
        "sha256": hashlib.sha256(rig.to_bytes()).hexdigest().upper(),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--preset",
        choices=("all", "advanced", "legacy"),
        default="all",
        help="Preset to build; the default deterministically builds both.",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Verify checked-in bytes instead of writing files.",
    )
    args = parser.parse_args(argv)
    selected = tuple(PRESETS) if args.preset == "all" else (args.preset,)
    reports: list[dict[str, object]] = []
    failed = False
    for preset_name in selected:
        preset = PRESETS[preset_name]
        rig = build_reference_crig(preset_name)
        output = ROOT / preset.output_relative_path
        payload = rig.to_bytes()
        if args.check:
            matches = output.is_file() and output.read_bytes() == payload
            reports.append(_report(preset_name, rig, output, "ok" if matches else "mismatch"))
            failed = failed or not matches
        else:
            rig.save(output)
            loaded = ChromeRig.load(output)
            loaded.validate().require_valid()
            reports.append(_report(preset_name, loaded, output, "written"))
    print(json.dumps({"status": "fail" if failed else "ok", "presets": reports}, ensure_ascii=False))
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
