# FBX preflight checks

DL ReAnimated runs preflight when an FBX is imported/analyzed and again before model source output, model compilation/installation, ANM2 output, and RPack packaging. All stages use the canonical production FBX scene and transform contract; model build can reuse the analyzed scene instead of parsing and normalizing it through a different path.

Import readiness and build readiness are intentionally different. A readable cross-rig animation may be added so its mapping can be repaired, while output remains blocked until the map and selected target are safe.

The Project preference is labeled **Recommended / forgiving (FBX parsing)**. Its
tooltip states:

```text
Controls recoverable FBX parsing and geometry diagnostics. It does not approve cross-rig bone mappings or bypass skeleton safety checks.
```

The alternative **Strict diagnostics** can promote selected recoverable parser or
geometry findings. Neither setting authorizes a map, changes retarget confidence, or
bypasses source/target/certificate validation.

## Purpose-scoped FBX loading

The production reader requires an explicit load purpose: `ANIMATION`, `MODEL`, `ANIMATION_AND_FACIAL`, or `FULL_DIAGNOSTIC`. All purposes share the same hierarchy, transform, unit, axis, and bind evaluator. They do not share validation for data the caller did not request.

Normal animation import loads the skeleton, animation stacks/curves, bind pose, and lightweight object/connection inventory. It does not construct `FbxGeometry`, triangulate polygons, validate model layer indexes, or load model materials/skin weights. Facial animation additionally follows `BlendShapeChannel`/`DeformPercent` curve connections without requiring polygon topology. Therefore a quad-heavy display mesh, missing tangent layer, or malformed unrequested model index array cannot become `fbx_unreadable` for ANM2 output.

`fbx_unreadable` is reserved for container-level failures such as an invalid signature, truncated node stream, unsupported fundamental encoding, or object/connection corruption that prevents the requested domain from being parsed. Requested-domain failures use scoped codes such as `animation_skeleton_unusable`, `animation_stack_unusable`, `model_geometry_unusable`, and `facial_shape_geometry_unusable`.

## Canonical transform inventory

Every readable binary FBX reports an `FbxTransformContract` containing:

- source path/hash and FBX version;
- metres per unit and FBX axis settings;
- declared `TimeMode`/custom FPS, origin, and confidence (valid GlobalSettings wins over inferred key spacing);
- requested/resolved orientation;
- unit and axis conversion counts;
- wrapper models and wrapper-scale normalization;
- bind source per bone and mesh bind source per geometry;
- BindPose/`TransformLink`/Model-fallback coverage;
- Unicode-normalized name collisions;
- skeletal roots and transformed non-bone ancestors;
- reflected/negative-scale nodes, warnings, and errors.

Animation analysis adds the full wrapper/bone graph, normalized bind-space joint
geometry, branch/depth/length evidence, left/right symmetry, available skin regions,
animated bones/components, observed motion domain, source archetype/confidence,
source-family hints, Unicode scripts, semantic roles/chains, and helper/control/end/
twist likelihood. Names are retained as Unicode evidence; they are not treated as
skeleton identity.

Units and the resolved orthonormal axis basis must each be applied exactly once. Bind and animated globals share the same normalizer. Auto orientation is derived from FBX `GlobalSettings`; manual `+90`/`-90` choices are explicit diagnostic policies.

Bind-source priority is `Pose::BindPose`, then skin-cluster `TransformLink`, then unanimated Model transforms for uncovered nodes. Disagreement between authoritative sources is never silently hidden.

## Blocking model findings

A model build stops before output when a reliable MSH/CRIG cannot be authored, including:

