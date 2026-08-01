# DL1 Python-to-C# regression map

The Python application remains an archived external regression reference at
`F:\DyingLightTools\ReAnimated - Python`. Installed DL1 1.55 assets,
matching-build decompiles, and captured game behavior are authoritative when
they conflict with Python. This map records where the current C# tests
exercise the same DL1 concern as a stable Python regression group. It is a
review aid, not a parity certificate:

- **Cross-checked subset** means at least one C# regression uses Python-derived
  expected values or an equivalent format control.
- **Invariant subset** means both implementations test the same rule, but not
  necessarily with the same fixture or emitted bytes.
- **Partial** means material Python behaviors in that group are still absent
  from the C# path.
- A passing C# test proves only the named C# behavior.

Python filenames below are relative to `tests/`. C# class names are in matching
files under `tests/ReAnimated.Tests/`.

## Applicable DL1 regression groups

| DL1 concern | Stable Python regression group | Current C# evidence | Honest status and remaining difference |
| --- | --- | --- | --- |
| Transform convention and binary FBX animation import | `test_fbx_transform_contract.py`, `test_fbx_animation_stacks.py`, `test_fbx_declared_timebase.py`, and `test_animation_fbx_domain_loading.py` | `CoreTransformTests`, `FbxBinaryReaderTests`, `FbxSemanticEvaluatorTests`, and `PythonSemanticParityTests` | **Cross-checked subset.** The live semantic oracle now compares all six Euler orders, a combined pivot/pre/post/scale transform, hierarchy globals, and bounded linear-curve samples through both production evaluators. C# also covers signed axes, units, stack selection, declared/custom time modes, and malformed-input rejection. Python wrapper normalization, model-domain preflight, reflected/custom-model policies, and its broad production FBX corpus are not all ported or differentially exercised. |
| DL1 ANM2 packed values, pages, semantic tracks, body, and mimic | `test_anm2_packed.py`, `test_anm2_multipage_writer.py`, `test_anm2_only_export.py`, applicable cached-decoder cases in `test_auto_root_cached_sparse.py`, and the DL1 portions of `test_mimic_builder.py` | `Anm2CodecTests`, `Dl1NameHashTests`, `Dl1AnimationExporterTests`, `EvaluationPipelineTests`, `PythonOracleParityTests`, and `PythonSemanticParityTests` | **Cross-checked subset; partial parity.** The ANM2 oracle compares exact packed bytes, generated/stock hashes, headers, descriptors, pages, and bounded samples. The bulk decoder parses layout once, reuses each page base, decodes every packed slot once, retains requested descriptor order, bounds materialized components, and checks cancellation throughout active work. Its stock `infected_turn_90r` output is bit-exact against random-access sampling at the Python control frames; synthetic DL1 controls lock selection and cancellation without adding a DL2 path. End-to-end body import also aligns rotation-equivalent Cayley samples into one quaternion hemisphere across 257 keys and 720 degrees while remaining equivalent to scalar conversion. The semantic oracle additionally compares consolidated mimic values after each implementation performs its own ANM2 encode/decode. It deliberately does not claim identical mimic bytes. Broad FBX-to-ANM2, FED, retail, rendering, and live-game seams remain open. |
| ANM2 timing provenance and reverse-FBX cadence | `test_anm2_provenance.py` and `test_anm2_fbx_timebase_resample.py` | `Anm2ProvenanceCodecTests`, `Anm2TemporalResamplerTests`, and `BlenderFbxHandoffTests` | **Direct invariant parity for all 17 nodes.** A public codec reads and atomically writes canonical schema-1 `.anm2.dlrmeta.json` sidecars under a 1 MiB/32-depth bound, validates every required and optional typed field, rejects boolean and oversized numeric impostors with one advisory, gates by ANM2 SHA-256 and frame count, and retains old sidecars without optional fields. The shared Blender-independent resampler preserves duration and exact endpoints, converts 381 samples at 30 fps to 305 at 24 fps, normalizes shortest-hemisphere quaternion interpolation, and keeps single-frame input singular. The retail Blender handoff consumes this same codec/plan and a binary staging assertion locks the first/last sampled translations. This does not map the remaining helper-sidecar or multi-Action reimport round trips. |
| Root motion, required helpers, and authored-versus-preview ownership | `test_root_motion_actor_basis.py`, `test_unified_root_mapping.py`, relevant DL1 cases in `test_auto_root_cached_sparse.py`, and the motion-accumulator cases in `test_anm2_to_fbx.py` | `Dl1AuthoringPolicyTests`, `EvaluationPipelineTests`, `EvaluationAuthoringLayerIntegrationTests`, `BlenderFbxHandoffTests`, and `PythonSemanticParityTests` | **Cross-checked synthetic subset; partial.** Python and C# now produce matching root and `0xCCC3CDDF` values for `inplace`, `bip01`, and `motion`, including translation/heading ownership, on one versioned root-level control. A separate parented-pelvis regression removes world-up heading in global space, reconstructs a finite child-local transform, preserves the animated parent, and restores bind-global position. The real C# Blender staging path additionally bakes active motion into the unique Root while preserving exact helper samples, and proves a present but static helper leaves Root unchanged. Python automatic root selection, cumulative long-turn reporting, the raw/no-bake Blender toggle, cross-rig motion scaling, private-fixture cases, and every legacy target profile are not mapped by those controls. |
| Retarget maps, bind correction, optional bones, bone layers, and IK | `test_bone_map_v2_row_policies.py`, `test_retarget_profiles.py`, `test_shared_humanoid_mapping.py`, `test_helper_retarget.py`, and the DL1 cases in `test_semantic_retarget_workflow.py` | `RetargetCompatibilityTests`, `HelperRetargetPolicyTests`, `RetargetTwoBoneIkTests`, `IkConstraintLayerTests`, `EvaluationAuthoringLayerIntegrationTests`, `AnimationDocumentTests`, `ViewModelWorkspaceTests`, `UserReportedRetargetAcceptanceTests`, and `PythonSemanticParityTests` | **Cross-checked bind-basis and helper-policy subsets; partial.** The live semantic oracle compares global bind-basis correction for a mapped root/camera helper and proves a reviewed target-only camera retains its bind local. Direct C# regressions now also cover all five helper component masks, rest-relative fan-out with distinct target binds, rotation-delta bind translation/scale retention, non-finite rejection, deterministic repeated evaluation, unmapped-helper bind preservation, conservative exact helper discovery, and fail-closed ambiguous helper identities. The authoritative evaluator now resolves body retargeting, authored edits, IK, and DL1 root policy before helper overrides; a non-commuting EyeCamera control proves identical final globals through single-frame preview/export, batch sampling, and ANM2 sampling after a Head edit and root correction. The anatomical solver ports the Python oracle's head-end fallback, absolute terminal hand/foot orientation, and palm-space finger direction reconstruction. The exact reported `Taunt.fbx` frame 85 against installed `zombie_voleteile_blue` locks the reconstructed head axis and all 30 target finger phalanxes; a self-contained control proves a wholly absent source middle digit is conservatively fanned out from one complete neighboring source chain, while partial and ambiguous same-digit chains fail closed. C# additionally covers suggestion order, fan-out reporting, policy-sensitive fingerprints, incompatible deform detection, and deterministic IK. The WPF mapping workspace exposes helper fan-out policies and preserves individual row/required-target-bind review decisions; hidden required rows and unchecked fallbacks block export instead of being bulk-accepted. Conservative name-only roles bridge common Mixamo, Character Creator, and Unreal-style axial/limb/finger names to DL1 proposals at review-required confidence; duplicate roles remain unmapped. A keyed IK layer can now be sampled at every rational-timeline frame into ordinary authored FK override tracks; overlapping enabled chains fail closed and the solver layer is removed in the same undoable editor transaction. WPF regressions prove selected-layer key insertion plus immutable, undoable, persisted enable, additive/override, finite layer-weight, selected-bone mask, and translate/rotate/local-scale gizmo edits without changing rest hierarchy, keys, or scope. Python's multilingual analysis, profile migrations, and broader source-family heuristics remain Python-only. |
| Mimic, FED expressions, and non-destructive facial authoring | `test_animation_fbx_domain_loading.py`, `test_fbx_declared_timebase.py`, `test_mimic_builder.py`, `test_mimic_profiles.py`, and the DL1 cases in `test_mimic_project_extensions.py` | `FbxFacialAnimationAdapterTests`, `Dl1MimicGenerationTests`, `Anm2CodecTests`, `Dl1AnimationExporterTests`, `AnimationDocumentTests`, `MimicProjectWorkflowTests`, `FacialFbxReviewViewModelTests`, `ProjectExportMimicCliTests`, `MorphEvaluatorTests`, `FedReaderTests`, `FedRetailCorpusTests`, `EvaluationAuthoringLayerIntegrationTests`, `ViewModelWorkspaceTests`, `AssetCompactMeshTests`, `AppDl1MeshPreviewAdapterTests`, `RetailMorphValidationTests`, `PythonSemanticParityTests`, and `PythonFedParityTests` | **Direct bounded facial-scan/mimic-generation controls plus cross-checked FED and retail position-delta subsets; partial family and visual support.** The C# facial adapter reads selected scalar `DeformPercent` curves without model topology, preserves declared rational timing and exact tick spans, and provides a bounded facial-only fallback timebase. The embedded Common46 profile is descriptor-unique and canonical; conservative auto-mapping supports reviewed consolidation and blink companions while retaining unresolved descriptor semantics. The production mimic builder deterministically consolidates sources into TX scalar ANM2, reports active unmapped sources instead of guessing, distinguishes percent from normalized FBX values, and verifies output through the production decoder. Those six Python mimic-builder/profile nodes and three facial scan/timing nodes are directly mapped. The two legacy Python project/copy nodes are exact exclusions, not evidence for the separate native C# project workflow. WPF and CLI regressions prove that a separately hashed `MimicAssetId` is persisted, reopened only against the exact retail target, synchronized with the body clip, and contributes decoded values to mimic export. The WPF facial-FBX workflow now copies the user-authored FBX into project-relative storage, retains its scalar curves on the body timeline, persists the explicit unit and `FacialSourceAssetId`, refuses coexistence with mimic ANM2, reopens fail-closed on hash/target/profile/mapping identity, and produces mimic ANM2 from reviewed/locked mappings through the shared evaluator. CLI `export-project` performs the same facial-source validation and synchronization. The bounded FED oracle compares two author-generated payloads by ordered names and exact float32 bits, duplicate diagnostics and first-match lookup, one mapped layer consolidation, and reject decisions for 16 malformed/strict-duplicate inputs. Manual facial keys are a final authored override, and WPF FED application requires complete exact-name coverage. Installed 1.55 evidence proves all five nonempty `player_1_fpp.fed` expressions resolve on `player_1_fpp` while `player_man_01_tpp.fed` is incompatible with `player_1_tpp`; no alias mapping is fabricated. The retail path decodes target-major signed SHORT4 position deltas at `1 / 16384` before skinning. Profiles beyond Common46, broader model-family bindings, visual goldens, and Windows 1.55 game-capture comparison remain open. |
| Animation scripts, RP6L libraries, and bounded RPack access | `test_rp6l_library.py`, `test_script_targets.py`, `test_anm2_only_export.py`, and the animation-RPack case in `test_unified_release_smoke.py` | `AnimationScrCodecTests`, `AnimationScrEventParityTests`, `InstalledAnimationScrEventEvidenceTests`, `Rp6lAnimationLibraryTests`, `RpackArchiveTests`, `AssetCatalogTests`, `RetailAssetCatalogPersistenceTests`, `RpackRetailValidationTests`, and `PythonSemanticParityTests` | **Exact canonical no-event and event-bearing-layout SCR subsets plus bounded RP6L parity; partial artifact support.** Python and C# emit identical section bytes for a bounded mixed-case no-event SCR recipe, produce identical range-patched and appended bytes, agree on normalized records and invalid-record skipping, and reject the same six malformed/missing/duplicate/auxiliary cases. A separate three-sequence event-bearing oracle compares exact parse and timing-patch bytes, preserves three opaque 12-byte event rows plus section 1, and locks five event-layout rejection decisions with actionable diagnostics. Decompile-backed C# parsing derives the name table from the 56-byte record table and summed 12-byte event rows; exact installed 1.55 controls lock 7,698/5,925/12,689 sequences and 64,092/5,511/68,679 event rows in `anims_man_all`, `anims_player`, and `anims_player_man_all`, respectively, without embedding retail bytes. The RP6L oracle separately proves identical bytes and manifests for one sorted, uncompressed multi-animation/multi-script recipe, which C# reopens and extracts. The schema-3 SQLite inventory restores unchanged base/DLC/user providers with complete precedence and duplicate candidates; a canonical asset count and row manifest detect logically incomplete caches, while missing/changed inputs, cancellation, and corrupt databases fail closed to rescan or preserve the prior atomic snapshot. Large-pack startup fingerprints are bounded samples, not full-file hashes. SCR event-field semantics and encoding, append into stock event/auxiliary layouts, broad DLC scripts, RP6L conflict behavior, compressed/retail archive parity, broader manifests, and live-game acceptance remain separate gates. |
| Retail compact mesh hierarchy, rig inventory, and family filters | `test_compact_mesh_matrices.py` and applicable matrix/inventory cases in `test_model_import_preflight.py` | `AssetCompactMeshTests`, `Dl1RigDefinitionFactoryTests`, `Dl1RetailMeshClassificationTests`, `InstalledDl1RigFamilyProfileTests`, `InstalledDl1VisualReferenceControlTests`, `InstalledDl1OrdinaryNonFiniteUvEvidenceTests`, `InstalledDl1MeshCorpusAcceptanceTests`, `AppDl1MeshPreviewAdapterTests`, `BoneRenderRoleTests`, `InstalledArmoredSkeletonIntegrityTests`, `RpackRetailValidationTests`, `RetailMorphValidationTests`, and `RetailMaterialResolutionTests` | **Complete installed type-272 classification; profile-limited runtime presentation.** C# covers local/reference/global matrix reconstruction, cycle rejection, serialized matrix orientation, skin tables, stable deform/helper/camera/prop preview roles, bounded material-database slot names/raw load values, evidence-backed ABDM/type-8480 base-color resolution, target-major SHORT4 morph position deltas, submesh renderer remapping, dynamic rig inventory, and evidence-bearing filters for geometry, rig signature, family, perspective, facial support, provider/DLC, and variants. The Windows 1.55 Release corpus passes all 8,738 resources across 62 configured packs with zero blockers. Preview locks one minimum-index/highest-detail decoded LOD per entity, excludes exact `shadow_caster.mat` draw parts, and applies the stock-headless `player_1_fpp` visible-surface subset only to its validated content fingerprint; `player_1_tpp` retains its head. Four exact stock 1.55 geometry fingerprints retain raw `+/-Infinity` UV0 values with a visible material-fidelity warning after installed evidence proves the serialized half patterns, non-degenerate topology, exclusive material ownership, finite same-layout siblings, and raw preview publication. Fingerprint mismatch, NaN, changed layout/pattern, and generic ordinary materials remain blocked. `EyeCamera` and `RefCamera` are camera helpers; no `EyeRef` alias is inferred. Installed armored evidence remains 57 deform rows plus 20 helper-flag rows, of which 18 render as ordinary helpers and the two named camera rows render as camera helpers. Installed 1.55 controls classify Jade/Rais, generic infected, volatile, screamer, Demolisher, and goon; 30 player FPP/TPP resource rows are table-verified. Malformed payloads remain local errors instead of fabricated data. Composite resources with non-TRS hierarchy rows remain raw-bind-preview-only when their affected rows are outside all skin palettes. General skin/variant-table mapping, facial visual goldens, exact shaders and normal/specular/mask behavior, and game validation remain open. |
| Retail D3D viewport facing and local stock-editor comparisons | No stable Python implementation is treated as an oracle for this renderer contract. | `InstalledDl1MeshOrientationTests`, `InstalledDl1VisualReferenceControlTests`, `InstalledDl1VisualReferenceWarpTests`, `InstalledDl1OversizedRigWarpTests`, `RendererD3D11SmokeTests`, `RendererRawInfinityUvTests`, `RendererOffscreenGoldenTests`, `RendererSkeletonOverlayGoldenTests`, `RendererAuthoringOverlayTests`, and `RendererAuthoringStageGoldenTests` | **C# first-pass renderer controls, not retail-pixel or game parity.** D3D11 explicitly treats counter-clockwise triangles as front-facing and back-face culls them; WARP controls distinguish outward from reversed winding. A textured WARP control submits raw positive/negative infinity UVs without validation rejection or device removal, but deliberately asserts no material-correct pixel result. The neutral inspection light uses a 68-percent floor plus a 32-percent directional term so away-facing textured surfaces remain readable without claiming DL1 runtime lighting. A generated-data nine-stage WARP matrix locks exact pixel/coverage hashes for retargeting, root motion, bone editing, hand IK, authored morphs, a FED-derived expression layer, FPP `EyeCamera`/hands projection, helper/prop overlays, and attachments, plus CPU/GPU bounds where both paths use a directly comparable projection; its validator emits atomic inspectable BMPs and a hash-checked manifest. Exact-build/content-fingerprinted WARP readbacks separately render all ten supplied reference identities—headed `player_1_tpp`, headless `player_1_fpp`, headed `player_11_tpp`, headless `player_11_fpp`, `jade`, `armored`, `zombie_voleteile`, `zombie_screamer`, `brecken_cin`, and `anim_slums_door_a`—checking finite CPU bounds, lit non-background mesh pixels, visible skeleton/helper pixels, zero renderer diagnostics, and successful device state. Every deform row stays inside expanded mesh bounds, humanoid bilateral bind residual stays below 1.5 percent of model diagonal, and the door's legitimate off-panel prop/pivot helpers are handled separately. Opt-in authoring overlays cover an authoritative sampled root trail, current CPU morphed/skinned bounds, one selected-model union AABB, a skinned/morphed expanded-backface selection silhouette, and role-aware local axes. Per-draw palette remapping preserves complete retail rigs above 256 total rows while keeping each shader draw bounded to 256 matrices; exact installed 1.55 adapter/WARP controls cover all currently known physical rows above that threshold. The white character diamonds and yellow selected-door wireframe in the user's stock-editor references are bone/pivot overlays rather than mesh geometry. No retail assets or screenshots enter the synthetic oracle; the screenshots remain local comparison evidence only and are neither test inputs nor embedded/redistributed goldens. Exact shader, normal/specular/mask, post-process, retail screenshot-pixel, exact stock-editor selection pixels, and live-game comparisons remain open. |
| Project identity, persistence, and failure safety | `test_project_format.py`, `test_encoding_contract.py`, and applicable background/persistence cases in `test_unified_gui_regressions.py` | `CoreAnimationProjectTests`, `AppPersistenceTests`, `AnimationDocumentTests`, `ViewModelWorkspaceTests`, `FppProjectionProjectTests`, `MimicProjectWorkflowTests`, `FacialFbxReviewViewModelTests`, `ProjectExportMimicCliTests`, and `ViewModelTimelineTests` | **Deliberately divergent; not project parity.** C# validates and atomically writes its own camel-case schema, binds body plus exactly one hashed facial source (mimic ANM2 or retained user-authored FBX), preserves edit state, project-stored FPP projection and external movie-camera authoring inputs, and tests crash/log persistence. Game-validated profiles require exact 64-hex build and validation-capture fingerprints; project capture metadata is never trusted as validation by itself. Facial references must name source-animation assets and are never silently ignored by WPF or CLI evaluation. It intentionally rejects Python schema 1-10 without rewriting them, so Python migration, unknown-field preservation, and Qt workflow regressions are not satisfied by these tests. |
| DL1 FPP and movie authoring context | No stable Python implementation is treated as an oracle for the new runtime-preview contract. | `Dl1PreviewContextTests`, `RendererSceneSourceTests`, `FppProjectionProjectTests`, `RendererFppProjectionTests`, `LinkedTargetExternalPreviewTests`, plus `DL1_FPP_MOVIE_PREVIEW_EVIDENCE.md` | **C# first-pass evidence contract, not parity.** EyeCamera/RefCamera roles, project-persisted user/runtime-capture inputs, captured-aspect safe frame, separate infinite-far FPP-hands mesh projection, and external movie reference-camera state are explicit and preview-only. Reopening restores the saved FPP inputs/context and the external movie `IBaseCamera` transform/lens. The movie snapshot is passed through `Dl1PreviewInputs` and routed to the target viewport; a rig `RefCamera` never substitutes for it. FPP/Cutscene publish one evaluated target scene to both panes: target keeps the evaluated EyeCamera/movie camera and target-only hands projection, while source is an unlocked external orbit view with FPP hands projection disabled. The regression covers evaluated pose/skeleton, morph, attachment mesh, gizmo/selection mirroring, manual morph republishing, target-only navigation lock, projection ownership, and exact authored-source restoration for both modes. The evaluator can apply the decompile-matched, preview-only HSpine/HSpine1 basis subset from an explicit world/model-basis and vehicle-state snapshot before extracting `EyeCamera`; it preserves world translation/nonuniform scale, propagates descendants, fails closed on missing or ambiguous roles, and never changes authored/export sampling. This is not game validation. Runtime camera motion remains a labeled fallback; the full head-position solver and hand inertia remain visibly unavailable without live state, and matching-build game-capture comparison remains open. |
| Retail prop/weapon attachments | No stable Python implementation is treated as an oracle for the new retail-assembly contract. | `AttachmentAuthoringTests`, `EvaluationPipelineTests`, plus `DL1_ATTACHMENT_AUTHORING.md` | **C# first-pass invariant coverage.** Schema-1 bindings retain exact retail project identity, parent index/name, local TRS, and authored/preview ownership. Static and independently skinned bind-pose assets are composed as rigid meshes on the evaluated parent. The skinned bind-bake regression preserves decoded tint, base-color texture, and scene/FPP-hands projection role while clearing independent skin/morph/selection state. Independent prop animation, keyed attachment offsets, broad retail material fidelity, retail corpus proof, and multi-actor serialization remain outside this evidence. |

