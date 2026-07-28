# ANM2 to FBX

The **ANM2 → FBX** workspace converts extracted Dying Light animations into editable skeleton-and-animation FBXs. Blender is used in background mode as the FBX writer; it is not required for the normal FBX → ANM2 workflow.

## Requirements

- An extracted `.anm2` file.
- Blender installed, or a selected `blender.exe`.
- The matching Chrome Rig (`.crig`). Bundled rigs include the DL1 male NPC/infected
  rig and the 271-node Dying Light 2 advanced player rig.

ANM2 contains descriptor hashes and sampled local transforms, but no bone names, hierarchy, bind pose, or authoritative cadence. Choosing the correct rig is therefore required. A sibling `<name>.anm2.dlrmeta.json` written by DL ReAnimated can supply hash-validated timing provenance; otherwise reverse conversion defaults to 30 FPS input and 30 FPS output.

## Native export

1. Open **ANM2 → FBX** and add one or more ANM2 files.
2. Select the matching source Chrome Rig.
3. Leave **Native rig** selected.
4. Set **ANM2 FPS**, **FBX FPS**, and the frame range, then choose an output folder.
5. Choose Blender if it was not detected, then export.

Rig bones omitted by a clip remain at bind pose.

## Quick ANM2-to-FBX-to-ANM2 round trip

This workflow uses two parts of DL ReAnimated:

1. **ANM2 to FBX** creates the editable FBX.
2. **Animations** imports the edited FBX and **Export ANM2 only…** writes the
   rebuilt ANM2.

For an editable, metadata-validated DL1 player round trip, use
**Dying Light 1 Player TPP — Helper-capable** for both directions. Do not
export with that rig and then import onto the legacy 69-bone rig.

> **DL2 boundary:** DL2 ANM2-to-FBX export is supported, but native DL2
> FBX-to-ANM2 writing is not. The complete round trip in this section is for
> DL1 only.

### 1. Preserve the source

- Work from a copy of the extracted ANM2.
- Keep the original ANM2 unchanged for comparison.
- Keep the generated FBX and its `.fbx.dlrroundtrip.json` together.
- Use a new filename for the edited FBX and rebuilt ANM2.

Example:

```text
run_source.anm2
run_helpers.fbx
run_helpers.fbx.dlrroundtrip.json
run_helpers_edited.fbx
run_helpers_edited.fbx.dlrroundtrip.json
run_helpers_edited.anm2
```

### 2. Export ANM2 to FBX

1. Open the **ANM2 to FBX** workspace and click **Add ANM2 files…**.
2. Enable **Show advanced settings**.
3. Select **Dying Light 1 Player TPP — Helper-capable**. A clip containing a
   helper-specific descriptor such as `refcamera` should select this rig
   automatically, but confirm it before exporting.
4. Leave **Native rig** selected. Do not use cross-rig retargeting for a native
   round trip.
5. Set **ANM2 FPS** to the cadence of the source samples. Use timing provenance
   when available; otherwise confirm the rate manually.
6. Set **FBX FPS** to the same value unless intentional duration-preserving
   resampling is required.
7. Select the exact frame range needed.
8. Set **Unresolved ANM2 tracks** to
   **Non-deforming helper roots in FBX**. The sidecar-only and drop choices
   cannot provide an editable lossless reimport.
9. Export and confirm that both files exist:

   ```text
   <name>.fbx
   <name>.fbx.dlrroundtrip.json
   ```

The exported armature contains all 87 rig nodes. Named helpers are armature
bones; source-only hash tracks are `DLR_*` Empties.

### 3. Edit in Blender

Import the FBX with:

- **Custom Properties** enabled;
- normal FBX pre/post rotation handling enabled;
- no manual scale or axis conversion after import.

Use the correct editing mode:

| What is being edited | Blender mode | How to animate it |
|---|---|---|
| Ordinary bone or named helper such as `refcamera` | Pose Mode | Key pose-bone location, quaternion rotation, and scale |
| `DLR_Track_XXXXXXXX` | Object Mode | Key the Empty's location, quaternion rotation, and scale |
| `DLR_OffsetHelper_CCC3CDDF` | Object Mode | Key the Empty; baked root unbaking uses recorded source samples |
| Armature rest pose | Do not edit | Edit Mode changes invalidate the contract |