- unreadable, ASCII, or unsupported-version FBX;
- invalid geometry, polygon, layer, normal, UV, or material indexes;
- non-finite coordinates, out-of-range control-point indexes, fewer than three usable distinct points, irreparably self-intersecting topology, or a face from which no valid output triangle can be produced;
- singular, sheared, or irreducibly reflected bind/geometry transforms;
- Unicode-normalized animation-node name collisions or descriptor collisions;
- a manually selected root or fitted target role that is absent;
- no skinned geometry in a requested skinned mode;
- one corrupted triangle with invalid influences;
- a subset that cannot satisfy the 256-entry local palette or 65,535-vertex limits;
- authored global/local/reference identity failure;
- generated CRIG bind/TRS mismatch;
- CPU bind-pose skin error or local-palette round-trip error;
- source-MSH/compiler artifact contract failure;
- a fitted custom bind assigned a raw stock animation script.

A missing `bip01`, pelvis, or root convention produces a named diagnostic with available roots and identifies Exact Rig when it is a viable alternative. There is no dictionary `KeyError`, identity fallback, or first-bone fallback.

No whole-model 256-bone check exists. More than 256 total hierarchy nodes are valid when every emitted subset palette has at most 256 entries and the separate parent/node/index limits are satisfied.

## Blocking animation findings

Animation or package output stops for conditions such as:

- unreadable or unsupported binary FBX;
- no usable animation skeleton or no selected animation stack;
- a multi-layer stack that has not been baked/flattened;
- non-finite or singular source/target bind matrices;
- ambiguous normalized names or descriptor collisions;
- source/target game-profile mismatch;
- a stale CRIG versus model-authored bind hash;
- a mapping profile made for another source signature or target full-bind hash;
- a stale analyzer, target-policy, or semantic-policy version that cannot be safely
  regenerated;
- a verified-plan certificate that differs from the live source analysis, target
  provenance, descriptors, row inventory, or deterministic mapping decisions;
- required cross-rig rows that remain automatic/unreviewed;
- an unresolved animated critical chain or an insufficient confidence margin;
- missing or ambiguous manually selected source/target root;
- unauthorized non-root translation/bone-length change;
- non-finite, singular-scale, detached, or exploding target hierarchy;
- ANM2 decode mismatch or invalid long-clip page transition;
- duplicate output resource names across target groups.

Preflight resolves every enabled animation's target, mapping, roots, solver, and script target before the output folder or RPack is created or replaced.

## Relationship classifications

### Exact

Required target names and ancestry match without meaningful source-only differences. Build uses global bind-basis correction and the target CRIG owns the output bind.

### Target-compatible source superset

Required target deform tracks and ancestry match, while the source has extra face, cloth, accessory, camera, weapon, twist, socket, or helper bones, or omits optional target helpers. Extra source nodes are informational and do not break required tracks. Missing optional targets remain at bind.

### Ready through automatic retargeting

A confidently inferred humanoid may differ in names and chain lengths. The universal
planner aligns ordered semantic chains and emits explicit `direct`, `composed`,
`distributed`, `inherit_bind`, and `static_bind` dispositions. Missing optional or
terminal source bones are normal: the target child retains its bind-local transform
under its animated target parent. Only an ambiguous animated critical chain produces
`manual_required`.

The built-in DL2 advanced body policy is a narrowly verified case: 52 deterministic
body rows plus 219 explicit bind-default rows, with no spatial-only or non-body
mapping. Its certificate is revalidated against the live source and coherent bundled
target before solver selection.

### Needs reviewed mapping

An animated critical chain remains ambiguous, a confidence margin is too small, or a
custom map is still unreviewed. Import remains non-modal and the animation row shows
one **Fix mapping…** action. Build remains blocked until the focused invariant is
resolved. A raw list of every missing target bone belongs in advanced diagnostics,
not in the primary message.

### Incompatible

The source cannot be evaluated safely, the selected game/target is incoherent, or the reviewed map still cannot reconstruct required tracks. Correct the named asset or select the matching target CRIG.

## Findings and user-facing severity

Findings have a requested-purpose disposition: `pass`, `warning`, `automatically_repaired`, or `block`. Warnings do not necessarily invalidate an FBX:

- multiple equally useful animation stacks require manual selection;
- a deliberately static stack is importable as a rest-pose clip;
- a unique changing skeletal stack was selected automatically while static peers remain manually selectable;
- unrequested model geometry was ignored for animation;
- a non-planar quad was triangulated by deterministic diagonal scoring;
- a simple convex/concave n-gon was triangulated by deterministic projected ear clipping;
- a valid polygon used the validated deterministic fan recovery path;
- a missing normal layer was reconstructed;
- excess skin influences were reduced to four and normalized, or minor unweighted vertices used the reviewed root fallback;
- BindPose coverage is partial and Model transforms cover remaining nodes;
- BindPose and `TransformLink` disagree;
- a non-bone wrapper carries a representable axis/scale conversion;
- scene units are unusual;
- multiple independent/helper-like roots are present;
- optional target helpers are missing;
- the source has safe extra bones;
- non-ASCII target names require explicit CRIG descriptors;
- source tangents are absent/invalid and will be rebuilt;
- a proven identity/no-op model blendshape will be skipped and recorded without changing the base mesh;
- an FPP/headless mesh set appears intentional.

Review warnings that affect a required deform chain, target root, or authored bind. Informational extra source bones normally require no action.

The basic animation UI groups those technical findings into four presentation
severities:

- `info`: advanced details/build report only; no badge or popup;
- `advisory`: one compact non-modal row note; build remains ready;
- `action_required`: yellow **Needs attention** row with one **Fix mapping…** action;
- `blocking`: red state and one modal only when the user explicitly builds/exports,
  except an unreadable file, missing requested stack, or unusable skeleton detected
  during import.

Namespace removal, multilingual recognition, ignored extra source bones, bind-held
optional/facial/secondary targets, inherited terminal bones, and chain-length
adaptation are normal information. They are grouped and deduplicated in **Retarget
details** and the build report; they do not generate warning spam.

## Reading and recovering from a finding

Each finding states:

1. what was detected;
2. why it matters;
3. the affected model, animation, bone, geometry, target, or stack;
4. a safe corrective action;
5. whether import can continue for mapping repair.

The animation row reports readiness before Export is opened, for example **Ready —
exact skeleton match**, **Ready — automatically retargeted**, **Ready — partial
skeleton; 8 target bones inherit parent motion**, **Ready — upper-body clip; lower
body held at bind**, or **Needs attention — left arm chain is ambiguous**. **Details…**
shows the grouped parser findings, analysis evidence, mapping-mode summary, suppressed
informational count, and certificate status. Batch import continues after one
genuinely corrupt file. Python tracebacks remain hidden in normal dialogs and are
available only through explicitly enabled developer diagnostics.

Common recovery paths:

- **source extra bones**: continue if all required target bones and ancestry match;
- **missing optional source bone**: no repair is normally required; confirm its
  `inherit_bind` or `static_bind` disposition in Retarget details;
- **ambiguous animated chain**: use the row's single **Fix mapping…** action and
  resolve the named chain;
- **custom automatic map**: review/import it explicitly; `automatic_repair` remains
  unable to authorize the mapped solver;
- **stale target bind**: rebuild model and CRIG together, reselect the generated target, then regenerate/review the map;
- **palette overflow**: inspect the named partition/triangle; ordinary large rigs split automatically, while a single corrupt triangle must be repaired;
- **root missing**: choose from the reported source/target root inventory rather than renaming blindly to `bip01`;
- **axis/unit issue**: apply/freeze unintended wrapper transforms in the DCC and re-export; do not stack a manual rotation over a correct Auto result;
- **blendshape finding**: identity targets need no action; real morphs require morph-capable model import or an intentional bake, while malformed sparse rows/connections must be repaired;
- **fitted DL1-name stock-track hazard**: clear the raw stock script and retarget the animation to the model-generated CRIG.

See [Troubleshooting](TROUBLESHOOTING.md) for symptom-driven help and [Custom model rig contract](CUSTOM_MODEL_RIG_CONTRACT.md) for the underlying bind/index rules.
