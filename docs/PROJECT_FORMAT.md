# `.dlraproj` project format

DL ReAnimated projects are UTF-8 JSON files with the extension `.dlraproj`. They are readable, diffable, migration-driven, and safe to reopen in later application versions.

## Compatibility guarantees

Every project contains:

```json
{
  "format": "dl-reanimated-project",
  "schema_version": 3,
  "minimum_reader_version": 1,
  "created_with": "0.3.1",
  "project_id": "stable UUID"
}
```

The loader applies one-step migrations in order. Unknown fields are retained under `extensions.unknown_fields` instead of being silently deleted. Projects are saved atomically through a temporary file and `os.replace`.

## Schema 2 source-bind policy

Schema 2 adds:

```json
{
  "rig": {
    "use_imported_animation_bind_pose": true,
    "source_rest_fbx": ""
  }
}
```

When `use_imported_animation_bind_pose` is `true`, each animation FBX supplies its own unanimated/bind transforms and no separate T-pose is required.

When it is `false`, `source_rest_fbx` must point to a neutral/rest-pose FBX with the same source skeleton.

Migration from schema 1 is deterministic:

- an existing non-empty `source_rest_fbx` keeps explicit-rest mode;
- an empty source-rest path becomes embedded-bind mode.

## Schema 3 target rigs

Schema 3 adds an explicit target reference, optional portable `.crig` path, and retarget engine:

```json
{
  "rig": {
    "target_rig_ref": "builtin:male_npc_infected",
    "target_rig_path": "",
    "retarget_mode": "humanoid"
  }
}
```

Schema-2 projects migrate to the bundled humanoid target with identical build behavior. The historical SMD/template/control fields remain available and are also recorded under `rig.extensions.legacy_target_files` during migration.

## Main sections

### `rig`

```
target_rig_ref
target_rig_path
retarget_mode                 humanoid | exact
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

```
mode                         new | append
output_directory
pack_filename
existing_rpack
collision_policy             error | replace
default_script_target
custom_script_resource
resource_prefix
include_validation_controls
write_intermediate_anm2
extensions
```

### `animations[]`

Each clip has a stable UUID and stores its FBX path, display/resource names, script target, root policy, IK recommendation, mapping profile, frame range, FPS, notes, tags, and extensions.

### `mapping_profiles`

Mappings are embedded by UUID so a project remains self-contained. They can also be exported as `.dlrmap.json` files for reuse.

## Portable paths

Known file paths are written relative to the project directory whenever possible and resolved to absolute paths in memory when loaded.

```
MyProject/
├─ MyProject.dlraproj
├─ inputs/
│  └─ Walk.fbx
└─ build/
```

## Formal schemas

```
docs/schemas/dlraproj.schema.v1.json
docs/schemas/dlraproj.schema.v2.json
docs/schemas/dlraproj.schema.v3.json
```

Runtime validation remains authoritative because file existence, duplicate resources, and cross-references cannot be fully described by JSON Schema.

## CLI build

```bash
dlanm2-project-build MyProject.dlraproj
```
