# Character Rig Studio implementation status

The full studio is under development in the existing C# model authoring and
Conform workspace. The current implementation provides persistence, profile
validation, source-linked geometry queries, reviewed existing-rig correspondence
and unrigged body-joint proposals, a draft generated-body/binding workflow, and
source-linked persistence of authored rig/skin/rest/morph edits.
The seven-stage navigation now routes these tools inside Conform. A complete
geometry-driven body/hand/eye workflow, full helper calibration and native
acceptance of generated rigs remain unfinished.

## Seven-stage workspace routing

Conform now exposes Import, Detect, Fit, Helpers & Hooks, Skin, Animate and
Verify & Export. Existing hierarchy, animation-stack and diagnostic controls are
shared with their original inspector tabs. Rigged conversion retains its existing
template/mapping/scale/refine/check sequence inside Fit; those are fitting steps,
not another top-level studio workflow. Unrigged guides live in Detect and Fit,
generated binding and correction tools in Skin, stress/source/selected-stock
review in Animate, and diagnostics/evidence history/build actions in Verify.

Legacy models can browse the stages without acquiring a session or changing
their package/build inputs. An explicit start chooses repair, adaptation or
geometry-based generation as appropriate to the source. Existing source bytes,
weights and clips remain intact. Component classification is available for
rigged and unrigged inputs; anatomy participation remains a separate switch.

The current stage is saved in `RiggingSession.Stage`. Navigation preserves
authoring inputs, review records and completed proposals while cancelling active
interaction or work. Completed detection and weight previews are rebound across
workflow-only metadata updates. Starting a compatible session can adopt their
existing session identity. Stage review creates only an authoring-review record;
it never creates a capability pass. The evidence panel labels historical results
and does not infer current profile/build/provider/actor applicability.

Compiler input hashing now uses version 3 and excludes navigation, review history
and job epochs while retaining source bytes, authored layers, recipes, masks and
other build decisions. Existing older fingerprints are refreshed on rebuild.
Build completion can retain a receipt across workflow-only metadata changes,
preserving the latest stage/review data; actual authoring edits still reject the
old build snapshot. Navigation adds no undo step. Explicit session starts and
review records use the shared authoring history.

This connects the current tools; it does not complete every stage's acceptance
conditions. Hand/eye detection, full joint/chain/rest editing, profile-specific
helper calibration, motion/contact comparison and native acceptance remain
explicit work items.

## Model package contract

Schema 6 introduced the optional `RiggingSession`; schema 7 adds an optional
source-linked authored layer. Packages predating the session migrate without
studio participation, preserving their existing conformance settings,
authored helpers, facial presets, secondary motion, animation clips and embedded
source data. The existing final authored-rig owner remains the emission boundary.

The authored layer is a deterministic, bounded binary package entry referenced
by SHA-256 and length. It stores source control-point weights/position deltas,
original polygon-corner normal deltas, morph edits/normal-array presence, stable
bone identities and per-component inverse binds. It contains no duplicate base
mesh or triangles. The saved bone table is authoritative only with a matching
source/target layer contract. Capture verifies source topology and rejects
unsupported component, UV or triangle/material reassignment changes.

Reopen decodes the original FBX, verifies the source render contract, restores
the authored hierarchy and applies edits using original source identities. Draw
palettes, inverse references and partitions are rebuilt together; split corners
and morph correspondence remain intact. Conformance now completes its new
authored inverse binds after weight remapping, and both the UI apply transaction
and CLI capture the result before publishing a package. Metadata, material and
secondary-motion package updates preserve the authored payload.

Changed FBX bytes require explicit reconciliation when this layer exists; the
current replacement path refuses to discard those edits. Older conformed
packages whose saved rig differs from the source but lack enough replay data
receive an explicit recovery error. Automated migration of those older derived
states remains unfinished. No silent restoration of the original rig is used
as a substitute for successful authored replay.

Conformance decisions now record a correspondence method. Older documents without
that field use `LegacyNameRoles`; new wizard/CLI work uses `GeometryHierarchyV1`.
Existing saved proportion choices retain their meaning. New conformance work
defaults to keeping source segment lengths; selecting DL1 proportions remains
an explicit choice. Rest-direction conversion is still a separate part of applying
the existing conformance, not a consequence of analyzing a correspondence.

A session records source identity, component selection, guides and locks, the
selected profile reference, semantic entity and asset identities, helper recipes,
separate scale decisions, component ownership, backend identities and validation
history. It persists all seven stage states. Changing upstream decisions
invalidates downstream reviews without erasing historical evidence. Background
results must match the session generation, revision and input fingerprint;
restoring an undo snapshot creates a new generation so an old job cannot become
current again.

Native representation and component ownership can remain explicitly unknown.
Neither a detector confidence value nor a user review sets a validation facet to
Passed. Reimported source changes require reconciliation and review before source
hierarchy inspection.

`RiggingSessions.ObserveSourceHierarchy` reads actual document parent indices and
translates them into stable entity identities. Reordering recipe rows cannot
retarget those observations. Planned helper changes are not treated as observed
source changes.

## Profile dependency and assignment checks

`RigProfileResolver` resolves selected capabilities, their prerequisites, and
conditional role dependencies. Consumers declare the capabilities they serve.
An unrelated, incomplete NPC consumer therefore does not contaminate a selected
partial view-model capability. Optional branches require their prerequisites
when used; unknown requirements and incomplete build evidence remain Unverified.

The resolver rejects cyclic or inconsistent dependency selections and attributes
transitive roles to their native consumers. Iterative traversal handles deep
graphs without recursive stack growth. The resolver never edits an imported rig.