The current WPF facial-FBX action provides an explicit-unit, exact-target
mapping review and persists only reviewed/locked bindings; retaining those
source curves and generating the synchronized mimic ANM2 remain open. Retail
preview now applies exact decoded `Default` skin substitutions for the
installed player controls and consistently omits both exact shadow-caster and
resolver-proven zero-technique draws. Embedded animated-prop layouts retain
their palette-driving rows for skinning/editing while presenting compact
prop/helper overlays, and camera framing uses view-space extents rather than a
diagonal sphere. These refinements do not change the parity counts below.

The separate exact-node drift gate prevents the table from overstating broad
file-level parity. It inventories all **616** currently collected Python node
IDs as **92** directly mapped, **317** explicitly excluded by the first-release
scope, and **207** still pending. Any added, removed, reordered, source-changed,
or newly unclassified node fails the live audit; moving a node to mapped
requires named C# evidence. See `DL1_PYTHON_SUITE_AUDIT.md`. This quantified
inventory is not a release-complete result: the 207 pending nodes remain open.

Some Python files mix applicable animation tests with excluded DL2, `.crig`, or
custom-model cases. Mapping a file above maps only the described DL1 invariant,
not every test in that file.

## Explicitly excluded from this first-pass map

### Dying Light 2

All `test_dl2_*.py` groups, DL2 fixtures, DL2 branches inside mixed tests, and
native DL2 writer expectations are excluded. The C# ANM2 reader rejects a DL2
header rather than treating that as support. This first pass is Dying Light 1
only.

