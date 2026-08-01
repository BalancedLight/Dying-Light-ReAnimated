# C# DL1 rewrite

> **Status:** active Dying Light 1 C# application. The legacy Python/Qt
> implementation has been retired from this repository and archived at
> `F:\DyingLightTools\ReAnimated - Python`.

The archived Python implementation remains an external regression reference,
not the authority for known game behavior. Installed DL1 1.55 assets,
matching-build decompiles, and captured game behavior take precedence when
evidence conflicts. The two implementations do not share runtime state and the
C# app does not modify legacy projects.

## Current refactor status

The current C# tree has automated coverage for its own immutable animation
domain, binary FBX and DL1 ANM2 paths, retarget/evaluation pipeline, body and
mimic output, RP6L/animation-script handling, bounded retail-asset access,
Direct3D 11 editor passes, WPF state, persistence, cancellation, and crash
reporting. These tests establish C# implementation invariants; they do not by
themselves establish equivalence to every Python workflow or acceptance by the
retail game.

The DL1 ANM2 bulk path parses sampler layout once, reuses page-base tables,
decodes each unique packed slot once, supports an ordered descriptor subset,
bounds the number of materialized components, and checks cancellation during
active decode loops. A pinned stock DL1 clip is bit-exact against random-access
sampling, while synthetic sampler-v1 controls lock slot-count, selection, and
cancellation behavior. Body import also aligns adjacent rotation-equivalent
Cayley samples to one quaternion hemisphere; the regression spans 257 keys and
two complete turns so authoring curves do not acquire sign-flip discontinuities.

Retail type-320 rows can now be played directly from the explorer by
double-click or **Play Animation**. A single click remains metadata-only. The
project **Animations** tab records source and target models, body/facial roles,
cadence, duration, mapping state, diagnostics, and the active clip, with
activate, rename, duplicate, source-rebind, remove, reveal, and facial-attach
commands. Retail entries are reused only for the same animation identity and
the same source-model fingerprint. Exact same-provider type-322
`AnimationScr` timing is used when unambiguous; conflicts require an explicit
choice and missing timing is visibly marked as a manual 30 FPS fallback.

Every ANM2 document has an immutable source binding and one decoded track
partition: exact source-rig descriptors are body tracks, exact source-mesh
morph descriptors are facial tracks, `0xCCC3CDDF` is an auxiliary motion
track, and unresolved or colliding descriptors remain diagnostic. Mixed
body/mimic files play synchronously, while attached facial clips retain their
native cadence and default to neutral outside their own range. Changing the
target invalidates the target map without changing the source. **Rebind
Source** creates a clean document rather than mutating authored mappings or
edits. Exact same-rig playback copies decoded local transforms directly;
cross-rig playback remains blocked until a valid fingerprint-bound map exists.

Retail playback defaults to `Recorded`. Optional preview accumulation applies
the auxiliary track as an actor/world transform to the mesh, rig, attachments,
and root trail together, leaving every skeletal local transform and
actor-relative skinned vertex unchanged. Compact retail mesh skeletons are not
required to contain the accumulator descriptor. Retarget/edit views collapse
to one pane when their complete scenes are identical, while **Compare** forces
the split. Different source/target scenes retain Raw Source and DL1 Target
panes; geometry-free FBX is labelled skeleton-only. FPP and Cutscene always
retain the external orbit pane plus the evaluated `EyeCamera` or movie-camera
pane. Both panes are published as one generation-tagged frame so stale scrub
or clip-switch work cannot mix generations.