For a constant edit, apply and key it on every sampled action frame. Blender
normally imports source frame 0 as action frame 1, so an ANM2 range of 0-12 is
usually edited on Blender frames 1-13. Use the action's actual range rather than
the scene's default 1-250 range.

For example, this Blender script fragment snapshots every original pose before
moving `refcamera` backward by 10 cm in its local camera basis. Snapshotting
first prevents an earlier inserted key from affecting a later sampled frame:

```python
import math

import bpy
from mathutils import Matrix

scene = bpy.context.scene
armature = next(obj for obj in scene.objects if obj.type == "ARMATURE")
pose_bone = armature.pose.bones["refcamera"]
action_start, action_end = armature.animation_data.action.frame_range
frames = range(math.ceil(action_start), math.floor(action_end) + 1)

source_poses = {}
for frame in frames:
    scene.frame_set(frame)
    bpy.context.view_layer.update()
    source_poses[frame] = pose_bone.matrix.copy()

pose_bone.rotation_mode = "QUATERNION"
for frame, source_pose in source_poses.items():
    scene.frame_set(frame)
    pose_bone.matrix = source_pose @ Matrix.Translation((0.0, 0.0, 0.10))
    pose_bone.keyframe_insert("location", frame=frame)
    pose_bone.keyframe_insert("rotation_quaternion", frame=frame)
    pose_bone.keyframe_insert("scale", frame=frame)
```

The exported camera faces local `-Z`, so local `+Z` moves it backward.

### 4. Export the edited FBX from Blender

Use **Selected Objects** and select:

- the armature;
- every `DLR_Track_*` or `DLR_OffsetHelper_*` Empty required by the source;
- the `DLR_RoundTripGuard_*` mesh.

Use these FBX settings:

| Setting | Value |
|---|---|
| Object Types | Armature, Empty, Mesh |
| Forward / Up | `-Z Forward`, `Y Up` |
| Primary / Secondary Bone Axis | `Y`, `X` |
| Apply Unit Scale | Enabled |
| Apply Transform / Bake Space Transform | Disabled |
| Add Leaf Bones | Disabled |
| Bake Animation | Enabled |
| Key All Bones | Disabled |
| NLA Strips / All Actions | Disabled |
| Sampling Rate / Step | `1` |
| Simplify | `0` |

Export one baked action over the original frame range. Constraints, drivers,
procedural animation, multiple actions, or NLA layers must be baked into that
single action before export.

Keeping **Custom Properties** enabled is recommended. If it is disabled or the
FBX exporter strips them, copy the original contract to the edited FBX's exact
companion name:

```text
run_helpers.fbx.dlrroundtrip.json
    -> run_helpers_edited.fbx.dlrroundtrip.json
```

Do not modify the JSON contents. If embedded metadata and the companion file
are both present, DL ReAnimated requires them to agree.

### 5. Import the edited FBX and rebuild ANM2

1. On the **Project** page, select **Dying Light 1** and set the default target
   rig to **Dying Light 1 Player TPP — Helper-capable**.
2. Open **Animations** and click **Add FBX animations…**.
3. Select the edited FBX. Leave its matching
   `.fbx.dlrroundtrip.json` beside it.
4. Confirm the row resolves to the helper-capable target. Contract-bearing
   native exports use exact reimport; do not remap them through the humanoid or
   cross-rig solver.
5. Keep the original sampling FPS and frame count.
6. Click **Export ANM2 only…** and choose a new output directory.
7. Compare the rebuilt ANM2 with the source before putting it into a mod.

The source descriptor table remains first and in its original order. A
rig-backed helper absent from the source is appended only when its sampled
animation was actually changed beyond the edit tolerances.

For ordinary mapped FBX imports, one unambiguous normalized-name helper is also
enabled automatically: for example, `RefCamera` maps to `refcamera` and
`Eye_Camera` maps to `eyecamera`. Ambiguous duplicates and semantic fallback
suggestions remain manual choices. Contract-bearing native reimports use the
validated exact nodes recorded by the contract.

