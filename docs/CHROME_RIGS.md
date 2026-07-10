# Chrome Rig custom targets

A Chrome Rig (`.crig`) is a shareable animation-target definition for a skeletal model that already exists in a Dying Light mod or DevTools project. It lets DL ReAnimated author ANM2 tracks for small objects, machinery, custom humanoids, and same-rig animals without asking users for an SMD, donor ANM2, descriptor list, or writer-control file.

## Create and use

In **Show advanced settings**, choose **Create .crig from model FBX…**. The current importer accepts a binary FBX containing at least one `LimbNode` armature. For a rigid object, add one root bone and skin the object to it.

The action extracts:

- exact bone names and parent hierarchy;
- local bind translation, quaternion, and scale;
- Dying Light descriptor hashes and deterministic track order;
- source units and the generic PC ANM2 writer profile;
- skeleton/package hashes and validation results.

It saves and installs the `.crig`. Add animation FBXs using the same skeleton, select the rig, and build the RPack. Another user needs only the `.crig` plus their animation FBXs; the target game model must separately exist in their project.

## Exact-rig behavior

Exact mode compares the complete source skeleton with the package. Missing or extra bones and parent mismatches stop the build with a specific error. Differences between the FBX model defaults and the packaged bind transforms are preserved as build/report warnings because animation-only FBXs do not always contain an authoritative BindPose. The exporter evaluates every local FBX transform at the selected FPS, converts translation to meters and rotation to ANM2 Cayley XYZ, packs changing curves, decodes representative samples, and then packages the result through the normal RPack workflow.

FBX animation stacks/actions are discovered by connection rather than by a hardcoded layer name. A file with multiple stacks creates one selectable project clip per stack. Stacks containing multiple blended layers must be baked or flattened to one layer before import.

Humanoid role mapping and humanoid root-motion policies do not run in this mode. The animation's local root transform is preserved exactly. The project's existing root-policy field is retained for compatibility but is reported as informational for exact-rig output.

## Package safety and reproducibility

`.crig` is a deterministic ZIP container with JSON metadata. It permits only known declarative members and optional documentation/preview assets. Executables, scripts, nested/traversal paths, duplicate entries, oversized members, invalid hierarchies, non-finite matrices, negative scale, descriptor collisions, and rigs that cannot fit an ANM2 page are rejected.

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
- exact same-rig animation export for arbitrary skeleton lengths;
- project schema-v3 migration and advanced GUI selection;
- one-bone and three-bone object regression fixtures.

Not yet implemented:

- source-to-target direct mapping for differently named custom rigs;
- semantic quadruped-to-quadruped retargeting;
- FBX-to-game-model/CHR/skin compilation;
- gameplay AI, physics, ragdoll, or navigation setup.