### `.crig` and legacy custom-model authoring

The following Python families are not C# parity evidence:

- `test_chrome_rig.py`, `test_model_authored_crig.py`, and
  `test_exact_model_crig_animation_integration.py`
- `test_custom_fbx_*.py`
- custom-model cases in `test_model_import_*.py`,
  `test_model_layer_validation.py`, `test_model_rig_contract_palette.py`, and
  `test_model_workspace_preoutput.py`
- `.crig` builder and custom-model branches inside otherwise mixed FBX or
  retarget tests

The C# retail-mesh preview path does not imply a C# custom-model compiler,
shareable `.crig` workflow, or legacy custom-model RPack parity.

### Python reverse export and Blender helper round trip

`test_anm2_to_fbx.py`, `test_dl1_helper_roundtrip.py`,
`test_dl1_helper_roundtrip_blender.py`, `test_blender_fbx_integration.py`, and
the remaining round-trip sidecar tests remain authoritative for the Python
single-clip ANM2-to-FBX-to-ANM2/Blender path.
Only `test_blender_exports_first_anm2_frame_and_animation` is directly mapped
to the bounded C# handoff. The separate first-sample BindPose node remains
pending because, after inspecting the FBX bind, it rebuilds ANM2 from that FBX
and compares decoded animation values; C# multi-Action/reverse reimport is not
implemented.

