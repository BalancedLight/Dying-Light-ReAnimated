# DL1 rig conformance

> **Status:** the conversion core, the persisted settings layer, and the
> in-app guided wizard are implemented and covered by 59 controls. Everything
> has been exercised end to end against a local retail `player_1_tpp` and a
> Character Creator / AccuRig FBX.

This seam converts an arbitrarily rigged model into one Dying Light can drive.
The problem it solves: models rigged in AccuRig, Character Creator, Mixamo or
Blender carry their own bone names, hierarchy and rest pose, while Chrome
resolves animation tracks by **name hash** against the model's own serialized
hierarchy. Without conformance a custom model resolves no stock clip.

Nothing here synthesizes a rest skeleton. The target skeleton is extracted at
run time from the user's own installed game and cached machine-locally; no
retail bytes enter the repository or a release.

## Measured DL1 conventions

These were decoded from a local retail `player_1_tpp` (type-272 compact mesh)
and are the contract the conversion targets.

| Property | Value |
|---|---|
| Units | meters |
| Axes | Y up, +Z forward, +X to the character's left, right-handed |
| Skeleton size | 87 entities: 69 `Bone`, 18 `Helper` |
| Full resource | 106 entities / 22 roots; the remaining 19 are `SkinnedMesh` rows |
| Bone axis | **local +X aims exactly at the child** |
| Rest pose | a steep A-pose, roughly 53 degrees below horizontal at the shoulder |
| Reference height | 1.7823 m from the lowest entity to `headend` |

The bone-axis convention was verified across nine independent chains
(`upperarm→forearm`, `forearm→hand`, `thigh→calf`, `calf→foot`,
`spine1→spine2`, `neck→neck1`, a finger chain, both sides):
`dot(+X, childDirection) = 1.0000` in every case. This is what
`Dl1CustomModelRigPreparer.AimPositiveX` already implements, so the existing
Chrome frame authoring is retail-correct and conformance deliberately does not
touch it.

### Two structural facts that drive the fit

1. **`bip01` is co-located with `pelvis` at hip height** (both at
   `y = 0.9331`), not at the floor. A Character Creator `RL_BoneRoot` sits at
   the origin. Anchoring the template on its root would drop the whole
   hierarchy by 93 cm, so the fit anchors on the **pelvis** instead and places
   the root relative to it.

2. **Skeleton Y-span is not a usable scale estimator.** DL1's span includes
   `headend`, a helper above the skull, while a Character Creator skeleton tops
   out at the head joint. On the test model that discrepancy alone produced a
   0.847 ratio against a true character-height ratio near 0.95. Scale is solved
   from corresponding segment lengths instead.

### TPP and FPP share one skeleton

`player_1_fpp` carries the same 87 entity names with **identical parent-name
topology**. The only difference is serialized order: `refcamera` is index 8 in
TPP and index 74 in FPP. Since conformance maps by name and the preparer
imposes its own depth-first order, one conversion serves both; TPP and FPP
differ only in which surfaces are emitted.

## Pipeline

```
FbxModelAuthoringImporter
        │  CustomModelDocument + RigDefinition + surfaces
        ▼
RigCorrespondenceSolver     join by shared humanoid role
        ▼
RigLandmarkSolver           uniform scale + pelvis anchor
        ▼
RigConformanceSolver        place every template entity
        ▼
Dl1RigConformanceApplier    rebuild document + remap skin weights
        ▼
Dl1CustomModelRigPreparer   unchanged - Chrome +X frames, bounds, order
        ▼
Dl1SourceModelWriter → package builder → compiler → Dev Tools deploy
```

### Correspondence

Every source bone and template entity is classified into exactly one
disposition:

- **Mapped** - a shared humanoid role joins them.
- **Synthesized** - a template entity no source bone claimed, generated from
  the template's own scaled rest offset.
- **Extra** - a source bone with no DL1 counterpart, retained under its mapped
  ancestor with its weights. Stock clips carry no descriptor for it, so it
  rests at bind.
