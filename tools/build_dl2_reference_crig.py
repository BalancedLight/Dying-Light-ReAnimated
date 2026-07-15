from __future__ import annotations
from dataclasses import fields, is_dataclass, replace
from pathlib import Path
import hashlib
import inspect
import json
import sys

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.chrome_rig_builder import decompose_local_matrix
from dlanm2_gui.dl2_anm2 import parse_dl2_header42
from dlanm2_gui.oracle.smd_bind_pose import parse_smd_bind_pose, smd_local_matrices
from dlanm2_gui.root_mapping import dl_name_hash


def make_instance(cls, values):
    signature = inspect.signature(cls)
    accepted = {name: value for name, value in values.items() if name in signature.parameters}
    return cls(**accepted)


def main() -> int:
    template_path = ROOT / "reference" / "male_npc_infected.crig"
    smd_path = ROOT / "reference" / "dl2" / "player_shadow_caster.smd"
    anm2_path = ROOT / "reference" / "dl2" / "0_m_fpp_farjump.anm2"
    output_path = ROOT / "reference" / "dl2" / "player_shadow_caster.crig"
    template = ChromeRig.load(template_path)
    pose = parse_smd_bind_pose(smd_path)
    locals_by_name = smd_local_matrices(pose)
    source_rows = list(pose.bones)
    old_to_new = {int(row.index): index for index, row in enumerate(source_rows)}
    bone_cls = type(template.bones[0])
    new_bones = []
    for new_index, row in enumerate(source_rows):
        matrix = np.asarray(locals_by_name[row.name], dtype=float)
        translation, quaternion, scale = decompose_local_matrix(matrix)
        name = str(row.name)
        parent_index = old_to_new.get(int(row.parent_index), -1) if int(row.parent_index) >= 0 else -1
        normal = "".join(ch for ch in name.casefold() if ch.isalnum())
        helper = any(token in normal for token in ("iktarget", "helper", "shadowcaster", "camera", "dummy"))
        values = {
            "index": new_index,
            "name": name,
            "parent_index": parent_index,
            "descriptor": dl_name_hash(name),
            "bind_translation": tuple(float(value) for value in translation),
            "bind_rotation_wxyz": tuple(float(value) for value in quaternion),
            "bind_scale": tuple(float(value) for value in scale),
            "deform": not helper,
            "helper": helper,
            "aliases": (),
            "tags": ("dl2", "helper") if helper else ("dl2",),
        }
        new_bones.append(make_instance(bone_cls, values))

    header = parse_dl2_header42(anm2_path)
    stock_order = list(header.active_descriptors) + list(header.reference_descriptors)
    bone_descriptors = {int(bone.descriptor) for bone in new_bones}
    descriptors = []
    for value in stock_order:
        if value == 0xCCC3CDDF or value in bone_descriptors:
            if value not in descriptors:
                descriptors.append(value)
    for bone in new_bones:
        if int(bone.descriptor) not in descriptors:
            descriptors.append(int(bone.descriptor))
    if 0xCCC3CDDF not in descriptors:
        descriptors.insert(0, 0xCCC3CDDF)

    extensions = dict(getattr(template, "extensions", {}) or {})
    extensions.update({
        "game_id": "dying_light_2",
        "source_smd": smd_path.name,
        "source_smd_sha256": hashlib.sha256(smd_path.read_bytes()).hexdigest().upper(),
        "source_reference_anm2": anm2_path.name,
        "source_reference_anm2_sha256": hashlib.sha256(anm2_path.read_bytes()).hexdigest().upper(),
        "source_anm2_format": 42,
        "format42_active_track_count": header.active_track_count,
        "format42_reference_track_count": header.reference_track_count,
        "allow_source_superset": True,
        "bind_pose_policy": "fbx_authoritative_global_to_target_bind_global",
        "primary_root": "pelvis",
        "independent_roots": ["l_iktarget", "r_iktarget", "player_shadowcaster"],
        "finger_policy": "dl2_explicit_finger10_20_30_40_roots",
        "resolved_model_axis_conversion": "fbx_y_up_to_dying_light",
        "writer_compatibility": "format1_compatibility_experimental",
    })

    if is_dataclass(template):
        available = {field.name for field in fields(template)}
        changes = {
            "rig_id": "builtin:dl2_player_shadow_caster",
            "name": "Dying Light 2 Player / Shadow Caster",
            "description": "DL2 player target built from the supplied shadow-caster SMD and format-42 descriptor inventory.",
            "source_model_name": smd_path.name,
            "bones": tuple(new_bones),
            "root_index": next(bone.index for bone in new_bones if bone.name == "pelvis"),
            "extra_track_descriptors": tuple(
                value for value in descriptors if value not in bone_descriptors
            ),
            "track_descriptors": tuple(descriptors),
            "extensions": extensions,
        }
        rig = replace(template, **{key: value for key, value in changes.items() if key in available})
    else:
        raise TypeError("ChromeRig must be a dataclass for reference generation")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    rig.save(output_path)
    loaded = ChromeRig.load(output_path)
    loaded.validate().require_valid()
    print(json.dumps({
        "status": "ok", "output": str(output_path), "bone_count": len(loaded.bones),
        "track_count": len(loaded.descriptors),
        "roots": [bone.name for bone in loaded.bones if bone.parent_index < 0],
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
