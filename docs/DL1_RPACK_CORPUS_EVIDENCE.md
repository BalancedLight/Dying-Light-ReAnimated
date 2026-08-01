# DL1 RP6L corpus and bounded-index evidence

This note records the local Windows DL1 corpus used to harden the C# RP6L
reader. It contains counts and structural observations only; no retail bytes
are checked into the repository or shipped in a package.

## Installed corpus result

On the configured Steam installation available on 2026-07-29:

- all 62 base/DLC packs selected by `Dl1RetailProviderSet` opened;
- 34,587 RP6L assets were cataloged;
- 34,600 total retail assets, including supported virtual FED/PAK entries,
  were published to SQLite;
- 843 logical precedence conflicts were retained with every physical provider
  identity; and
- a broader recursive diagnostic scan opened 97/97 `.rpack` files and parsed
  60,576 resource descriptors with no failures.

Catalog enumeration reads only bounded header/name/resource tables. Logical
chunk payloads are not inflated while indexing. The optional
`RpackInstalledCorpusTests` control repeats the configured-pack descriptor pass
when a complete Steam install is available.

## Type-272 decode and presentation acceptance

The opt-in acceptance command is:

```powershell
.\tools\validate_dl1_mesh_corpus.ps1 -Configuration Release
```

It writes an atomic schema-v2, per-pack and per-resource report to
`artifacts/validation/dl1-mesh-corpus-1.55.json`. Ordinary test runs do not
inflate the retail corpus. The acceptance command disables the in-memory
RP6L cache, uses the streaming disk cache with a 16 GiB LRU cap, processes
one resource at a time, checkpoints after every pack, and records a local
failure without aborting later packs. The validator itself also caps pack,
resource, and per-resource issue counts.

The Assets-layer validator exposes an optional cancellable presentation
callback over one decoded resource at a time. It has no dependency on App or
Renderer. The installed acceptance test supplies that higher-layer callback,
converts every geometry-bearing resource through `Dl1MeshPreviewAdapter`, and
requires every published draw to pass `RenderMeshValidation`, including its
complete skeleton and bounded per-draw skin-palette mapping. Callback
exceptions and invalid results become resource-local `DL1PRESENT` diagnostics;
metadata-only containers never invoke the callback. Presentation counts and
diagnostics are retained in every atomic pack checkpoint.

On the validated 1.55 install, the final Release corpus run on
2026-07-30 reported:

- 62/62 configured base/DLC packs opened;
- 8,738/8,738 type-272 resources were visited;
- 8,736 resources decoded geometry, covering 25,021,323 vertices and
  84,545,982 indices;
- all 8,736 geometry resources reached and passed the presentation callback:
  8,714 published 73,335 valid render draws, including 6,805 skinned draws,
  while the largest published skeleton contained 502 rows;
- 22 exact resources are `nonDisplayGeometry` results. `shadowcaster` at
  `common_meshes_PC.rpack` resource 3,568 uses only the validated
  `shadow_caster.mat` family. The other 21 use only material records whose
  resolved retail technique count is zero, including the exact stock
  `null.mat` and `default.mat` identities. The installed acceptance test locks
  every provider, resource index, and resource name; any other zero-draw
  geometry remains a blocker;
- `scaffolding_system_e` and `weapon_fists` in
  `common_meshes_PC.rpack` were the only two resources classified as the
  legitimate three-item metadata-only layout, and neither entered the
  presentation callback;
- all geometry resources passed the bounded container/layout, triangle
  topology, contiguous per-entity LOD, material-slot, palette-range, and
  morph-binding checks far enough to produce a local result, then passed the
  adapter/render-contract gate or one of the 22 exact non-display
  classifications;
- 2,665 of 2,674 declared morph channels decoded bound SHORT4 vertex deltas;
  the remaining nine are explicitly reported as channel-only inventory on
  Hellraid's `warrior`, not silently treated as decoded deltas; and
- zero resources were blocked and the error-code maps were empty;
- warning-only classification included 44 raw-matrix rig previews, four
  content-fingerprinted raw-GPU UV anomalies, and the bounded survivor
  palette inventory described below; and
- peak process working set was 261,701,632 bytes (approximately 249.6 MiB).
  The warm-cache scan took 31.1 seconds at approximately 281.9 meshes/second;
  throughput is diagnostic, not a release requirement.

The 2026-07-30 hierarchy-policy delta reduced the blocked set from 239 to
176 resources before the independent skin/UV/layout investigations were
applied. Engine-backed hierarchy, skin-declaration, index-tail, shadow,
static-reference, and exact stock-UV policies then reduced the final blocked
set to zero without replacing decoded retail bytes. The counts below are
per diagnostic and overlap; the JSON report contains the exact provider,
resource index/name, surface, and first offending vertex:

