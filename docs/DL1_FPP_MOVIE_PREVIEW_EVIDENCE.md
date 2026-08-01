# Dying Light 1 FPP and movie preview evidence

This first-pass contract is deliberately limited to Dying Light 1. It records
what the inspected decompile establishes, what the offline evaluator can apply,
and which game stages must remain visibly unavailable until their runtime inputs
are captured. A `Dl1Profile` preview is not labeled `GameValidated`.

The source snapshot inspected for this pass is:

- `E:\Debugging\DyingLightDebug\windows - no names\Dev Tools RE\DLE\gamedll_x64_rwdi.dll.c`
- `E:\Debugging\DyingLightDebug\libgamedll.dylib.NAMED.c`
- `E:\Debugging\DyingLightDebug\libengine.dylib.NAMED.c`
- `E:\SteamLibrary\steamapps\common\Dying Light\DW\Data0.pak`,
  specifically `data/vis/bodyvis.def` and
  `data/vis/playerfppbodyvis.scr`

## Camera contexts

| Context | Decompile evidence | Core/Evaluation contract |
| --- | --- | --- |
| Player FPP helpers | `libgamedll`:149511-149523 exposes camera position/direction/up, default FOV and clip-near methods and names `Spine`, `Spine1`, `Spine2`, `Spine3`, `HSpine1`, `EyeCamera`, and `RefCamera`. `PlayerFppVis::InitAnimBonesId` resolves those names at 6899768-6899859. The current Windows decompile resolves the same seven-name table in function `0x180B72D20` at lines 2337955 and 2338027-2338066. Retail `bodyvis.def`:85-86 assigns role IDs 47 and 48 to `EyeCamera` and `RefCamera`; `playerfppbodyvis.scr`:21 and 27-28 parents both helpers through `HSpine1`, while line 72 ends the FPP path at `EyeCamera`. | `EyeCamera` is the evaluated FPP camera helper. The distinct player-rig `RefCamera` is exposed as helper metadata when present and never substitutes for `EyeCamera`. |
| Player FPP view transform | `PlayerFppVis::GetCameraPos` at `libgamedll`:6904079-6904241 and `GetCameraDir` at 6904253-6904570 branch through live selfie, cinematic, model, look, shaker, vehicle, and eye-tracking state. A model path reads `eyecamera` at 6904328-6904335, but that is not the complete runtime path. | The useful offline view is anchored to evaluated `EyeCamera`, while `fpp_view_transform` is explicitly reported as a fallback. It is not labeled as the exact runtime camera transform. |
| FPP scene projection | `PlayerFppVis::GetDefaultFOV`, `GetCameraDefaultFOV`, `GetHandsDefaultFOV`, and `GetCameraClipNear` at `libgamedll`:6903858-6904037 read live player, vehicle, cinematic/dialog, FOV, aspect, and anti-wall state. | A supplied `Dl1FppProjectionSnapshot` is applied. Without one, the editor lens is explicitly reported as a non-game-validated fallback; no numeric DL1 default is invented. |
| FPP hands projection | `PlayerFppVis::GetHandsProjection` at `libgamedll`:6926255-6926275 reads camera angle/aspect and near-plane state and constructs a separate `mtx44::frustum_inf`. | Hands FOV axis, aspect ratio, near plane, and infinite far plane are represented separately from the finite scene-camera lens. A DL1 snapshot rejects a finite hands far plane. Missing capture data produces an unavailable stage and diagnostic. |
| Movie reference camera | `CMovieManager::SetMoviesRefCamera` at `libengine`:1302334-1302343 stores an external `IBaseCamera`. `CKeyCameraFOV` reads its FOV at 1219076-1219179; `CKeyCameraPos::GetPoint` reads its matrix position and forward vector at 1222582-1222649. | `Dl1MovieReferenceCameraSnapshot` carries an external world transform and lens. The evaluator never substitutes the player skeleton's similarly named `RefCamera` helper. |

## Procedural FPP stages

### Safe HSpine/HSpine1 basis subset

The first pass may reproduce the stateless basis reconstruction performed by
`CorrectHSpine` and `CorrectHSpine1`, but not the later runtime-driven head
translation solver:

- In the current Windows decompile, `PlayerFppVis::ApplyAnimation` is function
  `0x180B959E0` at lines 2356116-2356153. Lines 2356131-2356136 call
  `0x180B7BD90`, `0x180B7C030`, and `0x180B7DA30` in that order only while the
  vehicle-controller active byte is clear. The first two Windows functions
  are present at lines 2341729-2341733 but their frames did not decompile.
- The named Mac decompile identifies the matching sequence as
  `CorrectHSpine`, `CorrectHSpine1`, then `CorrectHeadPosition` in
  `PlayerFppVis::ApplyAnimation` (`0x17EA010`,
  `libgamedll`:6924582-6924640, especially 6924605-6924617).
  `CorrectHSpine` is `0x17D0A30` at 6907390-6907447 and
  `CorrectHSpine1` is `0x17D0BA0` at 6907452-6907510.
- Retail `playerfppbodyvis.scr`:15 and 21 map body roles 2 and 8 to
  `HSpine` and `HSpine1`. Lines 27-28 parent `EyeCamera` and `RefCamera` to
  `HSpine1`; therefore helper extraction must occur only after the corrected
  world transforms have been propagated to descendants.

The preview-only subset follows the named function bodies exactly:

1. Resolve exactly one `HSpine` role 2 and one `HSpine1` role 8. Missing or
   ambiguous role resolution fails closed, reports the stage unavailable, and
   leaves the display pose unchanged.
2. For each original world matrix, preserve its translation and capture the
   three basis-vector lengths independently so nonuniform scale is retained.
