from __future__ import annotations

import json
import math
import re
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Iterable

import numpy as np

from dlanm2_gui.anm2_components import decode_file_samples
from dlanm2_gui.trackmap import dl_name_hash, read_track_descriptors

_NODE_RE = re.compile(r'^\s*(\d+)\s+"([^"]+)"\s+(-?\d+)\s*$')


@dataclass(frozen=True, slots=True)
class SmdBone:
    index: int
    name: str
    parent_index: int
    translation: tuple[float, float, float]
    euler_xyz_radians: tuple[float, float, float]


@dataclass(frozen=True, slots=True)
class SmdBindPose:
    path: str
    bones: tuple[SmdBone, ...]

    @property
    def by_name(self) -> dict[str, SmdBone]:
        return {bone.name: bone for bone in self.bones}

    @property
    def by_index(self) -> dict[int, SmdBone]:
        return {bone.index: bone for bone in self.bones}


def parse_smd_bind_pose(path: str | Path) -> SmdBindPose:
    source = Path(path)
    names: dict[int, tuple[str, int]] = {}
    transforms: dict[int, tuple[tuple[float, float, float], tuple[float, float, float]]] = {}
    section = ""
    active_time: int | None = None
    for raw_line in source.read_text(encoding="utf-8", errors="replace").splitlines():
        line = raw_line.strip()
        if line == "nodes":
            section = "nodes"
            continue
        if line == "skeleton":
            section = "skeleton"
            continue
        if line == "end":
            section = ""
            continue
        if section == "nodes":
            match = _NODE_RE.match(line)
            if match:
                index = int(match.group(1))
                names[index] = (match.group(2), int(match.group(3)))
            continue
        if section == "skeleton":
            if line.startswith("time "):
                active_time = int(line.split()[1])
                continue
            if not line or active_time != 0:
                continue
            fields = line.split()
            if len(fields) < 7:
                continue
            index = int(fields[0])
            values = tuple(float(value) for value in fields[1:7])
            transforms[index] = (values[:3], values[3:])  # type: ignore[assignment]

    missing = sorted(set(names) - set(transforms))
    if missing:
        raise ValueError(f"SMD is missing time-0 transforms for {len(missing)} nodes: {missing[:8]}")
    bones = tuple(
        SmdBone(
            index=index,
            name=names[index][0],
            parent_index=names[index][1],
            translation=tuple(float(value) for value in transforms[index][0]),
            euler_xyz_radians=tuple(float(value) for value in transforms[index][1]),
        )
        for index in sorted(names)
    )
    return SmdBindPose(path=str(source), bones=bones)


def parse_ascii_global_positions(path: str | Path) -> dict[str, tuple[float, float, float]]:
    lines = [line.strip() for line in Path(path).read_text(encoding="utf-8", errors="replace").splitlines()]
    if not lines:
        raise ValueError("ASCII model file is empty")
    try:
        count = int(lines[0])
    except ValueError as exc:
        raise ValueError("ASCII model file does not begin with a bone count") from exc
    result: dict[str, tuple[float, float, float]] = {}
    cursor = 1
    for _index in range(count):
        if cursor + 2 >= len(lines):
            raise ValueError("ASCII model file ended inside the skeleton table")
        name = lines[cursor]
        _parent = int(lines[cursor + 1])
        xyz = tuple(float(value) for value in lines[cursor + 2].split()[:3])
        if len(xyz) != 3:
            raise ValueError(f"ASCII model bone {name!r} has no global position")
        result[name] = xyz  # type: ignore[assignment]
        cursor += 3
    return result


def smd_extrinsic_xyz_matrix(euler_xyz_radians: Iterable[float]) -> np.ndarray:
    """Return the SMD time-0 rotation matrix.

    The exported files use extrinsic XYZ, which is Rz @ Ry @ Rx for column vectors.
    """

    x, y, z = (float(value) for value in euler_xyz_radians)
    cx, sx = math.cos(x), math.sin(x)
    cy, sy = math.cos(y), math.sin(y)
    cz, sz = math.cos(z), math.sin(z)
    rx = np.asarray(((1.0, 0.0, 0.0), (0.0, cx, -sx), (0.0, sx, cx)), dtype=float)
    ry = np.asarray(((cy, 0.0, sy), (0.0, 1.0, 0.0), (-sy, 0.0, cy)), dtype=float)
    rz = np.asarray(((cz, -sz, 0.0), (sz, cz, 0.0), (0.0, 0.0, 1.0)), dtype=float)
    return rz @ ry @ rx


