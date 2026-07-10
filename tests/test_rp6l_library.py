from __future__ import annotations

from dlanm2_gui.animation_scr import AnimationScrSequence, build_animation_scr_sections
from dlanm2_gui.rp6l import build_animation_library_rpack, extract_animation_library


def _script(name: str):
    return build_animation_scr_sections(
        [AnimationScrSequence(name, name + ".anm2", 0.0, 9.0, 30.0)]
    )


def test_multi_script_library_roundtrip() -> None:
    animations = {"clip_a": b"ANM2-A", "clip_b": b"ANM2-B"}
    scripts = {
        "anims_player_dlc60": _script("clip_a"),
        "anims_woman_all": _script("clip_b"),
    }
    payload = build_animation_library_rpack(
        animation_resources=animations,
        animation_scripts=scripts,
    )
    parsed = extract_animation_library(payload)
    assert parsed.animations == animations
    assert parsed.animation_scripts == scripts


def test_rebuilt_library_can_append_without_losing_existing_resources() -> None:
    first = build_animation_library_rpack(
        animation_resources={"clip_a": b"A"},
        animation_scripts={"anims_man_all_DLC60": _script("clip_a")},
    )
    library = extract_animation_library(first)
    animations = dict(library.animations)
    animations["clip_b"] = b"B"
    scripts = dict(library.animation_scripts)
    scripts["anims_player_dlc60"] = _script("clip_b")
    second = build_animation_library_rpack(
        animation_resources=animations,
        animation_scripts=scripts,
    )
    parsed = extract_animation_library(second)
    assert set(parsed.animations) == {"clip_a", "clip_b"}
    assert set(parsed.animation_scripts) == {
        "anims_man_all_DLC60",
        "anims_player_dlc60",
    }
