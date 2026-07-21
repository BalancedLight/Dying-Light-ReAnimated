# GUI quick start

## Launch

On Windows, double-click `run_gui.bat`. The first run creates `.venv`, installs dependencies, verifies the environment, and opens the application.

```text
run_gui.bat --setup     repair/update the environment only
build_exe.bat           build the portable Windows EXE folder and ZIP
```

The **File > Open Recent** menu keeps the ten most recently opened or saved projects available across launches. Missing project files are removed when selected, and **Clear Recent Projects** resets the list.

## Project default and simple animation workflow

The Project page selects the game profile and **project default target rig**. Both
bundled DL1 and DL2 targets use **Auto**. A normal animation workflow is:

1. choose the default target CRIG;
2. add one or more animation FBXs;
3. leave **Use imported animation FBX bind pose (recommended)** enabled;
4. confirm each row is Ready or follow its single focused action;
5. open Retarget details only when you want the analysis evidence;
6. select root motion per clip;
7. build the RPack.

A separate T-pose is required only when an animation FBX has missing/unreliable bind transforms. Disable embedded-bind mode and select a matching neutral/rest FBX in that case.

Advanced mode exposes imported CRIGs, trusted rest matrices, custom target files,
full mapping controls, per-bone evidence, ignored/helper inventory, collision policy,
intermediate output, and diagnostic controls. For exact/custom CRIG targets,
**Root & .crig Mapping** also exposes **Import retarget recipe…** and **Export reviewed
recipe…**. Advanced mode is a local UI preference; it does not change stored animation
data by itself. A row's **Fix mapping…** action can temporarily open the relevant
advanced editor without making ordinary supported imports require it.

The bundled DL2 Advanced target stays in the normal **Retargeting** view and never
opens the full CRIG table as its ordinary action. Auto reports whether the clip
resolved through ExactRig or the verified automatic body bridge. Selecting a custom
CRIG switches to the expert Exact/CRIG workflow; custom/manual authorization is
unchanged.

The same is true for bundled DL1 and DL2 Shadow Caster targets: target ownership
selects the editor, while solver mode only selects execution. Setting a bundled
target to Exact does not expose **Root & .crig Mapping**. That page appears for a
user-created/imported custom rig, or when an expert override explicitly records both
deliberate intent and permission to expose the CRIG mapping surface.

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
- **Compatibility / mapping**: one concise readiness state and reason;
- **Fix mapping…** when action is required and **Details…** for the full evidence;
- **Source root** and **Target root**;
- **Root motion**;
- **Solver** and build-safety status;
- **Animation SCR**: project default or clip override;
- **IK** recommendation.

The row reports readiness before Export is opened:

```text
Ready — exact skeleton match
Ready — automatically retargeted
Ready — partial skeleton; 8 target bones inherit parent motion
Ready — upper-body clip; lower body held at bind
Needs attention — left arm chain is ambiguous
Cannot import — FBX has no usable skeleton or animation
```

Normal accommodations do not create warning badges or popups. Namespace stripping,
multilingual recognition, extra ignored source bones, missing optional/terminal
bones, chain-length adaptation, and facial/secondary targets held at bind appear in
the expandable **Retarget details** panel and build report. Information is grouped by
category rather than repeated per bone. A yellow **Needs attention** row has one
**Fix mapping…** action. A red blocker shows one focused modal when the user explicitly
builds/exports; import interrupts only for an unreadable FBX, a requested stack that
does not exist, or no usable skeleton. One corrupt file does not prevent valid peers
in a batch from being added.

The Project tab's parser option is **Recommended / forgiving (FBX parsing)**. Its
tooltip is:

```text
Controls recoverable FBX parsing and geometry diagnostics. It does not approve cross-rig bone mappings or bypass skeleton safety checks.
```

**Strict diagnostics** is useful for parser/model audits, but neither option changes
mapping authorization. Normal GUI errors omit Python tracebacks; advanced developer
diagnostics must be enabled explicitly to include them in logs.

An empty per-animation target inherits the Project target. An explicit target affects only that row. Animations for different CRIGs may coexist in one tool-owned RPack when resource names are unique; the build report groups output by target rig.

Use the Target rig column and the target-rig filter to audit clips by model; optional grouping keeps clips for the same resolved target together. The build report groups completed output by target. Changing the Project default updates only rows that inherit it.

## Compatibility and solver routing

### Exact

The required source names and target ancestry match. The exact solver uses the model CRIG's authored bind; no manual bind correction is needed.

### Source superset

All required target tracks match, while the source may contain additional face, cloth, accessory, camera, weapon, twist, socket, or helper bones. Extra source nodes do not break required target tracks. Map only extras that must drive a target helper.

### Needs review

An animated critical chain is ambiguous, a confidence margin is too small, or a
custom map is unreviewed. The clip remains importable without a popup. Click its one
**Fix mapping…** action; the primary message names the failed invariant while the
full per-bone list stays in Retarget details.

### Automatically retargeted

A source recognized through Unicode/multilingual names, topology, bind geometry,
symmetry, skinning, and animation activity is aligned to the target's semantic
chains. Different chain lengths use direct, composed, distributed, bind-inherited,
or static-bind rows. Optional missing bones are not failures.

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