`RigRoleAssignmentValidator` checks profile identity, role multiplicity, exact
native names or evidenced aliases, entity type, owning asset, direct/ancestor
parent constraints and observed hierarchy legality. Diagnostics identify the
role, entity when available, consumer and corrective operation. Unknown imported
entities are retained. Missing hierarchy observations remain Unverified; an
observed wrong parent fails.

These assignment checks do **not** verify role-specific frame axes, fitted
bounds, skin eligibility, component composition, LOD retention, compilation or
runtime behavior. Those checks still require their measured profile rules,
preparer integration and corresponding evidence. A successful assignment
assessment is not a native-readiness certificate.

## Validation evidence

The seven facets remain separate: geometry and bind, mapping, runtime node
completeness, motion, compiled verification, loaded-resource identity and live
scenario. Each has Passed, Failed, Unverified and NotApplicable states.

Receipts are scoped to source/input/profile/build identities and, where relevant,
compiler, compiled logical content, loaded resource/provider, runtime session,
actor, clip and scenario. Compile evidence cannot satisfy loaded-resource or live
facets. Native receipts must identify the corresponding build. A loaded logical
resource hash must match the intended compiled logical resource hash.

## Role-aware preparation

Studio sessions now supply per-entity `RigEntityFramePolicy` decisions to the
existing `Dl1CustomModelRigPreparer`. Imported entities default to retaining their
source frame. Explicit generated-deform decisions can use the existing aiming
pass; other frames are not changed by that pass. Helper recipes have one frame
owner and cannot compete with a separate entity frame policy.

Contact, camera, socket and structural helpers require retained or solved bounds.
Their footprints are not replaced by segment proxies. Explicit source bounds can
retain zero extents, while generated proxies keep the existing nonzero contract.
Missing helper creation, parent changes or renames must be materialized in the
source document before preparation; the preparer does not silently apply part of
an editing transaction.

For the studio path, local matrices are rounded to the native float precision
before global matrices are reconstructed. Inverse-global references are derived
from that final hierarchy and rounded for emission. The same immutable authored
contract supplies preview, MSH output, CHR transforms and skin palette bindings.
Exact affine source matrices are retained instead of forced into orthonormal
frames. TRS edit projections remain available, and studio presentation poses
carry exact local/global matrices so rebasing does not discard their affine
detail. Editing one local TRS retains its existing affine residual.

Models without a studio session retain the legacy preparation and pose paths.
These changes establish frame/bounds preservation and serialized consistency;
they do not establish measured native contact behavior, component-mask policy or
runtime compatibility. The complete role validator, transaction UI and compiled
read-back acceptance remain required.

## Helper transactions in the existing workspace

The helper panel provides **Apply prepared helpers**, **Undo model edit** and
**Redo model edit**. Prepared authored helper recipes are materialized together,
with parents before children. Existing unmentioned helpers and imported bones
are retained. Duplicate names, absent parents and illegal creation requests fail
before the document is adopted. Reapplying an unchanged helper batch is a no-op.
Imported-node renames and reparenting still require the complete rig transaction
path; helper application does not perform a partial imported-rig rewrite.

Manual helper edits synchronize stable entity IDs and frame decisions into the
studio recipe. Locked recipe fields reject edits before publication. TRS edits
retain an existing affine residual, and the inspector retains helper scale when
applying translation/rotation offsets.

Authoring history stores complete immutable model snapshots, including surfaces,
palettes, inverse binds and texture payloads. Conformance application no longer
clears its own undo entry. Helper, conformance, synchronized setting and texture
changes participate in this history; a new edit clears the redo branch. Undo and
redo restore helper/source selections and clip position, while studio session
generations advance to reject stale background results. This history is local to
the Models authoring workspace; remaining stage controls, cross-workspace command
routing and recovery integration remain part of RS-011.

## Explicit animation-component emission

Studio source export now requires a component policy for every emitted animation
entity. Each policy records POS/ROT/SCL ownership, an explicit component mask,
an explicit animation LOD selection, and artifact-backed ownership/LOD evidence.
Draft sessions may retain unresolved choices, but the source writer rejects
unresolved export policies before replacing output files. It does not infer
component masks from physical node zero, bone-name patterns or detector confidence.

Policies are matched by semantic entity ID against the prepared hierarchy. Their
source token combinations and evidence are written to a companion
`<resource>.components.json` audit. That audit explicitly leaves runtime behavior
unverified. Mixed ownership still requires an identified composition rule; the
emitter does not implement or certify that native composition itself.

Legacy documents without a studio session retain their existing BSCR output.
The studio emitter supports explicit NONE and POS/ROT/SCL combinations and the
LOD_0 through LOD_3/LOD_OFF source tokens. The official compiler validation path
compares the resulting compiled entity fields with the exact emitted decisions,
including name canonicalization and missing/ambiguous entity detection. A changed
component or LOD field fails validation before the model RPack is published.
The compiler receipt retains these read-back rows, and its contract identity was
advanced so older validation receipts are not treated as current.

The source-mask mechanics and compiled storage do not prove that a stock clip,
procedural controller, runtime scale operation or IK consumer combines those
channels correctly. Those measured ownership rules and live scenarios remain
required. Optional private compiler staging roots allow isolated diagnostic jobs
to stay in a configured workspace rather than the default application-data area.

Studio compiler validation also retains the source writer's actual prepared
contract and compares its animation entities with the compiled hierarchy. Checks
cover unique canonical names, representation, semantic parent relationships,
local matrices, inverse references and bounds. Read-back records include both
source physical indices and compiled indices, so sibling reordering cannot
silently change identity. Invalid/non-finite fields or changed frames and bounds
fail before publication. This check covers the prepared animation hierarchy;
full per-vertex palette, morph, variant and dependency verification remains part
of the broader compiled acceptance work.