The WPF first pass can discover and filter indexed retail assets, import FBX
or ANM2 animation, lock/review/save a fingerprint-bound mapping, scrub the
shared timeline, author additive/override bone layers, key validated two-bone
IK chains, deterministically bake a marked IK layer into ordinary FK override
tracks through a cancellable Jobs-panel operation that never commits a partial
or superseded result, apply FED expressions as non-destructive morph layers, key facial
values, select the DL1 root policy, and export body, mimic, or synchronized
body+mimic ANM2 through the same evaluator used by preview. The editor exposes
selected-layer enable, blend mode, finite weight, selected-bone interpolation,
and selected-bone mask controls as immutable undoable project edits; subsequent numeric or
translate/rotate/scale gizmo keys stay on that selected layer and preserve its
scope. Rotation commits normalized quaternions; scale is local-only,
multiplicative, positive, and bounded. Bone-edit tracks persist bounded
`Linear` or `Step` interpolation. `Linear` is the schema-1 compatibility
default and uses shortest-hemisphere normalized quaternion slerp; `Step` holds
one complete local TRS until the next key, and later keys preserve the selected
track mode. Cubic curves remain deferred until
the project format has an explicit tangent contract. Common Mixamo, Character Creator, and
Unreal-style humanoid names can seed conservative semantic mapping proposals,
but those aliases remain review-required and ambiguous/twist rows are never
guessed.
Facial search filters only the visible preset/control collections and retains
the authoritative morph objects and weights. Manual facial keys are stored as
a final authored override containing absolute totals, avoiding a second
application of sampled mimic values or earlier additive FED layers. FED
application from the WPF editor requires every source row to resolve against
the selected retail mesh's exact morph inventory; a wrong-family or partial
match is refused instead of producing a plausible but incomplete face. The
installed `player_1_fpp.fed` eye/blink expressions resolve completely against
the fingerprinted `player_1_fpp` mesh, while the unrelated
`player_man_01_tpp.fed` control vocabulary is deliberately rejected for the
`player_1_tpp` mesh. Broader model-family morph mappings are not guessed or
exposed without an evidence-backed contract.
Live facial sliders also pass through the active Raw/DL1 display policy before
their transient values reach either viewport. DL1 clamping, activation
threshold, and active-target limits therefore affect only the displayed copy;
the slider/authored values remain unchanged.
The persisted enable switch is the explicit A/B control and still honors each
layer's authored or preview-only scope, rather than adding a hidden override.
The target viewport can follow evaluated `EyeCamera` or a project-stored
external movie `IBaseCamera` while its ordinary orbit camera remains intact;
camera-helper axes and unavailable runtime stages are labeled in the editor.
During FPP and Cutscene preview, the left pane is a freely orbitable external
view of that same evaluated target on the same timeline: it mirrors the
evaluated pose, skeleton, morphs, attachments, selection, and gizmos while
deliberately disabling the FPP hands-projection override. Only the target pane
is camera/gizmo locked by the evaluated preview camera. The authored source
scene continues updating behind a display-only override and is restored intact
when the user leaves FPP/Cutscene.
Raw and DL1-profile pipelines are explicit toolbar choices stored separately
from the versioned profile, so Raw inspection does not erase saved validation
evidence. Deform bones, ordinary helpers, `EyeCamera`/`RefCamera`, and prop
helpers retain distinct render roles; ordinary helpers and props are hidden by
default and can be toggled without changing the authored rig.
Viewport controls can additionally show local bone axes, current CPU-reference
deformed bounds, one union bounds highlight for the selected retail model, a
deformed expanded-backface selection silhouette, and a root-motion trail. The
silhouette uses the same skin and morph inputs as the visible draw. The trail
samples at most 2,048 poses from the authoritative exportable pipeline on a
cancellable worker; preview-only procedural motion is excluded.
For Blender inspection, the selected decoded skinned retail model can be
handed to an optional local Blender installation with its decoded base-color
textures and up to 64 compatible ANM2 clips as named Actions. The output is
strictly parsed before commit; real named helpers stay on the armature while
unknown descriptors are preserved at their original cadence in hash-validated
sidecars. Action bones plus motion and unresolved sidecar rows are decoded in
two explicit ordered descriptor passes, so the handoff does not materialize
every source track in one full matrix. This is a bounded one-way
inspection/editing handoff, not yet a
multi-Action FBX-to-ANM2 round-trip contract. See
`docs/DL1_BLENDER_RETAIL_HANDOFF.md`.
At startup the app also streams the installed Windows `DyingLightGame.exe`
through a bounded, versioned SHA-256 identity reader and shows the result beside
the active preview tier. A saved Game-validated profile is visibly downgraded
unless both its Windows build identity and validation-capture fingerprint match
independently trusted evidence. Project metadata is never trusted by itself,
and no trusted capture registry ships yet. See
`docs/DL1_BUILD_FINGERPRINT.md`.
Decoded mesh profiles now expose static/skinned/container status, exact rig
signature, bounded family, explicit FPP/TPP, facial support, provider/DLC, and
variant filters with per-result evidence and confidence. Indexing does not
eagerly decode the mesh corpus: selection fills a session cache and the
explicit cancellable browser action classifies at most 128 generally filtered
rows per batch. Undecoded and failed rows remain visibly unknown and never
satisfy a positive or negative capability filter. See
`docs/DL1_RETAIL_RIG_PROFILES.md`.
Completed catalogs persist as schema-3 SQLite snapshots containing the full
provider/precedence/duplicate-candidate inventory. Unchanged base, DLC, and
configured user roots restore without re-enumerating their resources; changed,
missing, logically incomplete, or corrupt inputs fail closed to a full rescan
and atomic replacement. Canonical asset counts and ordered row hashes protect
the saved inventory, while strict WAL/SHM handling refuses unsafe replacement.
Large packs use length, timestamp, and five bounded 16 KiB fingerprint windows
rather than an unbounded full-file startup hash, while provider open-time
identity checks remain authoritative. The WPF and integrated CLI paths consume
one Core-owned LocalAppData contract, so both entry points reuse
`AssetCatalog\dl1-assets.sqlite3` and `AssetCache\Rp6l` instead of silently
building separate caches.

