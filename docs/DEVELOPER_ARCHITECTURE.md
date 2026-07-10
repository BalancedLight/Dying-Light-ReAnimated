# Developer architecture

The release base separates models, codecs, build orchestration, and UI so each layer can evolve without invalidating project files.

## Main modules

```
dlanm2_gui/workspace_project.py
  Versioned .dlraproj model, migrations, path portability, atomic save

dlanm2_gui/retarget_profiles.py
  Humanoid roles, auto-map, .dlrmap.json profile model

dlanm2_gui/script_targets.py
  Built-in and custom _ANIMATION_SCR_ target registry

dlanm2_gui/project_builder.py
  Multi-clip project orchestration; new/append build

dlanm2_gui/rp6l.py
  Strict RP6L animation-library parse/build

dlanm2_gui/pack_manifest.py
  Tool-owned pack provenance/hash sidecar

dlanm2_gui/fbx_pipeline.py
  Stable low-level FBX-to-RPack Python API

dlanm2_gui/gui.py
  Thin PySide6 interface over the models/build service
```

The GUI must not duplicate codec or build rules. CLI and GUI builds should call the same `build_project` or `build_fbx_rpack` functions.

## Project compatibility

Existing IDs and enum values are API:

```
humanoid role IDs
root policies: inplace / bip01 / motion
IK presets: runtime / off
script target IDs
project/mapping format names
```

Do not rename them without a migration.

Project schema 2 introduced `rig.use_imported_animation_bind_pose`. The builder
resolves the source rest per clip: the animation FBX in embedded mode, or the
explicit rest FBX otherwise. Trusted-rest validation is intentionally skipped in
embedded mode.

When adding a project field:

1. add a default to the dataclass;
2. bump schema only when semantics require it;
3. add a migration;
4. preserve unknown fields;
5. update JSON Schema and docs;
6. add round-trip tests.

## Adding animation-script presets

Add an `AnimationScriptTarget` to `BUILTIN_SCRIPT_TARGETS`. The underlying builder already accepts arbitrary resource names, so presets are presentation metadata rather than codec logic.

## Adding source skeleton conventions

Add aliases to the existing semantic role rather than branching the retargeter by tool name. For a genuinely new anatomical role, add a stable `HumanoidRole` and ensure old mappings remain valid.

## Adding target rigs

A target-rig preset should bundle or locate:

```
canonical SMD/reference hierarchy
ANM2 template/descriptor policy
writer regression control
human-readable target family
compatible default animation-script target
```

Target-rig presets should not be conflated with source mappings.

## RPack safety

Unknown RP6L resource types must not be silently dropped. The current append workflow is intentionally restricted to the known animation-library resource set. General RPack editing should be implemented as a separate capability with preservation tests.

## GUI behavior rules

- Simple mode hides target implementation files and diagnostic exports.
- Advanced mode is a local `QSettings` preference, not animation data.
- Closed combo boxes ignore wheel events so scrolling tables cannot change values.
- The GUI must expose clear tooltips while keeping build logic in service modules.

## GUI extension points

Suggested next GUI work:

- target-rig preset manager;
- custom script-target preset dialog;
- skeleton tree/3D preview;
- mapping copy/link across selected clips;
- background worker with cancel/progress;
- thumbnail and frame scrub preview;
- validation dashboard;
- installer/packaging.

Keep project/build APIs usable headlessly so automation remains possible.

## Release boundary

User-facing supported behavior belongs under `docs/`. Keep disassembly notes, runtime probes, scratch research, and generated diagnostics outside the release tree. Release code must not import those artifacts at runtime.
