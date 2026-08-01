# DL1 retail material and texture preview evidence

This note records the bounded DL1-only seam from compact-mesh material names
to retail texture identities and a neutral D3D11 base-color preview. It
contains no retail payload bytes and does not reconstruct Techland's material
techniques or shaders. Draw suppression is limited to evidenced zero-technique
material records and the separately evidenced shadow-caster identities.

## Runtime evidence

The Windows filesystem decompile establishes the `ABDM` container shape:

- `CFileHashMultiContainer::Open` reads a 16-byte header, seeks to its
  container-table offset, reads 48 bytes per container, then reads 16 bytes
  per entry from the offset at container-row `+40`
  (`filesystem_x64_rwdi.dll.c`, 19069-19220).
- `SaveContainerHashes` writes the four 32-bit entry fields in order
  (`filesystem_x64_rwdi.dll.c`, 19442-19491).
- `LoadEntry` seeks to entry `+4` and reads entry `+12` bytes
  (`filesystem_x64_rwdi.dll.c`, 19545-19587).

The named engine decompile establishes lookup and material contents:

- `CMaterialsPack::Load` opens the main and optional `_refs` packs with magic
  `1296319041` (`0x4D444241`) and loads their named containers
  (`libengine.dylib.NAMED.c`, 2958678-2958836).
- `CMaterialMgr::LoadMaterial` strips to the filename, ASCII-lowercases it,
  and calls zlib `crc32(0x811C9DC5, filename)` before pack lookup
  (`libengine.dylib.NAMED.c`, 2952339-2952434).
- `CMaterialsPack::LoadMaterial(const char*, int)` repeats the same seeded
  filename hash, while the hash overload binary-searches 16-byte inventory
  rows (`libengine.dylib.NAMED.c`, 2961963-2962045).
- `CMaterial::CMaterial` reads the technique count at `+16`, texture count at
  `+18`, and a relative texture-table pointer at `+22`. Each texture row is 12
  bytes: sampler state/hash at `+0`, texture filename hash at `+4`, and load
  flags at `+8` (`libengine.dylib.NAMED.c`, 2951061-2951185).
- The Windows 1.55 engine decompile independently copies the material-record
  technique count at `+16` into the runtime count byte at `+48`, initializes
  the technique-1 fallback index at `+50` to `0xFF`, and can replace that
  sentinel only while iterating a nonempty technique table
  (`engine_x64_rwdi.dll.c`, `sub_1808040C0`, 1560143-1560200).
- The ordinary material technique selector returns null when that runtime
  count is zero. Its technique-1 fallback also returns null while the `+50`
  sentinel remains `0xFF`
  (`engine_x64_rwdi.dll.c`, `sub_180816A30`, 1576574-1576605).
  A zero-technique retail material therefore supplies no render pass; drawing
  its geometry with a purple diagnostic tint would invent visible content.
- `CRLRPlatformPC::LoadTexture` obtains logical subresources 33 and 34, reads
  width/height/depth/array from the first four `ushort` values, masks the mip
  count with `0x7FFF`, and reads the format at byte 12
  (`libengine.dylib.NAMED.c`, 2739183-2739290).
- `IL_IsCompressedFormat` and `IL_FormatStr` identify serialized formats 17,
  18, and 19 as DXT1, DXT3, and DXT5
  (`imagelib_x64_rwdi.dll.c`, 11341-11409 and 12560-12615).

These locations are semantic evidence. Decompiler variable names that are not
confirmed by access patterns are not promoted into public contracts.

## Implemented fail-closed seam

`Dl1MaterialPackReader` validates the exact magic, bounded table counts,
offsets, file extents, sorted unique hashes, logical/stored sizes, material
hash, and texture-table range. It buffers only bounded tables and the selected
small material payload, never the complete material pack.

`Dl1MaterialTextureResolver`:

- hashes normalized retail filenames with the evidenced seeded zlib CRC32;
- resolves texture hashes only when one distinct type-8480 catalog name owns
  the hash, retaining the catalog winner's full `RetailAssetId`;
- classifies terminal and underscore-delimited retail texture tokens, so
  variant-qualified names such as `brecken_tshirt_dif_wing` retain their
  evidenced diffuse/base-color role without treating arbitrary substrings as
  semantics;
