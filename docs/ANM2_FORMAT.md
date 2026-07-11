# ANM2: Dying Light skeletal animation format

ANM2 is the sampled skeletal-animation resource consumed by Dying Light's Chrome Engine animation system. It stores animation tracks, per-frame transform components, compression metadata, and timing/page data. It does **not** by itself define the character mesh, bind pose, animation graph, inverse kinematics, or final actor/world motion.

This document describes the PC ANM2 variant used by the current DL ReAnimated pipeline. Fields still lacking a stable semantic name are deliberately left as `unknown` rather than guessed.

## 1. Where ANM2 sits in the pipeline

```
FBX animation
  -> source skeleton matrices
  -> retargeted Dying Light local transforms
  -> ANM2 component rows
  -> ANM2 packed/direct storage
  -> _ANIMATION_ resource in an RPack
  -> AnimationScr sequence registration
  -> engine SampleFrame
  -> runtime AnimXform rows
  -> animation graph / movie / IK / motion accumulation
```

ANM2 is therefore the **pose-sampling layer**. A clip can have perfectly correct ANM2 poses while actor translation, final yaw, foot locking, or hand IK is supplied later by a graph, movie key, OffsetHelper, gameplay controller, or another runtime layer.

## 2. Outer 32-byte header

The current parser uses this little-endian layout:

```
Offset  Size  Current interpretation
0x00    4     ASCII magic "ANM2"
0x04    2     outer format value; stock PC clips use 42
0x06    2     unknown/version-related field
0x08    2     frame_count
0x0A    2     track_count
0x0C    2     page_count / sampler page count
0x0E    2     first page file offset
0x10    4     declared file length in common files
0x14    4     low 16 bits currently used as duration-key count
0x18    4     unknown/alternate length slot in some files
0x1C    4     unknown/alternate length slot in some files
```

Engine code refers to the inner sampler layout as ANM2 **Version 1**, even though the outer PC header value commonly observed at `0x04` is `42`. These are different naming layers.

Immediately after the header is the track descriptor table:

```
track_count * uint32 descriptor/hash values
```

The descriptor is what the engine resolves against a skeleton/track table. A missing requested track is represented internally by index `0xFFFF`.

## 3. Track component order

Each skeletal track contains nine scalar components in this order:

```
0  rx
1  ry
2  rz
3  tx
4  ty
5  tz
6  sx
7  sy
8  sz
```

The translation and scale components have their expected meanings. The three rotation values are **not Euler angles** and are not simply quaternion XYZ.

### Rotation representation

ANM2 stores a three-component stereographic/Cayley-like quaternion parameter `v`:

```
v = (rx, ry, rz)
d = dot(v, v)

q.xyz = 2 * v / (1 + d)
q.w   = (1 - d) / (1 + d)
```

The runtime SampleFrame path performs shortest-hemisphere normalized interpolation for rotations and emits a full quaternion in the runtime AnimXform row. The exporter uses the inverse mapping when converting a target quaternion to ANM2 values.

This distinction is essential: treating ANM2 `rx/ry/rz` as Euler angles or raw quaternion XYZ produces plausible numbers but incorrect poses.

## 4. Timing, pages, and 16-frame segments

After the descriptor table, the current Version-1 layout contains:

```
page span table
duration/control words
page data beginning at header.first_page_offset
```

Pages are addressed in `0x10000`-byte steps. Every non-final page occupies exactly 64 KiB on disk; the final page may be shorter. Each page repeats the base sampler segment, followed by only the packed stream slots assigned to that page. A page begins with a table of 16-byte-unit offsets identifying:

```
base sampler segment
one or more per-time packed stream segments
end offset
```

The header-side page-span table contains one `uint16` span per page. Its values must sum to `frame_count - 1`. Each ordinary packed stream slot covers 15 frame intervals while storing the 16 endpoint samples needed for interpolation. Page boundaries therefore occur between stream slots, never inside one.

A valid long clip follows these physical rules:

```
page_count = number of physical 64 KiB page positions
page N file offset = first_page_offset + N * 0x10000
all non-final pages padded to 0x10000 bytes
base segment repeated on every page
page spans sum to frame_count - 1
```

