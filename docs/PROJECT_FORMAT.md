# `.dlraproj` project format

DL ReAnimated projects are UTF-8 JSON files with the extension `.dlraproj`. They are readable, diffable, migration-driven, and safe to reopen in later application versions.

## Compatibility guarantees

Current projects declare schema 10:

```json
{
  "format": "dl-reanimated-project",
  "schema_version": 10,
  "minimum_reader_version": 1,
  "created_with": "application version",
  "project_id": "stable UUID",
  "game_id": "dying_light_1"
}
```

The loader applies deterministic migrations in order. Saving is atomic through a temporary file and `os.replace`. Known paths are made relative to the project where possible.

Unknown top-level and nested object fields are retained in `extensions.unknown_fields` and re-emitted at their original object level on save. Existing mapping rows/profiles, model-workspace data, target selections, root choices, animation settings, notes, tags, and extension payloads are not discarded during migration.

## Schema history

### Schema 2: source-bind policy

```json
{
  "rig": {
    "use_imported_animation_bind_pose": true,
    "source_rest_fbx": ""
  }
}
```

When `use_imported_animation_bind_pose` is true, each animation FBX supplies its source bind and no separate T-pose is required. When false, `source_rest_fbx` must name a neutral/rest FBX with the same source skeleton.

Schema-1 migration preserves explicit-rest behavior when a non-empty rest path exists; otherwise it enables embedded-bind mode.

### Schema 3: target CRIG

```json
{
  "rig": {
    "target_rig_ref": "builtin:male_npc_infected",
    "target_rig_path": "",
    "retarget_mode": "humanoid"
  }
}
```

Older SMD/template/control fields remain available and are also recorded under `rig.extensions.legacy_target_files`.

### Schema 5: ANM2 to FBX workspace

Schema 5 adds the optional `anm2_to_fbx` section containing reverse-conversion jobs, source rig references, native/cross-rig mode, target skeleton FBX, FPS/frame ranges, translation scale, output directory, and generic mapping profiles. Blender's executable path is machine-local UI state and is not stored in a portable project.

### Schema 7: game profile

Schema 7 adds `game_id`. Missing projects migrate to Dying Light 1 unless existing target paths unmistakably identify DL2. The profile keeps target rig, root policy, reference format, and output status coherent. This does not claim native DL2 format-42 writing.

### Schema 8: multiple target rigs

Schema 8 adds explicit project-default aliases and optional per-animation target/root overrides:

```json
{
  "rig": {
    "target_rig_ref": "authored:default-character",
    "target_rig_path": "rigs/default-character.crig",
    "default_target_rig_ref": "authored:default-character",
    "default_target_rig_path": "rigs/default-character.crig",
    "retarget_mode": "exact"
  },
  "animations": [
    {
      "animation_id": "stable clip UUID",
      "source_fbx": "inputs/walk.fbx",
      "display_name": "Walk",
      "resource_name": "walk_default",
      "mapping_profile_id": "",
      "target_rig_ref": "",
      "target_rig_path": "",
      "source_root_bone": "",
      "target_root_bone": ""
    },
    {
      "animation_id": "another UUID",
      "source_fbx": "inputs/door_open.fbx",
      "display_name": "Door open",
      "resource_name": "door_open_custom",
      "mapping_profile_id": "reviewed door map UUID",
      "target_rig_ref": "authored:door-rig",
      "target_rig_path": "rigs/door.crig",
      "source_root_bone": "DoorRoot",
      "target_root_bone": "door_root"
    }
  ]
}
```

An empty animation `target_rig_ref`/`target_rig_path` means **Inherit project target**. An explicit override resolves through the CRIG registry and uses its portable path as fallback. CRIG bytes are not embedded repeatedly in animation rows.

Old projects retain the same project-level target and all old animations inherit it. Legacy `root_mapping_v1` extension values migrate into the explicit root fields without deleting the extension.

In exact mode the project default may be empty only when every enabled animation selects an explicit target. Build resolves each enabled clip independently and groups report output by target rig/skeleton hash. Resource names must remain unique across all groups and script targets.

### Schema 9: automatic built-in routing

Schema 9 adds `retarget_mode: "auto"`. Built-in DL1 clips route to the established humanoid solver, while built-in DL2 clips route to the exact/mapped CRIG solver. Deliberate expert overrides and custom CRIG selections remain explicit.

### Schema 10: explicit timing domains

Schema 10 stops using one `fps` value for three different jobs:

```json
{
  "animations": [{
    "source_fps": 24.0,
    "sample_fps": 24.0,
    "playback_fps": 30.0,
    "fps": 30.0
  }],
  "anm2_to_fbx": {
    "items": [{
      "anm2_input_fps": 30.0,
      "fbx_output_fps": 24.0,
      "fps": 24.0
    }]
  }
}
```