- **Dropped** - excluded on request; weights fold into the nearest surviving
  ancestor.

Roles come from the existing `HumanoidBoneSemanticClassifier`, which both rigs
already share. **No DL1-specific vocabulary was needed**: on the retail player
skeleton, 58 of 87 entities resolve a role, and all 29 that do not are exactly
the structural helpers that must be synthesized (`hspine1`, `l_normal`,
`l_foretwist`, `l_hand1`, `headend`, `propsholder1`, and so on). Having no role
is the correct signal.

Twist, share and other structural bones are **deliberately not auto-mapped**.
Their positions genuinely disagree across rigs - DL1's `l_foretwist` sits at
the wrist while a Character Creator `ForearmTwist01` sits near the elbow - so a
name-shaped guess would move skin weights to the wrong place. They stay extra
rows unless mapped explicitly.

Duplicate role claims resolve to the candidate that is a common ancestor of the
others, and every multi-candidate role is reported for review. A Character
Creator rig exposes both `CC_Base_Hip` and `CC_Base_Pelvis` as `body.pelvis`;
`CC_Base_Hip` wins because it is the bone the thighs hang from, matching DL1's
`pelvis`.

### Scale

Scale is a length-weighted least-squares fit over corresponding longitudinal
segments, `sum(t*s) / sum(t*t)`, so long bones dominate - they dominate visible
distortion. Cross-body spans are measured and reported but excluded from the
fit, because width scales independently of limb length.

Per-region ratios (leg, torso, arm, width) are reported separately, because a
stylised model routinely matches DL1 in one region and not another and a single
averaged number hides that.

### Fit, and why the mesh is re-posed

Bone **directions always come from DL1's rest pose**. This is the part that is
not negotiable, and getting it wrong was the original mistake: taking directions
from the source model leaves the bind pose 21.7 degrees off DL1's on average and
56.9 at the forearm, which neither resembles the retail player nor survives a
stock clip. Two facts close off the alternatives - the serialized reference
matrix is the inverse of the global bind on all 87 retail entities (worst
residual `4.5e-07`), and clips key per-bone translation - so the bind pose has
to be DL1's rest pose.

Segment **lengths** are the negotiable part. `ConformanceStrength` blends them
between DL1's at the solved scale (1.0, the default) and the model's own (0.0).
Template-declared coincidences such as `bip01`/`pelvis` are preserved at every
strength.

The model's own rig is then posed into that skeleton by `RigRestPoseTransfer`,
and the mesh is carried along by `Dl1RestPoseBaker`. That is what keeps the
bones inside the geometry while the bind pose matches DL1.

Each bone's rotation is solved from **all** the mapped joints hanging off it, as
an orthogonal Procrustes fit. Aiming at a single child is not good enough: the
pelvis carries both thighs and the spine, and choosing one makes the other two
inherit its correction. On the test model that alone was the difference between
97 cm and 30 cm of worst-case vertex movement, and between the legs moving 41 cm
and 9 cm. Solving jointly also recovers twist about the bone axis, which decides
where a hand's fingers land. A bone with a single constraint has its twist
settled by a small bias toward its parent.

Mapped joints are pinned exactly onto their targets - a locked control asserts a
zero residual - and unmapped bones ride rigidly with their nearest mapped
ancestor, so facial and twist rows stay attached to the joint they belong to.
Every transfer transform is a rotation and a translation, never a shear.

Orientation of the emitted rig is not solved here. Template entities carry DL1's
own rest frame and retained extras carry the frame the transfer left them in;
`Dl1CustomModelRigPreparer` authors the actual Chrome frames from those.

### Skin weights

Weights are never re-projected onto nearby geometry. Every vertex keeps the
influences the artist authored; only the bone each influence names changes.
Where a dropped bone folds into a surviving ancestor the two weights are
summed, then influences are re-sorted, capped at four, and renormalized. The
report names every folded bone, counts truncated vertices, and records the
largest discarded weight so a material change in deformation is visible rather
than silent.