def smd_local_matrix(bone: SmdBone) -> np.ndarray:
    matrix = np.eye(4, dtype=float)
    matrix[:3, :3] = smd_extrinsic_xyz_matrix(bone.euler_xyz_radians)
    matrix[:3, 3] = np.asarray(bone.translation, dtype=float)
    return matrix


def smd_local_matrices(pose: SmdBindPose) -> dict[str, np.ndarray]:
    return {bone.name: smd_local_matrix(bone) for bone in pose.bones}


def smd_global_matrices(pose: SmdBindPose) -> dict[str, np.ndarray]:
    by_index = pose.by_index
    local = {bone.index: smd_local_matrix(bone) for bone in pose.bones}
    cache: dict[int, np.ndarray] = {}

    def calculate(index: int) -> np.ndarray:
        if index in cache:
            return cache[index]
        parent = by_index[index].parent_index
        cache[index] = local[index] if parent < 0 or parent not in local else calculate(parent) @ local[index]
        return cache[index]

    return {bone.name: calculate(bone.index) for bone in pose.bones}


def quaternion_wxyz_from_matrix(matrix: np.ndarray) -> np.ndarray:
    m=np.asarray(matrix,float)[:3,:3]; trace=np.trace(m)
    if trace>0:
        scale=math.sqrt(trace+1)*2; q=(.25*scale,(m[2,1]-m[1,2])/scale,(m[0,2]-m[2,0])/scale,(m[1,0]-m[0,1])/scale)
    else:
        index=int(np.argmax(np.diag(m)))
        if index==0:
            scale=math.sqrt(max(0,1+m[0,0]-m[1,1]-m[2,2]))*2; q=((m[2,1]-m[1,2])/scale,.25*scale,(m[0,1]+m[1,0])/scale,(m[0,2]+m[2,0])/scale)
        elif index==1:
            scale=math.sqrt(max(0,1+m[1,1]-m[0,0]-m[2,2]))*2; q=((m[0,2]-m[2,0])/scale,(m[0,1]+m[1,0])/scale,.25*scale,(m[1,2]+m[2,1])/scale)
        else:
            scale=math.sqrt(max(0,1+m[2,2]-m[0,0]-m[1,1]))*2; q=((m[1,0]-m[0,1])/scale,(m[0,2]+m[2,0])/scale,(m[1,2]+m[2,1])/scale,.25*scale)
    quaternion=np.asarray(q,float);quaternion/=max(np.linalg.norm(quaternion),1e-12);return quaternion


def anm2_cayley_vector_from_quaternion(quaternion_wxyz: Iterable[float]) -> np.ndarray:
    quaternion = np.asarray(tuple(float(value) for value in quaternion_wxyz), dtype=float)
    quaternion /= np.linalg.norm(quaternion)
    if quaternion[0] < 0.0:
        quaternion *= -1.0
    denominator = 1.0 + float(quaternion[0])
    if denominator < 1.0e-10:
        raise ValueError("quaternion is at the Cayley singularity")
    return quaternion[1:4] / denominator


def quaternion_wxyz_from_anm2_cayley(vector_xyz: Iterable[float]) -> np.ndarray:
    vector = np.asarray(tuple(float(value) for value in vector_xyz), dtype=float)
    dot = float(np.dot(vector, vector))
    denominator = 1.0 + dot
    quaternion = np.asarray(((1.0 - dot) / denominator, *(2.0 * vector / denominator)), dtype=float)
    quaternion /= np.linalg.norm(quaternion)
    return quaternion


def rotation_angle_degrees(left: np.ndarray, right: np.ndarray) -> float:
    relative = np.asarray(left, dtype=float).T @ np.asarray(right, dtype=float)
    cosine = max(-1.0, min(1.0, (float(np.trace(relative)) - 1.0) * 0.5))
    return math.degrees(math.acos(cosine))


