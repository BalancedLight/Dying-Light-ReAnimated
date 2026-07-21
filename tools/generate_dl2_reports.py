"""Generate deterministic audit reports for the bundled DL2 reference inputs."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys
from typing import Any

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from dlanm2_gui.anm2_fbx import build_decode_report, decode_anm2_animation
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.dl2_anm2 import parse_anm2_v2_layout, select_v2_time
from dlanm2_gui.root_mapping import parent_names_from_smd, read_smd_hierarchy
from dlanm2_gui.trackmap import dl_name_hash


REPORT_FILENAMES = (
    "dl2_farjump_v2_layout.json",
    "dl2_farjump_descriptor_map.json",
    "dl2_advanced_skeleton_diff.json",
    "dl2_farjump_decode_smoke.json",
)
SMOKE_FRAMES = (0, 1, 15, 16, 119, 120, 121, 227, 228)


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def _write_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(payload, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def _input_provenance(anm2_path: Path, advanced_smd: Path, legacy_smd: Path) -> dict[str, Any]:
    return {
        "farjump_anm2": {
            "path": "reference/dl2/0_m_fpp_farjump.anm2",
            "size": anm2_path.stat().st_size,
            "sha256": _sha256(anm2_path),
        },
        "advanced_smd": {
            "path": "reference/dl2/player_skeleton.smd",
            "size": advanced_smd.stat().st_size,
            "sha256": _sha256(advanced_smd),
        },
        "legacy_smd": {
            "path": "reference/dl2/player_shadow_caster.smd",
            "size": legacy_smd.stat().st_size,
            "sha256": _sha256(legacy_smd),
        },
    }


def _descriptor_map(layout, advanced_rows, legacy_rows, provenance) -> dict[str, Any]:
    advanced_by_hash: dict[int, list[str]] = {}
    for row in advanced_rows:
        advanced_by_hash.setdefault(dl_name_hash(row.name), []).append(row.name)
    legacy_hashes = {dl_name_hash(row.name) for row in legacy_rows}
    matches = []
    unmatched = []
    for index, descriptor in enumerate(layout.descriptors):
        names = sorted(advanced_by_hash.get(descriptor, ()))
        record = {"track_index": index, "descriptor": f"0x{descriptor:08X}"}
        if names:
            matches.append({**record, "bone_names": names})
        else:
            unmatched.append({**record, "semantic": "unknown_transform_track"})
    newly_resolved = sorted(
        names[0]
        for descriptor, names in advanced_by_hash.items()
        if descriptor in set(layout.descriptors) and descriptor not in legacy_hashes
    )
    collisions = {
        f"0x{descriptor:08X}": sorted(names)
        for descriptor, names in advanced_by_hash.items()
        if len(names) > 1
    }
    return {
        "inputs": provenance,
        "container": layout.container,
        "descriptor_count": len(layout.descriptors),
        "advanced_skeleton_node_count": len(advanced_rows),
        "matched_descriptor_count": len(matches),
        "unmatched_descriptor_count": len(unmatched),
        "hash_collision_count": len(collisions),
        "hash_collisions": collisions,
        "newly_resolved_count": len(newly_resolved),
        "newly_resolved_names": newly_resolved,
        "matches": matches,
        "unmatched": unmatched,
    }


def _addition_category(name: str) -> str:
    lowered = name.casefold()
    if "leg_secanim" in lowered:
        return "secondary_animation"
    if lowered in {"refcamera", "eyecamera"}:
        return "camera"
    if lowered.startswith("player_collar_"):
        return "collar"
    if lowered == "headend" or "_fx_" in lowered or "holder" in lowered:
        return "attachment"
    if "helper" in lowered:
        return "helper"
    return "facial"


def _skeleton_diff(advanced_rows, legacy_rows, provenance) -> dict[str, Any]:
    advanced_parents = parent_names_from_smd(advanced_rows)
    legacy_parents = parent_names_from_smd(legacy_rows)
    advanced_names = set(advanced_parents)
    legacy_names = set(legacy_parents)
    common = sorted(advanced_names & legacy_names)
    added = sorted(advanced_names - legacy_names)
    removed = sorted(legacy_names - advanced_names)
    parent_changes = [
        {
            "bone": name,
            "legacy_parent": legacy_parents[name],
            "advanced_parent": advanced_parents[name],
        }
        for name in common
        if legacy_parents[name] != advanced_parents[name]
    ]
    categories: dict[str, list[str]] = {
        "facial": [],
        "secondary_animation": [],
        "camera": [],
        "collar": [],
        "attachment": [],
        "helper": [],
    }
    for name in added:
        categories[_addition_category(name)].append(name)
    return {
        "inputs": provenance,
        "advanced_node_count": len(advanced_rows),
        "advanced_roots": sorted(
            row.name for row in advanced_rows if row.parent_index < 0
        ),
        "legacy_node_count": len(legacy_rows),
        "legacy_roots": sorted(row.name for row in legacy_rows if row.parent_index < 0),
        "common_node_count": len(common),
        "common_parent_change_count": len(parent_changes),
        "common_parent_changes": parent_changes,
        "added_count": len(added),
        "added": added,
        "removed_count": len(removed),
        "removed": removed,
        "addition_category_counts": {
            category: len(names) for category, names in categories.items()
        },
        "addition_categories": categories,
    }


def generate_reports(root: Path, output_directory: Path) -> tuple[Path, ...]:
    anm2_path = root / "reference" / "dl2" / "0_m_fpp_farjump.anm2"
    advanced_smd = root / "reference" / "dl2" / "player_skeleton.smd"
    legacy_smd = root / "reference" / "dl2" / "player_shadow_caster.smd"
    advanced_crig = root / "reference" / "dl2" / "player_skeleton.crig"
    provenance = _input_provenance(anm2_path, advanced_smd, legacy_smd)

    layout = parse_anm2_v2_layout(anm2_path).require_valid()
    advanced_rows = read_smd_hierarchy(advanced_smd)
    legacy_rows = read_smd_hierarchy(legacy_smd)
    rig = ChromeRig.load(advanced_crig)
    animation = decode_anm2_animation(anm2_path)

    layout_report = layout.to_dict()
    layout_report["inputs"] = provenance
    layout_report["track_table_end"] = layout.track_table_offset + layout.track_count * 4

    descriptor_report = _descriptor_map(layout, advanced_rows, legacy_rows, provenance)
    skeleton_report = _skeleton_diff(advanced_rows, legacy_rows, provenance)

    pelvis_index = layout.descriptors.index(dl_name_hash("pelvis"))
    samples = []
    for frame in SMOKE_FRAMES:
        selection = select_v2_time(layout, float(frame))
        values = animation.values[frame]
        samples.append(
            {
                "time": frame,
                "selection": {
                    "evaluated_frame": selection.evaluated_frame,
                    "adjusted_frame": selection.adjusted_frame,
                    "block_index": selection.block_index,
                    "table_index": selection.page_table_index,
                    "frame_in_slot": selection.frame_in_15_frame_slot,
                    "fraction": selection.interpolation_fraction,
                },
                "finite": bool(np.isfinite(values).all()),
                "minimum": float(np.min(values)),
                "maximum": float(np.max(values)),
                "mean_absolute": float(np.mean(np.abs(values))),
                "pelvis_first_six": [float(value) for value in values[pelvis_index, :6]],
            }
        )
    decode_report = {
        "inputs": provenance,
        **build_decode_report(animation, rig),
        "shape": list(animation.values.shape),
        "all_frame_values_finite": bool(np.isfinite(animation.values).all()),
        "block_boundary_120_to_121_finite": bool(
            np.isfinite(animation.values[120:122]).all()
        ),
        "samples": samples,
    }

    payloads = (layout_report, descriptor_report, skeleton_report, decode_report)
    paths = tuple(output_directory / filename for filename in REPORT_FILENAMES)
    for path, payload in zip(paths, payloads):
        _write_json(path, payload)
    return paths


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root",
        type=Path,
        default=ROOT,
        help="Repository root",
    )
    parser.add_argument(
        "--output-directory",
        type=Path,
        help="Destination (default: <root>/build/reports)",
    )
    args = parser.parse_args(argv)
    root = args.root.resolve()
    output = (args.output_directory or root / "build" / "reports").resolve()
    for path in generate_reports(root, output):
        print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
