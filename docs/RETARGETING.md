# Universal animation retargeting

DL ReAnimated analyzes a source skeleton as a hierarchy in bind space. Bone names
are useful evidence, but they are not identity and no vendor prefix is required.
The same offline, deterministic pipeline handles exact rigs, common humanoids,
multilingual names, and anonymous but structurally unambiguous humanoids.

## Automatic built-in routing

Bundled DL1 and DL2 targets are stored as `retarget_mode = auto`. Auto is a
policy, not a fourth transform solver. After live source analysis it routes an
exact/subset source to `ExactRigRetargetEngine`, a known built-in humanoid to its
verified automatic bridge, a reviewed custom cross-rig map to
`MappedRigRetargetEngine`, and an unresolved custom source to **Needs attention**.
The bundled DL2 CRIG is an implementation asset and is not shown as a normal
per-bone Exact/CRIG mapping task. Exact mode and **Root & .crig Mapping** remain
available for custom targets and an explicitly recorded expert override.

The user-facing source of truth for every bundled humanoid is a semantic mapping
profile. The **Retargeting** page shows anatomical roles and their source-FBX
assignments even when the eventual execution route is ExactRig. For DL2 Advanced
and Shadow Caster, the table contains the same 52 target-owned body roles. Each row
can remain automatic, select one source bone, inherit its target parent at bind, or
stay at bind. Confidence, evidence, and the resulting plan mode are visible without
exposing the CRIG-sized backend table.

DL2 compiles that profile into a complete `GenericBoneMap` only for live validation
and build execution: 271 rows for Advanced or 81 rows for Shadow Caster. The compiled
map is a disposable cache, not the editable project profile. Editing, loading,
clearing, or applying semantic roles invalidates it. Build recomputes it against the
live source animation, source bind, selected bundled target, and target policy; a
serialized pass result or stale compiled hash cannot authorize a solver.

Schema-v9 loading migrates old built-in DL2 Advanced projects from `exact` to
`auto`. A deliberate expert choice is retained only when the rig extensions record
`expert_solver_override` with `deliberate: true` and `retarget_mode: exact`.
Custom CRIG and manually reviewed profiles are not migrated or promoted.

## Source analysis

The analyzer considers the complete FBX model graph, including non-bone armature
wrappers, LimbNode parents, authoritative `Pose::BindPose` and skin-cluster
`TransformLink` matrices, normalized joint positions, bone lengths, branch points,
left/right symmetry, skin-influence regions when available, animation channels,
unit/axis metadata, and helper/control/end/twist likelihood.

Source-family recognition is a hint into that shared analysis, not a separate
retarget engine. Hints cover Mixamo, Blender Rigify, Auto-Rig Pro, Maya HumanIK,
3ds Max Biped/CAT, Unreal and Unity humanoids, MotionBuilder, Rokoko, AccuRig /
ActorCore, and generic Blender or Maya armatures. The route is selected by inferred
archetype:

- exact/subset identity uses the exact solver;
- a confidently inferred humanoid uses semantic-chain planning;
- a generic object or mechanical hierarchy uses conservative exact/direct mapping;
- an unknown archetype remains exact/direct or **Needs attention**;
- an animal, door, weapon, or facial control rig is never forced through humanoid
  roles merely because a name contains `head`, `root`, or `leg`.

## Unicode and multilingual names

The name evidence pipeline preserves the original display name and separately
records Unicode NFKC/casefold comparison text, namespaces, normalized separators,
camel-case and digit boundaries, side/helper/control/end/twist tokens, optional
comparison-only transliteration, semantic tokens, and detected scripts. It does not
discard non-ASCII text.

The versioned anatomy lexicon includes common body and side terms in English,
Spanish, French, German, Polish, Portuguese, Italian, Russian, Ukrainian, Chinese,
Japanese, and Korean. Unknown languages and numeric names can still resolve through
topology, bind geometry, symmetry, skinning, and motion. A side word that conflicts
with bind-space geometry lowers confidence and is reported in advanced details; it
does not silently override the skeleton.

## Semantic chains

