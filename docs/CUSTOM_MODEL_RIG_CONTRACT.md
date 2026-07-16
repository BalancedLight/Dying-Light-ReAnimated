# Custom model rig contract

DL ReAnimated treats a custom skinned model and every animation built for it as one bind-owned asset family. The model build is the authority: it evaluates the FBX once, authors the source MSH hierarchy, freezes that hierarchy into an immutable rig contract, and generates the target `.crig` from that contract.

This is the safest path for exact custom rigs, source-superset animations, fitted DL1-named characters, and reviewed cross-rig animation.

## Ownership and data flow

```text
model FBX
  -> canonical FBX transform contract
  -> source MSH nodes and inverse-global references
  -> immutable authored rig contract
       -> model build report
       -> generated CRIG
       -> animation target selection and map fingerprints

animation FBX
  -> the same canonical FBX evaluator
  -> exact, source-superset, or reviewed cross-rig solver
  -> ANM2 for the generated CRIG
```

The model owns the target bind. An animation FBX owns its source bind and sampled motion; it does not replace the target bind. A `.crig` is therefore not a generic collection of familiar bone names. Its complete bind, hierarchy, descriptors, helper/deform classification, and aliases identify one target.

## Canonical FBX transform contract

Model and animation imports use the shared production FBX evaluator. It resolves local and global transforms, all six FBX Euler orders, pre/post rotation, pivots and offsets, geometric transforms, non-bone Model ancestors, scene units, axis metadata, skin `TransformLink`, bind poses, animation stacks, and wrapper transforms through one normalization object.

The serialized `FbxTransformContract` records the source hash and version, metres per unit, axis settings, requested and resolved orientation policies, unit and axis conversion counts, wrapper models, bind-source coverage, roots, non-bone ancestors, name collisions, reflected transforms, warnings, and errors.

The important invariants are:

- units are converted to metres exactly once;
- the resolved orthonormal axis basis is applied exactly once;
- bind globals and animated globals use the same normalization;
- `Pose::BindPose` is preferred, then skin-cluster `TransformLink`, then unanimated Model transforms for uncovered nodes;
- conflicts between authoritative bind sources are reported;
- unsupported shear, singular transforms, and irreducible reflections fail with the affected node named.

Leave orientation on **Auto** for normal imports. Auto derives the conversion from FBX `GlobalSettings`; the explicit legacy `+90`/`-90` choices are diagnostic overrides, not extra rotations to stack on top of Auto.

## Immutable authored-rig contract

The model builder creates `AuthoredRigContract` immediately before source-MSH serialization. It contains:

- source and resource identity, coordinate policy, and a stable contract ID;
- every physical source-MSH node index, name, normalized name, parent, and node type;
- exact local, reconstructed global, and inverse-global reference matrices;
- animation descriptors, deform/helper flags, semantic roles, and aliases;
- root inventory, primary root, animation-entity prefix, and deform/helper/mesh index sets;
- full bind, skeleton, and descriptor hashes;
- the generated CRIG reference and portable path after installation.

The contract validates each node in parent-before-child order:

```text
global[node] = global[parent] * local[node]
reference[node] = inverse(global[node])
global[node] * reference[node] ~= identity
```

This inverse-global MSH rule is unchanged. The generated CRIG copies the contract's animation hierarchy, locals, descriptors, classifications, aliases, and semantic tags. CRIG creation also verifies that its TRS representation round-trips the authored local matrix; shear or an irreducible reflected basis is rejected before output.

The CRIG and model report retain the authored contract ID and full-bind hash. Same names and parents are not enough to make a stale CRIG compatible.

## Relationship classes

### Exact

Every required target bone is present under the expected target ancestry and there are no meaningful source-only or missing optional rows. The exact solver transfers through global bind-basis correction. At the source bind, the corrected target reaches its authored CRIG bind without manual correction.

### Target-compatible source superset

Every required target deform track is present under the expected target ancestry, while the source may contain additional face, cloth, accessory, camera, weapon, twist, socket, or helper bones. Extra source rows do not invalidate required target tracks. They are ignored unless the target contract has a reviewed destination for them. Missing optional target helpers remain at bind.

### Cross-rig

Required names or ancestry differ. The clip may be imported for repair, but it cannot build until the map is explicitly reviewed. Ordinary deform-chain rows default to rest-relative rotation transfer while preserving target local translation and target bind scale. This prevents source bone lengths from changing target proportions.

Root displacement remains a separate per-clip policy. Helper/socket fan-out is applied after the primary body solve, and one source bone may drive several distinct target helper rows.

## Model modes

### Exact original FBX rig

Use Exact Rig when the target model and same-rig animations share the authored hierarchy. Weighted nodes are emitted as `BONE`; unweighted armature ancestors, controls, end markers, and other animated transform nodes are retained as `HELPER`. The generated CRIG is built from the emitted MSH contract, not independently from the raw FBX.

### DL1 humanoid names - preserve proportions, retarget animations

This fitted mode maps skin weights to DL1-style target names and fits target-named pivots to the imported character's proportions. It preserves the authored surface, creates Chrome `+X` local frames, and writes inverse authored-global references.

The result is a model-specific bind even though its names look stock. Do not attach raw `anims_man_all` or other stock absolute local tracks directly. Use the generated CRIG and retarget the stock or custom animation FBX to it.

### Static prop

A static prop has no animation contract or CRIG. Auto may select this mode when the FBX has geometry but no usable skinned armature.

## Total nodes and local skin palettes

Chrome skinning has two different index spaces:

```text
subset.bone_palette[]      uint16 global source-MSH node index
vertex.bone_indices[]      uint8 local index into that subset palette
```

