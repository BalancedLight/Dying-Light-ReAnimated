# ANM2 to FBX

The **ANM2 → FBX** workspace converts extracted Dying Light animations into editable skeleton-and-animation FBXs. Blender is used in background mode as the FBX writer; it is not required for the normal FBX → ANM2 workflow.

## Requirements

- An extracted `.anm2` file.
- Blender installed, or a selected `blender.exe`.
- The matching Chrome Rig (`.crig`). The bundled male NPC/infected rig is included.

ANM2 contains descriptor hashes and sampled local transforms, but no bone names, hierarchy, or bind pose. Choosing the correct rig is therefore required. Standalone ANM2 also has no authoritative playback FPS; the default is 30 FPS.

## Native export

1. Open **ANM2 → FBX** and add one or more ANM2 files.
2. Select the matching source Chrome Rig.
3. Leave **Native rig** selected.
4. Set FPS/frame range and choose an output folder.
5. Choose Blender if it was not detected, then export.

Rig bones omitted by a clip remain at bind pose. `0xCCC3CDDF` is preserved as the non-deforming Empty `DLR_OffsetHelper_CCC3CDDF`; other non-bone descriptors become `DLR_Track_XXXXXXXX` Empty objects. They retain their descriptor metadata and animation without appearing as metre-long terminal bones in Blender.

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

dlanm2-anm2-to-fbx clip.anm2 --source-rig source.crig \
  --target-fbx renamed-door.fbx --bone-map door.dlrbmap.json \
  --blender "C:\Program Files\Blender Foundation\Blender 4.2\blender.exe"
```

Use `--auto-map --save-auto-map mapping.dlrbmap.json` to generate a conservative CLI mapping for review.

## Current boundary

This release exports standalone ANM2 files, not arbitrary retail RPacks. Output contains a skeleton and action, not a model mesh. Automatic mapping proposes correspondences; unrelated anatomical or mechanical rigs may still require manual mapping or animation cleanup.