- preserves sampler state and load flags without pretending to interpret
  them;
- keeps absent names, distinct-name hash collisions, changed source
  snapshots, malformed rows, and unsupported layouts as local `DL1MAT`
  diagnostics; and
- shares decoded previews and enforces a 128 MiB per-mesh preview budget.

`Dl1PreviewMaterialPolicy` keeps draw suppression evidence-bounded:

- a resolved material record with zero techniques is omitted;
- before resolution, only exact case-insensitive `null.mat` and `default.mat`
  active identities receive the known zero-technique treatment;
- a declared `null.mat` or `default.mat` fallback is considered only when no
  skin replacement owns the active identity;
- when a resolved record exists, its technique count is authoritative; and
- no substring, path, surface-name, or approximate zero-technique rule is
  applied. The existing exact shadow-caster allowlist remains separate.

`Dl1TexturePreviewDecoder` accepts only the evidenced three-item PC layout,
one 2D image, bounded dimensions/mips, and formats 17-19. It opens the mip item
as a stream and reads only the calculated base mip. The renderer maps those
formats to `BC1_UNorm`, `BC2_UNorm`, or `BC3_UNorm`, uploads one immutable mip,
and samples it with neutral wrap/linear preview lighting. The mesh constants
are explicitly bound to both vertex and pixel stages; a WARP regression proves
that a synthetic red BC1 block reaches the pixel shader.

## Installed 1.55 control

The read-only installed control uses the build recorded in
`DL1_BUILD_FINGERPRINT.md` (Windows file version `1.55.0.0`, composite
fingerprint
`89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13`).
It does not launch the game.

For `armored` from `common_meshes_PC.rpack`, eight of nine named slots resolve
through `optimized_dx11.mp`. `eyes_def.mat` is absent and deliberately remains
unresolved. The `armored_torso.mat` row resolves all six texture identities:

1. `blood_a_grd`
2. `viral_head_blood_a`
3. `armored_torso_clr`
4. `armored_torso_nrm`
5. `armored_torso_skin_msk`
6. `armored_torso_spc`

The chosen base color is the actual `armored_torso_clr` type-8480 resource:
2048 by 2048, 12 mips, DXT1/BC1, with a 2,097,152-byte base mip. The WPF
adapter passes the same bounded data and retail identity to the renderer.

The stock-editor comparison controls additionally lock `player_11_fpp` and
`player_11_tpp`. Their `player_11_tshirt.mat` row resolves the exact
`brecken_tshirt_dif_wing` type-8480 texture. The `_dif_` token is
delimiter-bounded and therefore classified as base color; both FPP and TPP
previews now receive that shirt texture. Their legs also resolve
`player_legs_a_dif`, but that texture alone is not the stock visible color:
`player_legs_a.mat` uses the exact palette technique recorded below. The
neutral one-texture renderer deliberately does not replace that technique with
a guessed tint. No surface-name texture guess is made.

## Exact `player_legs_a` palette evidence

This section records a decoded technique prerequisite, not implemented preview
behavior. It was derived read-only from the installed 1.55 controls and
`optimized_dx11.mp`; no retail payload is copied into the project.

The `Default` compact-mesh skins for `player_11_fpp` and `player_11_tpp` both
have feature bits `0x0481` and select first mesh-color alpha byte `15`.
`CMesh::skinApplyColorPreset` handles the partial `0x500` feature case by
preserving the other color components and converting that byte to
`15 / 255` before storing mesh color 0
(`libengine.dylib.NAMED.c`, 1008682-1008799). `CMesh::GetMeshColor` exposes
the resulting 16-byte color row (`libengine.dylib.NAMED.c`,
1008890-1008905). The installed `brecken_cin` control selects the same alpha
byte, so this is shared stock data rather than a player-only visual guess.

`player_legs_a.mat` selects template `0xFE8B2336`. Its first technique selects
high-level pixel shader `0x1C51C425` / DXBC shader `0xB4A4680F` and
high-level vertex shader `0x4EE26753` / DXBC shader `0xA1F208B1`. The template
target-112 constant expression is:

```text
CONST_PALETTE = { 0.9375, 0.03125, 7.96875, 0.015625 }
```