## Schema maintenance

The checked-in `schemas/dlrmodel.schema.json` is generated from the actual model
serializer contract. It describes structural types and schema versions; C#
validation owns numerical and cross-field invariants.

From the repository root, use the schema tool with an explicit local build root:

```powershell
dotnet run --project tools/ModelSchemaTool/ModelSchemaTool.csproj -c Release --artifacts-path <build-root> -- write schemas/dlrmodel.schema.json
dotnet run --project tools/ModelSchemaTool/ModelSchemaTool.csproj -c Release --artifacts-path <build-root> -- check schemas/dlrmodel.schema.json
```

Use a build root appropriate to the workstation. Machine-specific paths, retail
payloads, dumps and character-specific controls belong in private work artifacts,
not ordinary fixtures or release packages.

## Installed corpus capture

`Dl1RigCorpusCollector` audits explicitly requested logical resources using the
existing asset catalog. It records the selected and shadowed physical candidates,
full readable-content hashes, source includes, aliases, sequence clip matches,
raw sequence values and supported CHR v4 data. It distinguishes readable,
missing and failed resources from incomplete semantic inspection. Dynamic and
ambiguous references are retained rather than resolved speculatively.

With `Dl1CompiledCorpusInspector`, capture also records individual resource item
hashes and flags, compact entity names/types/parents/matrices/bounds, structured
skin definitions, the dedicated embedded animation-script alias field, and
compiled sequence tables. Embedded mesh aliases extend the dependency traversal.
The inspector must match both the physical asset identity and the captured full
resource hash before its evidence is accepted.

The catalog's configured precedence is reported explicitly. A unique catalog
match is not proof of native lookup order. Concatenated RPACK payload hashes,
individual item hashes and source-file hashes identify different byte domains;
do not compare them interchangeably. Compiled skin definitions are not an
authored CHR companion. A source SCR can exist without a compiled bank of the
same name. Compiled sequence tables do not establish their source includes,
clip bindings, event behavior or runtime activation.

The optional source-extension inventory on `Dl1RetailProviderSet.Create` leaves
the normal catalog unchanged unless requested. `tools/RigCorpusTool` opts into
SCR, ASCR, DEF and CHR source inventory for private audits. Its JSON request has:

- `installPath`: selected local installation.
- `cacheDirectory`: private cache outside that installation.
- `roots`: entries containing `name` and `resourceType`; null resource type means
  a virtual-file path, and a numeric type means a compiled RPACK resource.
- `limits`: optional traversal and byte bounds.

Run the tool with the request path and a private output report path. Build it
with an explicit `--artifacts-path` appropriate to the workstation. Capture does
not launch the game, modify the installation, or publish data. The report can
contain proprietary decoded information and machine paths, so keep requests,
reports and installed-control inventories outside the public repository.

The collector supports cancellation, bounded reads and cyclic graph traversal.
An exceeded total budget aborts capture; a resource read or decode failure stays
visible in the report. Capturing a report successfully does not mean every
requested resource is present or that an export is ready.

## Source geometry provenance and topology

The FBX importer now retains a shared normalized source control-point inventory
for each geometry/model component. Every render corner retains its original
control-point and polygon-vertex identity; every emitted triangle retains its
source polygon and triangle ordinal. Material and palette partitioning remain
derived render organization, so they do not become source identity. These maps
are rebuilt from the embedded FBX rather than stored as a second editable mesh.
The source-to-authoring matrix records the mesh placement, geometric transform,
axis conversion and unit conversion already applied to those control points.
Analysis uses these normalized coordinates without applying the matrix again.

Each component also retains the original skin-deformer, cluster, joint and
entry identities with their unnormalized weights. Import-time retention flags
and per-control-point retained/discarded totals expose the deterministic top-four
reduction and sub-threshold losses. Multiple cluster contributions to the same
joint remain individually traceable. This evidence describes the original import;
it does not replace current edited weights or claim that a binding is suitable
for animation. All material/palette partitions share the same source inventory.

`FbxSourceGeometryAnalysis` validates that the selected components and render
partitions cover the declared source geometry, then adapts that provenance into
generic topology analysis. It honors included/anatomy component selections and
uses original normalized source coordinates. It rejects missing or duplicated
triangles and inconsistent component inventories instead of silently analyzing
a partial model. Source weight validation checks coverage, entry uniqueness,
joint identity, finite totals and consistent retention. Missing or conflicting
weight/coordinate provenance requires reimport rather than a guessed fallback.

`SourceMeshTopology` identifies source-index-connected islands, unused control
points, boundary edges, nonmanifold edges and winding conflicts. It never welds
coincident positions or repairs input. Traversal is iterative, checks cancellation
and validates count limits before allocation. High-valence adjacency lists are
expanded once rather than rescanned per incident triangle.

`TriangleSpatialIndex` supplies deterministic CPU nearest-surface and two-sided
ray queries through a bounded AABB tree. Results retain input triangle ordinals
and barycentric coordinates; distance bounds use normalized authoring metres.
Degenerate triangles remain available as segments/points for nearest queries.
Construction and queries support cancellation. Count and numeric bounds are
safety limits, not a measured large-character performance certification.

`SourceGeometrySpatialQueries` maps those results to source component and polygon
identities. An optional component restriction can query a hidden surface without
selecting a closer detached component. Unknown component IDs fail instead of
falling back to the whole model. Component exclusions come from the analysis
snapshot, and the FBX adapter rejects decisions from a different source revision.
The spatial index can be discarded and rebuilt without editing source geometry,
weights, render partitions or morphs.

This remains analysis infrastructure. Automatic component/anatomy classification,
body/hand/eye detectors, automatic binding, interactive fitting, source-revision
job integration and full fixture/performance acceptance remain release work.