Important gaps remain visible and fail closed:

- Python schema 1-10 projects are rejected, not migrated or rewritten.
- Event-bearing DL1 AnimationScr resources now use the decompile-backed
  56-byte-record/12-byte-event layout, expose raw event counts, and preserve
  the complete opaque event table during timing patches. Exact installed 1.55
  controls cover `anims_man_all`, `anims_player`, and
  `anims_player_man_all`. Event-field semantics, new event encoding, and append
  into event/auxiliary layouts remain unsupported; see
  `docs/DL1_ANIMATION_SCR_EVENT_PARITY.md`.
- The C# app can orchestrate the optional retail-mesh/multi-Action Blender
  handoff described above, but it is not yet a replacement for the validated
  Python single-clip ANM2-to-FBX-to-ANM2/helper round-trip. One exact installed
  Blender 5.2/DL1 1.55 volatile mesh, texture, and two-Action writer/strict-reader
  control passes; broader value-level and retail-animation corpus comparison
  remains open. Schema-1 ANM2 provenance and reverse-FBX cadence are no longer
  app-local or Python-only: the public bounded codec and temporal resampler
  directly cover all 15 provenance and both cadence regressions, including
  atomic canonical writes, hash/frame gating, malformed huge-number handling,
  exact endpoints, and shortest-hemisphere quaternion continuity.
- Retail type-272 morph names, entity/LOD channel mappings, and target-major
  SHORT4 position deltas are decoded at the proven `1 / 16384` scale and reach
  the D3D11 morph-before-skinning path. See
  `docs/DL1_RETAIL_MORPH_EVIDENCE.md`. Normal-delta payloads are not present in
  this compact row, and game-validated facial deformation still requires
  captured Windows 1.55 visual comparisons.
- Retail compact-mesh material database names, slot mappings, and raw load
  values are decoded with bounded per-row diagnostics. Evidence-backed ABDM
  rows resolve type-8480 DXT1/3/5 base-color textures into bounded BC1/2/3
  D3D11 previews, while missing or colliding identities retain diagnostic slot
  tints. Broader material-corpus proof, parameters, exact techniques, shader
  variants, and full retail rendering fidelity remain unresolved; see
  `docs/DL1_RETAIL_MATERIAL_TEXTURE_EVIDENCE.md`.
- Seven named Windows 1.55 family controls promote to dynamic rigs. Some large
  composite survivor/zombie resources still contain unsupported
  singular/sheared transforms (and one unexplained LOD range), so their family
  and rig signature remain visibly unknown instead of being guessed.
