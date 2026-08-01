# DL1 Retail `SC_head` Index-Count Correction

## Result

The `SC_head` LOD 1 failure in `survivor_woman_a` and
`survivor_woman_b` is a known retail mesh-compiler defect with an exact engine
workaround. It is not an index-buffer base-offset error and it is not evidence
for a general invalid-triangle trimming rule.

DL ReAnimated mirrors only the engine's exact predicate:

- retail resource `survivor_woman_a` or `survivor_woman_b`;
- entity name `SC_head`, compared case-insensitively;
- LOD 1;
- one submesh, submesh 0;
- serialized index count 1,368.

The effective decoded count is 1,365. The raw metadata and index-item bytes
remain unchanged, and diagnostic `CMESHG014` records the correction.

## Installed Windows 1.55 evidence

The evidence test is gated to build fingerprint
`89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13`.

| Resource | Provider and row | Metadata SHA-256 | Index-item SHA-256 |
| --- | --- | --- | --- |
| `survivor_woman_a` | `common_meshes_PC.rpack`, resource 4,357 | `2afc8b378edd2c62030f0a6c2b7325ca83cb8b232c1c13f66bf4b2b7cc23d086` | `f826fb80a543a173132d36d09400fd4751df93212fa80f5023a9b5357e89bc99` |
| `survivor_woman_b` | `DW_DLC17/Data/wasteland_PC.rpack`, resource 867 | `6d534647a180168583007e1103e69ffdcbd4e10464199c4625081522d134b5e1` | `4d49841598af5dfca901fe51b1e4c35298395f2dcb001e241c3942b85242ad05` |

Both resources serialize the same relevant layout:

- `SC_head` LOD 0: 500 vertices, index offset 0, count 2,295. Its
  4,590-byte stream is followed by two bytes before the 16-byte-aligned LOD 1
  offset at 4,592.
- `SC_head` LOD 1: 355 vertices, vertex byte offset 20,000 with stride 40,
  index offset 4,592, serialized count 1,368.
- Indices 0 through 1,364 are all within the 355-vertex local surface.
- The valid 1,365-index prefix ends at byte 7,322.
- The next surface, `sc_legs_a` LOD 0, begins at byte 7,328.
- `Align16(7,322)` is 7,328, so the intervening six bytes are exactly the
  alignment span.
- Those six bytes differ between the two packs:
  `31 5f 66 72 5f 44` and `30 35 5f 66 72 5f`. Interpreting them as three
  little-endian 16-bit indices produces out-of-range values in both resources.

The differing bytes and exact following-surface boundary rule out a shared
valid triangle. They also show why content-based truncation would be the wrong
contract: the stable fact is the engine's named workaround, not a general
sentinel value.

## Engine evidence

The Windows decompile at
`E:\Debugging\DyingLightDebug\windows - no names\Dev Tools RE\DLE\engine_x64_rwdi.dll.c`
around lines 1,522,687 through 1,522,711 performs the following operation in
the compact-mesh creation path:

1. Match `survivor_woman_a.msh` or `survivor_woman_b.msh`.
2. Find entity `SC_head`.
3. Retrieve LOD 1.
4. If its surface index count is 1,368, subtract 3.

The named engine decompile at
`E:\Debugging\DyingLightDebug\libengine.dylib.NAMED.c` around lines 2,917,644
through 2,917,667 independently contains the same
`CCompactMesh::Create` workaround and assigns the effective count 1,365.

This cross-build behavior is why DL ReAnimated applies an identity-scoped
runtime correction rather than classifying the retail resources as malformed.

## Regression coverage

`InstalledDl1TrailingTriangleEvidenceTests` covers:

- both exact resource identities and case-insensitive matching;
- the 1,368-to-1,365 correction and informational diagnostic;
- negative controls for another resource, another entity, and another
  serialized count;
- propagation of the RP6L resource identity through the public DL1 asset
  decoder;
- exact installed resource hashes, LOD offsets/counts, valid-prefix bounds,
  alignment bytes, following-surface boundary, and corrected decode.