The writer splits long clips into valid pages and rejects malformed output before RPack packaging. The supplied 2,210-frame stock control uses 12 pages, confirming that clip duration is not the limitation; physical page construction is.

Packed animation data is processed in blocks of 16 frames. The engine selects:

```
page index
table/segment index
frame within 0..15
interpolation fraction
```

The exact duration table is preserved and evaluated by the current tools, but several header/control words remain intentionally unnamed.

## 5. Base sampler segment

The base segment starts with eight little-endian `uint16` values. The first fields are now understood:

```
base + 0x00  direct/static component count
base + 0x02  packed/dynamic component count
base + 0x04  total component count (normally track_count * 9)
base + 0x06  packed calibration-table byte count
base + 0x08  decompressor/static selector or related control
base + 0x0A  unknown
base + 0x0C  unknown
base + 0x0E  unknown
```

The engine-aligned offsets are:

```
table_start = align_down_16(base + 0x19)
direct_values_start = align_up_16(table_start + packed_table_byte_count)
mask_start = align_up_4(direct_values_start + 4 * direct_count)
```

For a 16-byte-aligned base, `table_start` is `base + 0x10`.

### Direct/static values

Direct components are stored as ordinary 32-bit floats at `direct_values_start`. A direct component has one value for the segment rather than a changing packed curve.

### Track mask bytes

There is one mask byte per track. The engine-confirmed meaning is:

```
mask bit set   -> component comes from the direct/static float table
mask bit clear -> component comes from the packed/dynamic stream
```

Current bit assignment:

```
0x01 rx
0x02 ry
0x04 rz
0x08 tx
0x10 ty
0x20 tz
0x40 sx, sy, and sz as a scale group
```

The three scale axes share the scale-selection bit.

## 6. Packed calibration groups

Moving components are grouped eight lanes at a time. Each calibration group is 64 bytes:

```
0x00  8 float biases
0x20  8 float scales
```

For a decoded signed integer sample `raw`:

```
component_value = bias + raw * scale
```

The group count is:

```
ceil(packed_component_count / 8)
```

## 7. Packed stream encoding

A packed group contains eight component lanes and sixteen frames. Per-frame signed bit widths are stored as nibbles in the leading lane words, followed by a transposed bitstream.

The critical integration rule is the engine's bounded second-order predictor.

### Decode/integration

```
frame 0:
  value[0] = delta[0]

frame 1:
  value[1] = wrap16(delta[1] + value[0])

frame 2 and later:
  predictor = saturate16(2 * value[n-1] - value[n-2])
  value[n]  = wrap16(delta[n] + predictor)
```

Where:

```
saturate16(x) clamps to [-32768, 32767]
wrap16(x) applies signed 16-bit wraparound
```

### Encode/delta construction

The writer uses the exact inverse:

```
frame 0:
  delta[0] = value[0]

frame 1:
  delta[1] = wrap16(value[1] - value[0])

frame 2 and later:
  predictor = saturate16(2 * value[n-1] - value[n-2])
  delta[n]  = wrap16(value[n] - predictor)
```

An older unbounded Python predictor could round-trip through itself while producing exploding limbs in the engine. The saturate-then-wrap behavior is now a protected regression rule.

## 8. Runtime SampleFrame output

The engine maps requested descriptor hashes to track indices and samples ANM2 into `AnimXform` rows. The runtime row stride is `0x30` bytes:

```
Offset  Size  Meaning
0x00    12    translation vec3
0x0C    4     padding
0x10    16    rotation quaternion XYZW
0x20    12    scale vec3
0x2C    4     padding
```

A serialized standalone AnimXform is more compact (`0x28` bytes), so compact reference-pose rows must not be confused with the padded runtime row.

The runtime animation path subsequently converts between `AnimXform` and `Matrix3x4` rows, blends layers, and may apply additional graph/gameplay behavior.

## 9. Skeleton and reference pose are external

ANM2 descriptor rows do not contain human-readable bone names or a complete bind hierarchy. Correct custom animation requires separate target data:

```
bone names / hashes
parent indices
bind-local translations
bind-local rotations
track-to-bone mapping
```