- FPP `EyeCamera` preview, separate hands projection, and project-persisted
  external movie `IBaseCamera` capture/routing exist. A rig `RefCamera` is
  never substituted. Both panes show the same evaluated target in FPP/Cutscene:
  the target owns the preview camera/projection, and the source pane owns the
  unlocked external orbit view with FPP hands projection disabled.
  The editor explicitly supplies its canonical Y-up, -Z-forward identity-model
  basis and vehicle-inactive authoring state to the HSpine/HSpine1 subset.
  That subset is decompile matched but not game validated. Runtime-dependent camera motion, the full head-position
  solver, hand inertia, and matching-build game-capture validation remain
  labeled fallback, unavailable, or open as appropriate.
- Dying Light 2, `.crig`, legacy custom-model authoring, and the Python Qt GUI
  are not parity targets for this DL1 first pass.

In the source checkout, see `docs/DL1_REGRESSION_MAP.md` for the exact
Python-to-C# test mapping, exclusions, and release gates, and
`docs/DL1_FPP_MOVIE_PREVIEW_EVIDENCE.md` for the decompile-supported preview
boundary. `docs/DL1_PARITY_HARNESS.md` documents the reviewed, bounded
Python/C# ANM2 and name-hash oracle. Candidate-package documentation
completeness remains a release gate.

## Solution boundaries

| Project | Responsibility |
|---|---|
| `ReAnimated.Core` | Immutable rigs, clips, transforms, projects, edit layers, and preview contracts |
| `ReAnimated.Codecs` | Strict DL1 FBX/ANM2/RP6L/FED and retail mesh decoding, ANM2 provenance, and cadence resampling |
| `ReAnimated.DL1.Assets` | Steam discovery, resource identity, catalog, precedence, and bounded caches |
| `ReAnimated.Retargeting` | Compatibility analysis, exact/semantic/manual maps, and bind correction |
| `ReAnimated.Evaluation` | The authoritative retarget/edit/IK/root/morph evaluation pipeline |
| `ReAnimated.Renderer.D3D11` | Hosted Direct3D 11 viewport and render-thread ownership |
| `ReAnimated.App` | WPF/MVVM editor workspaces and asynchronous job orchestration |
| `ReAnimated.Cli` | Headless inspection, validation, conversion, and build surfaces |
| `ReAnimated.Tests` | Codec goldens, corpus controls, math, rendering, and stability regressions |

No UI layer may parse binary formats or independently evaluate animation.
Both preview and export consume the same authored pose. DL1 procedural preview
layers are applied afterward and are never silently baked into output.

## Project compatibility

New projects retain the `.dlraproj` extension but identify themselves with:

```json
{
  "format": "dl-reanimated-csharp-project",
  "schemaVersion": 1,
  "game": "dying-light-1"
}
```

Python schema 1-10 projects are rejected without being rewritten. Retail
assets are represented by installation-relative, pack-qualified identities
and hashes; game bytes and decoded caches are never embedded.

## Fidelity labels

- **Raw** means decoded retail data and authored animation only.
- **DL1 profile** adds explicitly selected, versioned runtime emulation.
- **Game validated** additionally requires matching Windows build and capture
  evidence.

The viewport must visibly downgrade fidelity when the installed executable,
asset fingerprints, or enabled procedural layers do not match the validation
profile.

Installed-executable matching is implemented. It is a necessary gate only:
`Game validated` also requires the saved validation-capture fingerprint to
match an independently trusted capture registry entry for the same preview
context and settings. Project data cannot establish that trust by itself, and
no trusted capture registry ships yet. The built-in body, FPP, and movie
profiles therefore remain labeled `DL1 profile`.

## Building

On Windows, install the .NET SDK selected by `global.json` and run:

```powershell
.\build_csharp.ps1 -Configuration Debug
```

That command performs a locked restore, builds the solution, and runs the C#
test project. For a direct test rerun:

```powershell
dotnet test .\tests\ReAnimated.Tests\ReAnimated.Tests.csproj --no-restore
```

Animation work can use content-addressed validation tiers instead of rerunning
unrelated acceptance suites:

