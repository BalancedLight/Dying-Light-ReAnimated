# Examples

## `multi_animation_project.example.dlraproj`

A schema-version-2 project demonstrating:

- three imported FBXs;
- a shared Mixamo humanoid mapping profile;
- project-default and per-clip animation-script targets;
- `inplace` and `motion` root policies;
- portable relative paths;
- multi-animation output.

Copy it beside an `inputs/` folder or open it in the GUI and browse to the missing files.

## `dl2_player_advanced.example.dlraproj`

A read/export example for the validated Dying Light 2 Header_Version2 path. It selects
the bundled 271-node `builtin:dl2_player_advanced` rig, the far-jump reference ANM2,
and the default deterministic `sidecar` policy for its 97 unresolved descriptors.

`dl2_player_shadow_caster.example.dlraproj` remains a legacy-topology example. Its
explicit `builtin:dl2_player_shadow_caster` selection is intentionally not migrated
to the advanced preset.

## `mixamo_humanoid.dlrmap.json`

Identity mapping for standard `mixamorig:*` names. Load it in the Retargeting tab or reference it from the single-shot CLI config.

## `fbx_to_rpack.example.json`

Low-level single-shot CLI configuration. New work should generally use `.dlraproj`, while this config remains useful for automation and focused builds.