- all 15 duplicate-name promotion failures now retain their original names,
  hashes, and topological indexes. A name-only lookup deliberately returns
  no result when it is ambiguous; callers may request the complete index
  set instead. No `hook` row is silently selected or renamed;
- 48 finite, helper-only, unskinned resources with non-TRS rows are
  classified as exact raw-matrix preview only. They remain ineligible for
  animation evaluation, retargeting, bone editing, or export;
- the remaining 44 bone-bearing resources contain 48 non-TRS rows. An
  installed 1.55 scan found zero effective-weight references, zero
  declared-palette-only references, and zero unresolved bindings for those
  rows: all 48 are outside every decoded palette. They therefore qualify
  for the same raw bind-pose matrix preview, while `Rig` remains null and
  every authoring operation stays disabled. Representative rows are
  `oh_box1`, `patch_elem_arm_left_a`, and five buggy mesh elements including
  `basic_damper_rl` and `steering_wheel`;
- 106 resources contain referenced vertices on an explicitly
  `SkinnedMesh` entity whose decoded four-byte weights are all zero. These
  are not rigid/static false positives: rigid `Mesh` entities are excluded,
  and the affected declarations contain both blend-weight and blend-index
  streams. Representative controls include Hellraid `bones_head`, DLC17
  `dlc_chest_loot_big_anm`, base `survivor_b`, `player_1_tpp`, and
  `wn_grenade_a_hq`;
- the bounded JSON report retained 62 single-palette/no-blend warnings across
  `survivor_b` and `survivor_woman_b`, but that is not the physical count:
  those large resources reached the 128-issue reporting cap. A focused
  exact-fingerprint decode found 133 such submeshes (85 and 48 respectively);
  all use the runtime-proven non-skinned entity/world path described below;
- four composite resources contain serialized palettes but no blend streams.
  The focused audit found 606 affected submeshes: 429 in `survivor_b`, 175
  in `survivor_woman_b`, and one each in `survivor_dr_zaebo_a` and
  `zere_cin`. Of these, 133 have one palette entry, 473 have multiple
  entries, 66 use exact `shadow_caster.mat`, and 540 are visible-material
  parts. Every affected hierarchy-element world matrix was finite. The
  palette is preserved as raw metadata but ignored by the runtime declaration
  path; visible parts publish as non-skinned entity/world geometry, exact
  shadow parts remain omitted, and neither becomes authorable skinning.
  Skin/variant visibility was not inferred;
- 23 resources contain referenced, not merely spare, vertices with raw
  IEEE-half non-finite UV0 values. In 19 resources every affected vertex is
  referenced exclusively by an exact `shadow_caster.mat`,
  `shadowcaster.mat`, or `shadow_caster_2s.mat` draw part. Those parts are
  omitted from visible preview and reported without changing the decoded UV
  values. The remaining four are exact stock 1.55 raw-GPU anomalies on
  ordinary textured draw parts:
  `furniture_weapon_rack_a` (`furniture_bookshelf_a.mat`), `ot_glass_a`
  (`ot_glass_a.mat`), `slums_cs_terrain_horizon_a`
  (`horizon_town_constructions.mat`), and `slums_noise_barrier_destro_b`
  (`slums_noise_barrier_a.mat`). Their raw values are preserved and published
  under a content-fingerprinted warning; the neutral preview is explicitly
  fidelity-limited because it does not emulate those retail material
  techniques;
- two survivor resources contain an out-of-range LOD-1 `sc_head` index; and
- two loft resources contain a finite static local transform and bounds but
  non-finite opaque secondary reference rows. Their raw rows are retained as
  warnings only for a plain static root `Mesh`; the same condition on a
  bone, helper, skinned mesh, non-root mesh, or local transform remains an
  error.

This distinction follows the runtime's data model rather than a tolerance
waiver. The named macOS decompile's `CCompactMesh::GetNode`,
`GetNodeType`, and `GetNodeParentIndex` access compact rows by integer index
around lines 2917944-2918138. The Windows decompile copies all twelve
`mtx34` floats when preserving element-local transforms around lines
161314-161327 and restores the complete matrix around line 161402. Raw
preview can therefore consume the finite matrix directly. The authoring
domain cannot losslessly turn genuine shear or a zero axis into local TRS,
so it does not synthesize a `BoneDefinition`.

`DL1CORPUS099` is a bounded-report sentinel, not a proprietary-layout
failure. It appears once on `wasteland_final_PC.rpack` resource
`survivor_b`, where the report retained the configured 128 detailed issues
and noted that one additional issue was omitted. It is a warning and does
not independently block the resource.

The named runtime was sampled before classifying these exceptions. In
`engine_x64_rwdi.dll.c` around 1547680-1547710, the retail optimizer can
remove whole blend streams and reports `optimized out:
BlendWeights/Indices`; around 1559940-1559970, feature bit `0x200` is named
`SKINNING_ONE_BONE`. The independently named macOS decompile maps the same
bit to the same literal around 2953490-2953570.