def bind_track_values(pose: SmdBindPose, descriptors: Iterable[int], fallback_tracks: Iterable[Iterable[float]]) -> tuple[list[list[float]], dict[int, str], list[int]]:
    by_hash: dict[int, SmdBone] = {dl_name_hash(bone.name): bone for bone in pose.bones}
    fallback = [list(float(value) for value in track) for track in fallback_tracks]
    values: list[list[float]] = []
    names: dict[int, str] = {}
    unmatched: list[int] = []
    for track_index, descriptor in enumerate(descriptors):
        bone = by_hash.get(int(descriptor))
        if bone is None:
            values.append(fallback[track_index])
            unmatched.append(int(descriptor))
            continue
        rotation = anm2_cayley_vector_from_quaternion(quaternion_wxyz_from_matrix(smd_extrinsic_xyz_matrix(bone.euler_xyz_radians)))
        values.append([
            float(rotation[0]), float(rotation[1]), float(rotation[2]),
            float(bone.translation[0]), float(bone.translation[1]), float(bone.translation[2]),
            1.0, 1.0, 1.0,
        ])
        names[int(descriptor)] = bone.name
    return values, names, unmatched


def validate_smd_against_ascii(smd_path: str | Path, ascii_path: str | Path) -> dict[str, Any]:
    pose = parse_smd_bind_pose(smd_path)
    expected = parse_ascii_global_positions(ascii_path)
    actual = smd_global_matrices(pose)
    rows = []
    maximum = 0.0
    total = 0.0
    compared = 0
    for name in sorted(set(expected) & set(actual)):
        delta = float(np.linalg.norm(actual[name][:3, 3] - np.asarray(expected[name], dtype=float)))
        rows.append({"bone_name": name, "position_delta": delta})
        maximum = max(maximum, delta)
        total += delta
        compared += 1
    return {
        "status": "ok" if maximum <= 1.0e-5 else "mismatch",
        "smd_path": str(smd_path),
        "ascii_path": str(ascii_path),
        "smd_bone_count": len(pose.bones),
        "ascii_bone_count": len(expected),
        "compared_bone_count": compared,
        "max_position_delta": maximum,
        "mean_position_delta": total / max(1, compared),
        "rotation_convention": "extrinsic_xyz_radians; matrix=Rz@Ry@Rx",
        "largest_position_deltas": sorted(rows, key=lambda row: row["position_delta"], reverse=True)[:20],
    }