### 6. Verify the result

At minimum, check:

- descriptor count and order;
- frame count and FPS;
- the intended target track on the first, middle, and final frames;
- ordinary body bones and `bip01`;
- independent roots such as `propsholder1` and `propsholder2`;
- any preserved `DLR_Track_*` or OffsetHelper track.

For a higher-confidence check, export the rebuilt ANM2 to FBX a second time and
sample the edited bone on the first, middle, and final frames.

## Blender best practices

### Safe practices

- Save a `.blend` working file, but keep the original generated FBX and
  companion contract as immutable references.
- Animate bones in Pose Mode and track Empties in Object Mode.
- Use quaternion rotation for round-trip keys.
- Prefer **Linear** interpolation for deliberate per-frame edits, then bake the
  final action at step `1`.
- Key location, rotation, and scale together on every sampled frame touched by
  a constant edit.
- Keep scene FPS, action range, and export range identical to the original
  round-trip contract.
- Bake constraints and procedural controls before FBX export.
- Export only the required armature, DLR Empties, and round-trip guard.
- Prefer custom-property export even though sidecar fallback is supported.
- Reimport a small test edit before spending time on a large animation pass.

### Operations that break exact reimport

- Renaming, deleting, duplicating, or reparenting a DLR armature bone or track
  Empty.
- Editing the armature in Edit Mode.
- Applying or animating the armature object's Object Mode transform.
- Deleting or renaming `DLR_RoundTripGuard_*`.
- Changing action duration, frame count, or sampling cadence.
- Exporting without required DLR track Empties.
- Using a different target rig for FBX -> ANM2.
- Copying a contract from a different source FBX.
- Enabling leaf bones, bake-space transform, animation simplification, or
  arbitrary axis conversion.

Unrelated scene meshes and ordinary objects are ignored by validation, but
using **Selected Objects** keeps the FBX easier to audit.

### Common errors

| Error | Likely cause | Correction |
|---|---|---|
| Round-trip metadata is missing | Custom properties were stripped and no matching sidecar exists | Copy the original sidecar to `<edited>.fbx.dlrroundtrip.json` |
| Embedded metadata and sidecar disagree | Wrong or stale sidecar | Restore the sidecar produced with that exact source FBX |
| Skeleton structure changed | Bone/Empty was renamed, deleted, duplicated, or reparented | Reopen the untouched export and redo the animation edit |
| Rest pose or armature transform changed | Edit Mode or armature Object Mode transform was used | Undo the structural transform; use Pose Mode instead |
| Frame count or cadence changed | Wrong action range, FPS, or FBX bake range | Restore the original range and sampling rate |
| Rig mismatch | Edited FBX is being imported onto another target rig | Select the same helper-capable rig used for export |
| Original descriptor cannot be resolved | A required DLR Empty was not exported | Re-export with the armature, all required DLR Empties, and guard selected |

Do not remove metadata to bypass an error. The rejection identifies a change
that would make exact reconstruction unsafe.

## DL1 player helper-capable round-trip details

The existing 69-bone DL1 target remains the compatibility default. Enable
**Show advanced settings** and choose **Dying Light 1 Player TPP —
Helper-capable** only when camera, holder, eye, normal, head-end, or spine
helper animation must be edited. Auto-detection chooses this rig only when an
ANM2 contains one of its helper-specific descriptors; an ordinary 69-bone clip
continues to select the legacy target.

The helper-capable rig deterministically uses the first 87 nodes from
`player_1_tpp.smd`: 69 deform bones and 18 named non-deforming helper bones.
The final 19 SMD mesh-root slots are not armature nodes. Named helpers such as
`refcamera` are ordinary hierarchical Pose Mode bones with
`use_deform=false` and `dlr_helper=true`. Source-only descriptors which do not
belong to the rig remain independent `DLR_Track_XXXXXXXX` or
`DLR_OffsetHelper_CCC3CDDF` Empties and are edited in Object Mode.

For an editable lossless export, use **Non-deforming helper roots in FBX** for
unresolved tracks. Sidecar-only and drop policies are one-way exports. A
helper-capable native export writes metadata version 5 both inside the FBX and
beside it as `<name>.fbx.dlrroundtrip.json`. The result object and CLI report
the exact companion path.

