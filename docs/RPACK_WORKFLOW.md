# RPack create and append workflow

DL ReAnimated writes an RP6L animation library containing `_ANIMATION_` and `_ANIMATION_SCR_` resources.

## Create new

The new-pack workflow starts with an empty library:

```
project clips
  → retargeted ANM2 payloads
  → sequences grouped by animation-script target
  → one RPack
  → manifest + build report
```

Use it for a new workshop/DLC animation package or a clean test pack.

## Append/replace

Append mode takes an **existing RPack created by DL ReAnimated** and:

1. parses its animation and script resources;
2. verifies the `.dlrmanifest.json` hash when present;
3. retains unrelated animations and script resources;
4. adds new resources;
5. either rejects or replaces name collisions;
6. writes a new pack to the selected output folder;
7. writes an updated manifest.

The source pack is not edited in place unless the output path is deliberately chosen to be the same path; using separate source/output paths is strongly recommended.

## Collision policy

### Error

Stops when an animation resource or sequence name already exists. This is the safest mode.

### Replace

Replaces the `_ANIMATION_` payload and updates the matching sequence’s start/end/FPS while retaining other script entries.

## Manifest

Every build writes:

```
<pack>.dlrmanifest.json
```

It includes:

```
manifest format/schema
pack name and SHA-256
project UUID
build mode
animation-script resource names
per-animation source, mapping, root, IK, frame and hash metadata
```

Append refuses a pack when its present manifest hash no longer matches. This prevents an external edit from being silently overwritten.

A parseable pack without a manifest may be appended with a warning, but its provenance cannot be verified and existing resources are marked unmanaged.

## Limitation: arbitrary game RPacks

The parser supports the animation-library RP6L layout produced by this tool. It is not a general-purpose merger for every retail/compressed RPack variant. Do not point append mode at an arbitrary stock game pack and assume every unknown resource type will survive.

## Atomic output

The final RPack is written to a temporary file, flushed, then atomically moved into place. Project files use the same policy. A failed build should therefore not leave a partially written final pack.

## Output report

`dl_reanimated_build/build_report.json` records the resulting pack hash, scripts, clip resources, generated ANM2 paths, mapping profiles, root policies, and warnings.

## Editor installation

Install the final file as:

```
common_anims_sp_pc.rpack
```

under the same working workshop/DLC data route used by the validated test packs. Do not replace `common_anims_PC.rpack`. Restart or reload the editor project to clear cached resources.
