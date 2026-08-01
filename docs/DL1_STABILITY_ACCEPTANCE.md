# DL1 stability acceptance regressions

The C# first pass uses bounded generated RP6L archives and temporary local
files to exercise failure and long-session behavior without launching Dying
Light or redistributing retail data.

`RpackStabilityAcceptanceTests` adds four deterministic lifecycle controls:

- six complete catalog/SQLite/provider/cache open-close sessions with 108
  alternating exact-identity and logical-identity asset opens;
- 16 simultaneous readers of one compressed chunk, producing one verified
  `.chunk`/`.sha256` cache pair;
- cancellation after a partial disk inflation is observed, followed by a clean
  retry through the same cache instance; and
- an eviction, deliberately corrupted retained SHA-256 sidecar, regeneration,
  and re-entry into the disk LRU.

`AtomicReplacementStabilityTests` locks existing project and output
destinations at replacement time. The production atomic-save APIs must preserve
the old bytes and remove their unique temporary files when replacement fails.
The output writer also has a cancelled-write control. `ProjectSerializer` is
synchronous and has no cancellation contract, so its deterministic
replacement-lock regression is the available interruption seam.

`ViewModelTimelineTests` drives a 30 fps timeline from 100 Hz rendering ticks.
Fractional frame time must accumulate to the exact expected frame instead of
being discarded on every sub-frame refresh. Separate controls prove that pause,
explicit seek, and playback-rate changes reset the clock and remainder, so
paused wall time and stale cadence cannot advance the next playback segment.

`ViewModelWorkspaceTests` locks two facial-editor state boundaries. Filtering
rebuilds only visible morph/preset collections and retains the authoritative
objects and nonzero weights. Manual facial keying appends a final enabled,
full-weight authored override; a sampled base value plus an earlier additive
FED layer still evaluates to the keyed absolute total instead of being added a
second time.

`FppProjectionProjectTests` round-trips an explicit external movie
`IBaseCamera` transform and lens through schema-1 project and recovery state,
passes the snapshot through `Dl1PreviewInputs`, and verifies that Cutscene mode
routes it to the target viewport with captured aspect while leaving FPP-hands
projection disabled. This is an offline authoring-input regression, not a
trusted game-validation capture.

These tests complement, rather than replace, the narrower
`RpackChunkCacheIntegrityTests`, which separately controls same-length data
corruption, paired eviction, and invalid-compressed-input cleanup.

`RetailAssetCatalogPersistenceTests` cover the startup index itself. A
schema-3 complete SQLite snapshot restores unchanged base and configured user
providers without resource enumeration while retaining precedence and every
duplicate candidate. Canonical row manifests, timestamp-preserving
sampled-content mutation, missing packs, user-root inventory growth, and
corrupt databases force a full rescan; cancellation and transient snapshot
failure preserve the previously published database. Writes use a same-directory
temporary database, strict SQLite sidecar handling, and atomic replacement. Large pack
fingerprints intentionally read at most five 16 KiB windows, so a
length/timestamp-preserving mutation wholly outside those windows may defer
detection until the provider opens and identity-checks that asset.

## Viewport and installed visual controls

The renderer's mesh pass owns an explicit rasterizer state with
counter-clockwise front faces and back-face culling. WARP offscreen controls
render the decoded outward winding and reject its reversed control, preventing
the far-side interior from masquerading as the visible surface. The neutral
inspection light uses a 68-percent floor plus a 32-percent directional term so
away-facing textured surfaces remain readable like the local stock-editor
references. This and the distinct source/target backgrounds are editor
visibility aids, not emulation of DL1 runtime lighting.

`RendererRawInfinityUvTests` submits a textured WARP triangle with the exact
positive/negative infinity float values produced by DL1 Half2
`0x7C00`/`0xFC00` components. The renderer accepts the mesh, submits the
base-color sampling path, and finishes without device removal. This is a
stability/raw-passthrough control only; it does not assert that a neutral
shader's sampled pixels reproduce any retail material technique.