## Geometry-assisted existing-rig correspondence

The existing `RigCorrespondenceSolver` accepts a `RigGeometryEvidence` snapshot.
The FBX adapter pairs current rest vertices and current palette weights with the
current exact bind frames. It validates row identities and bind state rather than
pairing preserved original FBX geometry with a rig already changed by conformance.
Original skin-region analysis remains separate and includes discarded influences;
both forms respect source identity and component boundaries.

The geometry method uses joint positions, selected-surface influence support,
ancestry, outgoing branch directions and supported descendant role anchors alongside
names. It does not consume a weighted pelvic root as an extra motion root merely
because the target has one. Optional axial slots cannot steal a later supported
head or spine role. Horizontal normalization follows the body anchor, avoiding a
torso reference-plane shift caused by forward-reaching hands. Hierarchy intervals
and cached branch directions avoid repeated scans of entire parent chains.

Candidate scores and competing source names remain available in the Conform mapping
table. Authors can select source bones directly or accept reviewed proposals;
those decisions become explicit role overrides. Inferred/ambiguous mappings and
missing core correspondences prevent applying the geometry-assisted preview until
addressed. Unmapped source anatomy stays retained by default. Correspondence itself
does not move vertices, replace weights or change proportions.

The CLI reports candidate evidence and review status. `--legacy-correspondence`
selects the original mapping method; `--strength 1` explicitly requests DL1
proportions. A generated report/package is not evidence of native compatibility.

This is a deterministic heuristic correspondence method, not a calibrated anatomy
predictor or the unrigged body detector. Automatic orientation solving, complete
partial-rig/profile handling, full pose/shape challenge coverage and the broader
fitting/skin/native acceptance gates remain incomplete. Candidate acceptance does
not set any runtime validation facet to Passed.

## Interior geometry and analysis seam proxies

`SourceGeometryVolume` rebuilds topology from the selected source triangles and
checks boundary edges, nonmanifold edges, winding, degenerate triangles and zero
volume. Closed candidates support interior/exterior queries using bounded ray
intersections with agreement checks. This does not certify absence of all
self-intersections. Unreliable or ambiguous input remains unknown instead of
being filled automatically.

Disconnected closed islands are filled separately and unioned. A single shell
provides a signed distance to its input surface; multiple shells provide the
minimum of their signed fields. That union field is not an exact Euclidean
distance to the combined outer boundary. Nested surfaces remain traceable as
input surfaces even when they lie inside another solid.

`SourceGeometrySeamProxy` creates disposable analysis adjacency for exact
coincident boundary edges. Only unique, oppositely directed pairs within a
component are joined. Ambiguous overlaps, same-direction pairs and near-but-unequal
positions remain unresolved. Original vertices, triangles, skin weights and
morph correspondence stay in the source snapshot. Proxy hits can be mapped back
to original triangle corner identities. This is not an authored-mesh repair or
an arbitrary hole-filling operation.

`SourceVolumeGrid` samples metric cell centres with explicit cell budgets and
cancellation. Unknown fields are unavailable, not zero-valued solid samples.
Geometry and lattice fingerprints include the actual selected coordinates,
topology and sampling settings. A diagnostic mode can retain unreliable cells;
automatic consumers can require a complete field.

`VolumeInteriorGraph` supplies deterministic six-neighbor lattice paths through
known interior cells, with optional weighting by the signed-field margin. It
keeps exact requested cell endpoints, reports disconnected regions, and enforces
visit and numeric budgets. It does not snap user guides. Sub-cell obstacles and
continuous fitted curves still require validation/refinement; these paths do not
themselves prove safe anatomical placement.

These services feed the first unrigged body-proposal pass described below.
Complete body/component classification, graph-constrained body/hand/eye fitting,
resolution adaptation and the full challenge-set acceptance remain unfinished.

## Draft body detection in Conform

`AnatomicalRigDetector` now proposes pelvis, spine, neck/head and paired limb
positions from a complete volume grid in a supplied upright/left frame. It uses
persistent lower branch separation, cross-section structure, medial support and
region-constrained paths. Temporary leg contact does not automatically become
the pelvis merge. Hinge proposals compare full-chain segment fits to reject
ordinary lattice stair-stepping; unobservable hinges remain explicit low-strength
interpolations. Positions follow observed geometry rather than native template
dimensions. Evidence strength is heuristic, not calibrated prediction confidence.

Explicit guides constrain regional paths and retain their requested world
positions. Invalid/out-of-region guides remain unchanged and produce assistance
diagnostics. Source, grid and configuration fingerprints accompany the result.
`AnatomicalDetectionAdoption` uses existing session revision tokens, preserves
locked and unrelated guides, and records draft landmarks without emitting bones
or replacing skin weights. Stale source/grid/job results are refused.

The Detect stage now has an unrigged body-guide command,
cancellation, sampling resolution, left-axis choice, proposal selection and an
explicit draft-guide adoption action. The source mesh is rendered with no rig;
proposals use independent line/cross overlays with no bone-index interaction
binding. Saved draft guides restore as stored guide data.

Saved guides can be selected and positioned with independent X/Y/Z translation
handles or numeric model-space coordinates. Handle identity is the stable guide
ID, never a bone/palette index. Dragging updates only the guide overlay; release
commits a single immutable session/document transaction. Selection, model, tab,
metadata or input changes cancel pending moves. Unchanged persistence snapshots
retain model identity, so autosave does not invalidate otherwise current edits.
Undo/redo restores positions and selection while renewing stale-job generations.
The guide panel exposes the shared model undo/redo commands directly. Pending
workspace recovery also gates timer and window-close autosave until Restore or
Dismiss, so opening an empty workspace cannot overwrite the recovery being offered.

