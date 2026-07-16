# GUI quick start

## Launch

On Windows, double-click `run_gui.bat`. The first run creates `.venv`, installs dependencies, verifies the environment, and opens the application.

```text
run_gui.bat --setup     repair/update the environment only
build_exe.bat           build the portable Windows EXE folder and ZIP
```

The **File > Open Recent** menu keeps the ten most recently opened or saved projects available across launches. Missing project files are removed when selected, and **Clear Recent Projects** resets the list.

## Project default and simple animation workflow

The Project page selects the game profile and **project default target rig**. A normal animation workflow is:

1. choose the default target CRIG;
2. add one or more animation FBXs;
3. leave **Use imported animation FBX bind pose (recommended)** enabled;
4. review each row's Target rig and Compatibility;
5. review only rows whose mapping needs attention;
6. select root motion per clip;
7. build the RPack.

A separate T-pose is required only when an animation FBX has missing/unreliable bind transforms. Disable embedded-bind mode and select a matching neutral/rest FBX in that case.

Advanced mode exposes imported CRIGs, trusted rest matrices, custom target files, mapping profile controls, ignored/helper inventory, collision policy, intermediate output, and diagnostic controls. Advanced mode is a local UI preference; it does not change stored animation data by itself.

## Models workspace

### Analyze

1. Click **Add model FBX...**.
2. Click **Analyze models**.
3. Review detected units, resolved orientation, mesh/material/triangle counts, emitted vertices, total hierarchy nodes, deform/helper/root inventory, and warnings.
4. Choose **Auto**, **Static prop**, **Exact original FBX rig**, or **DL1 humanoid names - preserve proportions, retarget animations**.

Auto uses FBX axis metadata and the canonical evaluated scene. Do not add a manual 90-degree policy simply because the DCC displays Y-up; use a manual orientation only when the analysis report proves the exporter metadata is wrong.

### Model mapping

The Bone Mapping page applies to fitted DL1-name imports. Automatic suggestions are a starting point. Review the source bone, skin ownership, semantic role, candidate/final target, confidence/evidence, transfer/component policy, review state, and status where shown.

Use **Review ambiguous only** to isolate rows that still need a decision. **Reset safe suggestions** restores only high-confidence automatic rows; it does not overwrite ambiguous or intentionally unmapped choices.

Use Exact Rig when a required fitted role has no safe target. The UI reports available roots instead of silently falling back to `bip01`, pelvis, root, identity, or the first bone.

### Build and install

Configure material handling, physical surface, output folder, Developer Tools paths, and optional animation script. Leave **Create/install .crig for every skinned model** enabled.

- **Build source MSH** creates and validates offline source output.
- **Build, compile & install** also runs the configured compiler and installs validated results.
- **Open model build report** and **Open generated CRIG location** expose the full retained diagnostics/artifact location after a build.
- **Show animations targeting this CRIG** jumps to Animations with the generated target filter applied.

For a fitted DL1-named model, leave Animation script empty until animation clips have been retargeted to its generated CRIG. The model-specific bind is not compatible with raw stock absolute tracks even when the bone names match.

The selected model details and JSON report include total hierarchy nodes separately from skin partition count and maximum local palette size. More than 256 hierarchy nodes are valid; every individual subset palette must remain at or below 256.

### Use the generated rig

After a passing skinned model build:

1. select the model row;
2. click **Use generated rig in Animations**;
3. DL ReAnimated verifies the current model/CRIG authored bind, installs or refreshes the CRIG, selects it in Animations, and retains the model resource/rig reference;
4. add animations for that model.

This is the normal handoff. Do not copy a generated path manually or create a second CRIG directly from the raw model FBX.

## Animations table

Each row is one output animation. Core columns/controls include:

- **Use**: include/exclude without deleting the clip;
- **Display name** and **Resource name**;
- **FBX source** and selected animation stack;
- **Target rig**: **Inherit project target** or an explicit CRIG override;
- **Compatibility**: Exact, Source superset, Mapped reviewed, Needs review, or Incompatible;
- **Mapping status** and **Edit mapping**;
- **Source root** and **Target root**;
- **Root motion**;
- **Solver** and build-safety status;
- **Animation SCR**: project default or clip override;
- **IK** recommendation.

An empty per-animation target inherits the Project target. An explicit target affects only that row. Animations for different CRIGs may coexist in one tool-owned RPack when resource names are unique; the build report groups output by target rig.

Use the Target rig column and the target-rig filter to audit clips by model; optional grouping keeps clips for the same resolved target together. The build report groups completed output by target. Changing the Project default updates only rows that inherit it.

## Compatibility and solver routing

