from __future__ import annotations

from dlanm2_gui.workspace_project import DlReanimatedProject
from dlanm2_gui.project_builder import _provenance_root_modes


def _payload(*, game_id: str, mode: str, fps: float = 48.0) -> dict:
    return {
        "format": "dl-reanimated-project",
        "schema_version": 9,
        "project_id": "timing",
        "name": "Timing",
        "created_utc": "2026-01-01T00:00:00+00:00",
        "modified_utc": "2026-01-01T00:00:00+00:00",
        "game_id": game_id,
        "rig": {
            "retarget_mode": mode,
            "target_rig_ref": (
                "builtin:male_npc_infected"
                if game_id == "dying_light_1"
                else "builtin:dl2_player_advanced"
            ),
        },
        "animations": [{
            "animation_id": "clip",
            "source_fbx": "clip.fbx",
            "display_name": "Clip",
            "resource_name": "clip",
            "fps": fps,
        }],
        "anm2_to_fbx": {
            "items": [{
                "conversion_id": "reverse",
                "source_anm2": "clip.anm2",
                "output_name": "clip",
                "fps": fps,
            }]
        },
    }


def test_schema_v10_migration_preserves_mode_dependent_legacy_sampling() -> None:
    humanoid = DlReanimatedProject.from_dict(
        _payload(game_id="dying_light_1", mode="auto")
    )
    exact = DlReanimatedProject.from_dict(
        _payload(game_id="dying_light_2", mode="auto")
    )
    assert humanoid.animations[0].source_fps is None
    assert humanoid.animations[0].sample_fps == 30.0
    assert humanoid.animations[0].playback_fps == 48.0
    assert exact.animations[0].sample_fps == 48.0
    assert exact.animations[0].playback_fps == 48.0
    reverse = exact.anm2_to_fbx.items[0]
    assert reverse.anm2_input_fps == 48.0
    assert reverse.fbx_output_fps == 48.0


def test_v9_aliases_mirror_v10_playback_and_output_rates() -> None:
    project = DlReanimatedProject.from_dict(
        _payload(game_id="dying_light_2", mode="exact")
    )
    project.animations[0].playback_fps = 23.976
    project.anm2_to_fbx.items[0].fbx_output_fps = 24.0
    rendered = project.to_dict()
    assert rendered["animations"][0]["fps"] == 23.976
    assert rendered["anm2_to_fbx"]["items"][0]["fps"] == 24.0


def test_provenance_uses_external_root_mode_vocabulary() -> None:
    project = DlReanimatedProject.from_dict(
        _payload(game_id="dying_light_1", mode="auto")
    )
    assert _provenance_root_modes(project.animations[0]) == (
        "in_place",
        "lock_initial_heading",
    )


def test_validation_reports_malformed_timing_without_raising() -> None:
    project = DlReanimatedProject.from_dict(
        _payload(game_id="dying_light_2", mode="exact")
    )
    animation = project.animations[0]
    animation.source_fps = 10**10_000
    animation.sample_fps = "bad"  # type: ignore[assignment]
    reverse = project.anm2_to_fbx.items[0]
    reverse.anm2_input_fps = "bad"  # type: ignore[assignment]
    reverse.fbx_output_fps = True  # type: ignore[assignment]

    errors = project.validate()

    assert "Animation 'Clip' has an invalid source FPS." in errors
    assert "Animation 'Clip' has an invalid sample FPS." in errors
    assert "Reverse item 'clip' has an invalid ANM2 input FPS." in errors
    assert "Reverse item 'clip' has an invalid FBX output FPS." in errors
