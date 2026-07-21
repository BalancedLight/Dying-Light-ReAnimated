# DL2 ANM2 Header_Version2 and advanced player target

Status: static, read/export-path reconstruction for the supplied PC sample and
engine build. This note does not describe a native DL2 writer.

## Evidence and limits

The following labels identify evidence in the supplied
`dl2_engine_x64_rwe.dll.asm`. They are convenient anchors for this one build,
not portable addresses, ABI declarations, runtime constants, or hook targets:

| Evidence | Static anchor |
| --- | --- |
| Header_Version2 validator | `sub_18039DAF0` |
| Header_Version1 validator | `sub_18039D4A0` |
| Header_Version2 time/block mapper | `sub_18034F580` |
| Header_Version1 time/page mapper | `sub_18034F3D0` |
| Sampler-data pointer from a block dictionary | `sub_180342800` |
| Sampler-data size from a block dictionary | `sub_180342830` |
| Common sampler | `CAnm2Sampler::SampleFrame`, symbolized at `0x1803F22A0` |

The two dictionary accessors are especially direct: the first adds
`dictionary[0] << 4` to the block address; the second returns
`(dictionary[1] - dictionary[0]) << 4`. The Version2 mapper reads the header
fields at their Version2 offsets, evaluates the VFR words, applies the
previous-index rule at exact integer times, subtracts block frame spans, and
selects a 15-frame dictionary slot.

## Correct disk header

All fields are little-endian:

```c
struct Anm2HeaderV2Disk {
    uint32 magic;                       // +0x00 "ANM2"
    uint16 signature;                   // +0x04 42
    uint16 header_version;              // +0x06 2
    uint32 payload_size_units16;        // +0x08
    uint16 header_size_units16;         // +0x0C
    uint16 payload_block_size_units16;  // +0x0E
    uint16 payload_block_count;         // +0x10
    uint16 time_domain_bound;           // +0x12, inclusive
    uint16 frame_domain_bound;          // +0x14, inclusive
    uint16 vfr_interval_count;          // +0x16
    uint16 track_count;                 // +0x18
    uint16 static_stream_count;         // +0x1A
};
```

The old preview read these bytes as `<4s12H>`. That split the 32-bit value at
`+0x08`, called its low word a frame count, and treated the header-size value
at `+0x0C` as a descriptor count. In the supplied file, `6702` is payload size
in 16-byte units (`107232` bytes), and `50` is header size in 16-byte units
(`800` bytes). The file contains one 189-entry descriptor table; there is no
engine-backed 50/139 active/reference partition.

Derived layout:

```text
header_bytes            = header_size_units16 << 4
payload_bytes           = payload_size_units16 << 4
payload_block_bytes     = payload_block_size_units16 << 4
frame_count             = frame_domain_bound + 1
time_sample_count       = time_domain_bound + 1
total_component_streams = track_count * 9
packed_stream_count     = total_component_streams - static_stream_count
```

Header-side variable tables are contiguous:

```text
track_table_offset = align_up(0x1c, 4)
track_table_bytes  = track_count * 4
block_spans_offset = track_table_offset + track_table_bytes
block_spans_bytes  = payload_block_count * 2
vfr_offset         = block_spans_offset + block_spans_bytes
vfr_word_count     = 1 + 2 * vfr_interval_count
```

## Supplied far-jump sample

`reference/dl2/0_m_fpp_farjump.anm2` has SHA-256
`9368914A4C59521BDD31FED064DF93A5D2D287E793FDC9447BE24ACD4A3FFF6D`
and is 108032 bytes.

```text
header bytes                 800 (0x320)
payload bytes             107232
nominal block bytes        65536 (0x10000)
payload blocks                 2
frames                       229
tracks                       189
component streams           1701
static streams              1354
packed streams               347
block frame spans     [120, 108]
VFR words             [1, 228, 1]
```

The descriptors occupy `0x1c..0x310`. Block dictionaries are:

```text
block 0: [2, 530, 956, 1341, 1730, 2152, 2511, 2799, 3183, 3653]
block 1: [2, 530, 1012, 1455, 1716, 1960, 2161, 2326, 2493, 2606]
```

Both base segments start at `block + 0x20`. Their first eight `uint16` values
are `[1354, 347, 1701, 2816, 0, 19501, 0, 0]`; `2816` is exactly
`ceil(347 / 8) * 64` calibration bytes.

For block `i`, the outer container resolves:

```text
block_offset   = header_bytes + i * payload_block_bytes
block_available = min(payload_block_bytes,
                      payload_bytes - i * payload_block_bytes)
base_offset    = block_offset + dictionary[0] * 16
base_size      = (dictionary[1] - dictionary[0]) * 16
stream_start   = block_offset + dictionary[slot] * 16
stream_end     = block_offset + dictionary[slot + 1] * 16
```

After VFR evaluation and clamping, an exact positive integer time uses the
preceding adjusted frame and interpolation fraction 1. The adjusted frame is
reduced by the block spans, then:

```text
dictionary slot = local_adjusted_frame / 15 + 1
frame in slot   = local_adjusted_frame % 15
fraction        = evaluated_frame - adjusted_frame
```

This maps time 120 to block 0/slot 8/frame 14 and time 121 to block
1/slot 1/frame 0, both with fraction 1.

## Shared sampler conclusion

DL1 and DL2 differ primarily in the outer header, block addressing, and
VFR/time-selection layer. The block-local base segment and packed sampler data
are compatible with the existing common decoder for this sample.

The common path retains nine components per track, direct values, the aligned
mask table, 64-byte calibration groups, `decode_group_8()`, 16 reconstructed
values per stream slot, second-order integration, calibration, interpolation,
finite-value validation, Cayley-to-quaternion conversion, and quaternion
hemisphere continuity. The protected predictor remains:

```text
frame 0: value = delta0
frame 1: value = wrap16(delta1 + previous)
frame 2+: predictor = saturate16(2*previous - before_previous)
          value = wrap16(deltaN + predictor)
```

## Advanced player target and unresolved descriptors

The supplied `player_skeleton.smd` has SHA-256
`D2FED6A5DA455147F85B8002671A23A6CD1E4890E8D50B62878C056457340904`.
It has 271 contiguous nodes, 271 time-0 bind rows, and one root, `pelvis`.
Against the 81-node legacy shadow-caster target it shares 78 nodes without a
common-parent change, adds 193 nodes, and omits only the three legacy roots
`l_iktarget`, `r_iktarget`, and `player_shadowcaster`.

The far-jump descriptor table resolves 92 tracks against the advanced SMD and
leaves 97 unknown transform tracks, with no name-hash collision. The unknown
tracks are preserved in a deterministic JSON sidecar by default or may be
represented as explicitly requested non-deforming helper roots. They are not
claimed as deform bones or silently discarded. This particular clip contains
no matched facial track; that is not evidence that DL2 facial animation is
unsupported.

New DL2 projects use `builtin:dl2_player_advanced`. The immutable
`builtin:dl2_player_shadow_caster` preset remains compatible for serialized
projects that explicitly selected it. Native DL2 writing remains outside this
reconstruction.
