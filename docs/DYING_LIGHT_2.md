# Dying Light 2 preview workflow

## Start a DL2 project

1. Create or open a project.
2. Select **Dying Light 2** in the Project workspace.
3. Import the animation FBX.
4. Review the FBX diagnostics.
5. Use **Dying Light 2 Player — Advanced (bundled)**, the default 271-node target,
   or select a compatible custom `.crig`.
6. Keep the root mapping on `pelvis` unless the model family requires another target.
7. Build and inspect the generated report before testing in DevTools.

Changing the game profile changes the complete target package. Do not mix a DL1 target CRIG/SMD with the DL2 reference ANM2 or vice versa.

The bundled Advanced target uses **Auto**. It is not an ordinary exact-CRIG mapping
workflow: compatible sources select ExactRig, while recognized body sources select
the live-verified 52-row bridge. **Root & .crig Mapping** is reserved for a custom
CRIG, a reviewed custom bridge, or an explicitly recorded expert override.

Open **Retargeting** to inspect or edit the source assignment for each anatomical
role. Advanced and Shadow Caster expose the same 52 role rows—pelvis, torso, limbs,
and fingers—with Auto, direct source, inherit-bind, and hold-at-bind choices. This
page remains available for an exact-name DL2 source; exact compatibility changes the
execution engine, not the ownership or visibility of the mapping workflow.

The 271-row Advanced map and 81-row Shadow Caster map are compiled backend artifacts.
They are stored separately from the semantic profile and regenerated at build time.
Do not edit those target-sized maps as ordinary project mappings. Any role edit
invalidates the cached artifact, and build requires a fresh source/target/policy
certificate before output.

## Why the bind pose matters

A source FBX may store its actual skinned bind pose in `Pose::BindPose` or skin-cluster `TransformLink` matrices. Its ordinary unanimated local transforms are not always equivalent. Blender FBXs may also carry axis and scale conversion on a non-bone `Armature` Model node.

DL ReAnimated therefore calculates animation in global space:

```text
correction[bone] = inverse(sourceBindGlobal[bone]) × targetBindGlobal[bone]
correctedGlobal[bone, frame] = sourceAnimatedGlobal[bone, frame] × correction[bone]
correctedLocal[bone, frame] = inverse(correctedGlobal[parent, frame]) × correctedGlobal[bone, frame]
```

This allows the source FBX to contain extra bones while the target uses a smaller descriptor-backed animation skeleton.

## Advanced and legacy player targets

New DL2 projects use `builtin:dl2_player_advanced`, backed by
`reference/dl2/player_skeleton.smd` and `.crig`. It has 271 nodes with `pelvis` as its
single root. The advanced additions include facial/tongue/eye/hair nodes, secondary
leg-animation nodes, `refcamera` and `eyecamera`, collar nodes, and attachment/FX
nodes. A clip may omit any of these; omitted target bones remain at bind pose.

### Verified advanced body bridge

The advanced target deliberately contains more than a body-animation source. A
target bone can participate in mesh deformation without requiring an independent
source row in a body-only clip. DL ReAnimated therefore applies a target-domain
policy instead of changing the meaning of the CRIG's `deform` flag.

For a complete recognized humanoid body source, the policy creates one row for every
advanced target bone:

```text
target rows:          271
mapped body rows:      52
bind-default rows:    219
spatial-only rows:      0
non-body mapped rows:   0
```

The 52 rows cover pelvis; three spine slots; neck and head; bilateral
clavicle/upper-arm/forearm/hand and thigh/calf/foot/toe chains; and fifteen finger
segments per side. Source spine slots 1/2/3 drive target `spine`, `spine2`, and
`spine3`. For index, middle, ring, and pinky, target `finger10/20/30/40` remains a
bind-held base while target segments 1/2/3 receive source segments 1/2/3. Thumb
`finger01/02/03` receives source thumb segments 1/2/3. A terminal source digit node
is not consumed to fill a target row.

Mapped rows use `global_bind_basis` with rotation-only ownership. Unassigned body
subdivisions use bind-local inheritance; facial, secondary-animation, collar,
camera, attachment, helper, socket, twist, and end rows use an explicit bind
disposition. This preserves target non-root translation, scale, lengths, and skin
pivots while optional target chains follow their animated target parent.