The C# first pass now has a separate bounded inspection/editing handoff covered
by `BlenderFbxHandoffTests`: the exact selected retail rig and mesh, decoded
base-color files, deterministic multi-Action timing, real named helpers,
original-cadence unresolved-track sidecars, unique-root motion-accumulator
handling, cancellation, staged output, and strict post-export binary-FBX
inspection. The public Codecs-owned provenance reader/writer and temporal
resampler now directly match the 15 provenance and two cadence regressions,
including hash/frame gating, bounded malformed-input handling, 381-at-30 to
305-at-24 conversion, exact endpoints, and quaternion continuity. Those tests
use a fake process runner for deterministic contract
coverage, and the final single-file self-test proves packaged helper extraction
and identity. `InstalledBlenderFbxAcceptanceTests` additionally passes one real
Blender 5.2 background export on the exact DL1 1.55
`zombie_voleteile_blue` resource: 97 bones, six mesh parts, five decoded
base-color files, and two exact-named generated ANM2 Actions pass the strict
written-FBX validator before atomic commit. The gate deletes every
retail-derived output after the run. Exact vertex/normal/winding/skin values,
sampled retail-Action values, other Blender versions, and broader model-family
coverage remain open.
Multi-Action FBX reimport is not claimed; Python remains the reverse-conversion
oracle. See `DL1_BLENDER_RETAIL_HANDOFF.md`.

