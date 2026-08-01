# DL1 retail rig profiles and filters

This seam turns a decoded type-272 retail mesh plus its physical
`RetailAssetId` into filterable, non-proprietary metadata. It is DL1-only.
Classification never changes a rig, invents a runtime correction, or stores
retail payload bytes.

## Contract

`Dl1RetailMeshClassificationService` produces a
`Dl1RetailMeshProfile` with:

- geometry kind: `Static`, `Skinned`, `MetadataContainer`, or `Unknown`;
- the complete `dlra-rig-signature-v1` signature when a valid dynamic
  `RigDefinition` exists;
- bounded family and confidence;
- FPP/TPP perspective only when the resource name has an explicit token;
- facial support distinguished as unknown, none, named channels, or decoded
  position deltas;
- provider, physical pack name, base/DLC/user-added scope, and DLC identifier;
- distinct decoded variant names; and
- stable evidence rows explaining every positive classification or refusal.

`Dl1RetailMeshFilter` combines these fields conjunctively. Unknown evidence is
not treated as a negative capability. For example, a metadata-only container
does not match `FacialSupport = false`.

Confidence is offline-classification confidence, not preview fidelity:

- `Low` is a resource-name hint.
- `Medium` is a hint corroborated by decoded skin and enough rig anchors.
- `High` is a hint corroborated by decoded skin and all bounded rig anchors.
- `None` means the result deliberately remains unknown.

None of those values means **Game validated**.

## Asset-browser behavior

The WPF asset browser keeps profile work lazy. Indexing creates catalog rows
without decoding all type-272 resources on the UI thread. Selecting a mesh
decodes and caches its profile as part of the existing preview job. The
**Classify next 128** action explicitly runs a bounded, cancellable background
batch over rows matching the text, resource-type, and provider filters.
Re-indexing clears the in-memory profile cache because physical asset
fingerprints may have changed.

The expanded **Decoded mesh filters** panel exposes geometry, exact rig
signature, bounded family, explicit FPP/TPP, facial support, base/DLC/user
scope, DLC identifier, and decoded variants. Positive and negative
capabilities require a classified profile. In particular, an undecoded row
does not match either `Facial support` or `No facial support`; it appears only
when no profile filter is active or when the user explicitly chooses
`Unknown / not decoded`. Decode failures stay local to their resource, remain
unknown, and carry their bounded error in the row tooltip and diagnostics
panel. Failed rows are excluded from subsequent normal batches so one malformed
resource cannot starve later rows; there is no implicit infinite retry loop.

The result label reports the complete match count separately from the
5,000-row UI display cap. Physical selection is preserved across a catalog
row refresh when the same stable retail identity remains available.

## Conservative family policy

A name hint is promoted only when the mesh has decoded submesh skin palettes,
a valid dynamic rig, the `bip01`, `pelvis`, and `head` anchors, and at least
two of the bounded left/right upper-arm and thigh anchors. Missing evidence
returns `Unknown`; proportions alone never select a family.
Resource-token classification is capped at 4,096 characters so malformed
names cannot drive an unbounded split; geometry evidence remains available
while family and perspective fail closed.

The first-pass name hints are deliberately narrow:

| Family | Bounded resource evidence |
|---|---|
| Player | An exact `player` token; FPP/TPP remains a separate explicit-token classification |
| Generic NPC | An exact `npc` token, or installed controls `jade`, `rais`, `survivor_a`, and `survivor_woman_a` |
| Generic infected | An exact `infected` token, or `zombie_man_a`, `zombie_woman`, and `zombie_prime` |
| Volatile | Exact `volatile` or retail-spelled `voleteile` token |
| Screamer | Exact `screamer` token |
| Demolisher | Exact `demolisher` token or the installed `armored`, `armored_b`, and `armored_rock` resources |
| Goon | Exact `goon` token |

This table is classification evidence, not a model-family runtime-correction
profile. Newly discovered names stay unknown until a bounded rule and control
are added.

## Current Windows 1.55 read-only controls

`InstalledDl1RigFamilyProfileTests` opens the installed tables and mesh items
without launching the game. On the current Windows DL1 1.55 installation it
found 30 type-272 `player_*_fpp` / `player_*_tpp` rows in
`common_cod_1_PC.rpack`, including `player_1_fpp` and `player_1_tpp`.
The exact counts and signatures below are locked only when the read-only
installed-build fingerprint is
`89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13`;
a different build reports the mismatch and does not inherit the 1.55 control.

It then decoded and classified these controls from
`common_meshes_PC.rpack`:

| Resource | Family | Rig nodes | Surfaces | Skin palettes | Morphs | Variants | Rig signature prefix |
|---|---:|---:|---:|---:|---:|---:|---|
| `jade` | Generic NPC | 185 | 15 | 20 | 46 | 4 | `fe268144f805` |
| `rais` | Generic NPC | 109 | 13 | 20 | 46 | 2 | `53be5c496f50` |
| `zombie_prime` | Generic infected | 79 | 16 | 24 | 0 | 4 | `50a669d0c22a` |
| `zombie_voleteile_blue` | Volatile | 97 | 10 | 16 | 15 | 4 | `468b9a24b213` |
| `zombie_screamer` | Screamer | 80 | 4 | 10 | 0 | 2 | `dd27a9f32719` |
| `armored` | Demolisher | 77 | 19 | 22 | 15 | 2 | `dc830e05f54d` |
| `zombie_goon` | Goon | 128 | 18 | 24 | 16 | 6 | `fcddc2ef8a4f` |