`RendererCpuReferenceTests` and `RendererNormalMatrixWarpTests` lock the CPU
and HLSL normal-transform contract under rotated, non-uniform static and
skinned scale. Both paths use inverse-transpose normal matrices, preserve
compact per-draw skin-palette order, and return deterministic zero normals for
singular transforms instead of publishing NaNs. The selection silhouette uses
the same corrected deformed normals; triangle winding and the explicit
counter-clockwise front-face convention remain unchanged.

The local renderer stability controls are intentionally bounded and
hermetic. `RendererD3D11SmokeTests` performs 256 repeated GPU asset switches
while requiring the mesh cache to remain bounded. `RendererStabilityTests`
locks non-torn concurrent resize-mailbox dimensions, and
`RendererRecoveryPolicyTests` locks deterministic device-loss, display-change,
Remote Desktop, and WARP decisions as policy. These controls catch local state
and resource-lifetime regressions; the recovery-policy tests do not constitute
physical device removal, adapter hot-swap, or Remote Desktop evidence.

`InstalledDl1VisualReferenceControlTests` read the validated Windows 1.55
installation without launching the game. They lock coherent preview payloads
for headed `player_1_tpp`, headless `player_1_fpp`, headed `player_11_tpp`,
headless `player_11_fpp`, `jade`, `armored`, `zombie_voleteile`,
`zombie_screamer`, `brecken_cin`, and
`anim_slums_door_a`, including the door's embedded animated-prop rig. The
exact supplemental `player_1_fpp` visible-surface rule is
content-fingerprint scoped; broader non-Default variant selection remains
unresolved. That stock-FPP authoring subset omits the decoded 1,160-triangle
shoulder flashlight as well as the head and TPP duplicate surfaces; the
installed `Default` skin itself directly hides no `player_1_fpp` entity, so
the supplemental presentation is not mislabeled as a decoded skin effect.
The stock editor's white
character diamonds and yellow selected-door wireframe are bone/pivot overlays
rather than mesh geometry. Each exact control also checks that every deform
row lies inside the current mesh bounds expanded by five percent. Humanoid
controls require at least 20 left/right deform pairs and cap their bind mirror
residual at 1.5 percent of model diagonal. The door is deliberately separate:
its four palette-driving Bone rows are presented as selectable/editable prop
rows rather than deform diamonds, and its legitimate off-panel prop/pivot
helpers are allowed outside the panel mesh.

`InstalledDl1VisualReferenceWarpTests` then reads back real WARP pixels for all
ten exact-build/content-fingerprinted controls: two player TPP/FPP pairs,
Jade, armored, volatile, screamer, Brecken, and the door. Each control must publish
finite CPU reference bounds, readable lit mesh pixels, additional
skeleton/helper overlay pixels, no renderer diagnostics, and a successful
D3D11 device state. This is a bounded editor-rendering regression for the
reported dark/interior-facing and lopsided-overlay failures; it is not a
live-game screenshot comparison.

`InstalledDl1OversizedRigWarpTests` separately locks all 14 validated physical
resources whose decoded bone/helper inventory exceeds 256 rows. Every skinned
draw retains an explicit mapping into its complete published skeleton while
uploading no more than 256 local matrices, and each resource must produce WARP
mesh pixels without diagnostics or device removal. The set is also required
to retain at least one actual published skeleton above 256 rows, so the
regression cannot silently collapse into ordinary small-rig coverage.

`RendererAuthoringOverlayTests` keep root trails, current CPU-reference
morphed/skinned bounds, selected-model union bounds, and scale-normalized local
bone axes finite and deterministic. The WPF controls publish immutable state;
root trails sample at most 2,048 authoritative export poses for InPlace,
Bip01, and MotionAccumulator modes on a cancellable worker that is awaited
during teardown. Project replacement clears stale jobs and overlay state.
Deformed bounds are measured once per changed immutable scene snapshot rather
than once per D3D frame; that one-time measurement currently runs
synchronously on the publishing thread. Selected draws also use an
expanded-backface silhouette pass driven by the same morph and skin palette as
the visible draw, while the selected model retains one union deformed AABB.
Neither outline, bounds, nor shader tint applies while the highlight toggle is
off. The WARP regression locks the outline's off/on pixel delta on an actively
morphed and skinned closed mesh; this is not a claim of exact stock-editor
selection pixels.