### Toolkit-specific GUI and legacy packaging

Python Qt widget behavior, Python EXE layout, release-manifest membership, and
schema-migration UI tests are not mapped one-for-one to WPF tests. C# WPF,
Direct3D, cancellation, persistence, and packaging need their own acceptance
criteria; similarity of screen purpose is not parity.

Private fixtures and tests that only run when a local retail installation is
available are useful evidence, but they are not deterministic public-CI release
gates by themselves.

## Release gates still open

Do not describe the C# application as a replacement release until each
applicable gate has evidence attached:

- [x] Define reviewed, redistributable, versioned bounded ANM2/name-hash and
  semantic-authoring parity corpora with hashes, tolerances, recipes, and scope
  notes. Broader format and corpus seams remain in the next gate.
- [ ] Run Python and C# over the same FBX, ANM2, mimic, FED, SCR, RP6L, and RPack
  cases; compare accepted/rejected inputs, normalized animation values, target
  ownership, diagnostics, and deterministic output bytes where the format has
  one canonical encoding. The bounded transform, retarget, root/helper, mimic,
  author-generated FED, canonical no-event SCR, opaque event-bearing SCR,
  eleven SCR rejection controls, and canonical RP6L seams are now covered.
  Retail FED/model-family breadth, event semantics and authoring, broad
  stock/DLC scripts, broader rejected-input parity, production fixtures, and
  release-scale coverage remain open. The exact 616-node audit currently
  records 92 mapped, 317 scope exclusions, and 207 pending nodes; no
  file-level row above overrides that node-level gap inventory.
