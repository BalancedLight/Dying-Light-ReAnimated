# Chrome Rig custom targets

A Chrome Rig (`.crig`) is a shareable animation-target definition for a skeletal model that exists in a Dying Light mod or Developer Tools project. It identifies exact target names, parent hierarchy, local bind transforms, descriptors, deform/helper roles, aliases, writer profile, and complete-bind fingerprint.

A CRIG does not compile a mesh, skin, CHR, physics, ragdoll, AI, or navigation resource. The target model must be installed separately.

## Bundled targets versus custom CRIGs

The bundled DL1 player and both bundled DL2 player targets are product-owned
humanoid packages. Their CRIG files provide descriptors, hierarchy, and bind data to
the backend, but normal users edit anatomical source roles on **Retargeting**. The
271-row DL2 Advanced and 81-row Shadow Caster `GenericBoneMap` artifacts are compiled
from those roles and are not user-facing CRIG maps.

**Root & .crig Mapping** is the user-facing editor for a target CRIG created or
imported by the user. Selecting ExactRig does not by itself change a bundled target
into a custom target. A bundled CRIG table can be exposed only through a deliberately
recorded expert override; this keeps ownership separate from solver selection.

## Generate from the authored model

For a new model, use the Models workspace instead of creating a rig independently from its raw FBX:

1. add and analyze the model FBX;
2. choose **Exact original FBX rig** or **DL1 humanoid names - preserve proportions, retarget animations**;
3. enable **Create/install .crig for every skinned model**;
4. build the source MSH or compile/install the model;
5. select the model and click **Use generated rig in Animations**.

The model builder freezes the exact emitted source-MSH hierarchy into an `AuthoredRigContract` immediately before serialization. CRIG generation consumes that contract, including its Chrome `+X` frames, fitted pivots, helpers, descriptors, and inverse-global bind ownership. It does not re-evaluate an approximate raw-FBX hierarchy.

The handoff validates the authored contract and bind fingerprint, installs or refreshes the CRIG registry entry, selects it in Animations, and retains its rig reference/path with the model resource. Rebuild and regenerate the CRIG whenever a model bind-affecting input changes.

For a rigid animated object, author at least one root bone and skin the object to it. A completely static prop does not need a CRIG.

## Exact, source-superset, and cross-rig clips

Every animation is classified against its selected target CRIG.

### Exact

The required target names and ancestry match with no meaningful source-only difference. The exact solver uses authoritative global bind-basis correction:

```text
basis_correction[bone] = inverse(source_bind_global[bone]) * target_bind_global[bone]
target_global[bone, frame] = source_animated_global[bone, frame] * basis_correction[bone]
target_local = inverse(target_parent_global) * target_global
```

At source bind, this produces the target authored bind. Differences between animation-file Model defaults and authoritative bind matrices are reported rather than repaired manually.

### Target-compatible source superset

All required target deform bones remain present under the expected target ancestry, while the animation source may add face, cloth, accessory, camera, weapon, socket, twist, or helper bones. These source extras do not break required tracks. Unmapped extras are ignored; optional target helpers absent from the source remain at bind.

Exact and source-superset clips use the same global bind-basis path. A helper override map can add reviewed value-level fan-out without changing the relationship of the required body rows.

### Reviewed cross-rig

Names or ancestry differ. The file remains importable so its map can be repaired, but export is blocked until required automatic suggestions are explicitly reviewed. The mapped solver starts every target at bind and applies each row's own transfer and component policy.

The normal cross-rig deform policy transfers rest-relative rotation while preserving target local translation and target bind scale. Source bone lengths are not copied into the target. Root displacement is selected separately per clip. One source may drive multiple target helpers/sockets after the primary body solve.

## Mapping schema v2

Generic maps serialize the direction explicitly:

```text
target_rig_descriptor <- source_fbx_bone
target_rig_bone       <- source_fbx_bone
```

Rows carry confidence, method/evidence, mapping kind, transfer policy, component policy, review state, notes, and extensions. Review states are `automatic_unreviewed`, `automatic_accepted`, `manually_reviewed`, `imported_reviewed`, and `intentionally_unmapped`.