`RendererAuthoringStageGoldenTests` adds a generated-data nine-stage WARP
matrix for the authoritative authoring path: retargeting, DL1 root motion, a
bone edit, hand IK, an authored morph, a FED-derived expression layer, FPP
`EyeCamera` plus separate hands projection/safe frame, helper/prop overlays,
and an attached prop. Each stage locks exact BGRA pixel and changed-pixel-mask
SHA-256 values, finite coverage bounds, and CPU/GPU projected-bounds agreement
where applicable. `tools\validate_renderer_authoring_goldens.ps1` additionally
writes each frame as an atomic BMP and publishes a bounded hash-checked
manifest, rejecting stale, missing, or unexpected captures. The fixture is
entirely synthetic and contains no retail geometry, textures, FED payload,
user screenshot, or game capture. It closes the generated authored-stage
matrix, not retail facial/FPP pixel parity or live-game validation.

`AttachmentAuthoringTests` also bind-pose bake an independently skinned prop
into the rigid target scene and require the rebuilt draw mesh to retain its
decoded tint, base-color texture object, and FPP-hands projection role. Skin
palette, morph, and selection state are deliberately cleared because the
attachment is no longer independently deformed or selected.

The user-supplied stock-editor screenshots remain local comparison evidence.
They are not repository fixtures, pixel-golden inputs, embedded resources, or
redistributed assets. These controls do not prove exact shaders,
normal/specular/mask behavior, post-processing, or live-game appearance.

## Installed Blender FBX handoff

`InstalledBlenderFbxAcceptanceTests` is intentionally opt-in and fail-closed
through `tools\validate_dl1_blender_handoff.ps1`; an ordinary green test run is
not evidence that Blender ran. The script requires an exact `blender.exe`,
probes it in background/factory-startup mode, and runs the installed gate with
the validated DL1 1.55 build.

The bounded Blender 5.2 control exports the exact
`zombie_voleteile_blue` retail resource with its 97-bone rig, six highest-detail
mesh parts, five decoded base-color files, and two generated compatible ANM2
clips as exact-named Actions. The strict C# binary-FBX reader validates stacks,
key ranges, complete armature/BindPose, topology, nonempty and complete skin
coverage, finite normals/UVs, and sibling-relative texture references before
the bundle is atomically committed. The test then deletes the FBX, textures,
manifest, and temporary decoded payloads; no retail bytes are retained in the
repository or acceptance evidence.

This control also prevents regressions to Blender 5.2's default empty Clusters
for unweighted bones and armature-prefixed all-action stack names. It does not
yet compare exact exported vertex, normal, winding, skin-weight, or sampled
retail-animation values across Blender versions or model families.

## Fail-closed installed DL1 1.55 receipt

`tools\validate_dl1_installed_acceptance.ps1` is the release-required,
read-only installed gate. An ordinary green test run is not equivalent because
the relevant tests are deliberately opt-in outside this runner.

The runner requires Release configuration and the exact validated Windows 1.55
build fingerprint. It also verifies the required base/DLC files, then runs 19
fully qualified tests one at a time and parses each TRX execution result. The
inventory covers bounded pack opening, the complete type-272 corpus, FED,
morphs, materials, rig families and promotion boundaries, skinning layouts,
orientation, installed authoring/RPack flow, and exact WARP payload/pixel
controls. A skipped, missing, duplicated, failed, or renamed test fails the
run.

After all tests pass, the runner independently verifies the corpus report is
for 62 packs and 8,738 type-272 resources, with 8,736 geometry decodes, 8,736
presentation validations, and zero blockers. It then atomically replaces:

`artifacts\validation\dl1-installed-acceptance-1.55.json`

The schema-1 receipt records the runner and Release assembly hashes, SDK,
repository HEAD/state, installed executable identity and required-file
metadata, all 19 exact test outcomes, corpus-report hash and totals, timings,
and a canonical `evidenceSha256`. Failed runs publish no complete receipt and
preserve their staged TRX diagnostics. The receipt itself is directly hashable
with `Get-FileHash`.