- [x] Implement the chosen non-migration workflow: C# schema 1 uses a distinct
  format marker and legacy Python schema 1-10 projects are refused without
  modification. No converter/importer is promised for this release.
- [x] Complete automated end-to-end DL1 authoring regressions:
  locate retail assets, load a model/rig, import body and mimic animation,
  retarget, edit bones, preview, export, reopen the generated library/RPack, and
  verify identities and animation values. Both the redistributable hermetic
  control and optional installed-1.55 `armored` control cover this chain.
- [x] Decode and bound retail target-major SHORT4 position deltas, map them to
  global channels, and hand them to the renderer. Synthetic plus installed
  `player_1_tpp` and `armored` controls cover this asset-format seam.
- [x] Add and execute a fail-closed installed Windows 1.55 offline gate. It
  verifies the production build fingerprint, executes 19 exact installed
  corpus/rig/material/facial/authoring/WARP tests, locks the 62-pack,
  8,738-resource, zero-blocker corpus totals, and atomically publishes a
  hashable evidence receipt. The game is never launched and this does not
  satisfy physical-hardware, clean-machine, capture, or live-game gates.
- [ ] Resolve or deliberately defer with release-visible labels the broad
  multi-LOD, representative skinning, facial visual-golden, and game-capture
  gaps. The generated nine-stage authored pipeline now has exact WARP
  pixel/coverage hashes and inspectable atomic captures for retarget, root,
  bone-edit, IK, morph, FED, FPP, overlay, and attachment stages. Preview also
  controls minimum-index/highest-detail decoded LOD selection, exact
  three-name non-display shadow-material omission, and one
  content-fingerprinted `player_1_fpp` stock-FPP authoring subset, including
  the stock-omitted shoulder flashlight. The decoded `Default` skin directly
  hides no entity on that resource, so the supplemental subset is not claimed
  as skin-table behavior. General
  skin/variant-table mapping is still open. The bounded material seam resolves
  evidence-backed ABDM rows and type-8480 DXT1/3/5 base-color textures into
  BC1/2/3 neutral preview data. Delimiter-bounded `_dif_`/`_clr_` variant
  tokens are retained, including the installed `player_11` shirt control;
  the missing `beard.mat` ABDM row, unsupported two-item `player_beard`
  candidate, and unevidenced alpha-test technique remain explicit rather than
  inventing a beard binding;
  retail facial/FPP screenshot goldens, broader material-corpus coverage,
  exact techniques/shaders, and normal/specular/mask behavior remain open.
