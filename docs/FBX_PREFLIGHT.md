# FBX preflight checks

DL ReAnimated runs preflight when an FBX is imported/analyzed and again before model source output, model compilation/installation, ANM2 output, and RPack packaging. All stages use the canonical production FBX scene and transform contract; model build can reuse the analyzed scene instead of parsing and normalizing it through a different path.

Import readiness and build readiness are intentionally different. A readable cross-rig animation may be added so its mapping can be repaired, while output remains blocked until the map and selected target are safe.

## Canonical transform inventory

Every readable binary FBX reports an `FbxTransformContract` containing:

- source path/hash and FBX version;
- metres per unit and FBX axis settings;
- requested/resolved orientation;
- unit and axis conversion counts;
- wrapper models and wrapper-scale normalization;
- bind source per bone and mesh bind source per geometry;
- BindPose/`TransformLink`/Model-fallback coverage;
- Unicode-normalized name collisions;
- skeletal roots and transformed non-bone ancestors;
- reflected/negative-scale nodes, warnings, and errors.

Units and the resolved orthonormal axis basis must each be applied exactly once. Bind and animated globals share the same normalizer. Auto orientation is derived from FBX `GlobalSettings`; manual `+90`/`-90` choices are explicit diagnostic policies.

Bind-source priority is `Pose::BindPose`, then skin-cluster `TransformLink`, then unanimated Model transforms for uncovered nodes. Disagreement between authoritative sources is never silently hidden.

## Blocking model findings

A model build stops before output when a reliable MSH/CRIG cannot be authored, including:

- unreadable, ASCII, or unsupported-version FBX;
- invalid geometry, polygon, layer, normal, UV, or material indexes;
- non-finite/repeated/zero-area triangle data;
- unsafe concave or non-planar polygon data that must be triangulated in the DCC;
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
- required cross-rig rows that remain automatic/unreviewed;
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

### Needs reviewed mapping

Required names or target ancestry differ. The finding is repairable at import: add the clip, open the mapping editor, assign every required row, select safe per-row policies, and mark ambiguous rows reviewed. Build remains blocked until that work is complete.

### Incompatible

The source cannot be evaluated safely, the selected game/target is incoherent, or the reviewed map still cannot reconstruct required tracks. Correct the named asset or select the matching target CRIG.

## Warnings and informational findings

Warnings do not necessarily invalidate an FBX:

- multiple animation stacks are present and require an explicit selection;
- no changing skeletal channels were found in the selected stack;
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

## Reading and recovering from a finding

Each finding states:

1. what was detected;
2. why it matters;
3. the affected model, animation, bone, geometry, target, or stack;
4. a safe corrective action;
5. whether import can continue for mapping repair.

The GUI shows a concise summary and keeps the complete structured report in build output. Do not bypass a failure by deleting a report or reusing an older source MSH/CRIG.

Common recovery paths:

- **source extra bones**: continue if all required target bones and ancestry match;
- **required target missing/ancestry mismatch**: create or open the map, review required rows, and use the mapped solver;
- **stale target bind**: rebuild model and CRIG together, reselect the generated target, then regenerate/review the map;
- **palette overflow**: inspect the named partition/triangle; ordinary large rigs split automatically, while a single corrupt triangle must be repaired;
- **root missing**: choose from the reported source/target root inventory rather than renaming blindly to `bip01`;
- **axis/unit issue**: apply/freeze unintended wrapper transforms in the DCC and re-export; do not stack a manual rotation over a correct Auto result;
- **blendshape finding**: identity targets need no action; real morphs require morph-capable model import or an intentional bake, while malformed sparse rows/connections must be repaired;
- **fitted DL1-name stock-track hazard**: clear the raw stock script and retarget the animation to the model-generated CRIG.

See [Troubleshooting](TROUBLESHOOTING.md) for symptom-driven help and [Custom model rig contract](CUSTOM_MODEL_RIG_CONTRACT.md) for the underlying bind/index rules.