The runner does not start Dying Light and records both `gameLaunched=false` and
`liveGameEvidence=false`. Installed Blender, physical GPU/device-loss/Remote
Desktop, clean-machine, longevity, matching-build capture, and live-game
acceptance remain explicitly separate.

## Candidate-package provenance

`PackageSelfTest` and `CliDispatchTests` require package provenance to be
either entirely absent for an ordinary developer build or complete and
canonical for a release candidate. `global.json` pins the .NET 10 SDK used by
the build. `package_csharp.ps1` constructs a deterministic ordered input set
covering the C# solution and projects, including untracked source files,
schemas, relevant DL1 documents, parity fixtures, and validation tools. It
streams an aggregate SHA-256 and embeds that value together with Git HEAD,
clean/dirty state, input count, source identity, and informational version.
The hidden packaged self-test must match all of those values exactly before
the executable and one-entry ZIP are committed, and `SHA256SUMS.txt` records
the same provenance as comments. Partial, malformed, or invocation-mismatched
assembly metadata fails closed.

C# validation receipts are content-addressed from the selected C# gate inputs, C# test assemblies, checked-in compatibility fixtures, renderer identity, installed-DL1 evidence when applicable, and the optional Blender executable. No Python interpreter, external archive, or Python-environment hash participates in packaging or validation.

## WPF/D3D startup acceptance

`tools\validate_dl1_wpf_startup.ps1` launches the exact selected executable
through a private, non-public startup switch in an isolated LocalAppData,
AppData, temporary, bundle-extraction, working-directory, and minimal-PATH
environment. The WPF window is moved offscreen but still shown so both real
`HwndHost` child windows and their D3D11 swap chains initialize. The bounded
gate requires exactly two viewport hosts, each in Ready state on Hardware or
the documented WARP fallback and each with at least three presented frames.
It then drives the real window through six deterministic alternating
compact/expanded sizes. Every step requires both hosts to remain Ready,
advance their presented-frame counts, and publish swap-chain pixel dimensions
equal to the ceiling of their arranged WPF DIP sizes times the current DPI
scale. It rejects unexpected diagnostics, crash reports, missing or malformed
evidence, stale/mismatched pixels, and a nonzero or timed-out process before
atomically publishing the schema-2
`artifacts\validation\dl1-wpf-startup-smoke.json`.

`tools\validate_dl1_wpf_startup_stress.ps1` repeats that exact gate against one
unchanged executable and proves stable executable SHA-256 and informational
version across all runs before atomically publishing
the schema-2 `artifacts\validation\dl1-wpf-startup-stress.json`, including
each run's per-step DIP/DPI/pixel and frame-advance evidence. The current
development machine passed two consecutive hardware-D3D runs with both
viewports presenting. Every rebuilt release candidate must pass five
iterations, and the receipt must name that candidate's exact executable
SHA-256. These gates use no interactive computer control. They do not prove a
clean Windows machine, physical Remote Desktop, adapter hot-swap, forced
device removal or WARP fallback, or multi-hour stability.

## Still requiring release-machine runs

The installed 1.55 type-272 corpus gate is complete: 62 packs, 8,738
resources, and zero blockers. The following remain hardware, longevity, or
runtime-accuracy acceptance work:

- multi-hour editor sessions and repeated real retail-model switching;
- low-memory and multi-gigabyte logical-chunk pressure with the installed
  base/DLC corpus;
- process termination during the final filesystem rename (the tests model a
  denied replacement, not power loss);
- forced Direct3D device loss, adapter/display changes, physical Remote
  Desktop transitions, and forced startup-WARP fallback;
- screenshot-pixel comparison against approved controls and broader
  skin/variant-table selection;
- exact shader, normal, specular, mask, and post-process comparison;
- crash recovery after an actual process failure; and
- interrupted writes on every supported filesystem/storage configuration.

No result in this file is live-game-validated evidence.