Recommended Blender round-trip settings:

1. Import the FBX with custom properties enabled and the normal FBX pre/post
   rotation handling enabled.
2. Edit named helpers in Pose Mode. Key location, quaternion rotation, and
   scale on every required sampled frame. Edit hash-named track Empties in
   Object Mode.
3. When exporting selected objects, include the armature, every required DLR
   track Empty, and the `DLR_RoundTripGuard_*` mesh. The guard is a tiny
   non-rendering skinned point mesh which preserves authoritative bind
   matrices when Blender otherwise strips an armature-only BindPose.
4. Export FBX with **Forward -Z**, **Up Y**, **Primary Bone Axis Y**,
   **Secondary Bone Axis X**, no leaf bones, animation baking enabled, all-bones
   keying disabled, NLA strips/actions disabled, step `1`, and simplification
   `0`.
5. Custom-property export may be disabled. If it is, copy the original
   companion contract to the exact new name
   `<edited-name>.fbx.dlrroundtrip.json`.
6. Reimport with the same helper-capable rig and the original frame count and
   FPS.

Exact reimport starts with the source ANM2 descriptor order. It appends only a
rig-backed bone absent from the source whose sampled animation differs from
bind by more than `2e-5 m` translation, `2e-5` scale, or `0.01` degrees of
quaternion angular difference. Appends follow stable rig order;
quaternion-sign-equivalent poses are no-ops.

The round-trip contract rejects a mismatched rig, stale sidecar, changed
cadence/frame count, renamed/deleted/duplicated/reparented DLR nodes, unexpected
bones, Edit Mode rest changes, and armature Object Mode transforms. Unrelated
scene meshes and ordinary objects are ignored. Do not rename or remove the DLR
bind guard.

When OffsetHelper motion was baked, exact reimport removes the recorded
original helper samples from only the recorded primary root. The currently
edited Empty is written as the current OffsetHelper track, and independent
roots such as `propsholder1` and `propsholder2` are not compensated. A static
or explicitly unbaked OffsetHelper is never subtracted.

The Blender handoff is sparse and binary. JSON contains the complete hierarchy and
bind metadata; compressed NPZ arrays contain frame numbers and only TRS components
that differ from bind by more than `1e-7`. Static rows remain in the armature with no
curves, and a rotation-only row receives quaternion curves but no location/scale
curves. Quaternions are hemisphere-continuous. When input and output rates differ,
the complete reconstructed/retargeted scene is resampled before sparse-curve
generation: translation and scale use linear interpolation, rotation uses normalized
shortest-hemisphere SLERP (or NLERP near identity), duration is preserved, and both
endpoints are exact. For example, 381 samples at 30 FPS become 305 samples at 24 FPS.

For DL1, the compatibility default preserves descriptors that do not resolve to rig
bones as non-deforming Empty objects. `0xCCC3CDDF` is named
`DLR_OffsetHelper_CCC3CDDF`; other descriptors use `DLR_Track_XXXXXXXX`. They retain
their descriptor metadata and animation without appearing as metre-long terminal
bones in Blender.

For DL2, the default keeps the output skeleton at 271 advanced-player nodes, animates
the descriptors that resolve to those nodes, leaves unmatched advanced bones at bind
pose, and writes every unresolved transform track to
`<animation>.dlr_unknown_tracks.json`. The sidecar is deterministic and records the
source ANM2 SHA-256, original descriptor-table index, `0xXXXXXXXX` descriptor, nine
component values for every selected frame, and the neutral semantic
`unknown_transform_track`.

## Motion accumulator / OffsetHelper

`0xCCC3CDDF` is recognized as Chrome's OffsetHelper motion-accumulator track rather
than as an anonymous transform. When its translation, rotation, or scale changes over
the selected frame range, ANM2 â†’ FBX exports bake its complete TRS transform into the
primary skeleton root before resampling. This gives both native and cross-rig DL1/DL2
exports their visible accumulated trajectory without requiring an FBX consumer to
understand Chrome's runtime graph.