Pin/unpin is explicit. Optional mirroring requires an unlocked reciprocal pair
and reflects the moved position across the session's declared symmetry plane.
New paired anatomical proposals receive explicit reciprocal IDs when adopted;
existing pair decisions are retained. Mirroring is off by default and an empty
drag does not symmetrize existing asymmetric positions. Guide edits invalidate
their earlier approval/evidence without changing meshes, bones or skinning.
Native viewport Escape handling cancels active pointer interactions; full mouse
and keyboard UI acceptance still requires the interactive suite.

These are model-space guide controls. The remaining fitting interaction tools,
orthographic/clipping inspection, depth-aware selection, editable symmetry plane,
local/world frame controls and joint/chain/rest editing remain unfinished.

Current synthetic coverage includes T/A poses, bent limbs, altered segment
proportions, scale/translation, exact locks, unsupported-shape refusal, stale
adoption and null-rig overlay integration. This is not full challenge-set
acceptance: automatic up/front inference, reliably separating garments/touching
anatomy, open-geometry fallback, precise rotation pivots, hand/eye detection,
binding and native validation remain incomplete. The head proposal is an
interior geometric centre and still requires pivot fitting review.

## Draft generated body workflow

Saved anatomical guides can now create a 19-node draft authoring rig: a motion
root and 18 body deform nodes. `GeneratedBodyRig` uses guide coordinates and a
fixed semantic parent graph, with deterministic stable IDs and +X segment frames.
Terminal head/hand/foot skin influences are point handles; their frames continue
the incoming direction without extending the deform segments. Imported rigs
cannot enter this replacement path. The source mesh stays unchanged.

The Conform panel exposes component processing/anatomy selection and separate
keep-current, automatic or explicit rigid binding choices. Unsaved component
choices disable jobs. Automatic binding uses the selected anatomy volume;
fully fixed assignments use a direct path with no volume requirement. Selected
source control points are validated before their weights are transferred to
original draw corners and regenerated palettes. Session, source, point/handle,
volume and calculation fingerprints reject stale or mismatched results.

Build and bind are distinct undoable transactions with asynchronous work and
cancellation. The source-linked layer retains the result through package reopen.
Generated role identities also feed the existing hierarchy observer and source
rig semantic roles. Body-guide position editing currently precedes generation;
revising an already-generated joint without undo/rebuild remains unfinished.

The real synthetic FBX integration executes detection, guide adoption, generation,
binding, save/reopen and project handoff. Assignment coverage is not deformation
approval. The ordinary compiler path requires explicit animation component and
LOD policies for generated nodes; these can now be selected in Helpers & Hooks.
Complete hands/eyes, helpers, correction tools, quality benchmarks and native
behavior remain release work.

## Animation channel decisions

Helpers & Hooks exposes channel ownership, emitted POS/ROT/SCL and
animation LOD for observed bones and helpers in a current studio session. New
policies start undecided. Selecting a node loads its saved mask/LOD; owner edits
default to keeping each node's own existing channels. This preserves mixed
composition rules and evidence. Applying to all listed nodes is an explicit
operation, separate from selecting or editing a row.

`RigComponentPolicyAuthoring` checks source/session freshness and observed owned
entity identities. It rejects unknown owners, unsupported masks/LODs, foreign or
unobserved IDs and attempts to preserve incomplete decisions. Changed ownership
and LOD carry source-backed UserOverride evidence, recording authoring intent
without creating native capability validation. Unchanged channels retain their
original evidence. Changes invalidate animation review and participate in model
undo/redo; source FBX, geometry and authored-layer bytes remain intact.

The private synthetic generated-body workflow now passes the official compiler
both before and after package reopen. Compiled read-back validates all 19 node
names/parents/frames/bounds and their selected component/LOD bits. This confirms
source/compiled policy transfer; it does not establish stock-bank binding,
deformation quality, LOD runtime behavior or native Player acceptance.

## Remaining release work

### Source-point weight correction

The Skin stage provides explicit source-weight inspection, influence
heatmaps, point/range or whole-component selection, persistent influence locks,
rigid/fractional assignment, normalization and bounded mesh-edge smoothing.
These operations run on original component/control-point identities. Neighbors
come from source triangle edges within each component; smoothing does not infer
cross-component or positional seam welds. Conflicting corner weights or inverse
binds are refused for reconciliation rather than silently averaged.

Corrections are previewed before a separate apply transaction. The point table
shows before/after weights, locks, influence-cap removal and source-MSH rounding
error using the same quantizer as export. Native packing can add further error;
the compiled read-back remains the authority for the emitted result. The
heatmap changes preview UVs/materials only and retains skinning and morph
streams. It shows pending influence values, not a new deformation-quality pass.

Locked fractions stay exact, including absent/zero exclusions. The set operation
preserves its requested target when capping other influences. Smoothing uses
synchronous snapshots and bounded influence lookup work. Invalid constraints
refuse the whole transaction. Generated rebinding carries the saved locks and
refuses incompatible rigid assignments. Source frames, original expert inverse
binds, source geometry, UVs and morphs are retained; newly assigned influences
use their current exact bind inverses. Weighted bone metadata and rig signatures
are updated coherently when a new bone receives weight.

The source-linked layer persists applied changes; locks live in the versioned
session and invalidate Skin-stage reviews. Undo/redo restores both. Metadata-only
saves can rebind a pending preview only when its source, rig, surfaces and
session remain identical. Source/model changes and cancellation reject pending
work. The private morph-bearing correction fixture compiles before/after reopen
and compiled point weights confirm the requested edit. Native visual quality,
complete quantization inspection across output variants and artist acceptance of
stress deformations remain unverified or unfinished.

### Viewport strokes and mirrored corrections