Installed shader evidence bounds a different one-bone behavior. The file
`preview_ground_dark_gray_mat_11.vs_src` under the installed DevTools
`opengl_shaders_dump` directory declares a skinning index but no weight at
lines 594-640, selects one palette transform, and directly applies it to
position and normal. It supports an index-only declaration; it does not
support the former inference that a declaration with neither
`BlendIndices` nor `BlendWeights` should synthesize palette index zero.

The named macOS runtime resolves the neither-stream case directly. In
`CCompactMeshEntity::CreateSurface` around lines 2918469-2918730, the
surface skinning feature bit is set only when declaration semantic
`BlendIndices` channel zero is present. In `CMesh::DoSetupRendering` around
lines 1005262-1005445, `CCompactMesh::GetNodeBonesPtr` and palette upload are
inside that feature-bit branch. `CHierarchyElement::GetWorldMatrix` and
`GetWorldMatrixPrevFrame` are submitted after the branch on every path.
Therefore a declaration without `BlendIndices` ignores its serialized
palette regardless of palette cardinality and draws with the hierarchy
element's world transform.

`StaticEntityTransformIgnoredPalette` encodes that boundary. Classification
requires a skinned-mesh entity, a nonempty serialized palette, neither blend
stream, and a finite world matrix reconstructed from the validated compact
hierarchy. It does not depend on root status or the opaque entity fields at
offsets `0x90`/`0x98`, because the no-skin branch never reads the palette.
Mixed one-stream declarations, non-finite/unreconstructible transforms, and
all other ambiguous layouts remain blocked. Preview publishes the part with
`IsSkinned == false`, no inverse binds, and the reconstructed entity/world
matrix; decoded palette and vertex bytes remain unchanged.

`InstalledNoBlendDeclarationsUseFiniteEntityWorldPath` locks the four retail
resources and the exact Windows 1.55 build fingerprint. Synthetic controls
lock finite parented transforms, ignored opaque pointer values, partial
stream rejection, non-finite world rejection, and CPU preview independence
from animated skeleton matrices. It makes no skin/variant visibility claim.

The installed DevTools shader dump also makes the shadow-only UV exception
bounded. Compiled variants named `shadow_caster_mat_*`,
`shadowcaster_mat_*`, and `shadow_caster_2s_mat_*` declare position plus
only the applicable instance/skin inputs in `main`; none declares or reads a
UV input. The material builder log independently lists the three exact
material names as shadow templates. Corpus validation therefore permits raw
non-finite UV0 only when every affected referenced vertex is exclusive to
one of those exact omitted draw parts. Similar names, paths, mixed
shadow/visible vertex use, and ordinary materials remain errors unless the
complete geometry matches one of the four exact stock controls below.

The four ordinary controls are not a name- or material-wide exception. The
decoder fingerprints the raw item 0 metadata, item 1 variant data, item 3
vertex bytes, and item 4 index bytes in fixed order, with each item prefixed by
its little-endian signed 64-bit length:

| Resource | Length-delimited geometry SHA-256 | Referenced bad vertices | Raw half infinity components |
| --- | --- | ---: | --- |
| `furniture_weapon_rack_a` | `9704fc19b87038046287a11dde300a48c40d565e38df2644accdd573502c6456` | 56 | 51 `+Inf`, 32 `-Inf` |
| `ot_glass_a` | `dc630a7f9425b5a7680682bbb8f49826eec3f2a51c94e4f33ede92100d9fb38d` | 16 | 16 `+Inf`, 16 `-Inf` |
| `slums_cs_terrain_horizon_a` | `030cccd52ecf3f59c54a696c6c0aa84aaf374ef48589656e40af7bc733b9d8fb` | 8 | 0 `+Inf`, 8 `-Inf` |
| `slums_noise_barrier_destro_b` | `bc728cfecfea850aa630fe14b5c8ad4e06cef0810f0d6b269ce17ffd8bd3ee55` | 6 | 6 `+Inf`, 0 `-Inf` |

All 70 affected triangles across those four controls are non-degenerate.
Finite siblings use the same decoded declarations, including the same
ordinary material for the barrier and horizon controls, which rules out a
global stride/semantic correction. The policy additionally locks the exact
resource and surface names, declaration layout, material ownership, bad
vertex count, and infinity-sign counts. A fingerprint mismatch, similar name,
different material owner, NaN, non-finite attribute other than UV0, or changed
pattern remains `DL1CORPUS028`.