For an active bake, the raw track is also preserved as the animated non-deforming
Empty `DLR_OffsetHelper_CCC3CDDF`, tagged as `motion_accumulator`; DL2 sidecars retain
that same descriptor with the `motion_accumulator` semantic. The **Bake detected motion
accumulator into root** control is enabled by default. Disable it (or pass
`--no-bake-motion-accumulator`) when inspecting raw helper curves only. This does not
claim to recreate the game's external `AccumulateMotion` / movie configuration.

The **Unresolved ANM2 tracks** setting (or the CLI option below) also offers:

- `helpers`: place unresolved descriptors in the FBX as non-deforming hash-named roots;
- `drop`: explicitly discard them and emit a warning.

Unknown DL2 descriptors are never silently discarded or presented as deform bones.

Decode reports also include a diagnostic-only `root_motion_diagnostics` object for
the selected rig's real primary root and `0xCCC3CDDF` when present. It records
translation start/end/net/min/max/range and accumulated target-up heading. Report
generation reads the decoded arrays without rewriting them.

The 3,343-frame acceptance clip produces the complete 271-bone armature with 52
rotation-only animated bones and 219 bind-only bones: 208 FCurves and 695,344 scalar
keys. The former dense handoff represented 9,059,530 scalar values. On the reviewed
workstation the cached decode completes in about 1.47 seconds (the prior dense
all-frame audit did not finish within 180 seconds); these figures are audit evidence,
not absolute CI timing thresholds.

Blender installs FCurves in bulk with `keyframe_points.add` and
`foreach_set("co")`. It evaluates the completed action once per frame for the root
parity audit. Export uses
`bake_anim_use_all_bones=False` and
`bake_anim_force_startend_keying=False`.

Chrome's internal bone axes do not necessarily point toward the next visible joint. Native export therefore aims each visible edit bone at its nearest usable child pivot, with a parent-direction or native-axis fallback for terminal and coincident joints. Every bone keeps its authored CRIG parent and `use_connect=False`.

The FBX stores a per-bone display-basis correction so its child-facing Blender rest pose converts back to the original CRIG game-space axes when it is imported into DL ReAnimated again. Before writing FBX, Blender audits the evaluated primary root in that displayed basis on every frame and hard-fails above 0.05° total rotation, 0.05° heading, or `1e-5 m` translation error.

## Timing provenance

Forward standalone/intermediate ANM2 writes deterministic `<name>.anm2.dlrmeta.json` containing the ANM2 and source-FBX SHA-256 values, source/sample/playback FPS, source duration, frame count, and externally named root-motion/heading modes. Reverse conversion uses it only when its format, schema, timing values, ANM2 hash, and frame count validate. A stale or malformed sidecar produces one advisory and contributes no timing values.

Valid provenance defaults ANM2 input cadence from `sample_fps` and FBX output cadence from `source_fbx_fps`; `playback_fps` remains the separate animation-script playback intent. Explicit GUI/CLI rates override provenance. Fractional FBX rates are preserved through Blender's `render.fps` plus `render.fps_base` instead of integer truncation.

## Doors, props, and other rigs

If no matching rig is installed, choose **Create .crig from model FBX** and select a binary FBX of the same model/skeleton used by the ANM2. A door normally contains a root, hinge/door bone, and optionally a handle bone. The model mesh is not included in the exported FBX.

## Cross-rig export

Select **Retarget onto another skeleton**, choose the target skeleton FBX, and click **Automatic map**. The mapper prefers descriptors, exact/normalized names, aliases, hierarchy, and unique structural matches. Review the entire mapping table; uncertain bones remain unmapped rather than being guessed aggressively.

Mappings can be saved as `.dlrbmap.json`. They are tied to source and target skeleton hashes. Unmapped target bones stay at bind pose, and one target bone cannot be assigned twice.

Cross-rig transfer applies bind-relative local motion when mapped parents correspond. A global bind-relative fallback handles reparented mappings. Automatic translation scaling affects animated deltas only; it does not replace the target bind offsets or proportions.

## CLI

