"""Command-line ANM2 to FBX batch converter."""

from __future__ import annotations

import argparse
from pathlib import Path

from ..blender_fbx import export_anm2_to_fbx
from ..bone_maps import GenericBoneMap, auto_map_skeletons
from ..chrome_rig import ChromeRig
from ..chrome_rig_builder import build_chrome_rig_from_smd_template
from ..chrome_rig_registry import BUILTIN_MALE_RIG_REF
from ..game_profiles import DL2_RIG_REF
from ..anm2_fbx import chrome_rig_from_fbx_skeleton
from ..runtime_paths import resource_root


def load_source_rig(value: str) -> ChromeRig:
    if not value or value == BUILTIN_MALE_RIG_REF:
        root = resource_root()
        packaged = root / "reference" / "male_npc_infected.crig"
        if packaged.is_file():
            return ChromeRig.load(packaged)
        return build_chrome_rig_from_smd_template(
            root / "reference" / "player_1_tpp.smd",
            root / "reference" / "infected_turn_90r.template.anm2",
        )
    if value == DL2_RIG_REF:
        return ChromeRig.load(resource_root() / "reference" / "dl2" / "player_shadow_caster.crig")
    return ChromeRig.load(value)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Decode extracted Dying Light ANM2 files and export skeleton animation FBXs through Blender."
    )
    parser.add_argument("anm2", nargs="+", type=Path)
    parser.add_argument("--source-rig", default=BUILTIN_MALE_RIG_REF, help="Matching .crig or bundled rig ID")
    parser.add_argument("--target-fbx", type=Path, help="Optional different target skeleton FBX")
    parser.add_argument("--bone-map", type=Path, help="Reviewed .dlrbmap.json for cross-rig export")
    parser.add_argument("--auto-map", action="store_true", help="Use conservative automatic mapping when no map is supplied")
    parser.add_argument("--save-auto-map", type=Path)
    parser.add_argument("--fps", type=int, default=30)
    parser.add_argument("--start-frame", type=int)
    parser.add_argument("--end-frame", type=int)
    parser.add_argument("--translation-scale", default="auto")
    parser.add_argument("--blender", type=Path)
    parser.add_argument("--output-directory", type=Path, default=Path("build/fbx"))
    args = parser.parse_args(argv)

    source_rig = load_source_rig(args.source_rig)
    mapping = GenericBoneMap.load(args.bone_map) if args.bone_map else None
    if args.target_fbx and mapping is None:
        if not args.auto_map:
            parser.error("--target-fbx requires --bone-map or --auto-map")
        target = chrome_rig_from_fbx_skeleton(args.target_fbx)
        parents = {
            bone.name: target.bones[bone.parent_index].name if bone.parent_index >= 0 else None
            for bone in target.bones
        }
        mapping = auto_map_skeletons(
            source_rig, [bone.name for bone in target.bones], parents,
            target_skeleton_hash=target.skeleton_hash,
        )
        if args.save_auto_map:
            mapping.save(args.save_auto_map)
    args.output_directory.mkdir(parents=True, exist_ok=True)
    translation_scale: str | float = args.translation_scale
    if translation_scale != "auto":
        translation_scale = float(translation_scale)
    for source in args.anm2:
        result = export_anm2_to_fbx(
            source, source_rig, args.output_directory / f"{source.stem}.fbx",
            fps=args.fps, start_frame=args.start_frame, end_frame=args.end_frame,
            target_fbx=args.target_fbx, bone_map=mapping,
            translation_scale=translation_scale, blender_executable=args.blender,
            progress=print,
        )
        print(f"{result.output_path}: {result.frame_count} frames, {result.bone_count} bones")
        for warning in result.warnings:
            print(f"WARNING: {warning}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