### Exact

The required source names and target ancestry match. The exact solver uses the model CRIG's authored bind; no manual bind correction is needed.

### Source superset

All required target tracks match, while the source may contain additional face, cloth, accessory, camera, weapon, twist, socket, or helper bones. Extra source nodes do not break required target tracks. Map only extras that must drive a target helper.

### Needs review

Names or ancestry differ. The clip remains importable, but build is blocked until required rows are reviewed. Click **Edit mapping** or the mapping-status control.

### Mapped reviewed

A cross-rig map has been explicitly reviewed and can select the mapped solver. Target proportions remain owned by the CRIG unless a reviewed row intentionally owns translation.

### Incompatible

The FBX/target cannot be evaluated safely or has a hard profile/bind error. Follow the named finding before output.

## Mapping editor

Bone-map schema v2 labels directions explicitly:

```text
Target CRIG descriptor / Target CRIG bone <- Source FBX bone
```

Every ordinary row and helper override can select:

- transfer: Default, Global bind basis, Rotation delta, Rest relative, Copy local, or Bind;
- components: Rotation, Translation, Rotation + translation, Scale, or Full transform.

The saved map also records automatic/unreviewed, automatic accepted, manually/imported reviewed, or intentionally unmapped state. **Approve mapped solver** records deliberate review of a repair map; clearing a target and selecting Bind records the intention to leave it at authored bind.

Automatic suggestions use one deterministic one-to-one assignment across the whole skeleton. The row status exposes the selected candidate, runner-up, score margin, and available evidence. When the FBX provides bind globals, that evidence includes normalized pivots, parent-child directions, relative extents, and chain depth in addition to names, roles, side, and deform/helper class. Ambiguous or low-margin rows remain unreviewed and cannot build until you approve or replace them.

For a differently named character, use rotation-delta transfer with rotation ownership on ordinary deform chains. This preserves target translation/scale and therefore bone lengths. Translation-owning policies are appropriate only for reviewed roots, props, sockets, mechanical parts, or deliberate exceptions.

Intentionally unmapped targets stay at bind. One source bone may drive several distinct helper/socket/camera targets; helper fan-out is applied after the primary body solve.

**Apply to compatible clips** copies a map only to clips with the matching source signature and target skeleton/full-bind fingerprint.

## Root motion

Root motion is independent of ordinary bone rows. Select the source and target roots from their inventories and choose in-place, raw root, or motion accumulator behavior per clip.

A missing `bip01` is not filled silently. For multiple independent/helper roots, keep them independent unless the target CRIG explicitly parents them.

Use motion accumulator for locomotion that should move the object in the consuming graph/movie rather than moving only the mesh.

## Multiple animation stacks

FBX stacks/actions are discovered by connection. Files with multiple stacks create selectable rows. Choose the intended stack. A multi-layer blended stack must be baked/flattened before build; a stack with no changing skeletal channels may be disabled or shown as a bind-pose warning.

## Export

**Export ANM2 only...** retargets every enabled clip to its selected per-row target and writes only generated `.anm2` files.

### Create new

Writes a new tool-owned animation RPack.

### Append/replace

Loads a previous DL ReAnimated pack, preserves its resources, and writes an updated pack. Sidecar/hash validation must pass.

Output preflight resolves every enabled clip's target, mapping, roots, solver, script target, and unique resource name before the output directory or pack is changed.

## ANM2 to FBX

The reverse workspace batches ANM2 files into skeleton-and-animation FBXs through Blender. Select the matching CRIG because ANM2 does not contain bone names, hierarchy, or bind transforms. Native mode needs no map; cross-rig mode exposes conservative automatic suggestions and review.

## Installation

Install the result under the working DLC/workshop data route as:

```text
common_anims_sp_pc.rpack
```

Do not replace `common_anims_PC.rpack`. Reload the editor project to clear cached resources.

## Facial workspace

The Facial page scans FBX BlendShapeChannel animation and maps source shapes to mimic descriptors. Body-only, Mimic-only, and combined exports remain selectable. Source-superset skeletal extras are separate from blend-shape/mimic routing.

## Manual validation

DL ReAnimated's checks are offline and do not launch Developer Tools or the game. After a passing build, manually:

1. compile one exact-rig large-skeleton model in DL1 DevTools;
2. inspect bind pose and the bone overlay;
3. play one same-rig animation;
4. retarget/play one differently named animation through a reviewed map;
5. record animated deformation and any compiler/editor diagnostics.

See [Custom model rig contract](CUSTOM_MODEL_RIG_CONTRACT.md) for bind ownership and [Troubleshooting](TROUBLESHOOTING.md) for recovery.
