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

## `mixamo_humanoid.dlrmap.json`

Identity mapping for standard `mixamorig:*` names. Load it in the Retargeting tab or reference it from the single-shot CLI config.

## `fbx_to_rpack.example.json`

Low-level single-shot CLI configuration. New work should generally use `.dlraproj`, while this config remains useful for automation and focused builds.
