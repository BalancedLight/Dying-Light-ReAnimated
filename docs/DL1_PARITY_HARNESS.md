# Historical DL1 Python-to-C# fixture provenance

> **Historical background only.** The C# application, package, validation scripts, and GitHub Actions do not install, start, or require Python. The JSON files described here are checked-in static C# compatibility fixtures; the former live differential wrappers were retired.

The Python application remains an archived external regression oracle
during the C# rewrite. It is not treated as more authoritative than installed
DL1 1.55 assets, matching-build decompiles, or captured game behavior. The
live parity gates run both implementations over small, versioned,
redistributable corpora without changing the Python application modules;
every divergence is investigated instead of automatically forcing C# to copy
Python behavior.

## Reviewed oracle fixtures

### ANM2 and descriptor oracle

`tests/fixtures/dl1_python_csharp_parity_v1.json` contains:

- exact Chrome/DL1 descriptor hashes for representative ASCII names;
- rejection of non-ASCII implicit descriptor names;
- exact packed-group encoded bytes for flat, signed-ramp, and quadratic inputs;
- exact generated-ANM2 length and SHA-256 for a two-track direct, packed, and
  packed-scale recipe;
- exact source and preserving-round-trip SHA-256, header fields, descriptors,
  and page spans for the two checked-in stock controls;
- selected semantic component values and sampler locations at no more than
  seven fixed times and eight deterministic track indices per clip.

ANM2 numeric samples use an absolute tolerance of `0.00001`. Inputs are capped
at 64 MiB and the generated JSON consumed by the C# test is capped at 4 MiB.

### Semantic authoring oracle

`tests/fixtures/dl1_python_csharp_semantic_parity_v1.json` contains explicit
recipes and Python results for:

- all six FBX Euler orders, a combined pivot/pre/post/scale transform, a
  two-node hierarchy, and bounded linear-curve sampling;
- global bind-basis retarget correction, including a mapped camera helper and
  a reviewed target-only camera helper that stays at target bind local;
- `inplace`, `bip01`, and `motion` root/heading ownership, including the
  `0xCCC3CDDF` motion-accumulator values;
- three source facial curves consolidated into two mimic scalar targets,
  followed by each implementation's own ANM2 encode/decode;
- exact bytes, SHA-256, resource manifest, and extraction semantics for one
  canonical sorted, uncompressed animation-library RP6L/RPack recipe.

The C# tests reconstruct every recipe through production domain APIs. They do
not merely deserialize the stored result as their output. Matrix values use an
absolute tolerance of `0.000000001`, root components use `0.00000001`, and
decoded mimic scalars use `0.002` to account for each writer's packing.

The semantic fixture is capped at 2 MiB. The checked-in fixture is currently
about 27 KiB and the exact canonical RPack control is 593 bytes.

### FED oracle

`tests/fixtures/dl1_python_csharp_fed_parity_v1.json` contains two
author-generated accepted FED payloads. Python and C# compare ordered names,
exact float32 bits, duplicate-name diagnostics and first-match lookup, plus one
mapped non-destructive facial-layer consolidation. Sixteen malformed or
strict-duplicate controls compare accept/reject decisions. This is a bounded
format control; it is not retail/model-family breadth or visual/game
validation. See `docs/DL1_FED_PARITY_HARNESS.md`.

### Event-bearing AnimationScr oracle

`tests/fixtures/dl1_animation_scr_event_parity_v1.json` contains three
synthetic sequence records, event counts `2/0/1`, three opaque 12-byte event
rows, exact original/patched sections, and five rejected inputs. Python and C#
compare parsed metadata and exact bytes while proving a timing patch preserves
the complete opaque event table and second section. A separate exact-build
control reads three stock Windows 1.55 scripts without checking retail bytes
into the repository. Event-row semantics and event authoring remain
unsupported. See `docs/DL1_ANIMATION_SCR_EVENT_PARITY.md`.

No checked-in oracle contains a retail mesh, texture, animation, FED file, or
other proprietary game payload.

## Honest scope

This is a bounded differential gate, not a full parity certificate:

- FBX coverage proves only the listed transform, hierarchy, and curve recipes;
  it does not cover the production FBX corpus or every wrapper/preflight path.
- Retarget coverage proves the shared bind-basis correction and reviewed bind
  fallback, not every semantic mapping heuristic or source family.
- Root coverage uses root-level and parented-pelvis synthetic controls; long
  cumulative rotations, automatic root selection, and all legacy target
  profiles remain broader gates. Cached DL1 ANM2 bulk decode is covered
  separately by a stock random-access comparison plus bounded synthetic
  selection/cancellation controls.
- Mimic coverage proves weighted many-source-to-one scalar consolidation and
  semantic output. Python and C# mimic ANM2 bytes are not claimed identical.
- FED coverage proves the two author-generated payloads, 16 rejection
  decisions, and one mapped layer recipe only. Retail breadth, model-family
  mappings, deformation goldens, and game validation remain separate.
- Event-bearing SCR coverage proves record/event/name layout, metadata, timing
  patch preservation, and the five named rejection controls. The 12-byte
  event rows stay opaque; creating or appending event payloads is unsupported.
- Exact RP6L bytes are claimed only for the named canonical writer recipe.
  Append/conflict modes, SCR generation, retail archives, compression, and
  live-game acceptance require separate evidence.
- Renderer output, retail-model deformation, and live DL1 behavior are not
  covered by these fixtures.

## Current C# validation

The Hermetic and Release C# tiers exercise the checked-in ANM2, semantic, FED, and AnimationScr fixtures without launching Python:

```powershell
.\tools\validate_csharp.ps1 -Tier Hermetic -Configuration Release
```

Fixture changes are ordinary reviewed source changes. The former live generators and refresh wrappers are no longer part of this repository.