The bridge is produced by the universal Unicode/multilingual, topology-, bind-, and
animation-aware source analyzer described in [Retargeting](RETARGETING.md). Mixamo is
one regression fixture and family hint, not a naming requirement. Upper-body,
lower-body, single-limb, and partially represented clips can remain ready when their
animated chains are unambiguous; absent optional chains inherit motion or stay at
bind without a warning popup.

### Certificate, migration, and solver safety

Every automatic advanced plan includes a deterministic certificate with analyzer and
policy versions, source skeleton/bind signatures, target rig ID/full skeleton hash,
row descriptors, mapping-mode counts, animated domains, unresolved chains, and
spatial/non-body safety counts. Build regenerates or revalidates that evidence against
the live FBX and CRIG before selecting the mapped solver. The serialized
`automatic_verified` label alone is never trusted.

An old unreviewed DL2 `automatic_repair` map is not promoted in place; it is replaced
by a newly generated complete plan and a migration audit record. A manually reviewed
or imported map is not overwritten. Exact compatible sources still use the exact
solver. A revalidated advanced-body plan may use the mapped solver; ordinary
incompatible `automatic_repair` and arbitrary custom-rig maps remain blocked until
explicit review.

`builtin:dl2_player_shadow_caster` remains available as **Dying Light 2 Player —
Shadow Caster [Legacy] (bundled)** under Advanced Settings. Existing projects that
explicitly select it keep its 81-node topology. Loading or saving such a project does
not silently replace its independent IK and shadow-caster roots.

Shadow Caster uses the same semantic editor and automatic planner as Advanced. A
normal non-exact body source compiles to 52 mapped roles and 29 explicit bind or
inherited rows. Users do not need to map its independent roots or helper rows in the
CRIG editor.

## Multiple roots

The advanced target has one root, `pelvis`. The legacy shadow-caster target retains
four independent roots: `pelvis`, `l_iktarget`, `r_iktarget`, and
`player_shadowcaster`.

Both bundled target profiles use target-space Y as world up. In-place and Motion
therefore remove quaternion heading twist about Y without freezing pelvis swing or
tilt. The reviewed two-turn fixture measures approximately `718.9099 degrees` at
the source and `718.9033 degrees` at the target before policy. In-place leaves at
most `0.1 degree` of accumulated pelvis heading; Motion leaves the same pelvis
residual and transfers the original heading/displacement to `0xCCC3CDDF`;
skeletal-root mode retains the complete turn.

## Native Header_Version2 decode and FBX export

Native DL2 Header_Version2 ANM2 decoding and ANM2-to-FBX are supported for the
validated PC block/sampler layout. The supplied far-jump file is identified as:

```text
container:             DL2 Header_Version2
signature:             42
header version:        2
frames:                229
tracks:                189
static streams:        1354
packed streams:        347
payload block spans:   [120, 108]
VFR words:             [1, 228, 1]
```

The value `6702` at offset `0x08` is a 32-bit payload size in 16-byte units, not a
frame count. The value `50` at `0x0C` is the header size in 16-byte units, not an
active-track count. Header_Version2 has one 189-entry descriptor table; there is no
proven 50/139 active/reference descriptor split.

The advanced skeleton resolves 92 descriptors in this clip. By default its remaining
97 transform tracks are written, in file order, to
`0_m_fpp_farjump.dlr_unknown_tracks.json`. Advanced users may instead export them as
non-deforming helper roots. Explicit dropping is available but always produces a
warning.

Contiguous export uses the cached all-frame decoder. It parses the layout/base
tables once, decodes each unique packed 16-frame slot once, assembles selected rig
tracks directly into NumPy arrays, and vectorizes Cayley-to-quaternion conversion.
Unknown tracks are decoded in a separate selected-track pass only when the chosen
sidecar/helper policy needs them.

Native DL2 Header_Version2 ANM2 writing remains unavailable. FBX-to-ANM2 currently
emits only the explicitly labeled format-1 compatibility experiment; it must not be
described as a native DL2 writer. Header_Version2 support in this release is the
validated read/decode and ANM2-to-FBX path.

## Reproduce the bundled audit reports

Run this non-interactive command from the repository root:

```text
python tools/generate_dl2_reports.py
```

It deterministically regenerates the validated layout, descriptor map, advanced
skeleton diff, and all-frame decode smoke reports under `build/reports`. Each report
records the exact reference ANM2 and SMD hashes.
