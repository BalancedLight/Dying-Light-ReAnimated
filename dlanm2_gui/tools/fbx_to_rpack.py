from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from dlanm2_gui.fbx_pipeline import ROOT_POLICIES, build_fbx_rpack
from dlanm2_gui.retarget_profiles import SourceBoneMappingProfile


def _resolve(value: str | Path, base: Path) -> Path:
    path = Path(value)
    return path if path.is_absolute() else (base / path).resolve()


def _load_config(path: Path) -> dict[str, Any]:
    payload = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(payload, dict):
        raise ValueError("configuration root must be a JSON object")
    base = path.parent.resolve()
    path_fields = (
        "source_rest_fbx",
        "trusted_source_rest_json",
        "mapping_profile",
        "canonical_smd",
        "target_template_anm2",
        "stock_writer_control_anm2",
        "out_dir",
    )
    for key in path_fields:
        if key in payload:
            payload[key] = _resolve(payload[key], base)
    if "animation_fbxs" in payload:
        payload["animation_fbxs"] = [
            _resolve(value, base) for value in payload["animation_fbxs"]
        ]
    mapping_profile = payload.pop("mapping_profile", None)
    if mapping_profile:
        payload["source_bone_aliases"] = SourceBoneMappingProfile.load(mapping_profile).canonical_aliases()
    return payload


def _explicit_args(args: argparse.Namespace) -> dict[str, Any]:
    required = {
        "animation_fbxs": args.animation_fbx,
        "source_rest_fbx": args.source_rest_fbx,
        "trusted_source_rest_json": args.trusted_source_rest_json or None,
        "canonical_smd": args.canonical_smd,
        "target_template_anm2": args.target_template_anm2,
        "stock_writer_control_anm2": args.stock_writer_control_anm2,
        "out_dir": args.out_dir,
    }
    optional = {"trusted_source_rest_json"}
    missing = [key for key, value in required.items() if not value and key not in optional]
    if missing:
        raise ValueError(
            "without --config, these options are required: " + ", ".join(missing)
        )
    required["root_policies"] = args.root_policy or ("inplace", "motion")
    required["ik_authoring_preset"] = args.ik_authoring_preset
    required["animation_script_resource_name"] = args.animation_script_resource
    required["include_controls"] = not args.no_controls
    if args.mapping_profile:
        required["source_bone_aliases"] = SourceBoneMappingProfile.load(args.mapping_profile).canonical_aliases()
    return required


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Retarget standard Mixamo FBX animations to Dying Light ANM2 and build common_anims_sp_pc.rpack."
    )
    parser.add_argument("--config", type=Path, help="JSON configuration file")
    parser.add_argument("--animation-fbx", action="append")
    parser.add_argument("--source-rest-fbx")
    parser.add_argument("--trusted-source-rest-json", help="Optional trusted rest-matrix JSON used as an FBX parser oracle")
    parser.add_argument("--mapping-profile", help=".dlrmap.json source humanoid mapping profile")
    parser.add_argument("--canonical-smd")
    parser.add_argument("--target-template-anm2")
    parser.add_argument("--stock-writer-control-anm2")
    parser.add_argument("--out-dir")
    parser.add_argument(
        "--animation-script-resource",
        default="anims_man_all_DLC60",
        help="_ANIMATION_SCR_ resource name (for example anims_player_dlc60 or anims_woman_all)",
    )
    parser.add_argument("--no-controls", action="store_true", help="Do not include stock/bind validation controls")
    parser.add_argument(
        "--root-policy",
        action="append",
        choices=ROOT_POLICIES,
        help="Repeat to build multiple variants. Default: inplace + motion.",
    )
    parser.add_argument(
        "--ik-authoring-preset",
        choices=("runtime", "off"),
        default="runtime",
        help="Authoring sidecar recommendation; IK is not encoded in ANM2.",
    )
    args = parser.parse_args()

    try:
        options = _load_config(args.config) if args.config else _explicit_args(args)
        if args.root_policy:
            options["root_policies"] = args.root_policy
        if args.config and args.ik_authoring_preset != "runtime":
            options["ik_authoring_preset"] = args.ik_authoring_preset
        if args.config and args.animation_script_resource != "anims_man_all_DLC60":
            options["animation_script_resource_name"] = args.animation_script_resource
        if args.config and args.no_controls:
            options["include_controls"] = False
        if args.config and args.mapping_profile:
            options["source_bone_aliases"] = SourceBoneMappingProfile.load(args.mapping_profile).canonical_aliases()
        summary = build_fbx_rpack(**options)
    except Exception as exc:
        parser.error(str(exc))
        return

    pack = summary["pack"]
    print(f"status={summary['status']}")
    print(f"resources={summary['resource_count']}")
    print(f"pack={pack['path']}")
    print(f"sha256={pack['sha256']}")


if __name__ == "__main__":
    main()