Anatomy is represented as ordered chains and anchors: pelvis, spine, neck/head,
bilateral shoulder/arm/hand, bilateral thigh/leg/foot/toe, and optional fingers.
Source and target chains may have different lengths. The ordered alignment uses
semantic role, topology, side, normalized bind positions, bone-length ratios,
branch points, skin regions, animation activity, and name evidence. A nearest
position alone cannot invent an anatomical relationship.

Every target row receives one explicit mode:

- `direct`: one source bone drives one target bone;
- `composed`: an ordered multi-bone source segment drives one shorter target segment;
- `distributed`: one source segment is shared across a longer target chain;
- `inherit_bind`: the target retains its bind-local transform under its animated
  target parent;
- `static_bind`: an independent optional/helper/socket/facial/secondary target stays
  at bind;
- `manual_required`: an animated critical chain is genuinely ambiguous.

For example, when a source leg ends at the calf but the target continues through
foot and toe, thigh and calf remain directly mapped while foot and toe use
`inherit_bind`:

```text
targetGlobal[foot] = targetGlobal[calf] * targetBindLocal[foot]
targetGlobal[toe]  = targetGlobal[foot] * targetBindLocal[toe]
```

Missing hands, fingers, clavicles, neck subdivisions, toes, terminal nodes, twists,
helpers, facial chains, and secondary chains follow the same adaptation rules.
Their absence is normal and does not produce a warning popup. A missing pelvis name
may become a virtual calculation role when the common thigh/spine ancestor is
topologically and geometrically unambiguous; virtual roles are never output tracks.

## Animated domains and confidence

Analysis classifies the observed clip domain: full body, upper body, lower body,
single limb, facial only, mostly static pose, or root motion. A static or absent
chain does not become required merely because the target contains it. Only an
ambiguous animated critical chain needs user action.

Automatic acceptance requires a minimum multi-signal score and a minimum margin
over the runner-up, parent-chain and left/right consistency, finite nonsingular bind
matrices, valid hierarchy samples, and no accidental duplicate source consumption or
cross-domain mapping. Spatial evidence can rank anatomically coherent candidates;
it cannot create a relationship by itself.

## DL2 bundled body policies

The coherent built-in `builtin:dl2_player_advanced` target has 271 rows. In body
mode, the verified bridge emits 52 deterministic body rows and 219 explicit bind
rows. Mapped rows use global-bind-basis correction with rotation ownership, so the
target keeps its authored non-root translation, scale, bone lengths, and skin
pivots. Facial, secondary-animation, collar, camera, attachment, helper, socket,
twist, end, and body subdivisions without a deterministic source role remain at
bind or inherit target-parent motion.

The three source spine slots map deterministically to target `spine`, `spine2`, and
`spine3`; the intervening target spine rows inherit bind-local motion. For each
index/middle/ring/pinky chain, target `finger10/20/30/40` is the bind-held base and
target segments `1/2/3` receive source segments `1/2/3`. Thumb targets
`finger01/02/03` receive source thumb segments `1/2/3`; terminal source segment 4 is
unused.

The legacy `builtin:dl2_player_shadow_caster` package uses the same 52 semantic
roles and planner. Its compiled backend contains 81 rows: the same 52 body
assignments plus 29 bind/inherited target rows. Its independent IK and
shadow-caster roots remain part of the target package and are never presented as
ordinary humanoid source roles.

The saved `automatic_verified` origin is not authority by itself. Before solver
selection, build re-runs source analysis and validates the analyzer/policy versions,
source and bind signatures, target rig ID/full skeleton hash/provenance, every
descriptor and mapped pair, every explicit bind row, mapping modes, spatial-only
count, animated-chain resolution, and hierarchy safety. A stale plan is regenerated
or fails closed. Old unreviewed DL2 `automatic_repair` maps are replaced with a new
verified plan plus a migration audit record; manually reviewed or imported maps are
preserved.

Ordinary incompatible `automatic_repair` maps still cannot select the mapped solver.
Arbitrary/custom CRIG maps still require explicit review. Exact identity still uses
`ExactRigRetargetEngine`; a revalidated DL2 body plan or reviewed/imported cross-rig
map uses `MappedRigRetargetEngine` through its declared transfer policy.

Old target-sized DL2 maps migrate on first semantic use. Reviewed source choices are
copied into a new schema-v2 semantic profile, while automatic rows are regenerated
from live evidence. The original map remains in the project for audit and is never
silently promoted or deleted. Schema-v1 semantic profiles remain readable; manual
assignments become explicit overrides and automatic/alias assignments remain Auto.

