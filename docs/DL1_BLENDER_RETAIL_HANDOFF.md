# DL1 retail mesh and ANM2 Blender handoff

> **Status:** implemented as a bounded, optional Blender handoff in the C#
> refactor. Automated contract tests and one exact installed Blender 5.2/DL1
> 1.55 retail control pass. Broader value-level and retail-animation corpus
> comparisons remain release gates. This is not yet a replacement for the
> validated Python single-clip round-trip workflow.

This workflow is for inspecting or editing one or more Dying Light 1
animations on the exact retail model selected in the C# asset browser. It can,
for example, create one FBX containing a decoded volatile model and several
volatile ANM2 clips as named Blender Actions.

## Workflow

1. Configure a local Dying Light 1 installation and select a decoded **skinned**
   mesh in the retail asset browser.
2. Choose **Mesh + ANM2 to FBX…**.
3. Select between 1 and 64 compatible DL1 ANM2 files. The first selected clip
   establishes the FBX output cadence; clips with another proven cadence are
   deterministically resampled and reported.
4. Select `blender.exe` when it is not already discoverable. Its machine-local
   path is stored under LocalAppData, never in a project.
5. Choose the FBX destination and acknowledge the retail-data warning.

The job is cancellable and runs Blender in background/factory-startup mode.
The WPF UI owns selection and progress only; ANM2 parsing remains in the codec
layer, and Blender receives a bounded, versioned handoff.

## Output bundle

The selected destination receives:

- one binary FBX with the decoded retail armature and mesh parts;
- one armature-owned Action/AnimationStack for each selected ANM2;
- content-addressed `DLR_BaseColor_*.dds` files for decoded base-color slots;
- `<output>.fbx.dlrahandoff.json`, which records source hashes, rational timing
  provenance, selected retail identity, Actions, helpers, root policy, and
  limitations; and
- `.dlrtracks` sidecars named with the full SHA-256 of their complete binary
  payload when an ANM2 contains transform descriptors that are not real bones
  or named helpers on the selected rig. The identity therefore changes when
  cadence, descriptors, source fingerprint, or samples change even if the
  ANM2 bytes themselves do not.

The FBX uses relative external texture paths so it can be moved locally with
its bundle. Projects and releases contain only identities and tooling; they
never embed retail mesh, texture, animation, FED, or other proprietary bytes.
The generated bundle itself contains decoded game data and must not be
redistributed.

## Rig, helper, and timing rules

- Compatibility is checked against the exact dynamically decoded retail rig.
  Wrong-family, mimic-domain, and unexplained low-overlap clips fail before
  Blender starts. A clip with unresolved descriptors must match the selected
  Root, at least 75% of its non-motion descriptors, and at least 12 Root or
  Deform tracks. Fully known partial/helper/camera clips do not need to meet
  that character-clip threshold.
- A legitimate known-helper-only clip, such as a camera-helper take, is
  accepted when its descriptors belong to the selected rig.
- Real named helpers remain editable armature bones. Unknown descriptors are
  not invented as bones; their raw local TRS samples are retained at the
  original ANM2 frame count and cadence in hash-validated `.dlrtracks`
  sidecars.
- ANM2 materialization uses two ordered selected-descriptor passes. The Action
  pass contains only exact retail-rig tracks plus `0xCCC3CDDF`; unresolved
  sidecar tracks are decoded separately before the Action matrix. This avoids
  retaining one all-track frame matrix while preserving source descriptor
  order in both outputs.
- An active `0xCCC3CDDF` motion accumulator is baked into the unique parentless
  retail Root bone for the inspection Action while its source track remains
  documented in its original-cadence sidecar. A present but static accumulator
  leaves Root unchanged and is reported as not baked. Ambiguous roots fail
  closed.
- Child-pivot display transforms, quaternion ordering, bind transforms, and
  animation correction use the same explicit conventions as the C# evaluator.
  Blender receives a guard mesh so the armature bind pose survives FBX export.
- Timing metadata is read by the public Codecs-owned schema-1 provenance codec,
  not by WPF. Sidecars are limited to 1 MiB and JSON depth 32, strictly validate
  required and optional scalar/list/matrix fields, and fail nonfatally with one
  diagnostic when missing, malformed, hash-mismatched, or frame-mismatched.
- The shared Blender-independent cadence plan pins the exact first and last
  source samples, normalizes shortest-hemisphere quaternion interpolation, and
  leaves one-frame clips at one frame. A direct control locks 381 samples at
  30 fps to 305 FBX samples at 24 fps.

Each Action preserves its original ANM2 input cadence and frame count in the
handoff manifest even when the shared FBX cadence requires resampling.

## Validation and limits

Console completion markers are not treated as proof. Before any staged output
is committed, the C# codec layer parses the written binary FBX and verifies
the structural contract:

- the exact requested AnimationStack names;
- the expected LimbNode armature and a finite, nonsingular BindPose covering
  it;