def build_smd_bind_pose_audit(
    *,
    models_dir: str | Path,
    canonical_smd: str | Path,
    stock_anm2: str | Path,
    out_dir: str | Path,
) -> dict[str, Any]:
    models = Path(models_dir)
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    canonical = parse_smd_bind_pose(canonical_smd)
    header, descriptors = read_track_descriptors(stock_anm2)
    descriptor_set = set(descriptors)
    canonical_lookup = {dl_name_hash(bone.name): bone.name for bone in canonical.bones}
    canonical_matches = {descriptor: canonical_lookup[descriptor] for descriptor in descriptors if descriptor in canonical_lookup}
    unmatched = [descriptor for descriptor in descriptors if descriptor not in canonical_lookup]

    ascii_validation = validate_smd_against_ascii(canonical_smd, Path(canonical_smd).with_suffix(".ascii"))
    canonical_local = smd_local_matrices(canonical)
    model_rows: list[dict[str, Any]] = []
    identical_standard_tpp: list[str] = []
    for smd_path in sorted(models.glob("*.smd")):
        pose = parse_smd_bind_pose(smd_path)
        lookup = {dl_name_hash(bone.name): bone for bone in pose.bones}
        matched = sorted(descriptor_set & set(lookup))
        common_names = sorted(set(canonical_local) & set(pose.by_name))
        pose_local = smd_local_matrices(pose)
        descriptor_core_names = sorted(
            name for name in common_names
            if name != "bip01" and dl_name_hash(name) in descriptor_set
        )
        translation_delta = 0.0
        rotation_delta = 0.0
        for name in descriptor_core_names:
            translation_delta = max(translation_delta, float(np.linalg.norm(canonical_local[name][:3, 3] - pose_local[name][:3, 3])))
            rotation_delta = max(rotation_delta, rotation_angle_degrees(canonical_local[name][:3, :3], pose_local[name][:3, :3]))
        full_standard_core = (
            len(matched) >= 69
            and translation_delta <= 2.0e-6
            and rotation_delta <= 0.1
            and "player_zombie" not in smd_path.name
        )
        row = {
            "file": smd_path.name,
            "view": "tpp" if "_tpp" in smd_path.stem else "fpp",
            "bone_count": len(pose.bones),
            "stock_descriptor_match_count": len(matched),
            "stock_descriptor_unmatched_count": len(descriptors) - len(matched),
            "common_with_canonical_count": len(common_names),
            "descriptor_core_compared_count": len(descriptor_core_names),
            "max_descriptor_core_translation_delta": translation_delta,
            "max_descriptor_core_rotation_delta_degrees": rotation_delta,
            "canonical_descriptor_core_close": full_standard_core,
            "rig_classification": "player_zombie_distinct" if "player_zombie" in smd_path.name else ("standard_full_player" if full_standard_core else "partial_or_specialized_player"),
        }
        if full_standard_core and row["view"] == "tpp":
            identical_standard_tpp.append(smd_path.name)
        model_rows.append(row)

    stock_sample = decode_file_samples(stock_anm2, [0.0])
    bind_values, names_by_descriptor, fallback_descriptors = bind_track_values(canonical, descriptors, stock_sample.frames[0].tracks)
    stock_bind_comparison = []
    for track_index, descriptor in enumerate(descriptors):
        if descriptor not in names_by_descriptor:
            continue
        stock_track = stock_sample.frames[0].tracks[track_index]
        target_track = bind_values[track_index]
        stock_bind_comparison.append({
            "track_index": track_index,
            "descriptor": f"0x{descriptor:08X}",
            "bone_name": names_by_descriptor[descriptor],
            "rotation_vector_delta": max(abs(float(stock_track[axis]) - target_track[axis]) for axis in range(3)),
            "translation_delta": max(abs(float(stock_track[axis]) - target_track[axis]) for axis in range(3, 6)),
        })

    summary = {
        "status": "ok" if ascii_validation["status"] == "ok" and len(canonical_matches) == 69 else "needs_review",
        "models_dir": str(models),
        "canonical_smd": str(canonical_smd),
        "stock_anm2": str(stock_anm2),
        "stock_frame_count": header.frame_count,
        "stock_track_count": header.track_count,
        "canonical_bone_count": len(canonical.bones),
        "canonical_descriptor_match_count": len(canonical_matches),
        "canonical_descriptor_unmatched": [f"0x{value:08X}" for value in unmatched],
        "fallback_descriptors": [f"0x{value:08X}" for value in fallback_descriptors],
        "canonical_ascii_validation": ascii_validation,
        "standard_tpp_descriptor_core_count": len(identical_standard_tpp),
        "standard_tpp_descriptor_core": identical_standard_tpp,
        "model_count": len(model_rows),
        "conclusions": [
            "player_1_tpp.smd is a direct target bind/reference-pose source for 69 of 70 stock ANM2 tracks",
            "0xCCC3CDDF is not a mesh bone and must retain a donor/fallback value",
            "SMD time-0 rotations are extrinsic XYZ radians (Rz@Ry@Rx)",
            "player_zombie_tpp is a distinct Night Hunter/player-zombie rig and is excluded from the standard player consensus",
        ],
    }
    _write_json(out / "smd_bind_pose_summary.json", summary)
    _write_json(out / "canonical_smd_ascii_validation.json", ascii_validation)
    _write_json(out / "model_rig_inventory.json", model_rows)
    _write_json(out / "anm2_descriptor_to_smd_bone.json", [
        {"track_index": index, "descriptor": f"0x{descriptor:08X}", "bone_name": canonical_lookup.get(descriptor)}
        for index, descriptor in enumerate(descriptors)
    ])
    _write_json(out / "stock_frame0_vs_smd_bind.json", sorted(stock_bind_comparison, key=lambda row: max(row["rotation_vector_delta"], row["translation_delta"]), reverse=True))
    _write_json(out / "canonical_bind_track_values.json", [
        {
            "track_index": index,
            "descriptor": f"0x{descriptor:08X}",
            "bone_name": names_by_descriptor.get(descriptor),
            "values": bind_values[index],
            "source": "smd_bind" if descriptor in names_by_descriptor else "stock_frame0_fallback",
        }
        for index, descriptor in enumerate(descriptors)
    ])
    return summary


def _write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