Therefore, a model may have more than 256 total hierarchy nodes. The 256-entry limit applies independently to each emitted subset palette. Current source-MSH hierarchy parents are signed 16-bit indexes, so hierarchy size, subset global-index storage, vertex count, and ANM2 descriptor/page capacity are validated as separate real limits.

For each material, weighted triangles are processed in stable source order. A partition is flushed before adding a triangle would exceed 256 palette entries or 65,535 unique emitted vertices. The emitted palette uses deterministic ascending global node order; only then are global influences remapped to local byte indexes. No valid influence is dropped to make a palette fit.

The build report records every partition's global nodes, palette size, triangle and vertex counts, maximum influences, weight loss/fallback totals, quantization error, and tangent policy. It also records the local-to-global round-trip contract.

## Mapping rows and review

Bone-map schema v2 uses unambiguous directions:

```text
target_rig_descriptor
target_rig_bone
source_fbx_bone
```

It also stores mapping kind, confidence, method/evidence, review state, notes, transfer policy, component policy, and extension data. Schema-v1 fields are migrated without reversing their historical meanings or deleting a row.

Review states distinguish accepted exact evidence, unreviewed automatic suggestions, manually/imported reviewed rows, and targets intentionally left at bind. An incompatible cross-rig clip cannot select the mapped solver while required rows remain automatic and unreviewed.

Automatic mapping treats descriptor and exact/normalized name identity as strongest evidence, then uses aliases, semantic anatomy roles, side, chain depth, deform/helper class, and parent/child roles. When the animation document exposes authoritative bind globals, it additionally compares normalized bind pivots, parent-child direction, segment/radial extent, and normalized chain depth; positive mesh-cluster ownership contributes deformation evidence when a skinned source mesh is present. One deterministic global assignment chooses the primary one-to-one rows, so two target bones cannot independently consume the same source candidate. Every row records its top candidate, runner-up, component scores, margin, and whether review is required. Low-margin or repair-map rows stay automatic/unreviewed and build-blocking. A short shared token alone is not an approval. Explicit helper targets may still fan out from one reviewed source after the primary solve.

### Transfer policy

| Policy | Use |
| --- | --- |
| `global_bind_basis` | Exact/name-equivalent and source-superset rows with authoritative global bind matrices. |
| `rotation_delta` | Cross-rig deform chains; preserves target local translation and bind scale. |
| `rest_relative` | Reviewed helpers, props, sockets, or rows that intentionally transfer rest-relative motion. |
| `copy_local` | Only a proven identical local basis. |
| `bind` | Leave the target at authored bind. |

`default` resolves to the solver's safe policy for the relationship class.

### Component policy

Each ordinary mapped row, not only helper overrides, selects one of `rotation`, `translation`, `rotation_translation`, `scale`, or `full_transform`. Every target starts at bind for each frame, and only the selected components are merged. A non-root row can change target length only when it explicitly owns translation and that choice has been reviewed.

## Generated CRIG handoff

After a skinned model is built with **Create/install .crig for every skinned model** enabled:

1. verify that the model report shows passing authored-rig and CPU bind-skin validation;
2. select the model and click **Use generated rig in Animations**;
3. the CRIG is installed or refreshed, selected as the project target, and retained with the model resource name and generated rig reference;
4. add animation FBXs and review their compatibility badge;
5. review only ambiguous cross-rig rows, select root policy, and build.

Rebuild the model and regenerate its CRIG together after any bind-affecting source change. A stale CRIG with the same names is still rejected by its full-bind fingerprint.

## Multiple targets in one project

Project schema v8 stores a default target rig and an optional target override on each animation. **Inherit project target** leaves the per-animation reference/path empty. Each enabled clip independently resolves its target, mapping profile, root pair, solver, script target, and resource name.

Animations for different CRIGs may share one tool-owned RPack when resource names are unique. Build reports group outputs by target rig and skeleton hash. Mapping profiles are bound to both the source skeleton signature and the target skeleton/full-bind hash, preventing accidental reuse on a same-named but differently proportioned model.

Older projects keep their project-level target; existing animations inherit it. Unknown fields, mapping profiles, target choices, animation settings, and extension data survive migration and round-trip saving.

## Validation and recovery

Model build stops before source or compiled output when the transform contract, topology, palette, authored bind, CRIG TRS, or CPU bind-skin check is unsafe. Animation/RPack build performs preflight before creating output and blocks stale target binds, incompatible unreviewed maps, unauthorized translation, hierarchy instability, non-finite tracks, and profile mismatch.

Recovery follows the named finding:

- missing or ambiguous root: choose one of the listed source and target roots; there is no silent `bip01`, pelvis, or first-bone fallback;
- target-compatible source superset: continue; review only a source extra that must drive a target helper;
- incompatible cross-rig: open the mapping editor, resolve required rows, choose per-row policies, and mark them reviewed;
- stale CRIG or map: rebuild the model CRIG, reselect it, and regenerate/review the map;
- palette overflow: allow the partitioner to split by material/palette; if one reported triangle is invalid, repair its skin weights in the DCC;
- unsupported transform: remove the named shear, zero scale, or reflection and re-export;
- fitted DL1-named model with stock script: clear the raw stock script, retarget to the generated CRIG, and use only the script containing those rebuilt clips.

No editor or game validation is implied by offline preflight. The next manual validation is to compile one exact-rig large-skeleton model, inspect its bind pose/bone overlay, play one same-rig clip, and play one reviewed cross-rig clip in DL1 DevTools.

The documentation-only C-like format reconstructions are in [Chrome 6 custom model/rig reconstructions](reverse_notes/CHROME6_CUSTOM_MODEL_RIG_RECONSTRUCTIONS.md). They are behavioral notes, not runtime addresses or hooks.