The exact first-technique shader path binds, in order, the color palette,
diffuse, mask, normal, specular, wind-weave, and wind-mask textures. The
palette is the installed `player_legs_a_colors` 16-by-32 format-2 resource;
the current bounded preview decoder does not accept that two-item layout.
Disassembly establishes these palette coordinates:

```text
paletteU = specular.a * 0.9375 + 0.03125
paletteV = frac(meshColor0.a) * 7.96875 * 1.0039215686 + 0.015625
```

Thus the stock alpha selector `15 / 255` addresses palette row approximately
`15.06`, not a freehand material tint. The pixel shader then tests
`diffuse.a > 0.05882353`. On the recolorable branch it writes
`palette * mask.rgb` to render target 0 and zero to render target 1. On the
other branch it writes the gamma-adjusted grayscale mask to target 0 and
`palette * diffuse.rgb` to target 1. This two-target split is material
behavior; summing it or collapsing it into the current one-target neutral
shader has not been evidenced.

A correct implementation therefore requires all of the following to land
together:

- retain and decode the diffuse, mask, specular, and format-2 palette
  bindings, rather than only the first BC base-color preview;
- retain evaluated compact-skin mesh color 0 on the draw;
- implement the exact palette coordinates, alpha threshold, and branch; and
- preserve the two-render-target meaning through the DL1 profile renderer, or
  first prove an equivalent final-color reconstruction from the downstream
  1.55 resolve pass.

Until that seam exists, the light `_dif`-only pants are a known Raw-preview
limitation. Applying the dark stock-editor appearance as a tint would hide the
missing material contract and is intentionally rejected.

Direct bounded inspection of the installed material pack records finds
`null.mat` and `default.mat` as 24-byte rows with zero techniques and zero
textures. `shadow_caster.mat` is a useful contrast: it has one technique and
zero textures, so it remains governed by the separate exact shadow policy.

The exact player controls use `null.mat` on the FPP left-hand decal and on
inner head/watch parts. Those parts now publish no preview draw. The installed
door provides an independent non-character control:
`anim_slums_door_a/metal_door_a/lod0/part0` is a 740-triangle skin replacement
whose active identity is `null.mat` (declared `METAL_DOOR_BB.MAT`), and part 1
is a four-triangle `shadow_caster.mat` draw. They are omitted. Parts 2 and 3
remain visible with `metal_door_b.mat` and `metal_door_a.mat`, respectively,
for 396 slab/handle triangles. The installed WARP control renders that
remaining door geometry and the exact player controls without device or
renderer diagnostics.

## Unresolved `beard.mat` alpha technique

The exact Windows 1.55 `player_1_tpp` mesh assigns `beard.mat` to the
3,068-vertex beard shell. The seeded runtime hash for that name is
`0x13259018`, but `optimized_dx11.mp` contains no material row with that hash.
The installed catalog does contain a `player_beard` type-8480 texture in
`common_cod_1_PC.rpack`, but no decoded retail record binds that candidate to
`beard.mat`. That texture has a two-item layout, while the current bounded
preview decoder accepts only the evidenced three-item PC DXT1/3/5 layout, so
its format and alpha channel are not claimed. The ordinary filename classifier
correctly leaves `player_beard` unknown for `beard.mat`; treating a shared
`player_` prefix as material semantics would be a guess.

The current neutral renderer is an opaque pass. It neither alpha-tests nor
blends the sampled base-color alpha, and no exact beard threshold/technique
has been decoded. Consequently the editor retains the unresolved diagnostic
tint instead of assigning `player_beard`, hiding the shell, or inventing an
opacity rule. The pale opaque beard seen in the local WARP comparison is a
known material-fidelity blocker for accurate facial preview, not evidence of
reversed normals.

## Fidelity boundary

This is a useful Raw/profile preview of decoded identity, UVs, BC base color,
skinning, and morphs. Apart from the bounded no-pass decision above, it does
not reconstruct template techniques, material parameters, alternate runtime
variants, authored sampler semantics, sRGB policy, normal/specular/mask
composition, exact lighting, exact shaders, or post-processing. Unknowns use
diagnostics or the existing slot tint; they are never guessed. Broader
base/DLC material corpus coverage remains a release gate.
