"""Regenerate the bundled DL1 player helper-capable Chrome Rig."""

from __future__ import annotations

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.dl1_player_tpp import (
    DL1_PLAYER_TPP_HELPER_RIG_RELATIVE_PATH,
    build_dl1_player_tpp_helper_rig,
)


def main() -> int:
    legacy = ChromeRig.load(ROOT / "reference" / "male_npc_infected.crig")
    rig = build_dl1_player_tpp_helper_rig(
        ROOT / "reference" / "player_1_tpp.smd",
        legacy,
        reference_anm2=(
            ROOT / "reference" / "infected_turn_90r.template.anm2"
        ),
    )
    destination = ROOT / DL1_PLAYER_TPP_HELPER_RIG_RELATIVE_PATH
    rig.save(destination)
    print(destination)
    print(rig.skeleton_hash)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