Inverse-bind matrices are cleared rather than carried forward; the emitted
hierarchy owns them and `Dl1CustomModelRigPreparer` re-derives them.

## Why proportion warnings matter

`docs/ANM2_FORMAT.md` section 3: an ANM2 track carries **nine scalar
components** - translation, rotation and scale - so a stock clip supplies
absolute local translation for every bone it drives, not rotation alone. A
stock animation therefore imposes the stock skeleton's bone offsets on whatever
rig it plays on, and skinning is `GlobalAnim · InverseGlobalBind`. Anywhere the
fitted bind departs from `DL1 rest x scale`, that departure appears as mesh
stretch under stock animation.

This is why the default is to conform and report, and why a bone whose segment
ratio is off by more than 15% is flagged with the distance conforming moved it.

A model with genuinely non-DL1 proportions cannot be strictly conformed without
visible deformation, and no single uniform scale fixes it. For those, the
honest options are to accept the deformation, to reduce conformance strength
and accept that stock clips stretch the character instead, or to retarget the
clips themselves onto the custom proportions and ship them with the model.

## Local exercise against real data

Run against a local retail `player_1_tpp` and a Character Creator FBX, the
current implementation produced:

| Result | Keep extras | Drop extras |
|---|---:|---:|
| Emitted bones | 134 | 87 |
| Mapped / synthesized | 54 / 33 | 54 / 33 |
| Extra retained / folded | 47 / 0 | 0 / 47 |
| Skin palette entries | 61 | 53 |
| Weighted vertices | 53,298 | 53,298 |
| Vertices left unweighted | 0 | 0 |
| Worst per-vertex weight-sum error | 3.3e-16 | 3.3e-16 |
| Weight discarded by the influence cap | 0 | 0 |
| Preparer diagnostics | 0 | 0 |
| Worst `dot(+X, childDirection)` | 1.000000 | 1.000000 |

Bone directions against DL1's rest pose, measured on the emitted skeleton:

| | mean | worst |
|---|---:|---:|
| taking directions from the model (the original mistake) | 21.7 deg | 56.9 deg |
| taking directions from DL1 (current) | **0.00 deg** | **0.00 deg** |

Carrying the mesh into that pose moves 53,298 vertices with none left
unweighted, by 15.4 cm on average and 30.1 cm at worst. The largest movers are
the fingertips, because the arms genuinely swing from hanging out at the sides
to DL1's down-and-forward rest; the hips move 4.9 cm and the thigh twists 8.7.
Character height is preserved at 1.71 m against the original 1.69 m.

That test model is stylised - legs fit DL1 at 0.979 while torso and arms fit at
0.683 and 0.653 - so the solver reports a 19.2% proportion residual and warns on
the limbs whose length must change to reach DL1's. That is the intended
behavior: the mismatch is surfaced, not hidden, and
`PreserveSourceProportions` is there for authors who would rather keep the
silhouette and accept that clips stretch it.

## Running a conversion

```bash
DLReAnimated conform-model --fbx model.fbx --template-mesh <decoded player_1_tpp payload> --out model.dlrmodel --ignore-morphs
```