Schema-v1 maps are migrated deterministically. Its historical fields were reversed in name (`source_bone` meant the target CRIG bone and `target_bone` meant the source FBX bone); migration preserves that meaning and retains every row and unknown field.

Each target descriptor/bone may appear only once. A source FBX bone may drive multiple distinct target helper rows, preserving camera/socket/accessory fan-out. An intentionally unmapped target has no source and uses bind transfer.

Maps are fingerprinted by source skeleton signature plus target skeleton and full-bind hashes. A map created for a same-named but differently proportioned CRIG is stale and is blocked until regenerated or deliberately reviewed for the current target.

## Per-row transfer and components

Ordinary mapped bones and helper overrides use the same policy vocabulary.

Transfer choices:

- `global_bind_basis`: authoritative exact/name-equivalent or source-superset transfer;
- `rotation_delta`: preserve target translation and bind scale while transferring source rest-relative rotation;
- `rest_relative`: reviewed transfer for helpers, props, sockets, and mechanical parts;
- `copy_local`: only for a proven identical local basis;
- `bind`: deliberately keep the target at bind;
- `default`: resolve to the relationship's safe solver policy.

Component ownership choices are `rotation`, `translation`, `rotation_translation`, `scale`, and `full_transform`. Only selected components replace the target bind component. Non-root target translation cannot change bone length unless the row owns translation and the user reviewed that choice.

Helper fan-out is applied after the primary body solve. Root motion remains independent of ordinary mapping rows.

## Multiple CRIGs in one project

Project schema v8 stores a project default target and an optional target override per animation. Select **Inherit project target** when a clip should use the default. Each clip independently resolves its CRIG, map, roots, solver, script target, and resource name.

Animations for different target CRIGs may coexist in one tool-owned RPack when their resource names are unique. The build report groups them by rig reference and target skeleton hash. The CRIG payload is resolved through the installed registry with a portable project path fallback; it is not duplicated into every animation row.

## Package safety and reproducibility

`.crig` is a deterministic ZIP container with declarative JSON metadata. Core members are:

```text
manifest.json
skeleton.json
writer_profile.json
validation.json
```

Optional members are `aliases.json`, `semantic_profile.json`, `preview.png`, `README.md`, and `LICENSE.txt`.

Packages reject executables/scripts, traversal or nested unsafe paths, duplicate entries, oversized members, invalid hierarchies, non-finite matrices, unsupported reflected/sheared transforms, descriptor collisions, negative scale, and rigs that cannot fit supported ANM2 limits. The rig ID fingerprints the complete skeleton payload including bind transforms, so topology/name identity alone cannot overwrite a different bind.

The generated package records the authored rig contract ID, authored MSH resource, source FBX hash, bind/skeleton/descriptor hashes, and bind-validation result.

## Animation-stack behavior

FBX actions/stacks are discovered through connections rather than a hardcoded layer name. A file with multiple stacks creates selectable project clips. Select the intended stack for each clip. A stack with multiple blended layers must be baked or flattened to one layer before import.

## Current boundary

Implemented:

- bundled humanoid and installed custom CRIG targets;
- model-authored contract to source MSH and generated CRIG;
- exact same-rig and target-compatible source-superset transfer;
- reviewed cross-rig mapping with ordinary per-row transfer/component policies;
- helper/socket/camera fan-out;
- multiple target CRIGs per project and per-animation target selection;
- exact and mapped export for arbitrary supported skeleton lengths;
- CRIG-backed ANM2 to FBX descriptor resolution.

Not claimed:

- semantic quadruped-to-unrelated-quadruped automation without review;
- gameplay AI, physics, ragdoll, or navigation setup;
- native Dying Light 2 format-42 writing;
- successful in-editor/game behavior without manual validation.

See [Custom model rig contract](CUSTOM_MODEL_RIG_CONTRACT.md) for the full bind/palette contract and [FBX preflight checks](FBX_PREFLIGHT.md) for recovery diagnostics.