DL ReAnimated currently uses the extracted `player_1_tpp.smd` hierarchy as the target reference for the tested standard male infected/NPC skeleton. Of the 70 stock ANM2 descriptors in the template, 69 map to SMD bones. The remaining known descriptor, `0xCCC3CDDF`, is treated as a non-mesh motion/offset-helper track.

## 10. Root motion and `0xCCC3CDDF`

The authoring pipeline exposes three policies:

```
inplace:
  pose only; root and motion helper remain fixed

bip01:
  source Hips displacement is written to the skeletal root
  raw loops return to frame zero

motion:
  vertical/pose placement remains on bip01
  horizontal displacement and body-orientation delta go to 0xCCC3CDDF
```

The third mode authors data suitable for a motion accumulator, but continuous movie/gameplay movement still depends on the consumer. Relevant editor-facing concepts include `CKeyAnimation.m_UseOffsetHelper`, `CPoseObject.AccumulateMotion`, and `CPoseObject.MotionAccumulatorBone`.

## 11. IK is not an ANM2 switch

Inverse kinematics is a later authoring/runtime layer. ANM2 contains sampled local transforms; it does not expose one universal "enable IK" bit. A movie, animation graph, sequence, or gameplay controller may alter feet, hands, head, or final actor orientation after the ANM2 pose is sampled.

The tool therefore records an IK authoring recommendation in a sidecar rather than inventing ANM2 data.

## 12. RPack and AnimationScr delivery

An ANM2 payload becomes usable in the editor/game when packaged as an `_ANIMATION_` resource and registered by an `_ANIMATION_SCR_` resource. The current working DLC route is:

```
pack file:          common_anims_sp_pc.rpack
animation script:   selectable per project/clip
                    (for example anims_man_all_DLC60,
                     anims_player_dlc60, or anims_woman_all)
```

Do not replace the base `common_anims_PC.rpack` while testing this workflow.

## 13. Current confidence levels

### Editor/runtime validated

```
ANM2 outer header parsing used by the project
track/component order
Cayley/stereographic rotation conversion
base segment table/direct/mask layout
mask direct-versus-packed meaning
64-byte bias/scale groups
engine-equivalent packed second-order integration
runtime 0x30-byte AnimXform rows
stock rebuild through the writer
full-body Mixamo retarget for the tested target skeleton
```

### Supported but still being refined

```
finger retarget across the Dying Light hand/hand1 hierarchy
motion-accumulator authoring and movie-loop consumption
exact editor/game IK workflow
additional actor/skeleton families
```

### Intentionally not claimed

```
all unknown header fields have final names
all ANM2 variants/platforms share this exact layout
ANM2 alone owns final actor/world movement
one target skeleton automatically fits every Dying Light character
```

## 14. Useful source modules

```
dlanm2_gui/anm2.py
  outer header, pages, timing selection

dlanm2_gui/anm2_base_segment.py
  engine-aligned base-segment offsets

dlanm2_gui/anm2_components.py
  direct/packed component selection and frame decode

dlanm2_gui/anm2_packed.py
  packed bitstream and engine second-order integration

dlanm2_gui/anm2_writer.py
  validated packed writer and multi-page payload assembly

dlanm2_gui/fbx_pipeline.py
  public FBX -> ANM2/RPack build surface

dlanm2_gui/anm2_fbx.py
  full-clip decode, rig reconstruction, and generic reverse retarget

dlanm2_gui/blender_fbx.py
  Blender-assisted skeleton-and-animation FBX export
```

## 15. Practical debugging order

When a custom animation is wrong, test in this order:

```
1. Does a stock clip rebuilt by the writer still match stock?
2. Does the target bind control match editor Reset?
3. Does frame 0 match source frame 0 rather than target bind?
4. Are the intended descriptors mapped to the intended target bones?
5. Are source FBX matrices evaluated with the correct intrinsic Euler order?
6. Are target-local rotations reconstructed through the actual parent hierarchy?
7. Is the remaining difference pose data, finger/palm hierarchy, IK, or root accumulation?
```

This prevents a consumer-layer motion or IK issue from being misdiagnosed as an ANM2 codec failure.