The weight panel now exposes a source-surface brush and saved mirror settings.
Painting deliberately displays unposed source geometry, so picking and the
visible surface agree even with expert noncanonical inverse binds. It does not
paint a moving animation or bake a displayed pose. The brush uses the nearest
ray-hit triangle and follows within-component mesh edges; disconnected nearby
geometry and occluded components cannot acquire weights through proximity.
The pointer ray uses physical client pixels, the actual scene rectangle,
letterboxing and the near clip plane. Existing gizmos retain priority outside
painting; inactive/missed brush gestures retain camera fallback.

A stroke accumulates the strongest falloff per source point, with interpolated
samples between pointer positions. Release computes and applies one undoable
correction. Escape, capture/focus loss, changed source/target or changed authoring
inputs discard the unfinished stroke. Background application is cancellable and
checks source/snapshot identity before commit. A visible radius guide and point
count accompany the transient stroke; changed weights are published on release.
Strokes are bounded to 20,000 original points and 4,096 samples, with at most 64
interpolated samples per pointer update. Oversized gestures fail explicitly.
Dense-scene latency and native pointer interaction still require interactive
acceptance; isolated gesture tests do not prove those gates.

Mirroring is opt-in and stores an explicit influence pairing, model-space plane
and distance tolerance in the session. Pairing is symmetric and unambiguous;
saving a new pair replaces prior pairings for its two influences. Source points
must have a unique reciprocal nearest counterpart in the same component.
Ambiguous, unmatched and nonreciprocal cases remain diagnosed. Selected missing
counterparts block the transaction instead of producing partial mirrored edits.
Paired target fractions are computed from the same original weights, including
centreline/overlapping requests, and existing locks remain exact. Impossible
combined fractions refuse the edit. Mirrored set-weight corrections and brush
strokes are supported; normalization/smoothing require mirroring to be disabled.
Manual correspondence overrides for ambiguous geometry remain future work.

Generic tests cover source-surface selection, disconnected depth layers,
mirrored/centreline constraints, locks, save/reopen, gesture source/target
replacement, cancellation and one-stroke undo/redo. The private paired-correction
fixture compiles before and after reopen; decoded weights confirm changes on
both source points. This is source-to-compiled evidence, not native-game
deformation approval.

### Joint stress and morph review

Skin and Animate support temporary multi-joint local rotation offsets,
signed morph weights, pose-amount scrubbing and a rest-to-peak-to-rest cycle.
These are review controls, not saved rest edits or animation tracks. Preview
does not change model-package bytes or its compiler input fingerprint. Applied
model weights are evaluated; pending weight corrections must be applied first.
Stress review and unposed weight painting are mutually exclusive. Model/source,
rest-frame or morph-inventory changes clear incompatible offsets; metadata-only
changes preserve them. Leaving the workspace or disposing it stops the cycle.

`RigStressPose` rotates within each declared local TRS frame while retaining the
exact affine residual, including shear, reflection and non-unit scale. A zero
amount preserves exact rest matrices without a decomposition round trip. The
renderer adapter now publishes exact local as well as global matrices. The
prepared-rig rebase path uses exact source bind globals for exact poses, retaining
the existing projected-bind route for ordinary TRS animation samples. This
prevents a projected reference from being applied twice to an exact rest pose.

Bounded measurements use `CpuMeshDeformationEvaluator`, the existing reference
for the vertex shader, with identical morph inputs at rest and in the stress
pose. They report displacement, edge-length ratios, collapsed/opened degenerate
edges and finite normals. They are numerical inspection data, not visual-quality
or native-runtime passes. Measurement work is cancellable and limited to one
million vertex samples and three million edge samples; pose changes invalidate
pending measurements.

Private three-second review cycles exercise a generated forearm bend, lower-leg
bend and combined joint/morph motion using the actual ReAnimated D3D11 WARP
render pass. Source-package hashes remain unchanged. These synthetic fixtures
do not substitute for private character review or native Player acceptance.
Reusable saved review poses and automatic anatomical-axis stress presets are
not yet implemented; the current offsets and cycle remain transient.

An initial constraint-aware volume-distance binding solver and its generic tests
are now present; see [binding candidates](DL1_SKIN_BINDING_CANDIDATES.md) for the
algorithm, reproducibility contract and pending quality/workflow integration.
Source-linked rig/binding replay is implemented for new authored layers; full
legacy derived-state recovery and changed-source reconciliation remain required.

Sole-contact authoring is available under Helpers & Hooks. Select one observed
foot deformation node and a source geometry component, optionally limit the
selection by normalized foot-branch weights and original control-point IDs,
then fit and review a draft. The solver forms a convex sampling footprint from
the selected lower vertices and uses polygon area moments to estimate the long
axis without vertex-density bias. Nearly isotropic footprints retain an
ambiguous status until explicit directions are chosen. Contact origins can
retain the parent pivot or use the footprint center. Bounds cover the selected
shoe geometry; flat sources need a positive authored thickness.

The contact preview uses actual source bind-deformed positions, including each
draw's expert inverse binds, and pauses clip playback. Cyan shows the sampled
footprint, orange the bounds, and green the draft bottom plane. Numeric offsets,
rotations, centers and extents are explicit overrides. Applying a draft is one
undoable edit, checks stale source/session identities and locks, and preserves
surface weights, morphs and original bytes. Imported helper frame/bounds repair
retains its name and parent; imported reparenting still requires the broader rig
transaction tools. Separate authored helpers coexist with source-linked surface
payloads through save/reopen. Workflow-only navigation retains a pending draft.

These controls do not declare a native contact profile or prove trace consumers,
foot planting or terrain response. Channel/LOD decisions remain explicit. The
generic parent guard admits an observed deformation node or a previously typed
bone; complete foot-role/profile constraints and native calibration remain open.