`source_fps` records the FBX-declared timebase, `sample_fps` selects the transforms written to ANM2, and `playback_fps` is authored into the animation-script sequence. In reverse conversion, `anm2_input_fps` describes the existing sample cadence and `fbx_output_fps` selects the resampled output cadence. The old forward `fps` alias mirrors playback; the old reverse alias mirrors FBX output.

Migration preserves actual schema-9 behavior: exact/custom and DL2 automatic clips sample at their old `fps`; DL1 automatic/humanoid clips retain the old forced 30 FPS sampler; playback stays at the old `fps`. Reverse rows copy the old value to both new rates. Unknown source cadence remains `null` until the source FBX is inspected.

## Main sections

### `rig`

```text
target_rig_ref                 legacy/current storage for the project default
target_rig_path
default_target_rig_ref         schema-v8 explicit alias
default_target_rig_path
retarget_mode                  auto | humanoid | exact
use_imported_animation_bind_pose
source_rest_fbx
trusted_source_rest_json
canonical_smd
target_template_anm2
stock_writer_control_anm2
target_rig_name
extensions
```

### `export`

```text
mode                           new | append
output_directory
pack_filename
existing_rpack
collision_policy               error | replace
default_script_target
custom_script_resource
resource_prefix
include_validation_controls
write_intermediate_anm2
extensions
```

### `animations[]`

Each clip has a stable UUID and stores:

```text
source_fbx
source_animation_stack
display_name
resource_name
enabled
script_target
root_policy
ik_preset
mapping_profile_id
target_rig_ref                 empty = inherit project default
target_rig_path                empty = inherit project default
source_root_bone
target_root_bone
source_fps                    declared FBX cadence; nullable for migrated projects
sample_fps                    FBX-to-ANM2 sampling cadence
playback_fps                  animation-script playback cadence
fps                           compatibility alias for playback_fps
start_frame / end_frame
notes / tags / extensions
```

### `mapping_profiles`

Mappings are embedded by UUID so the project remains self-contained. They may also be exported as `.dlrbmap.json`/`.dlrmap.json` files where the relevant workspace supports it.

A generic bone map uses schema v2 and fingerprints:

- source skeleton signature;
- target skeleton hash;
- target full-bind hash;
- target rig reference.

Each row uses explicit `target_rig_descriptor`, `target_rig_bone`, and `source_fbx_bone` fields, plus mapping kind, transfer/component policies, confidence, method, review state, notes, and extensions. Schema-v1 historical row names are migrated without reversing their stored meaning.

### Models workspace extension

The unified Models workspace is stored under the project extension payload. It retains model FBX/resource/mode/orientation choices, humanoid overrides, generated CRIG references/paths, build settings, and unknown model/settings fields across a round trip.

The optional top-level extension value `import_tolerance` stores `recommended` (the forgiving default) or `strict_diagnostics`. Keeping this preference in the extension payload avoids a schema bump and preserves it through older-project migration alongside all unknown fields. Per-animation saved `fbx_preflight` and `import_state` extension records retain selected targets, mappings, animation settings, and grouped repaired/ignored/review/fatal diagnostics without changing build-authoritative fields.

### `anm2_to_fbx`

Reverse-conversion jobs and `.dlrbmap.json` payloads use the same portable-path rules. Each item selects the CRIG that provides the otherwise absent ANM2 hierarchy/descriptors. `anm2_input_fps` and `fbx_output_fps` are independent so reverse export can preserve duration while changing cadence. A valid sibling `.anm2.dlrmeta.json` supplies defaults; missing, malformed, stale-hash, or frame-count-mismatched metadata is ignored with one advisory and falls back to 30/30.

## Portable paths

Known file paths are written relative to the project directory whenever possible and resolved to absolute paths in memory when loaded.

```text
MyProject/
|-- MyProject.dlraproj
|-- inputs/
|   |-- Walk.fbx
|   `-- DoorOpen.fbx
|-- rigs/
|   |-- Character.crig
|   `-- Door.crig
`-- build/
```

Per-animation CRIG fallback paths and generated model-rig paths use the same rule. Moving the whole project tree preserves those references.

## Formal schemas

```text
docs/schemas/dlraproj.schema.v1.json
docs/schemas/dlraproj.schema.v2.json
docs/schemas/dlraproj.schema.v3.json
docs/schemas/dlraproj.schema.v4.json
docs/schemas/dlraproj.schema.v5.json
docs/schemas/dlraproj.schema.v7.json
docs/schemas/dlraproj.schema.v8.json
docs/schemas/dlraproj.schema.v10.json
```

Runtime validation remains authoritative because file existence, registry resolution, source/target fingerprints, duplicate resources, and cross-references cannot be fully expressed in JSON Schema.

## CLI build

```bash
dlanm2-project-build MyProject.dlraproj
```

The CLI uses the same per-animation target resolution and preflight as the GUI and stops before creating output when any enabled clip has a blocking finding.