The saved map records automatic/unreviewed, verified automatic, manually/imported
reviewed, or intentionally unmapped state. **Approve mapped solver** remains a
deliberate action for unknown/custom maps; it is not offered as a shortcut for a
failed built-in certificate. Clearing a target and selecting Bind records the
intention to leave it at authored bind.

Automatic planning works on ordered semantic chains, not a forced one-to-one global
fill. The details table exposes direct, composed, distributed, `inherit_bind`,
`static_bind`, and `manual_required` decisions, candidate/runner-up scores, margins,
and evidence from hierarchy, bind geometry, symmetry, skin regions, motion, names,
and source-family hints. Pure nearest-position evidence cannot invent an anatomical
mapping. Ambiguous animated chains remain unreviewed; missing static or optional
chains do not.

For a differently named character, use rotation-delta transfer with rotation ownership on ordinary deform chains. This preserves target translation/scale and therefore bone lengths. Translation-owning policies are appropriate only for reviewed roots, props, sockets, mechanical parts, or deliberate exceptions.

Intentionally unmapped targets stay at bind. One source bone may drive several distinct helper/socket/camera targets; helper fan-out is applied after the primary body solve.

For either bundled DL2 player target, **Retargeting** shows 52 editable anatomical
roles rather than the 271- or 81-row CRIG inventory. The source dropdown offers the
automatic choice and every live source bone, plus explicit **Inherit parent / target
bind** and **Hold at bind** dispositions. The Required, Confidence, Method, and
Result columns explain the current plan. **Fix mapping** focuses the first genuinely
ambiguous animated role instead of opening a raw CRIG table.

For **Dying Light 2 Player — Advanced**, a recognized body source normally shows:

```text
Verified DL2 body map
52 body rows mapped
219 target rows held at bind
0 spatial-only mappings
certificate: pass
```

The semantic profile—not that compiled table—is saved as the animation's editable
mapping. **Auto-map humanoid** resets explicit role choices and rebuilds from live
evidence. Loading, clearing, or manually changing a role clears any compiled map ID
and certificate. Build always recompiles and revalidates; it never trusts cached
rows. Old target-sized maps are retained for audit while reviewed choices migrate to
the semantic profile. The four
index/middle/ring/pinky base rows per side (`finger10/20/30/40`) remain bind-held,
while segments 1/2/3 map deterministically; thumb `01/02/03` maps directly.

**Apply to compatible clips** copies semantic choices only to clips with the same
source name/parent signature and the same target policy, rig ID, skeleton, and full
bind fingerprint. Each recipient compiles its own fresh backend map.

## Root motion

Root motion is independent of ordinary bone rows. Select the source and target roots
from their inventories and choose in-place, raw root, or motion accumulator behavior
per clip. In-place removes planar translation and accumulated target-space heading
while preserving pelvis tilt/pose. The normal Retargeting tab's **Root & locomotion**
panel selects the actual source root, actual target root, root-motion owner, heading
owner, source/target feet, and IK recommendation. Raw skeletal-root motion preserves the complete
orientation unless **Lock initial heading** is selected. Motion transfers planar displacement and heading to `0xCCC3CDDF` while
keeping vertical/pose motion on the skeletal root.

A missing `bip01` is not filled silently. For multiple independent/helper roots, keep them independent unless the target CRIG explicitly parents them.

Use motion accumulator for locomotion that should move the object in the consuming graph/movie rather than moving only the mesh.

**Show helper bones** exposes the bundled target's camera, holder, head-end, sole,
and (legacy only) IK/shadow-caster rows. **Show all target bones** exposes the
complete target hierarchy once; Advanced DL2 shows exactly 271 unique target rows.
Every additional row supports Auto, Direct, Inherit bind, or Static bind plus
transfer/component policy. Unmapped rows remain at bind.

## Multiple animation stacks

FBX stacks/actions are discovered by connection without loading model geometry. When exactly one stack contains changing skeletal channels, it is selected automatically; common static peer stacks remain available in the row dropdown. Equally useful peers create one editable row that requires manual selection rather than manufacturing one resource per stack. A multi-layer blended stack must be baked/flattened before build. A constant skeletal stack remains importable as an intentional T-pose/rest-pose clip.

## Export

**Export ANM2 only...** retargets every enabled clip to its selected per-row target and writes only generated `.anm2` files.

For DL2 targets this remains the explicitly labeled format-1 compatibility
experiment. Native Header_Version2 writing is not implemented; native DL2
Header_Version2 support is currently read/decode and ANM2-to-FBX only.

### Create new

Writes a new tool-owned animation RPack.

### Append/replace

Loads a previous DL ReAnimated pack, preserves its resources, and writes an updated pack. Sidecar/hash validation must pass.

Output preflight resolves every enabled clip's target, mapping, roots, solver, script target, and unique resource name before the output directory or pack is changed.

## ANM2 to FBX

The reverse workspace batches ANM2 files into skeleton-and-animation FBXs through Blender. Select the matching CRIG because ANM2 does not contain bone names, hierarchy, or bind transforms. Native mode needs no map; cross-rig mode exposes conservative automatic suggestions and review.

Progress remains nonmodal through reading, cached page/segment decoding, sparse-curve
construction, Blender startup, armature creation, bulk curve installation, and FBX
write. Each stage shows elapsed/current/total work and can be cancelled during
decode or Blender execution. Bind-only skeleton rows are expected and do not create
warning badges.

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