Local hand analysis and review is available in Detect and Fit. A selected source
component is reconstructed from current bind-deformed control points and exact
per-draw inverse binds, then sampled at a separate regional resolution. Persistent
geodesic volume branches propose fingers; local palm moments and interior paths
propose orientation, knuckles and curl planes. Straight-chain hinge locations are
explicit interpolation proposals. A proximal lateral branch can establish a
thumb and finger ordering; ambiguous, fused and insufficiently sampled results
require manual assignment. This deterministic candidate has not completed a
held-out quality benchmark.

Hand declarations store present, absent, fused and unresolved digits separately
from missing detector output. Candidate assignment retains source-linked guide
IDs and pins; saved guides can be corrected numerically or with the existing
viewport handles before generation. Reviewed curl/roll decisions append finger
chains to an owned generated body without rebuilding its existing bones or
changing its current weights. Four standard digit guides produce three deform
bones and a terminal endpoint. Separate helpers retain their semantic parents
when the base bone table grows. The source-linked authored layer carries the
extended rig through save/reopen.

Regional hand weight refinement uses the selected source component and local
sampling box. Automatic eligibility is restricted to the selected hand's wrist
and finger handles; locked fractions, including explicit zero exclusions, are
retained. Points outside the box do not enter the edit transaction. The adapter
requires an existing normalized body bind, rejects expert nonidentity rest
transforms that could move neutral geometry, and reuses the source-linked weight
editing/partitioning path. Preview heatmaps select a digit segment; applying the
candidate is one undoable operation and preserves outside weights, source data,
morphs and separate helper identities through save/reopen.

Parenting beneath separate authored helpers, explicit finger mirroring, stress/grip calibration, broader binding quality
benchmarks and native profile acceptance remain incomplete. New finger bones
are not claimed to deform the hand until weights have been bound and reviewed.
An offline diagnostic with root-only weights was rejected after the official
compiler omitted unused deform nodes; compiled hand acceptance requires a bound
fixture and verified retention behavior, not dummy weights or a weakened check.
A subsequent private body-and-two-hands fixture uses meaningful body weights
and independent regional finger refinement. The installed official compiler
retained all 49 expected rig nodes, including 30 finger nodes; strict frame,
reference, parent and bounds read-back passed. This proves that fixture's offline
export path. It does not certify arbitrary unused-node retention, authored
character quality, gameplay animation families or native Player behavior.

Eye setup is available in Detect and Helpers & Hooks. It keeps observed source
eye nodes, geometry pivots, gaze references and existing mimic descriptors in
separate side/mode records. Source-eye observations retain exact affine globals
and original identities; camera nodes and reserved camera names are rejected as
source eyes. Painted eyes can use an observed source node or a manual gaze
reference, but cannot be recorded as inferred globe geometry.

The deterministic globe candidate fits one source topology island in the current
bind-deformed geometry. Area-weighted sphere fitting reports vertex and sampled
surface residuals, depth conditioning, closure and an interior-center check.
Open caps, flat graphics and co-spherical cube corners do not establish an
accepted globe. The candidate remains subject to visual eye-identity and gaze
review. Manual position and forward/up directions support assisted placement.
The viewport displays the globe and axes in the source bind view; navigation
retains current proposals, and actual edits invalidate their review approval.

Reviewed pivot/gaze setups can materialize an unweighted authored helper under
an explicitly selected parent. Exact affine parent inversion preserves the
requested global frame, including residual scale/shear. Existing helper names,
parents, locks and source data remain protected; updates do not reparent or
rename imported nodes. Save, helper apply and undo are distinct transactions;
existing morph descriptors, camera metadata and source bytes survive reopen.
Reviewed geometry pivots can also append deforming eye bones to an imported or
generated rig. Left, right and shared/single-eye setups retain separate stable
identities. Existing eye bones require a distinct rest transaction for changed
frames or parents; appending a bone preserves all earlier bone indices and
shifts only references into the separate helper table. Owned eye and finger
extensions can be interleaved, while unrecognized extra bones remain rejected.

Rigid eye binding previews and applies exactly one inspected source island.
It supports initially unskinned geometry and source components containing other
eyes or surfaces, preserves those outside points, and refuses conflicting
weight locks and expert nonidentity rest binds. The existing palette partitioner
keeps skinned and unskinned triangles separate. Source bytes, morph targets and
authored surface correspondence survive reopening. The generated-body unbound
diagnostic clears only when all rendered surfaces have normalized skin weights.

Transient yaw/pitch review composes rotation with the exact eye-local bind;
the reviewed global pivot and radius remain stable under affine parents.
Existing facial preview values can blend with this pose. Numerical CPU/shader
reference checks include position and normal preservation, independent eyes,
and a sheared/scaled parent. UI bone creation, binding and undo are separate
transactions. Native gaze consumers, calibrated legacy schemas, eyelid/contact
quality, broader geometry benchmarks and actual Player facial acceptance remain
unverified.

The Fit stage now provides a same-hierarchy rest-frame transaction for imported
and generated joints. Child joints/helpers can retain their global frames or
follow the edited joint. Exact local matrices, helper recipes and frame policies
are synchronized, and existing guide/helper locks remain enforced. Affected
hand and eye placements lose their old approvals; stationary compensated eye
placements remain unchanged. Manual finger frames carry a fingerprint of their
guide/curl/roll inputs so later guide edits cannot be silently ignored.

