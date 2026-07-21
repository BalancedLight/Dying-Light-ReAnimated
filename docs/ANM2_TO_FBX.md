# ANM2 to FBX

The **ANM2 → FBX** workspace converts extracted Dying Light animations into editable skeleton-and-animation FBXs. Blender is used in background mode as the FBX writer; it is not required for the normal FBX → ANM2 workflow.

## Requirements

- An extracted `.anm2` file.
- Blender installed, or a selected `blender.exe`.
- The matching Chrome Rig (`.crig`). Bundled rigs include the DL1 male NPC/infected
  rig and the 271-node Dying Light 2 advanced player rig.

ANM2 contains descriptor hashes and sampled local transforms, but no bone names, hierarchy, or bind pose. Choosing the correct rig is therefore required. Standalone ANM2 also has no authoritative playback FPS; the default is 30 FPS.

## Native export

1. Open **ANM2 → FBX** and add one or more ANM2 files.
2. Select the matching source Chrome Rig.
3. Leave **Native rig** selected.
4. Set FPS/frame range and choose an output folder.
5. Choose Blender if it was not detected, then export.

Rig bones omitted by a clip remain at bind pose.

The Blender handoff is sparse and binary. JSON contains the complete hierarchy and
bind metadata; compressed NPZ arrays contain frame numbers and only TRS components
that differ from bind by more than `1e-7`. Static rows remain in the armature with no
curves, and a rotation-only row receives quaternion curves but no location/scale
curves. Quaternions are hemisphere-continuous and frames are never decimated.

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
`foreach_set("co")`. If display-basis evaluation is needed, it sets all active bones
for a frame and updates the view layer once. Export uses
`bake_anim_use_all_bones=False` and
`bake_anim_force_startend_keying=False`.

Chrome's internal bone axes do not necessarily point toward the next visible joint. Native export keeps the exact Chrome joint transforms but applies a fixed Blender display-basis correction, so ordinary limb and spine bones point at their anatomical children. Zero-length upper-arm and thigh twist nodes use a display-only grandparent; their animated world transforms and round-trip descriptor data remain unchanged.

## Doors, props, and other rigs

If no matching rig is installed, choose **Create .crig from model FBX** and select a binary FBX of the same model/skeleton used by the ANM2. A door normally contains a root, hinge/door bone, and optionally a handle bone. The model mesh is not included in the exported FBX.

## Cross-rig export

Select **Retarget onto another skeleton**, choose the target skeleton FBX, and click **Automatic map**. The mapper prefers descriptors, exact/normalized names, aliases, hierarchy, and unique structural matches. Review the entire mapping table; uncertain bones remain unmapped rather than being guessed aggressively.

Mappings can be saved as `.dlrbmap.json`. They are tied to source and target skeleton hashes. Unmapped target bones stay at bind pose, and one target bone cannot be assigned twice.

Cross-rig transfer applies bind-relative local motion when mapped parents correspond. A global bind-relative fallback handles reparented mappings. Automatic translation scaling affects animated deltas only; it does not replace the target bind offsets or proportions.

## CLI

```text
dlanm2-anm2-to-fbx clip.anm2 --source-rig door.crig --output-directory build/fbx

python -m dlanm2_gui.tools.anm2_to_fbx \
  reference/dl2/0_m_fpp_farjump.anm2 \
  --source-rig builtin:dl2_player_advanced \
  --unknown-track-policy sidecar \
  --output-directory build/dl2_fbx

dlanm2-anm2-to-fbx clip.anm2 --source-rig source.crig \
  --target-fbx renamed-door.fbx --bone-map door.dlrbmap.json \
  --blender "C:\Program Files\Blender Foundation\Blender 4.2\blender.exe"
```

Use `--auto-map --save-auto-map mapping.dlrbmap.json` to generate a conservative CLI mapping for review.

`--unknown-track-policy` accepts `sidecar`, `helpers`, or `drop`. When omitted, DL2
uses `sidecar` and DL1 retains its existing `helpers` behavior.

## Progress and cancellation

The operation stays nonmodal and reports elapsed time plus current/total work for:
**Reading ANM2**, **Decoding pages/segments**, **Building sparse curves**,
**Starting Blender**, **Creating armature**, **Installing animation curves**, and
**Writing FBX**. Cancellation is checked during cached decoding and while Blender is
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
    for (frame in job.npz.frames) { set_active_pose_bones(frame); dependency_update_once(); }
    for (channel in job.npz.channels) {
        fcurve.keyframe_points.add(job.frame_count);
        fcurve.keyframe_points.foreach_set("co", interleaved_frame_value_pairs(channel));
        fcurve.interpolation = LINEAR;
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