```powershell
.\tools\validate_csharp.ps1 -Tier Focused
.\tools\validate_csharp.ps1 -Tier Hermetic
.\tools\validate_csharp.ps1 -Tier Release -Configuration Release `
  -SkipUnavailableOptionalBlender
```

`Focused` covers the changed playback, ViewModel, and renderer surfaces;
`Hermetic` expands to local codec/evaluation, WPF, and renderer controls; and
`Release` adds renderer goldens, Python parity, AnimationScr parity, installed
DL1 1.55 named animation controls, the existing mesh-corpus evidence check, and
the optional Blender handoff. Each gate writes an atomic passing receipt keyed
to its exact source/test inputs, fixtures, dependencies, environment, and
relevant game/renderer identity. A candidate-identical gate runs at most once
and reports whether it ran or was reused. `-ForceAll` deliberately invalidates
all tier receipts. Missing Blender can be explicitly skipped, but no passing
Blender receipt is written. The unchanged 8,738-mesh corpus is verified from
its build-bound receipt for animation-only changes and is not executed again.

Every solution build produces the ordinary framework-dependent WPF build and
also publishes exactly one self-contained file at
`artifacts\csharp\solution-build\<Configuration>\win-x64\DLReAnimated.exe`.
The command-line surface is an in-process library and cannot emit a second
application host. To create the fully validated Windows candidate:

```powershell
.\package_csharp.ps1
```

The packaging script performs the Release C# build and tests and requires a
passing complete external Python behavioral-oracle result before writing exactly one self-contained
`win-x64\DLReAnimated.exe`, a ZIP containing only that executable, and
`SHA256SUMS.txt` under `artifacts\csharp`. Native dependencies, WPF resources,
schemas, license, and DL1 status documentation are embedded in the executable.
The expensive Python run is content-addressed separately from C# work. By
default the oracle is loaded from `F:\DyingLightTools\ReAnimated - Python`;
`-PythonOracleRoot` selects another archive. A passing receipt is reused only
while every Python source/test/fixture/schema,
requirements file, interpreter/dependency environment fingerprint, oracle
contract version, and optional-Blender mode remain identical. Missing,
malformed, or mismatched receipts rerun the full oracle and are replaced only
after both the main suite and isolated Qt lifecycle control pass.
`-ForcePythonOracle` deliberately bypasses the receipt.
The SDK is pinned by `global.json`. Before compilation, packaging hashes a
deterministically ordered candidate-input set covering the C# solution,
untracked source files, schemas, relevant status documents, parity fixtures,
and validation tools. Git HEAD, clean/dirty state, input count, aggregate
SHA-256, and their canonical source identity are embedded in the assembly and
written as comments in `SHA256SUMS.txt`; the packaged executable's fail-closed
self-test verifies those values against the packaging invocation.
The same packaged executable opens WPF when no CLI verb is supplied and
dispatches the 12 supported headless inspection, validation, catalog, RPack,
and project-export verbs. There is no second shipped CLI executable. The
bounded cross-implementation ANM2 and semantic-authoring oracles can also be
refreshed and compared with:

```powershell
.\tools\validate_dl1_parity.ps1
```

The separate opaque event-bearing AnimationScr differential gate is:

```powershell
.\tools\validate_dl1_animation_scr_event_parity.ps1 `
  -Configuration Release
```

Add `-InstalledEvidence` to require the exact fingerprinted Windows 1.55
`common_anims_PC.rpack` controls.

The exact installed Windows 1.55 offline gate is:

```powershell
.\tools\validate_dl1_installed_acceptance.ps1
```

It preflights the production build fingerprint through the same application,
executes 19 required installed corpus, rig, material, facial, authoring, and
WARP tests, checks the locked 62-pack/8,738-resource corpus totals, and only
then atomically replaces
`artifacts\validation\dl1-installed-acceptance-1.55.json`. The receipt binds
the runner, Release app/test assemblies, installed build, corpus report, exact
test results, repository state, and canonical evidence hash. Dying Light is
never launched; installed Blender, physical hardware/Remote Desktop,
clean-machine, capture, and live-game evidence remain separate gates.

Passing the applicable commands is necessary for a candidate package.

Generated application binaries, test output, packages, and caches remain
outside version control.