Surface handling is explicit. Refit mode compensates each draw inverse bind as
`newGlobal^-1 * oldGlobal * oldInverse`, preserving expert offsets and the visible
base/morph result. Bake mode evaluates the proposed pose into affected source
components and resets their inverse binds coherently. Position deltas remain
displacements; normals use weighted per-influence inverse-transpose matrices.
A shared base-normal denominator preserves simultaneous signed normal-morph
blends. Topology, UVs, material identities, weights and source bytes remain owned
by the original model; this format has no authored tangent channel to transfer.

Preview uses the candidate model without replacing the saved model. Applying is
one undoable operation, and workflow navigation preserves a current draft while
other source/material edits invalidate it. Original animation data is retained
with an explicit motion-review diagnostic. Generic shader-reference and
save/reopen checks cover both operations; private official compiler read-back
passed on a 21-node morph-bearing fixture for both modes. These checks do not
approve animation behavior, physical contacts or skinning quality. The raised-arm
diagnostic exposes a shoulder-weight artifact that remains a binding-quality
issue. Broader multi-joint rest transactions and native motion/physics review remain open.

Parent editing is available in Fit and Helpers & Hooks. It reparents an observed
base node beneath another base node, retaining every rest global and preserving
unselected exact local matrices. Stable topological reordering produces explicit
old-to-new bone maps; draw palette indices and decoded animation-track indices
follow those maps. Raw keyframes, scalar/auxiliary tracks, source bytes, weights,
materials and helper placement remain intact. Save/reopen rebinds decoded source
tracks by retained bone identity. Untracked preview nodes retain exact affine
rest matrices instead of falling back to their projected TRS alone.

Persisted parent decisions are checked against the actual document. Generated
body and hand validation use stable roles after reorder, preserve semantic finger
segment endpoints, and still reject unknown or inconsistent ownership. Empty or
valid eye declarations cannot bypass invalid body ownership. Cycles, foreign IDs
and conflicting helper locks fail before publication. Parenting under a separate
authored-helper row and broader source-change decision review remain incomplete.
Native profile parent rules and motion behavior require independent acceptance;
retaining local keyframe values does not preserve world motion after reparenting.

The older whole-rig rest baker now delegates to the shared draw-slot pose baker.
Inverse-transpose normals and a common base-normal denominator apply consistently
to simultaneous signed morphs. Legacy handling of unweighted and partially usable
influence arrays is retained in a separate internal mode; strict authoring rejects
those invalid inputs. Regression tests cover draw/source index distinction,
unnormalized weights, extra morph positions and malformed influence arrays.

The Animate stage can derive a separate sampled clip from an immutable FBX source
after rest or parent edits. Stable source identities map into the current rig;
unmapped helpers follow their target parents. The global bind-delta strategy
preserves position, rotation and scale, retimes scalar and auxiliary keys, and
measures reconstruction at output frames and their midpoints. Dynamic local shear,
excessive interpolation error and exhausted sample budgets refuse publication.
The preview is transient; saving is one undo step and leaves export disabled for
the new clip until explicitly selected. Source clips remain unchanged.

Derived samples live in hashed, bounded package entries separate from the FBX.
Their source and target signatures are recorded. Source replacement or rig changes
retain historical payloads while making stale clips unavailable for playback and
export. Export checks required POS/ROT/SCL masks and rejects a derived emitted
frame that cannot round-trip through TRS. The timeline exposes scalar and scale
curves; source/derived preview applies signed facial values in their declared
units. This path covers absolute-local FBX clips, not native additive-reference,
event, contact or runtime IK parity. Those still need independent work and evidence.

The Animate stage also exposes native size-study preparation. A supplied HumanAI
preset source can be used to create three explicitly named fixed-size controls.
The writer retains the original source and copies the selected control while
changing only its name and the two forced body-scale assignments. It requires
declared fields, rejects ambiguous/nested scale assignments, preserves opaque
macros/comments, and writes separate study source and hash receipts. Source changes
on disk or existing output files stop the save. The offline `ScaleStudyTool` uses
the same writer for multiple controls; no retail preset is embedded in the repo.

These are S1 preparation tools, not a scale implementation inside the mesh writer.
They do not change anatomy, bake size into geometry, replace animation banks, deploy
the source or certify spawn/post-spawn scale, IK, contacts or collision. Generated
trial values do not establish native supported ranges. Exact-build consumer
evidence and S1–S6 runtime observations remain required.

The Helpers & Hooks stage now includes a Rig Doctor contact-repair workflow.
It loads an explicit contact-rule array, audits the existing parent/type/skin use,
fits weighted geometry, and previews the complete required repair as one undo step.
Existing frame, bounds and component/LOD data are compared against the selected
rules; conflicting or unavailable settings require review and are never silently
replaced. Ambiguous geometry can be selected in the panel before re-running the
diagnosis. Correct repeated diagnoses return the same model without duplicate
helpers. Source bones, weighted surfaces, morph data and source clips remain intact.

Repair-rule JSON contains caller-supplied role/helper/parent names, optional source
component IDs, footprint orientation/origin and bounds choices, explicit component
and animation-LOD settings, and an evidence reference with its artifact hash.
Directions can be expressed in parent or model space. No character-specific rule,
4 mm box or native readiness claim is built into the product. Rules are authoring
inputs; they do not replace a complete capability profile or native validation.
This implementation addresses missing contact insertion and existing-contact
diagnostics. Automatic repair of conflicting imported nodes and other runtime role
families remains open. Compiled and loaded/live acceptance are distinct gates.

The master-plan scope remains all RS-001–038 and applicable VT-01–35. Installed
corpus/dependency records and native consumer evidence, frame/component rules,
role-aware preparation and repair, the staged UI, geometry-driven body/hand/eye
detection, automatic skinning and corrections, scale/motion behavior, compiled
semantic read-back, deployment freshness and actual-load/live scenario acceptance
remain release requirements. Passing foundation tests does not close those gates.
