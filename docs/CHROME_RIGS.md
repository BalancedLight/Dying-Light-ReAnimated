# Chrome Rig custom targets

A Chrome Rig (`.crig`) is a shareable animation-target definition for a skeletal model that already exists in a Dying Light mod or DevTools project. It lets DL ReAnimated author ANM2 tracks for small objects, machinery, custom humanoids, and same-rig animals without asking users for an SMD, donor ANM2, descriptor list, or writer-control file.

The same package resolves descriptors for ANM2 → FBX, making doors, props, machinery, animals, and custom characters reversible when their matching `.crig` is available.

## Create and use

The Models workspace creates and installs a `.crig` whenever **Create/install .crig for every skinned model** is enabled. The current importer accepts a binary FBX containing at least one `LimbNode` armature. For a rigid object, add one root bone and skin the object to it.

The action extracts:

- exact bone names and parent hierarchy;
- local bind translation, quaternion, and scale;
- Dying Light descriptor hashes and deterministic track order;
- source units and the generic PC ANM2 writer profile;
- skeleton/package hashes and validation results.

The package is generated from the exact hierarchy authored into source MSH, not from the raw FBX bind. This matters because the model importer converts FBX bone roll to Chrome's local `+X` convention and Dying Light Humanoid mode fits stock-named joints to imported proportions. Cluster-weighted nodes are marked deform bones; unweighted ancestors, end markers, and transform controls remain animated helpers. Add animation FBXs, select the rig, review its root/mapping, and build the RPack. Another user needs only the `.crig` plus their animation FBXs; the target game model must separately exist in their project.

## Exact-rig behavior

Exact mode first compares the source skeleton with the package. A same-rig or compatible subset uses strict transfer. A different readable skeleton is still added to the project with an editable auto-map; open **Root & .crig Mapping**, review body rows, and leave intentional helpers at bind pose. Once saved, that map selects the mapped-rig build engine instead of being ignored by strict exact transfer. Differences between FBX model defaults and packaged bind transforms remain build/report warnings because animation-only FBXs do not always contain an authoritative BindPose.

FBX animation stacks/actions are discovered by connection rather than by a hardcoded layer name. A file with multiple stacks creates one selectable project clip per stack. Stacks containing multiple blended layers must be baked or flattened to one layer before import.

Strict same-rig transfer makes no humanoid assumptions. Cross-rig auto-map recognizes common Mixamo, Character Creator/CC Base, Blender, and Dying Light anatomy names, then exposes every suggestion for manual editing. Cross-rig playback preserves target bind translations and scales and applies source rest-relative local rotation; source bone lengths are never copied into the target. Root source and target choices remain editable per clip, independently of the mapped pelvis pose.

## Package safety and reproducibility

`.crig` is a deterministic ZIP container with JSON metadata. It permits only known declarative members and optional documentation/preview assets. Executables, scripts, nested/traversal paths, duplicate entries, oversized members, invalid hierarchies, non-finite matrices, negative scale, descriptor collisions, and rigs that cannot fit an ANM2 page are rejected. The custom rig ID fingerprints the complete skeleton payload, including bind transforms, so two same-named/topologically identical models with different binds cannot silently overwrite one another.

Core members are:

```text
manifest.json
skeleton.json
writer_profile.json
validation.json
```

Optional members are `aliases.json`, `semantic_profile.json`, `preview.png`, `README.md`, and `LICENSE.txt`.

## Current boundary

Implemented now:

- bundled humanoid represented by the Chrome Rig data model;
- bundled `reference/male_npc_infected.crig` loaded by the normal project path;
- model-FBX to `.crig` creation and installed-rig registry;
- FBX-to-source-MSH authoring, Techland mesh/skin compilation, and loose DevTools installation;
- exact same-rig and reviewed mapped-rig animation export for arbitrary skeleton lengths;
- project schema-v3 migration and advanced GUI selection;
- one-bone and three-bone object regression fixtures.

Not yet implemented:

- semantic quadruped-to-quadruped retargeting;
- gameplay AI, physics, ragdoll, or navigation setup.