3. Replace the basis with world up, model left, and negative model forward.
   `HSpine1` additionally runs the equivalent of
   `mtx34::make_ortho_clear_scale` before restoring the three captured scales;
   `HSpine` does not add that extra operation.
4. Apply `HSpine` before `HSpine1`, rebuild affected descendant transforms,
   and only then extract `EyeCamera`/`RefCamera`.

This subset is enabled only for an explicitly vehicle-inactive FPP authoring
context. Vehicle-active or unknown context does not guess through the runtime
gate. It affects only `EvaluationPurpose.Preview` and only the display pose;
the immutable authored/export pose and sampled ANM2 tracks remain untouched.
The WPF authoring target has a fixed identity entity transform in the
renderer/compiler's documented right-handed `Y`-up, `-Z`-forward space, so it
supplies `worldUp=+Y`, `modelLeft=-X`, `modelForward=-Z`, and an explicit
vehicle-inactive state. This describes the offline editor scene; it is not
presented as captured live-player state.

The fidelity label for this subset is **decompile matched, not game
validated**. It must not produce a `GameValidated` badge until matrices from
the fingerprinted Windows 1.55 runtime have been captured and compared.

### Runtime-dependent stages kept unavailable

The complete head-position behavior remains unavailable. Windows
`UpdateAnimation` at lines 2338422-2338435 calls the sprint offset, edge-grab
offset, and local-head-position functions before `ApplyAnimation`. In the
named Mac decompile, `ComputeLocalHeadPosition` at 6900826-6901002 and
`IsHeadCorrectionEnabled` at 6907665-6907767 consume live movement,
look/controller, landing, grab, jump, melee, carry, knockdown, and other
player state. `CorrectHeadPosition` at 6907771-6908030 consumes that computed
state. The stateless HSpine basis subset must not be described as reproducing
this solver.

Hand inertia also remains unavailable. Windows function `0x180B7A220` is
called after hand-roll/virtual corrections in `0x180B95AC0` at lines
2356157-2356191. The named equivalent `PlayerFppVis::UpdateHandInertia` at
6905905-6906959 uses frame history, springs, camera/player velocities, weapon
descriptors, and current pose state; `IsHandInertiaEnabled` at
6906966-6907048 gates interactions, vehicles, weapons/aiming, grabs, landing,
movement, and ladders.

The WPF surface exposes the two corrections independently.
`hspine_basis_correction` is enabled by default and may apply only the bounded
HSpine/HSpine1 subset above. `head_position_correction` is off by default and
reports `Unavailable` when explicitly requested. `hand_inertia` is also off by
default; requesting it reports `Unavailable` and leaves the display pose
unchanged. The old `head_spine_correction` identifier is accepted only as a
load-compatibility alias that restores both concrete controls. Newly saved
profiles always persist the concrete stage identifiers independently.
Reserved built-in stage identifiers prevent an injected generic offset or
sway from masquerading as any of these game behaviors.

## Export boundary

DL1 FPP/movie context evaluation runs only for `EvaluationPurpose.Preview`, after
the exportable authored pose has been finalized. Camera snapshots, helper
transforms, stage reports, fallback diagnostics, and future procedural display
changes are not ANM2 tracks. Export evaluations return no preview camera,
camera-helper metadata, or DL1 preview-stage reports, and the ANM2 adapter
samples only `EvaluationFrame.AuthoredPose`.

## Capture input and renderer behavior

The WPF FPP workspace exposes explicit **User/runtime-capture projection
inputs**. A fresh project can store a capture label, scene vertical
FOV/aspect/near, and separate hands FOV-axis/aspect/near values. The hands far
plane is fixed to the evidence-backed infinite contract. No fields are
prefilled as claimed DL1 defaults.

An enabled incomplete capture fails closed: evaluation retains the visibly
labeled editor scene fallback and an FPP-hands mesh is not drawn through the
scene lens. A complete capture is passed through `Dl1PreviewInputs`; `Applied`
means the explicit input was used, not that it was game validated.

The WPF Movie camera tab separately stores an external `IBaseCamera` world
position, XYZW quaternion, and finite scene lens. An enabled complete snapshot
is passed through `Dl1PreviewInputs` and the evaluated
`Dl1MovieReferenceCamera` is routed to the target viewport with its captured
aspect. An incomplete snapshot fails closed, and the evaluator never searches
the skeleton for `RefCamera` as a replacement. These numeric authoring inputs
do not supply the independently trusted capture fingerprint required for a
`GameValidated` badge.

D3D11 preserves captured scene aspect with a centered viewport and draws the
safe frame inside the `HwndHost` airspace. High-confidence explicit-FPP retail
meshes carry a hands projection role and use the separate captured projection;
other scene geometry and attachments retain the scene projection. During FPP
and Cutscene preview, both panes consume the same evaluated target scene on the
shared timeline. The target owns evaluated `EyeCamera`/movie camera and
projection state. The source uses its ordinary freely navigable orbit camera
and never receives the FPP hands-projection override, so FPP-hands-role meshes
use the ordinary scene projection in that external view.
The finite scene far clip remains an editor culling boundary because the
inspected evidence establishes scene FOV/aspect/near, not a captured scene far.

The source override mirrors evaluated skeleton/pose, morph weights, composed
attachments, gizmos, and selection. It does not replace the authored source
buffer: source-scene changes continue behind the display override, and leaving
FPP/Cutscene reveals that authored scene intact. Only the target is locked while
an evaluated preview camera override exists. This is an offline authoring
comparison, not evidence that runtime camera motion, head/spine correction,
hand inertia, anti-wall behavior, shake, or post effects match the game.