## Local mapping recipes

Explicitly reviewed manual corrections for an exact/custom CRIG target can be
exported with **Export reviewed recipe…** or imported with **Import retarget
recipe…** in **Root & .crig Mapping**. Import materializes the corrected plan first,
then stores the typed recipe in the per-user deterministic cache after live
validation. On later imports, the planner first builds a fresh plan, computes its
recipe key, and applies a matching reviewed recipe; corrupt, unreviewed, stale, or
invalid cache content is ignored and the fresh attention state remains authoritative.
Recipe-derived maps use reviewed provenance and record the recipe ID, key hash, and
decision fingerprint; they never impersonate the built-in DL2 automatic certificate.

The cache key contains the source skeleton, source name/parent, and source bind
hashes; target rig ID and full target skeleton hash; target-policy ID; analyzer,
planner, semantic-policy, and multilingual-lexicon versions; and clip domain. The
animation hash is retained for audit but excluded so another clip on the same bound
skeleton can reuse a correction. Changed bind, target, or policy identities produce a
different key, and every reuse is still revalidated against the live source and
target.

## Manual workflow

Normal supported humanoids build without visiting mapping. If a row says **Needs
attention**, use **Fix mapping…** to open the focused chain, then inspect score and
runner-up evidence in **Retarget details**. Full CRIG tables and **Approve mapped
solver** for unknown/custom mappings remain under Advanced settings. Reviewed recipe
import/export appears in **Root & .crig Mapping** whenever an exact/custom CRIG target
is selected. Closed dropdowns ignore the mouse wheel.

Root displacement remains independent from pelvis pose. The source motion root and
target root are selected from their actual inventories, then the target-neutral
In-place, Skeletal root, or Motion accumulator policy is applied after pose
retargeting. `bip01` remains only the legacy serialized spelling for Skeletal root.

Root vectors never reuse the model normalization matrix. Raw source bind-to-frame
translation is scaled once, decomposed along the analyzed source actor right/up/
forward frame, reconstructed along the target bind actor frame, and only then
converted through the selected target root's parent.

## Heading-safe root policies

Root heading is extracted in target-global space as quaternion twist about the
target profile's declared/inferred world-up axis (Y-up for the bundled profiles).
There is no Euler conversion. Quaternion hemispheres are kept continuous and every
matrix/quaternion is checked for finite, nonsingular values. If the selected root
has a parent, the corrected global is converted back through the live corrected
parent global.

- **In-place** writes the skeletal root's authored bind translation, removes only
  accumulated heading, preserves swing/tilt/pose, and resets descriptor
  `0xCCC3CDDF` to identity/zero.
- **Bip01 / skeletal root** preserves translation and the complete orientation,
  including multi-turn heading.
- **Motion** removes planar displacement and accumulated heading from the skeletal
  root, preserves its vertical/pose contribution, and transfers the planar delta
  and heading delta to `0xCCC3CDDF`.

The production contract is equivalent to this C-like pseudocode:

```c
quat dlr_extract_target_space_heading_twist(quat world_delta, vec3 up) {
    vec3 projected = up * dot(world_delta.xyz, up);
    return normalize(quat(world_delta.w, projected));
}

mat4 dlr_remove_heading_from_root_global(mat4 current, mat4 first, vec3 up) {
    quat world_delta = rotation(current) * inverse(rotation(first));
    quat heading = dlr_extract_target_space_heading_twist(world_delta, up);
    return with_rotation(current, inverse(heading) * rotation(current));
}

void dlr_apply_target_root_policy(frames, rig, root, policy, vec3 up) {
    globals = reconstruct_target_globals(frames, rig);
    heading_locked = dlr_remove_heading_from_root_global(globals.root, globals.first_root, up);
    if (policy == IN_PLACE) { heading_locked.position = rig.root_bind_global.position; helper = identity; }
    if (policy == MOTION)   { helper = planar_delta_and_heading; heading_locked.position -= planar_delta; }
    if (policy == BIP01)    { heading_locked = globals.root; }
    frames.root_local = inverse(globals.corrected_parent) * heading_locked;
}
```
