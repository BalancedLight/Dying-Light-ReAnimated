# Root motion, actor basis, looping, and IK

Pose transfer, skeletal-root motion, actor/world accumulation, and runtime IK
are separate contracts. The Retargeting tab stores the source root, actual
target root, translation owner, and heading owner independently.

Legacy project strings remain readable adapters:

| Serialized value | Translation owner | Default heading owner |
|---|---|---|
| `inplace` | none | locked at the initial target-global heading |
| `bip01` | selected skeletal root (`bip01` in DL1, `pelvis` in DL2) | skeletal root |
| `motion` | `0xCCC3CDDF` for planar motion; vertical pose stays on the skeletal root | `0xCCC3CDDF` |

`bip01` is not a target assumption. New profile data uses `skeletal_root` and
stores the real target name. Old DL1 projects keep their prior byte behavior
until the new Root & locomotion selection is explicitly saved.

## Actor-frame displacement mapping

Model coordinate normalization and actor motion are deliberately independent.
For every source root sample the retargeter:

1. subtracts the raw source bind-global position from the raw animated-global
   position;
2. applies the FBX unit/wrapper scale exactly once;
3. decomposes the vector along the analyzed source body frame's right, up, and
   forward axes;
4. reconstructs those semantic components along the target bind frame's right,
   up, and forward axes;
5. converts the reconstructed target-global vector to the selected target root's
   parent-local space.

The FBX/model basis matrix is never reused as the root vector mapping. This is
what prevents a source actor-forward `+Z` displacement from becoming target
vertical `+Y` merely because a model-axis conversion contains that rotation.
Frames are finite, orthonormal, right-handed, sign-stable, and backed by pelvis,
axial, and bilateral hip/shoulder evidence. An underdetermined frame stops with
one focused mapping diagnostic.

Heading is extracted from the selected target root's global quaternion as a
swing/twist decomposition about the target profile's world-up axis. No Euler
conversion or arbitrary source-root bone roll is used. `lock_initial` removes
only accumulated heading and preserves swing/tilt; `preserve` retains all root
orientation; `to_motion_accumulator` moves heading to `0xCCC3CDDF`.

## Target-owned locomotion

- DL1 uses the `bip01` root and `l/r_thigh -> calf -> foot` chains. It has no
  advanced sole-helper or hidden IK-root requirement.
- DL2 Advanced uses `pelvis`, both leg/foot chains, and `l_sole_helper` /
  `r_sole_helper`. It deliberately has no `l_iktarget` or `r_iktarget` dependency.
- DL2 Shadow Caster [Legacy] retains `l_iktarget`, `r_iktarget`, and
  `player_shadowcaster` as explicit legacy target nodes. They remain at bind
  unless a user maps them.

The helper-only and complete-target table views never imply runtime ownership.
Unmapped target rows stay at bind; direct overrides serialize source, mode,
transfer policy, and component ownership and are recompiled/revalidated before
build.

## Looping and consumers

A raw sequence restarts at frame zero. Continuous actor movement requires the
consumer to apply the accumulator rather than resetting it. Known editor-facing
controls include `CKeyAnimation.m_UseOffsetHelper`,
`CPoseObject.AccumulateMotion`, and `CPoseObject.MotionAccumulatorBone`.

## IK

ANM2 stores sampled local transforms; it has no universal IK-enable bit. The
per-clip `runtime` / `off` value is an authoring recommendation recorded for the
movie or animation-graph consumer. The converter does not fabricate IK curves
or claim that Advanced DL2 has hidden IK roots.

ANM2-to-FBX decode reports root and accumulator translation ranges and
target-global accumulated heading as diagnostics only. Those measurements never
mutate decoded curves or the sparse FBX handoff.
