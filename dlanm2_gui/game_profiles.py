"""Coherent game-level target packages for DL ReAnimated projects."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any


DL1_GAME_ID = "dying_light_1"
DL2_GAME_ID = "dying_light_2"
SUPPORTED_GAME_IDS = (DL1_GAME_ID, DL2_GAME_ID)

DL1_RIG_REF = "builtin:male_npc_infected"
DL2_RIG_REF = "builtin:dl2_player_shadow_caster"


@dataclass(frozen=True, slots=True)
class GameProfile:
    game_id: str
    display_name: str
    target_rig_ref: str
    target_rig_name: str
    target_rig_relative_path: str
    canonical_smd_relative_path: str
    reference_anm2_relative_path: str
    stock_control_relative_path: str
    primary_root: str
    anm2_format_label: str
    output_status: str
    finger_policy: str

    def paths(self, root: str | Path) -> dict[str, str]:
        base = Path(root)
        return {
            "target_rig_path": str(base / self.target_rig_relative_path) if self.target_rig_relative_path else "",
            "canonical_smd": str(base / self.canonical_smd_relative_path),
            "target_template_anm2": str(base / self.reference_anm2_relative_path),
            "stock_writer_control_anm2": (
                str(base / self.stock_control_relative_path) if self.stock_control_relative_path else ""
            ),
        }


GAME_PROFILES = {
    DL1_GAME_ID: GameProfile(
        DL1_GAME_ID,
        "Dying Light 1",
        DL1_RIG_REF,
        "Dying Light player_1_tpp / male humanoid",
        "",
        "reference/player_1_tpp.smd",
        "reference/infected_turn_90r.template.anm2",
        "reference/stock_writer_control.anm2",
        "bip01",
        "format 1",
        "validated",
        "dl1_hand1_parent_policy",
    ),
    DL2_GAME_ID: GameProfile(
        DL2_GAME_ID,
        "Dying Light 2",
        DL2_RIG_REF,
        "Dying Light 2 Player / Shadow Caster",
        "reference/dl2/player_shadow_caster.crig",
        "reference/dl2/player_shadow_caster.smd",
        "reference/dl2/0_m_fpp_farjump.anm2",
        "",
        "pelvis",
        "format 42",
        "inspection-only native / experimental format-1 compatibility export",
        "dl2_explicit_finger10_20_30_40_roots",
    ),
}


def get_game_profile(game_id: str | None) -> GameProfile:
    value = str(game_id or DL1_GAME_ID)
    if value not in GAME_PROFILES:
        raise ValueError(
            f"Unsupported game identifier {value!r}. Choose Dying Light 1 or Dying Light 2."
        )
    return GAME_PROFILES[value]


def infer_game_id(payload: dict[str, Any]) -> str:
    """Infer only unmistakable DL2 projects; all old/ambiguous projects stay DL1."""

    explicit = payload.get("game_id")
    if explicit in SUPPORTED_GAME_IDS:
        return str(explicit)
    extensions = payload.get("extensions", {})
    if isinstance(extensions, dict) and extensions.get("game_id") in SUPPORTED_GAME_IDS:
        return str(extensions["game_id"])
    rig = payload.get("rig", {})
    if isinstance(rig, dict):
        values = "\n".join(
            str(rig.get(key, ""))
            for key in ("target_rig_ref", "target_rig_path", "canonical_smd", "target_template_anm2")
        ).casefold()
        if "builtin:dl2_" in values or "reference/dl2/" in values.replace("\\", "/"):
            return DL2_GAME_ID
    return DL1_GAME_ID


def path_looks_dl2(value: str) -> bool:
    normalized = str(value or "").replace("\\", "/").casefold()
    return "reference/dl2/" in normalized or "/dl2/" in normalized or "dl2_" in normalized


def project_coherence_errors(project: Any) -> list[str]:
    profile = get_game_profile(getattr(project, "game_id", DL1_GAME_ID))
    rig = project.rig
    errors: list[str] = []
    selected_ref = str(rig.target_rig_ref or "")
    if selected_ref.startswith("builtin:") and selected_ref != profile.target_rig_ref:
        errors.append(
            f"{profile.display_name} cannot use built-in target {selected_ref!r}; "
            f"select {profile.target_rig_ref!r} or a compatible custom .crig."
        )
    target_values = (rig.target_rig_path, rig.canonical_smd, rig.target_template_anm2)
    if profile.game_id == DL1_GAME_ID and any(path_looks_dl2(value) for value in target_values):
        errors.append("Dying Light 1 is selected, but a DL2 target SMD/CRIG/reference ANM2 is configured.")
    if profile.game_id == DL2_GAME_ID:
        if selected_ref == DL1_RIG_REF:
            errors.append("Dying Light 2 is selected, but the DL1 male NPC target is configured.")
        template = Path(str(rig.target_template_anm2 or ""))
        if template.name and template.name in {"infected_turn_90r.template.anm2", "stock_writer_control.anm2"}:
            errors.append("Dying Light 2 is selected, but a DL1 reference ANM2 is configured.")
    custom_rig = Path(str(rig.target_rig_path or ""))
    if custom_rig.is_file() and custom_rig.suffix.casefold() == ".crig":
        try:
            from .chrome_rig import ChromeRig
            rig_game = str(ChromeRig.load(custom_rig).extensions.get("game_id", "") or "")
            if rig_game in SUPPORTED_GAME_IDS and rig_game != profile.game_id:
                errors.append(
                    f"The selected custom .crig identifies {rig_game!r}, but the project "
                    f"identifies {profile.game_id!r}."
                )
        except (OSError, ValueError):
            pass
    template = Path(str(rig.target_template_anm2 or ""))
    if template.is_file() and template.stat().st_size >= 8:
        try:
            from .dl2_anm2 import detect_anm2_format
            detected = detect_anm2_format(template)
            expected = 42 if profile.game_id == DL2_GAME_ID else 1
            if detected != expected:
                errors.append(
                    f"{profile.display_name} expects ANM2 {profile.anm2_format_label}, but "
                    f"{template.name!r} was detected as format {detected}."
                )
        except ValueError as exc:
            errors.append(f"The target reference ANM2 is unsupported: {exc}")
    return list(dict.fromkeys(errors))


def _matches_profile_path(value: str, relative: str, root: Path) -> bool:
    if not relative:
        return not str(value or "")
    normalized = str(value or "").replace("\\", "/").casefold()
    rel = relative.replace("\\", "/").casefold()
    absolute = str(root / relative).replace("\\", "/").casefold()
    return normalized in {rel, absolute} or normalized.endswith("/" + rel)


def apply_game_profile_defaults(
    project: Any,
    root: str | Path,
    *,
    previous_game_id: str | None = None,
    force: bool = False,
) -> dict[str, list[str]]:
    """Switch coherent defaults without overwriting deliberate custom choices."""

    base = Path(root)
    current = get_game_profile(project.game_id)
    previous = get_game_profile(previous_game_id or project.game_id)
    changed: list[str] = []
    retained: list[str] = []
    rig = project.rig
    if force or rig.target_rig_ref == previous.target_rig_ref:
        rig.target_rig_ref = current.target_rig_ref
        rig.target_rig_path = current.paths(base)["target_rig_path"]
        rig.target_rig_name = current.target_rig_name
        rig.retarget_mode = "humanoid" if current.game_id == DL1_GAME_ID else "exact"
        changed.append("target rig")
    else:
        retained.append("custom target rig")
    path_fields = {
        "canonical_smd": (previous.canonical_smd_relative_path, current.canonical_smd_relative_path),
        "target_template_anm2": (previous.reference_anm2_relative_path, current.reference_anm2_relative_path),
        "stock_writer_control_anm2": (previous.stock_control_relative_path, current.stock_control_relative_path),
    }
    current_paths = current.paths(base)
    for field_name, (previous_relative, _current_relative) in path_fields.items():
        old_value = str(getattr(rig, field_name, "") or "")
        if force or _matches_profile_path(old_value, previous_relative, base):
            setattr(rig, field_name, current_paths[field_name])
            changed.append(field_name)
        elif old_value:
            retained.append(field_name)
    for item in project.anm2_to_fbx.items:
        if force or item.source_rig_ref == previous.target_rig_ref:
            item.source_rig_ref = current.target_rig_ref
            item.source_rig_path = current_paths["target_rig_path"]
    extensions = dict(project.extensions or {})
    extensions["game_id"] = current.game_id
    extensions["game_profile_status"] = {
        "expected_anm2_format": current.anm2_format_label,
        "primary_root": current.primary_root,
        "finger_policy": current.finger_policy,
        "output_status": current.output_status,
    }
    project.extensions = extensions
    return {"changed": changed, "retained": retained}


__all__ = [
    "DL1_GAME_ID", "DL2_GAME_ID", "DL1_RIG_REF", "DL2_RIG_REF", "GAME_PROFILES",
    "GameProfile", "SUPPORTED_GAME_IDS", "get_game_profile", "infer_game_id",
    "apply_game_profile_defaults", "path_looks_dl2", "project_coherence_errors",
]
