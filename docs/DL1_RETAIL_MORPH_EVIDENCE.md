# DL1 retail morph and material evidence

This note records the evidence-backed retail position-delta decoder and the
material-database boundary. No retail game payload is included in the
repository or package.

## Confirmed morph layout and dequantization

The inspected compact-mesh morph row contains:

| Offset | Meaning |
| --- | --- |
| `+0` | target-major packed morph payload pointer |
| `+8` | local-to-global `ushort` channel-index table |
| `+16` | vertex count |
| `+20` | target count |
| `+24` | observed value `1`; retained as an unclaimed field |

The payload is target-major: all `vertexCount` elements for local target zero
come first, followed by the vertices for local target one. The `ushort`
mapping row at `+8` maps those local target indexes to the global morph-name
inventory at compact-mesh header `+0x20`.

`CCompactMeshEntity::Create` uploads `vertexCount * targetCount` elements and
passes the morph payload directly to the renderer. Installed
`player_1_tpp` controls independently establish an eight-byte element stride:

- `beard`: `3068 * 31 * 8 = 760864` bytes
- `player_4_head`: `2006 * 48 * 8 = 770304` bytes

In both rows the channel-index table begins exactly at the calculated payload
end. Every inspected fourth component is zero. The installed `armored`
control provides a separate 1,803-vertex, 15-channel binding.

The Windows compiler selects declaration format 7 (`SHORT4`) when a morph
component multiplied by 16384 exceeds the signed DEC4 range. Before conversion
it multiplies each source `float3` morph delta by `16384.0`, without swapping
or reflecting components. `SConvShortVec4` sign-extends each 16-bit component
back to a float. Therefore the exact model-local position delta is:

```text
delta = float3(short_x, short_y, short_z) / 16384
```

The fourth signed short is padding for this `float3` path, not a normal delta.
The decoded delta is in the same model-local basis as the compact mesh
position. The C# preview blends it before skinning. No separate normal-delta
payload is present in this row.

Evidence locations used for this pass:

- `E:\Debugging\DyingLightDebug\windows - no names\Dev Tools RE\DLE\engine_x64_rwdi.dll.c`,
  approximately 1523260-1523425 (`CCompactMeshEntity::Create`),
  1548275-1548310 (SHORT4 range selection),
  1552050-1552075 (SHORT4/DEC4 declaration selection), and
  1552628-1552695 (target-major conversion and `* 16384.0`)
- `E:\Debugging\DyingLightDebug\libengine.dylib.NAMED.c`, approximately
  2917732-2917780 (`CCompactMeshEntity::Create`) and
  2937721-2937860 (`SConvShortVec4`)

## C# implementation and fail-closed boundary

The codec now:

- bounds the payload as `vertexCount * targetCount * 8` before allocation;
- caps decoded position-delta storage at 256 MiB by default;
- requires the morph vertex count to match the decoded entity/LOD surface;
- rejects out-of-range channel mappings and unexplained nonzero SHORT4 `W`;
- decodes signed XYZ at exactly `1 / 16384` in target-major order; and
- observes cancellation between target allocations.

`Dl1MorphPayloadStatus.VertexDeltasDecoded` is published only for a mapped
target with a complete per-vertex array. The WPF preview adapter remaps those
source-vertex arrays into each rendered submesh, and the existing D3D11 morph
path blends them before GPU skinning. Malformed or unknown layouts remain
local errors rather than plausible-looking fallback data.

Synthetic controls prove positive, negative, and fractional dequantization,
allocation limits, payload bounds, nonzero-W rejection, and renderer handoff.
Read-only installed controls prove `player_1_tpp` and `armored` decode on the
current local retail installation.

The installed FED compatibility control separately locks the exact-name
boundary used by the authoring UI. All five nonempty eye/blink expressions in
`player_1_fpp.fed` resolve every row against the decoded `player_1_fpp` morph
inventory. `player_man_01_tpp.fed` uses a different control vocabulary and is
incomplete against `player_1_tpp`; the WPF application path refuses that pair
instead of silently applying only an accidental subset. This establishes
asset compatibility behavior, not a guessed cross-family mapping.

Those installed controls used the executable/build identity recorded in
`docs/DL1_BUILD_FINGERPRINT.md` (file version `1.55.0.0`, composite
fingerprint
`89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13`).
The provider cache identities were
`8C2FC288B404F3878DA07F334DD0B34549F225A30C89EE5F16981808B3BBE55B`
for `common_cod_1_PC.rpack` and
`5162EB5A3405D2DDB98709CAD98974903E370B03B3CD77C74E6136AA993F729D`
for `common_meshes_PC.rpack`.

This is asset-format evidence, not a `Game validated` result. Facial visual
goldens and captured Windows 1.55 comparisons remain required before claiming
game-validated facial fidelity.

## Confirmed material-database boundary

The compact-mesh header `+0x18` points to a material-database holder:

| Holder offset | Meaning |
| --- | --- |
| `+0` | pointer to the database-entry rows |
| `+8` | declared material-slot count (`ushort`) |
| `+10` | complete database/preload entry count (`ushort`) |

Each database row is `0x18` bytes:

| Entry offset | Meaning |
| --- | --- |
| `+0` | pointer to the NUL-terminated database name |
| `+8` | runtime material pointer, populated by the material manager |
| `+16` | raw 32-bit value forwarded with the name when loading |

`CCompactMesh::GetNumMaterialSlots` reads the holder count at `+8`.
`CCompactMesh::PreloadMaterialSlots` iterates the count at `+10`, advances by
24 bytes, and passes the row name and `+16` value to the material manager.
`CCompactMesh::GetMaterialFromDatabaseName` returns the same name and raw
value. `CopyMaterialSlotsTo` indexes the first declared-slot rows, so later
database entries are retained as database lookup inventory rather than
reported as mesh slots. No narrower purpose is inferred from the row layout.

The C# decoder now:

- bounds both counts and the complete row table before allocating;
- observes cancellation between database rows;
- validates every name pointer, UTF-8 sequence, and NUL terminator in-buffer;
- preserves the exact row index, name, and raw `+16` value;
- maps the first declared rows to `Dl1MaterialSlot` with
  `DatabaseNameDecoded`;
- reports a local error and leaves a declared slot unresolved instead of
  inventing a database name when a row is malformed.

The checked-in synthetic control covers all fields, an empty default name,
extra database rows, invalid holder/table/name pointers, and inconsistent
counts. The optional installed-retail control also decodes the `armored` mesh from
`common_meshes_PC.rpack` without unresolved declared slot names.

Material evidence locations used for this pass:

- `E:\Debugging\DyingLightDebug\libengine.dylib.NAMED.c`,
  2917813-2917866
  (`CCompactMesh::PreloadMaterialSlots`)
- `E:\Debugging\DyingLightDebug\libengine.dylib.NAMED.c`,
  2918796-2918884 and 2919127-2919130
  (slot/database accessors)
- `E:\Debugging\DyingLightDebug\windows - no names\Dev Tools RE\DLE\engine_x64_rwdi.dll.c`,
  1522207-1522262
  (`MeshMaterialDatabaseHolder::AddMaterial` evidence string and 24-byte rows)

Texture references are not embedded directly in these mesh rows. They require
resolving and decoding the named material resource. The raw `+16` field is
therefore deliberately named `RawLoadValue`, and neither it nor the runtime
pointer is treated as a texture reference. The separately evidenced,
fail-closed ABDM-to-type-8480 base-color preview seam is documented in
`DL1_RETAIL_MATERIAL_TEXTURE_EVIDENCE.md`. Full material techniques,
parameters, variants, and exact shader interpretation remain outside that
seam.
