# Humanoid retargeting

DL ReAnimated maps arbitrary source bone names onto stable humanoid roles, similar to Unity's Humanoid Avatar workflow.

## Source rest pose

Recommended default:

```
Use imported animation FBX bind pose (recommended)
```

The exporter reads the FBX's unanimated transforms and uses them as the source rest reference. This was verified against the separate Mixamo T-pose workflow: the generated Standing Greeting ANM2 payload is byte-identical in both modes.

Use a separate rest/T-pose FBX only when the animation file does not contain trustworthy bind transforms. It must use the same names, hierarchy, and avatar proportions.

## Auto-map order

1. Exact canonical Mixamo names.
2. Common humanoid aliases.
3. Namespace/tool-normalized names.
4. Conservative side, digit, token, and parent-aware heuristics.

Required unresolved roles stop the build rather than being guessed.

## Manual workflow

1. Select the clip in **Retargeting**.
2. Click **Auto-map humanoid**.
3. Review required roles and suspicious low-confidence rows.
4. Choose the exact source bone in a dropdown when needed.
5. Use **Apply to compatible clips** for animations with the same skeleton hash.
6. In Advanced mode, save/load reusable `.dlrmap.json` profiles.

Closed dropdowns ignore the mouse wheel, so scrolling the table cannot alter a mapping accidentally.

## Target rig

The bundled preset is the editor-validated Dying Light male NPC/infected skeleton, represented internally by the same Chrome Rig model used for custom targets. Selecting a different `_ANIMATION_SCR_` target does not automatically change the target skeleton.

Custom `.crig` targets prefer exact-rig transfer when names and parents match. When they do not, import creates a reviewed generic map instead of rejecting the clip. Use **Root & .crig Mapping** to edit each target-to-source pair. The saved profile is consumed by the mapped-rig builder; unmapped helpers remain at target bind pose.

Model-importer `.crig` files describe the exact Chrome-authored MSH bind, including local `+X` bone frames and any fitted humanoid pivots. Even when a fitted model uses stock names such as `bip01`, `pelvis`, and `l_forearm`, build its animation against that generated `.crig`; do not assume a name match makes raw stock absolute tracks bind-compatible. A name-identical, hierarchy-compatible rig uses authoritative global bind correction. A reviewed cross-rig map instead preserves every target local translation and scale and transfers only each source bone's rest-relative rotation. This keeps the target's authored bone lengths and skin pivots intact.

Root displacement is independent from pelvis pose. For example, Mixamo `Hips` can drive the pose of `CC_Base_Hip` while the same source root's displacement is written to the selected `RL_BoneRoot`/Bip01 track according to the clip's In-place, Bip01, or Motion policy. Mapping a wrapper root manually no longer suppresses this displacement.

## Extra bones

Unmapped end nodes, controls, constraints, and non-deforming helpers may remain ignored. Strict exact-rig mode preserves matching tracks; mapped `.crig` mode preserves unmapped target tracks at bind pose.