- [ ] Validate FPP and movie preview against matching Windows-build captures.
  Runtime camera state, the decompile-matched HSpine basis subset, the full
  head-position solver, and hand inertia cannot be promoted to game validated
  without that evidence.
- [ ] Complete the remaining physical-hardware, longevity, low-memory,
  installed multi-gigabyte pressure, device-loss/fallback, Remote Desktop, and
  crash-recovery runs. Hermetic controls already cover corrupt input,
  cancellation, cache corruption/eviction, atomic replacement failure,
  repeated catalog/cache open-close, 256 bounded GPU asset switches,
  non-torn resize requests, and recovery-policy decisions. The private
  startup gate and repeated same-binary runner additionally exercise both real
  HwndHost swap chains through six alternating window sizes per run, requiring
  Ready state, frame advancement, and exact agreement between
  renderer-published pixels and DPI-scaled arranged host sizes without
  interactive computer control. Those local controls do not replace clean
  hardware, physical Remote Desktop/display transitions, forced device
  removal or WARP fallback, low memory, multi-hour longevity, or actual crash
  recovery.
- [ ] Validate the single-file self-contained App on a clean Windows machine,
  including embedded schemas/license/status documentation, hashes, no developer
  SDK dependency, actionable startup/failure behavior, WPF startup, and the
  packaged headless command surface. WPF and all 12 supported CLI verbs now
  dispatch from the same shipped executable; there is deliberately no second
  CLI executable. The packaging host is SDK-pinned and now embeds and
  self-validates a deterministic candidate-input SHA-256, including untracked
  C# inputs, together with Git HEAD/state and a canonical source identity.
  Isolated offscreen WPF/D3D startup and a repeated same-executable run pass on
  the development machine and publish hashable receipts. Every rebuilt release
  candidate must produce a fresh receipt against its exact executable hash.
  Clean-machine execution and physical Remote Desktop remain open.
- [ ] Perform a bounded live DL1 acceptance pass for representative body,
  mimic, root-motion, helper, SCR/RP6L, and RPack outputs. This is release
  evidence, not a requirement to test every authoring adjustment in game.
- [x] Publish an explicit first-release support matrix. DL2, `.crig`, and
  legacy custom-model workflows remain excluded unless separately implemented
  and validated.

## Validation commands

C# build and tests:

```powershell
.\build_csharp.ps1 -Configuration Debug
```

Python behavioral oracle:

```powershell
py -3 -m pytest -q
```

Self-contained candidate package, which runs both suites:

```powershell
.\package_csharp.ps1
```

Exact installed Windows 1.55 offline acceptance and atomic receipt:

```powershell
.\tools\validate_dl1_installed_acceptance.ps1
```

Passing these commands is required evidence for the current tree. The
installed receipt and bounded cross-implementation controls are not substitutes
for clean-machine, physical renderer/Remote Desktop, capture, and live-game
gates above.