```text
dlanm2-anm2-to-fbx clip.anm2 --source-rig door.crig --output-directory build/fbx

dlanm2-anm2-to-fbx clip.anm2 \
  --source-rig builtin:dl1_player_tpp_helpers \
  --unknown-track-policy helpers \
  --anm2-fps 30 --fbx-fps 30 \
  --output-directory build/dl1_helper_fbx

python -m dlanm2_gui.tools.anm2_to_fbx \
  reference/dl2/0_m_fpp_farjump.anm2 \
  --source-rig builtin:dl2_player_advanced \
  --unknown-track-policy sidecar \
  --output-directory build/dl2_fbx

dlanm2-anm2-to-fbx clip.anm2 --source-rig source.crig \
  --target-fbx renamed-door.fbx --bone-map door.dlrbmap.json \
  --anm2-fps 30 --fbx-fps 24 \
  --blender "C:\Program Files\Blender Foundation\Blender 4.2\blender.exe"
```

Use `--auto-map --save-auto-map mapping.dlrbmap.json` to generate a conservative CLI mapping for review.

`--anm2-fps` selects the input cadence and `--fbx-fps` selects the output cadence. The legacy `--fps` option remains as an alias that sets both. Omitting all three allows valid provenance to supply defaults.

`--unknown-track-policy` accepts `sidecar`, `helpers`, or `drop`. When omitted, DL2
uses `sidecar` and DL1 retains its existing `helpers` behavior.

`--no-bake-motion-accumulator` disables the default root bake for an animated
`0xCCC3CDDF` helper while preserving the selected unresolved-track policy.

## Progress and cancellation

The operation stays nonmodal and reports elapsed time plus current/total work for:
**Reading ANM2**, **Decoding pages/segments**, **Resampling animation**, **Building sparse curves**,
**Starting Blender**, **Creating armature**, **Installing animation curves**, and
**Auditing root parity**, **Writing FBX**. Cancellation is checked during cached decoding and while Blender is
running; a cancelled job removes its temporary FBX. Expected bind-only rows do not
produce warnings.

## Decoder and sparse-job contracts

```c
Decoded dlr_decode_anm2_all_frames_cached(bytes data, descriptor_set selected) {
    layout = parse_layout_and_base_tables_once(data);
    for (frame = 0; frame < layout.frame_count; ++frame) {
        slot = select_page_segment_and_16_frame_slot(layout, frame);
        packed = cache_get_or_decode_once(slot);
        output[frame] = numpy_assemble_selected_direct_and_interpolated_packed(packed);
    }
    return vectorized_cayley_to_continuous_quaternions(output);
}

SparseJob dlr_build_sparse_fbx_job(Decoded scene, double tolerance) {
    job.json = complete_hierarchy_and_bind(scene.bones);
    job.npz.frames = contiguous_frames_without_decimation(scene);
    job.npz.channels = components_different_from_bind(scene, tolerance);
    return job;
}

void dlr_blender_install_sparse_action(SparseJob job) {
    armature = create_complete_armature(job.json.bind_hierarchy);
    for (channel in job.npz.channels) {
        fcurve.keyframe_points.add(job.frame_count);
        fcurve.keyframe_points.foreach_set("co", interleaved_frame_value_pairs(channel));
        fcurve.interpolation = LINEAR;
    }
    for (frame in job.npz.frames) {
        dependency_update_once();
        require_native_root_parity(frame, 0.05, 0.05, 1e-5);
    }
}
```

## Dying Light 2 support boundary

Native Dying Light 2 Header_Version2 ANM2 decoding and ANM2-to-FBX are supported for
the validated PC block/sampler layout. The outer Header_Version2 block and VFR/time
selection feeds the same validated inner packed sampler used by DL1. The supplied
far-jump sample decodes as 229 frames, 189 tracks, 1,354 static streams, and 347 packed
streams across block spans `[120, 108]`.

Native DL2 ANM2 writing remains unavailable. FBX-to-ANM2 controls must not be read as
a claim that a Header_Version2 writer exists.

## Current boundary

This release exports standalone ANM2 files, not arbitrary retail RPacks. Output contains a skeleton and action, not a model mesh. Automatic mapping proposes correspondences; unrelated anatomical or mechanical rigs may still require manual mapping or animation cleanup.