The compact transform contract is locked to the engine's serialized row
order. In the named DL1 decompile,
`CCompactMeshEntity::GetWorldXform` copies the 12-float local transform and
composes `parent * local` through `mul3434_34`
(`libengine.dylib.NAMED.c`, lines 2918410–2918444 and
2886607–2886636). The decoder therefore preserves the three serialized rows
and transposes only at the `System.Numerics` row-vector boundary.

`InstalledArmoredSkeletonIntegrityTests` independently locks that convention
against the stored reference matrices. For `armored`, all 77 animation
entities reconstruct with a maximum global/reference identity error below
`8.35e-7`; all 27 named left/right pairs mirror within `0.00538` model units.
All 22 decoded skin palettes address true deform bones, while the 20 helper
entities remain unweighted. The decoded skeleton also stays inside the
16,277-vertex preview bounds. This proves coherent retail bind placement; it
does not claim that every model or animation pose is authored symmetrically.

The installed test asserts base-game provider scope, decoded skin palettes,
valid rig signatures, family confidence, and exact family counts. The
synthetic suite also locks player classification, FPP/TPP conflict handling,
static/skinned/container behavior, facial/variant filters, DLC scope,
user-added precedence, catalog/decode identity mismatch, and non-mesh
rejection.

## Installed visual-reference controls

`InstalledDl1VisualReferenceControlTests` adds read-only mesh and preview
controls for the resource labels visible in the user's local stock-editor
comparisons:

- `player_1_tpp` and `player_1_fpp` from `common_cod_1_PC.rpack`;
- `player_11_tpp` and `player_11_fpp` from `common_cod_2_PC.rpack`;
- `jade`, `armored`, `zombie_voleteile`, `zombie_screamer`, `brecken_cin`,
  and `anim_slums_door_a` from `common_meshes_PC.rpack`.

For the validated Windows 1.55 build, the control locks each resource's
physical provider/index, resource SHA-256, decoded counts, hierarchy/render
roles, material/texture availability, selected preview topology, and
normal-versus-winding coherence. Preview uses the minimum decoded LOD index
for each entity as its highest-detail decoded LOD and reports omitted LODs.
Draw parts whose exact material name is `shadow_caster.mat`,
`shadowcaster.mat`, or `shadow_caster_2s.mat` are excluded from the display
payload. Installed compiled shader variants for these three names do not
consume UV input; the policy is an exact allowlist rather than a fuzzy
name/path rule.

Zero-technique retail materials are also non-display. The Windows 1.55
material constructor and technique selector show that a zero count yields no
render pass. Resolved material records are authoritative; before resolution,
only exact `null.mat` and `default.mat` identities receive this treatment.
This removes the purple fallback decal and exact inner head/watch draws from
the player controls. On `anim_slums_door_a`, it also omits the 740-triangle
part 0 whose applied skin identity is `null.mat`; the four-triangle part 1 is
already omitted by the shadow-caster rule. Parts 2 and 3 retain the visible
door slab/handle with 396 triangles. No surface-name or fuzzy material rule is
used.

The stock-editor comparisons establish visible headed-TPP/headless-FPP pairs
for both `player_1` and `player_11`. `player_11_fpp` is already headless in its
decoded retail inventory. The `player_1_fpp` headless selection is
deliberately narrow: it applies only when the resource name is
`player_1_fpp` and its SHA-256 is
`fcadbe6419cee4e5b8065e5c14e324b2576ee9015c5a9125896efa945250525c`.
That control omits the decoded beard, hair, TPP shirt/hands, and head surfaces
while retaining the FPP shirt/hands and other selected body parts. A different
name or fingerprint does not inherit this rule. This is one validated
default-skin preview control, not a general decoder for the retail
skin/variant table.

The door control retains a small decoded prop/pivot hierarchy. Its four
palette-driving `bone_*` rows and `metal_door_a` are renderer `Prop` roles,
while the other five rows remain helpers. The Bone rows still drive skinning
and remain selectable for editing, but their ordinary hierarchy overlay is
suppressed so they do not masquerade as character deform diamonds. Visible
children also do not draw links to hidden parents. The white character
diamonds and the yellow selected-door wireframe visible in the stock editor
are bone/pivot overlay shapes, not decoded mesh geometry. The local
screenshots are comparison evidence only; they are not copied into the
repository, used as test inputs, embedded in a release, redistributed, or
claimed as pixel goldens.

## Known open controls

The current decoder can read substantial geometry and morph inventories from
the composite `survivor_a`, `survivor_woman_a`, `zombie_man_a`,
`zombie_woman`, and `zombie_voleteile` resources, but their current
`RigDefinition` promotion fails on singular or sheared local transforms.
`survivor_woman_a` also reports one unexplained LOD index range. Those assets
therefore remain `Unknown`; this classifier does not suppress the decoder
errors or fabricate a rig signature.

The 30 player rows are table-verified here. Full installed `player_1_tpp`
geometry/morph decoding is covered by the separate installed morph and
authoring regressions, and the installed visual-reference control now locks
the selected `player_1_fpp` geometry/camera roles for the exact fingerprint
above. General skin/variant-table membership, a broader DLC family corpus,
pixel goldens against the local stock-editor references, game-captured family
hierarchy comparison, live-game proof, and universal family recognition remain
release-open work. Base-color preview is not exact shader parity; normal,
specular, mask, technique, and post-process behavior also remain open.

Run the focused controls with:

```powershell
dotnet test tests\ReAnimated.Tests\ReAnimated.Tests.csproj `
  -c Debug --no-restore `
  --filter "FullyQualifiedName~Dl1RetailMeshClassificationTests|FullyQualifiedName~InstalledDl1RigFamilyProfileTests|FullyQualifiedName~InstalledDl1VisualReferenceControlTests|FullyQualifiedName~AssetProfileFilterViewModelTests"
```
