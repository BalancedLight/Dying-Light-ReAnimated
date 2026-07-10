from dlanm2_gui.script_targets import ScriptTargetRegistry


def test_builtin_script_targets_resolve_to_resource_names() -> None:
    registry = ScriptTargetRegistry()
    assert registry.resolve_resource_name("player_male") == "anims_player_dlc60"
    assert registry.resolve_resource_name("npc_female") == "anims_woman_all"
    assert registry.resolve_resource_name("my_custom_script") == "my_custom_script"