- every requested retail mesh plus the explicit bind-pose guard;
- topology counts, skin connectivity/coverage, finite normals and UVs, and
  bounded animation-key ranges; and
- safe relative Texture/Video references for the expected base-color files.

The handoff is limited to 64 clips, 1,000,000 sampled transforms, 2,000,000
vertices, 6,000,000 indices, 64 MiB of decoded textures, and 192 MiB of
aggregate temporary payloads. Strict post-write inspection additionally caps
the FBX at 256 MiB, an individual decoded FBX array at 64 MiB, and aggregate
decoded FBX allocations at 256 MiB.

Output is staged beside the destination, bounded before and after Blender, and
committed under a per-output lock. Before any public path changes, a bounded,
depth-limited journal is flushed beside the destination. Existing FBX and
manifest files are changed with same-volume `File.Replace`, so neither public
name is vacated: each path is always the old file or the new file. The journal
records canonical transaction paths plus old/new hashes and is itself updated
with `File.Replace`.

An interrupted journal in the `prepared` phase conservatively restores the old
FBX and manifest generation. An `installed` journal verifies and retains the
new generation, then finishes backup cleanup. Recovery rejects noncanonical,
escaping, duplicate, over-depth, oversized, or hash-inconsistent state before
touching files. Content-addressed DDS and `.dlrtracks` dependencies are retained
as safe orphans during rollback because another output may have installed the
same bytes concurrently. Cancellation or validation failure therefore does not
vacate an existing public file. If the operating system prevents recovery, the
journal and staging directory are retained and reported instead of being
deleted.

The parser checks structure, counts, connectivity, finiteness, and timing
envelopes. It does not yet prove exact exported vertex/normal values, winding,
or every sampled Action value after Blender's coordinate conversion. Those
value-level comparisons require the still-open real-Blender acceptance corpus;
the C# preview has separate decoded-winding and GPU-culling regressions.

The first handoff intentionally exports only the decoded base color. Exact DL1
shader techniques, normal/specular/mask maps, cloth, physics, post-processing,
and morph targets are not reproduced.

## Installed Blender acceptance control

`InstalledBlenderFbxAcceptanceTests` is an explicit opt-in gate. The ordinary
test suite does not claim this external dependency ran. Invoke it fail-closed
with an exact local executable:

```powershell
.\tools\validate_dl1_blender_handoff.ps1 `
  -BlenderExecutable 'F:\SteamLibrary\steamapps\common\Blender\blender.exe' `
  -Configuration Release
```

The validator probes Blender in `--background --factory-startup` mode and then
sets the opt-in environment contract before running only
`Gate=DL1InstalledBlenderFbx`. No interactive Blender window is opened.

The 2026-07-30 control passed with:

- Blender 5.2.0 LTS, build `fbe6228777e7`;
- the validated DL1 1.55 build fingerprint
  `89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13`;
- `zombie_voleteile_blue` from `common_meshes_PC.rpack` resource 5201,
  SHA-256
  `c6ed07a38942faa6c45865e28952ede1c4afd72def645e81a9e54ca3c48c6fbb`;
- 97 armature bones, six exported retail mesh parts, five decoded base-color
  files, and two generated compatible ANM2 clips exported as exact-named
  `volatile_pose_a` and `volatile_pose_b` Actions; and
- a 2,986,348-byte binary FBX accepted by the strict stack, hierarchy,
  BindPose, topology, skin-coverage, normal/UV, timing, and portable-texture
  validator before atomic commit.

The real run exposed and locked two Blender 5.2 behaviors that fake-runner
tests could not prove: Blender otherwise emits empty Clusters for every
unweighted armature bone on each mesh part, and prefixes all-action stack names
with the armature name. The embedded helper now emits only actually weighted
Clusters per mesh while retaining the full armature/BindPose, and restores the
source Action name for each stack.

The generated ANM2 controls contain no retail animation bytes. The selected
retail mesh, decoded textures, written FBX, and manifest exist only under a
disposable test directory and are deleted after each run. Only hashes and
aggregate evidence above are recorded. This closes one bounded installed
writer/strict-reader control; it does not close exact vertex, normal, winding,
skin-value, or sampled retail-Action comparison across Blender versions and
model families.

## Round-trip boundary

A multi-Action FBX is an inspection and editing handoff. Its provenance and
temporal resampling are now native, bounded C# contracts, but it does not carry the
legacy one-clip `.fbx.dlrroundtrip.json` contract, and the C# refactor does not
yet promise deterministic reimport of several edited Blender Actions into
separate ANM2 files.

Until that release gate closes, use the existing validated Python/Blender
single-clip path as a reverse-conversion regression reference and fallback,
not as authority over contradictory retail/game evidence. Built-in C# preview,
retargeting, bone/morph editing, ANM2 output, mimic output, and RPack output do
not require Blender.
