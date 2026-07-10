from __future__ import annotations

from pathlib import Path

from .project_paths import common_anims_pc_dir, data0_pak_path, repo_root

DEFAULT_GAME_ROOT = data0_pak_path().parents[1] if len(data0_pak_path().parents) > 1 else repo_root() / "external" / "Dying Light"
DEFAULT_DATA0_PAK = DEFAULT_GAME_ROOT / "DW" / "Data0.pak"
DEFAULT_ANM2_CORPUS = common_anims_pc_dir()
DEFAULT_ANIMATION_DIR = DEFAULT_ANM2_CORPUS / "Animation"
DEFAULT_TEMPLATE_ANM2_BY_TARGET = {
    "player_fpp": DEFAULT_ANIMATION_DIR / "fpp_car_alarm_arming_player_01.anm2",
    "player_tpp": DEFAULT_ANIMATION_DIR / "tpp_beretta_stand.anm2",
    "npc_zombie": DEFAULT_ANIMATION_DIR / "dncrs_0001_3dc_3p.anm2",
}
DEFAULT_TEMPLATE_ANM2 = DEFAULT_ANIMATION_DIR / "m_melee_fighter_a_patrol_idle_01.anm2"
DEFAULT_BLENDER_EXE = repo_root() / "external" / "Blender" / "blender.exe"
DEFAULT_RESPACK_COMPILER = repo_root() / "external" / "Dying Light Developer Tools" / "ResPackCompilerConsole_x64_rwdi.exe"
