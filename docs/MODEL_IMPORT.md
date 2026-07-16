# Model import and installation

The Models workspace imports static or skinned binary FBX models and prepares Dying Light model assets. Models, their authored rig metadata, manual mappings, and generated CRIG references are stored in the same `.dlraproj` project as animation work.

For the bind and target rules behind this workflow, see [Custom model rig contract](CUSTOM_MODEL_RIG_CONTRACT.md).

## Choose a model mode

| UI mode | Use it for | Bind result |
| --- | --- | --- |
| **Auto** | Ordinary imports where DL ReAnimated should choose static or skinned behavior from the analyzed scene. | Uses the canonical FBX scene and reports the selected mode. |
| **Static prop** | Geometry with no animated skin hierarchy. | No generated CRIG. |
| **Exact original FBX rig** | Custom characters, animals, doors, machinery, props, or any skeleton whose authored hierarchy must remain exact. | Emits the exact animation hierarchy and creates a matching CRIG. |
| **DL1 humanoid names - preserve proportions, retarget animations** | A custom-proportioned character that needs stock-style DL1 target names. The serialized compatibility name is `dying_light_humanoid`. | Fits a model-specific stock-named bind and requires animation retargeting to its generated CRIG. |

Exact Rig is the safest alternative when a fitted DL1 target role is missing or ambiguous. There is no silent fallback to `bip01`, pelvis, `root`, or the first node.

## Analyze once through the canonical FBX contract

Click **Analyze models** before building. Model and animation paths share the same production FBX evaluator. Model analysis keeps the parsed scene for build and records a serializable transform contract with:

- FBX version, source hash, detected units, and axis metadata;
- requested and resolved orientation policy;
- wrapper scale/axis normalization and conversion counts;
- BindPose, `TransformLink`, and Model-transform bind coverage;
- roots, non-bone ancestors, normalized-name collisions, and reflection diagnostics;
- warnings and blocking errors.

Leave orientation on **Auto** for ordinary exports. Auto derives the representable orthonormal basis conversion from FBX `GlobalSettings`. Units and axes are each applied exactly once to bind, geometry, and animation evaluation. Explicit `+90`/`-90` policies remain available for diagnosing older projects.

Unsupported binary layout, ASCII FBX, shear, singular scale, irreducible reflection, invalid layer indexes, or an unsafe hierarchy is reported before an MSH is written. The finding names what was detected, why it matters, the affected node or geometry, and a corrective action.

## Exact original FBX rig

Exact mode preserves the authored animation hierarchy and proportions. Nodes with real skin-cluster ownership are emitted as `BONE`. Unweighted armature ancestors, end markers, facial controls, sockets, cameras, and other transform-only animation nodes are emitted as `HELPER`; they remain addressable without receiving artificial visible weights.

The builder authors Chrome local `+X` frames, then writes the known source-MSH rule:

```text
global[node] = global[parent] * local[node]
reference[node] = inverse(global[node])
```

Immediately before serialization it freezes the emitted nodes into an immutable `AuthoredRigContract`. The source MSH, build report, and generated CRIG all consume that exact contract. The generated CRIG therefore has the same local/global bind, parents, descriptors, and helper/deform classification as the authored MSH rather than an independently reconstructed raw-FBX bind.

Use exact same-rig animation when names and target ancestry match. A source-superset animation may also include extra face, cloth, accessory, camera, weapon, twist, or helper bones; extra source nodes are harmless to required target tracks and are ignored unless mapped.

## DL1 humanoid names with a fitted bind

This mode preserves evaluated source positions and arbitrary proportions while mapping weights to stock-style DL1 names. It fits target-named pivots to imported proportions, authors Chrome `+X` frames, and uses inverse authored-global references. It does not pre-deform the mesh with a weighted target-bind/source-bind warp.

The familiar names do not make this a stock bind. Raw `anims_man_all` tracks contain stock absolute local translations and can compress the torso, stretch the neck, detach clothing, or collapse limbs on a fitted custom model.

The safe rule is:

> This model uses DL1-style names but a model-specific bind pose. Use the generated CRIG to retarget stock or custom animations. Do not attach raw stock animation tracks directly.

Leave **Animation script** empty until the desired clips have been retargeted to the generated CRIG. Then reference only the script resource containing those rebuilt animations.

The fitted hierarchy emits the useful stock-style animation prefix while imported geometry replaces stock clothing/head mesh roots. Bone display bounds use compact bind-segment proxies rather than every influenced vertex, preventing terminal and twist visualizations from becoming enormous.

## Per-subset skin palettes

A complete model hierarchy is not limited to 256 nodes. Chrome uses two index spaces:

```text
subset palette entry      uint16 global source-MSH node index
vertex bone index         uint8 local index into the current subset palette
```

The real skin limit is at most 256 global entries in each emitted subset palette. Weighted triangles are partitioned independently by material in stable source order. A partition is flushed before it would exceed:

- 256 local palette entries;
- 65,535 unique emitted vertices;
- the source index range;
- its current material.

When flushed, its global palette is deterministically ordered and every normalized top-four influence is remapped to a local byte. The writer validates the local-to-global round trip; a global node index is never stored directly in a vertex byte. A valid influence is not discarded merely to force a palette to fit.

A three-corner triangle can use at most 12 distinct bones after top-four normalization. A larger reported set means corrupt input and is rejected with the material/triangle identified.

The model report separates total hierarchy nodes from per-subset palette sizes and includes partition count, global palette entries, triangle/vertex counts, maximum influences, dropped/fallback weights, quantization error, and tangent policy.

## Geometry stability

The importer preserves source triangle order and deduplicates only a complete emitted-vertex key: transformed position, normal, tangent/binormal inputs, UV, color, normalized global influences, and morph identity where present. UV seams, hard-normal seams, material boundaries, different weights, and different morph values remain distinct.

Source tangents are imported when valid; otherwise the report states that they were rebuilt. Invalid or repeated indexes, non-finite positions, zero-area triangles, and unsafe concave/non-planar polygons fail with instructions to repair or triangulate the named geometry before export.

The model AABB remains independent from skinned bone-display bounds. A non-rendering ordinary `MESH` carrier after the `MESH_SKINNED` elements stores the exact emitted-vertex bounds without entering the animation prefix or visible skin palettes.

## Blendshape preflight

Model analysis reads each sparse FBX `Geometry::Shape` together with its channel, base mesh, `DeformPercent`, `FullWeights`, and connected weight keys. A target is ignored only when its position deltas are at most `1e-8`, normal deltas are at most `1e-5`, its default weight is zero within `1e-8`, and every constant animation key is zero within `1e-8`. The ignored target is listed in `ignored_identity_blendshapes`; the base mesh is emitted unchanged.

Names do not affect this decision. A real deformation remains a non-morph build blocker, even if its name resembles a placeholder. Invalid sparse counts, out-of-range control-point indexes, non-finite values, or ambiguous Shape/channel/base-geometry links block with the exact object names, IDs, and malformed field. The importer never rewrites or strips the source FBX.

## Skeleton retention

Visible palettes contain only actual weighted deform nodes. Unweighted animated helpers survive through animated node flags, ASCR/BSCR entity declarations, the authored hierarchy, and generated CRIG descriptors. The importer does not inject tiny artificial weights into visible vertices just to retain every node.

The report distinguishes:

- `retained_by_real_skin_weight`;
- `retained_as_animated_helper`;
- `retained_by_explicit_carrier`;
- compiler-pruned or unexpected nodes.

An explicit retention carrier is not used unless a compiler audit proves it is needed; the ordinary bounds carrier is not a skin-retention workaround.

## Build and generated-rig handoff

1. Add the model FBX and click **Analyze models**.
2. Select Static, Exact Rig, or fitted DL1-name mode.
3. Review fitted humanoid mapping only when that mode is selected.
4. Configure materials, surface, output folder, and Developer Tools paths.
5. Leave **Create/install .crig for every skinned model** enabled.
6. Click **Build source MSH** for offline source output, or **Build, compile & install** for compiler/install output.
7. Confirm the build report passes authored-bind, CPU skin, source artifact, and compiled-artifact validation.
8. Select the model and click **Use generated rig in Animations**.
9. Add animation FBXs, review their target compatibility and any ambiguous map rows, then build ANM2/RPack output.

The handoff verifies that the model source is current, the generated CRIG fingerprints the authored MSH bind, and the installed rig reference/path are retained with the model. You do not need to copy a CRIG path manually.

After changing model hierarchy, bind, orientation, units, fitted mapping, or resource identity, rebuild the source and CRIG together. Existing animations that point at an older bind must be reselected or remapped; same names do not make a stale bind safe.

## Compiler and offline validation

Before writing output, model validation reconstructs every authored global from parent/local matrices and checks `global * reference` against identity. It then resolves every sampled vertex-local palette byte back to its global node, CPU-skins at bind, and compares the result with the emitted position, including quantized weights.

Source artifact checks reject invalid node types, parent indexes, palette entries, local vertex indexes, missing LODs, and bounds contracts. Compiled skinned imports are rejected if required bones/helpers are pruned, the animation prefix changes, no skinned mesh survives, render flags are lost, a compiled bone bound collapses, or the ordinary-mesh bounds carrier differs from the emitted AABB. A compiler exit code of zero alone is not considered success.

The importer does not launch ChromeEd, Dying Light, or Developer Tools for validation. Manual editor/game verification remains required after offline checks pass.

For animation targets and relationship routing, continue with [Chrome Rig custom targets](CHROME_RIGS.md). For errors, see [Troubleshooting](TROUBLESHOOTING.md).
