# DL ReAnimated mimic profiles

A `.dlrmimic.json` file describes facial ANM2 tracks for one target face family. It contains no executable code and no mesh geometry.

## Why profiles are needed

Skeletal ANM2 descriptors identify bones. Facial ANM2 descriptors identify morph targets, and the mimic consumer reads the ordinary ANM2 `tx` component as a scalar weight:

```text
[rx, ry, rz, tx, ty, tz, sx, sy, sz]
[ 0,  0,  0, weight, 0,  0,  1,  1,  1]
```

The writer itself does not need a new binary format. It needs a semantic profile so a descriptor is treated as `morph_scalar_tx` instead of as a fake translation bone.

## Minimal structure

```json
{
  "format": "dl-reanimated-mimic-profile",
  "schema_version": 1,
  "profile_id": "custom:my_face",
  "name": "My custom face",
  "license": "Unlicense",
  "weight_component": "tx",
  "tracks": [
    {
      "index": 0,
      "descriptor": "0xD38C4C58",
      "name": "morph_jaw_open",
      "label": "Jaw open",
      "semantic": "morph_scalar_tx",
      "component": "tx",
      "region": "jaw",
      "side": "center",
      "aliases": ["jawOpen", "mouthOpen", "viseme_AA"],
      "neutral": 0.0,
      "recommended_min": -1.5,
      "recommended_max": 1.5,
      "name_status": "resolved",
      "confidence": 1.0,
      "tags": ["mouth", "manual_speech"]
    }
  ]
}
```

Indexes are zero-based and contiguous. Descriptors must be unique.

## Mapping formula

For target channel `j` at frame `f`:

```text
target[j,f] = neutral[j]
            + sum(source[i,f] * weight[i,j] + bias[i,j])
```

This supports:

- one source curve driving one target;
- several source curves consolidated into one target;
- one source curve duplicated across several targets;
- negative contributions and corrective cancellation.

## Built-in common-46 profile

`reference/mimic_profiles/human_common46.dlrmimic.json` contains the common 46-descriptor face set observed across stock human and infected mimic ANM2 files.

Exact resolved names include eyelids, eye compression, jaw, upper/lower lips, smile, dimple, funnel, nose, and the speech controls `w`, `fv`, `pbm`, `open`, and `wide`.

The unresolved descriptors retain stable hashes plus broad motion-region labels derived from the supplied editor video. They are marked `video_region_only`; these labels are mapping hints, not claims of exact original morph names.

## Custom `.crig` integration

A custom Chrome Rig may include the complete profile in:

```json
{
  "extensions": {
    "mimic_profile": {
      "format": "dl-reanimated-mimic-profile",
      "schema_version": 1,
      "...": "..."
    }
  }
}
```

This is the preferred future route for a custom face because the target skeleton and facial descriptor semantics travel together. A standalone profile remains useful when several rigs share one face set.

## Authoring a profile for another model family

1. Collect one or more known-good mimic ANM2 files for the target model.
2. Extract the descriptor set and identify which tracks vary only in `tx`.
3. Build a sweep that activates one descriptor at a time.
4. Observe the real model and label each descriptor conservatively.
5. Add exact source aliases only when the relationship is unambiguous.
6. Test stock exact, rebuilt stock, single-channel sweeps, and combined expressions.

Do not clamp values to 0–1 by default. Record a recommended range, then preserve stock or source values unless target-mesh testing proves a clamp is required.

## Distribution

Profiles should contain only names, hashes, semantics, ranges, aliases, and author-created documentation. Do not bundle retail meshes, morph delta arrays, game DLLs, decompiled source, or original stock ANM2 payloads in a public release.