The named DL1 runtime supplies the raw-GPU boundary. In the named macOS
decompile, `CRLRPlatformPC::RegisterBuffers` around lines
2739158-2739195 passes the loaded logical type-240/type-241 resources directly
to `RendererAPI::RCreateVertexBufferResource` and
`RCreateIndexBufferResource`. `CCompactMeshEntity::CreateSurface` around
2918469-2918730 constructs the GPU declaration from serialized declaration
rows while reusing that vertex-buffer resource; it does not walk, clamp, or
replace vertex values. Thus the stock `0x7C00`/`0xFC00` Half2 components
reach the retail GPU unchanged.

`DL1MESH017` makes the fidelity downgrade visible on normal mesh load, and
`DL1CORPUS035` records it in corpus validation. The preview adapter likewise
copies the decoded float infinities unchanged into its `R32G32_FLOAT` vertex
buffer. A WARP stability control exercises a textured triangle with raw
positive and negative infinity UVs and checks for no validation rejection or
device removal; it deliberately makes no material-correct pixel claim.

The exact `shadow_caster.mat` missing-stream exception still does not
synthesize weights: it retains the palette and omits the part from visible
preview. Ordinary visible no-`BlendIndices` parts use the same proven
palette-ignored entity/world branch. `DL1CORPUS055` remains reserved for a
missing-stream case whose finite entity/world path cannot be established;
`DL1CORPUS056` continues to block declarations containing only one of the
two expected blend streams.

For the two loft resources, `CCompactMeshEntity::GetWorldXform` in the named
macOS decompile copies/multiplies the first local 3x4 row for static world
placement; it does not read the second serialized 3x4 row. The exception is
therefore restricted to a plain static root with finite local transform and
bounds. The non-finite secondary bytes are never replaced with identity.

## Unsigned retail sizes

Several legitimate retail rows use the full unsigned 32-bit range. Examples
observed in the configured corpus include logical chunk sizes above 2 GiB in
`common_cod_2_PC.rpack`, `common_textures_0_pc.rpack`,
`common_textures_1_pc.rpack`, `common_textures_2_pc.rpack`, and
`weapons_PC.rpack`. Item offsets also exceed signed `Int32` in multiple packs.

The serialized fields remain `uint32`, while the C# domain uses `long` for
offsets and logical/stored sizes. Materialization still requires bounded,
checked buffer sizes and streams decompression to an atomic cache file. The
per-item limit remains independent of the logical chunk limit.

## `static_load_PC.rpack` tail layout

The installed `static_load_PC.rpack` has:

- file length 7,341,837 bytes;
- table end at byte 613;
- chunk 0 serialized with offset 0 and size 7,340,032;
- explicit chunks ending at byte 1,805; and
- `file length - chunk 0 size == 1,805`.

Chunk-0 item offsets begin at 1,805 and are archive-relative, whereas item
offsets in the explicit chunks are chunk-relative. The parser therefore
normalizes the implicit tail's physical offset to 1,805 and subtracts an
`ItemOffsetBias` of 1,805 when exposing logical item offsets.

This matches the named runtime evidence in
`E:\Debugging\DyingLightDebug\libengine.dylib.NAMED.c`:

- approximately 2715473-2715499 reads the fixed 20-byte chunk rows, 16-byte
  item rows, 12-byte resource rows, and name offsets;
- approximately 2715625-2715627 preserves chunk offset, logical size, and
  packed size as 32-bit fields; and
- approximately 2718514-2718516 forms the read location from the unsigned item
  offset plus the stored chunk offset and rejects carry.

The synthetic `ResolvesZeroOffsetChunkAsBoundedTailPayload` regression locks
the normalization and item-bias rule without redistributing the retail file.

## Additional roots and local failures

Project-configured additional RPack roots are top-level, explicit inputs. The
first configured root has the highest user-root precedence, and all user roots
override base/DLC priorities. Missing, unsupported, unreadable, or duplicate
roots produce diagnostics without removing the base catalog.

A malformed optional pack fails locally and is recorded in
`RpackAssetProvider.SourceErrors`; it does not abort enumeration of the other
packs. The CLI accepts repeated `--rpack-root <path>` options and reports both
root diagnostics and per-pack errors in its JSON result.

## Cache integrity

Inflated disk chunks are paired with a SHA-256 sidecar written through a unique
temporary path. A cache entry from another process or session is accepted only
when its length and content hash match. Same-length corruption removes the
entry and regenerates it from the immutable source pack. LRU eviction removes
the data and integrity sidecar together, and failed or cancelled inflation
leaves neither a publishable chunk nor a temporary hash file.

`RpackChunkCacheIntegrityTests` covers same-length corruption, regeneration,
paired LRU eviction, and failed-inflation cleanup with bounded generated
archives.

This evidence proves safe descriptor access and gives a complete local
classification for the named type-272 corpus. It does not claim
material-correct pixels for every resource, reconstruct every proprietary
shader/runtime fallback, or prove that generated animation libraries are
accepted by the live game.
