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

Custom `.crig` targets use exact-rig mode: source and target bone names and parents must match, and evaluated FBX local translation, rotation, and scale are copied into the target tracks. This mode has no humanoid, `bip01`, finger, or motion-helper assumptions, so it is appropriate for doors, machines, props, and same-rig animal animations.

## Extra bones

Unmapped end nodes, controls, constraints, and non-deforming helpers may remain ignored in humanoid mode. Exact-rig mode instead requires the source to match all bones declared by the `.crig` and preserves their tracks.