`--template-mesh` takes a decoded retail type-272 mesh resource from the user's
own game data; nothing retail is bundled. `--strength` blends conformance
(1 = DL1 proportions, 0 = the model's own), `--scale` overrides the solved
scale, `--scale-region leg` fits on the leg chain alone, and `--drop-extras`
emits an exact template-shaped skeleton. The command prints a JSON report
covering correspondence, per-region scale fit, every proportion warning, and
the emitted contract, and exits non-zero if any stage refuses.

`--ignore-morphs` is required for Character Creator exports, which emit several
empty blend-shape channels all named `V_None`; DL1 morph descriptors must be
unique, so import otherwise fails closed.

## Tests

```powershell
dotnet test tests\ReAnimated.Tests\ReAnimated.Tests.csproj -c Debug `
  --filter "FullyQualifiedName~RigConformance|FullyQualifiedName~Dl1SkinWeightConformer|FullyQualifiedName~Dl1RigConformanceApplier"
```

The controls lock template extraction and scaling, role correspondence,
synthesize-versus-extra classification, twist bones staying unmapped, ambiguity
resolution and override, exact source reproduction at strength 0, template
segment lengths at strength 1, coincidence preservation, weight normalization
and folding, influence-cap reporting, and acceptance by the existing authoring
preparer including the retail +X convention.

The wizard adds stage navigation and command gating including the no-install
path, mapping override round-trips, gizmo begin/update/commit and the transient
preview a cancelled drag must discard, refusal to drag synthesized helpers,
mirroring, settings capture and restore, and mismatched-source detection. One
STA control loads the view's markup and lays out every stage, so a resource or
binding mistake in a hand-reached tab fails the suite instead of the user.

Because the tab is new UI, the packaged startup gate must also stay green:

```powershell
.	oolsalidate_dl1_wpf_startup.ps1
```

## The wizard

A **Conform** tab in the model workspace's inspector, beside Rig and Materials.
It drives the shared viewport and runs in five stages:

1. **Target** - resolves the DL1 skeleton from the indexed installation and
   reports what it found, or why it could not.
2. **Mapping** - the correspondence table with disposition, role, confidence and
   evidence. Ambiguous roles offer their candidates in a combo; choosing one
   records an explicit override and re-solves.
3. **Scale** - the solved scale, per-region fit (leg / torso / arm / width), the
   scale policy, and the conformance-strength slider.
4. **Refine** - the guided sequence over real fitted joints, each with an
   instruction, a traffic-light fit rating and the distance conforming moved it.
   The existing axis gizmo does the dragging; edits mirror to the opposite side
   by default, and descendants follow.
5. **Verify** - imports a chosen retail ANM2 against the conformed rig and
   reports descriptor coverage, bind-fallback bones, peak vertex displacement
   and preparer diagnostics, then applies the conformance.

### Preview pairing

A mesh's skin palette is a set of indexes into the skeleton it was bound
against, so the preview must always pair the **conformed mesh with the conformed
skeleton**. Showing the conformed skeleton over the imported mesh skins it
through unrelated bones - silently wrong wherever the row counts happen to fit,
and rejected outright where they do not. On the Character Creator control that
surfaced as `palette entry 38 references skeleton bone 88 outside 87 rows`,
because dropping extra bones takes the emitted rig below the imported one.

Rebuilding that pairing measures about 240 ms on a 53,000-vertex model - 76 ms
to transfer weights, 164 ms to build the preview session. So it is rebuilt only
when the **emitted bone table** changes: resolving a template, toggling extra
bones, overriding a mapping. Placing a joint or moving a slider changes bone
positions alone and reuses the built session through a skeleton swap costing
under 2 ms, which is what keeps gizmo dragging responsive.

At the bind pose this is visually identical to the imported model, since
skinning through a bind pose is the identity. The author sees their own mesh
with DL1 bones laid into it, which is exactly what the fitting stages are for.

Joint dragging is routed to the wizard only while the Conform tab's refine stage
is visible, and structural DL1 helpers refuse the gizmo entirely - they take
their placement from the template, and letting them drift would break the
structure stock clips expect.

### Re-editing

Applying records the decisions - template identity, mapping overrides, scale
policy, strength and joint placements - on the document as schema 3. The
conformed bone table is never persisted as a layer; the package already retains
its source FBX, so reopening re-imports, re-resolves the template and replays
the settings. Settings solved against a different source model are reported
rather than silently reused.

## Open

- Spreading a source rig's three spine bones across DL1's four; the current
  join leaves `spine3` synthesized and reports the resulting `spine1` ratio.
- Bone-driven morph synthesis, which can cover roughly 6 of DL1's 38 facial
  channels and no more.
