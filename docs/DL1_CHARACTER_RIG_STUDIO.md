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
Studio export preflight now lists all emitted nodes with missing masks, owners,
evidence or LOD decisions in one diagnostic rather than stopping at the first
node. The reviewed stock-humanoid body proposal reports how many observed nodes
remain undecided. Exact-stock only-unset review can supply matching helper
mask/LOD values without replacing saved body choices, but its owner selection
is uniform; camera and other helper ownership still require explicit review.

For bodies substantially smaller or larger than a stock animation's authoring
rig, clip translation keys can replace shorter or longer local bind offsets and
visibly change limb lengths. This is a channel-ownership problem to review,
not an expected result of the Fit setting. On the installed DL1 build, decoded
retail `zombie_screamer` and `zombie_goon` meshes use rotation-only animation
bits on ordinary upper-arm, forearm and thigh bones, while their root accepts
position, rotation and scale. Their `hspine` helper still accepts position and
rotation, so a blanket rotation-only conversion is not an exact stock policy.
They both embed `anims_man_all.scr`. The compiled
type-322 `anims_man_all` bank has 7,698 parsed sequences; the separate
`anims_player` bank has 5,925 and contains `tpp_stand_menu_a`, which
`anims_man_all` does not. This establishes a stock rotation-only precedent,
not proof that a player-bank clip behaves identically on either creature.
The Screamer and Goon source BSCR/ASCR companions were not present in the
available RPACK members. Their compiled type-272 meshes do retain the full
component/LOD flag table and embedded animation alias, so a readable script
equivalent can be reconstructed for analysis without presenting it as the
original source. The Screamer has 80 bone/helper policy rows and the Goon has
134. Both use `ROT` on ordinary upper arm, forearm, thigh and calf nodes,
`POS | ROT` on `hspine`, and `POS | ROT | SCL` on `bip01`. This supports
reviewed bind-owned limb offsets for stock reuse across sizes; it does not
identify a hidden per-model scale setting or certify arbitrary animations.
Stock `HumanAI.pre` supplies an independent actor-size route: Screamer presets
using `zombie_screamer.msh` inherit the base forced scale 1.0, while the
standard `Goon` preset scales its different `zombie_man_a.msh` mesh to 1.4 and
the `Demolisher` preset scales `Armored.msh` to 1.5. Compare model bind size
and preset body scale separately when diagnosing a small or large actor.
Other installed type-272 controls show that channel policy also differs by
role. `player_1_fpp` and `player_1_tpp` both embed `anims_player.scr` and use
POS/ROT on pelvis, spine, arms and hands but ROT on thighs and calves.
`survivor_woman_a` embeds `anims_man_all.scr`, with ROT on most ordinary body
bones and POS/ROT on `hspine`. The Following `mother` combat body also embeds
`anims_man_all.scr` but permits POS/ROT/SCL across its core skeleton. Its
separate `bossfight_mother_bar` and `mother_head` resources are not the skinned
combat body. These compiled tables are controls for review, not rules to copy
by gender, size or bank name. A stock player upper-body mask can still change
a shorter custom bind; a rotation-only policy can preserve that bind but needs
separate weapon, camera, contact and special-move checks.
The retail Demolisher preset selects `Armored.msh`; its compiled body limbs
follow the same rotation-only pattern, but the mesh embeds the dedicated
`armored.scr` script. Its 70 sequences also occur in `anims_man_all`, while the
sampled player menu and sprint clips occur in neither bank. Large stock actors
therefore support the channel-policy comparison, not universal cross-bank
compatibility. Hands, head LOD, prop roots and cloth branches require their own
review.

The Channel Policies panel offers a reviewed stock-humanoid proposal for a
mapped body: retain authored non-root translation and scale, accept clip
rotation, choose root channel ownership explicitly, and leave unmatched
secondary bones and independent prop-holder roots alone. Existing decisions
require a separate overwrite choice. Applying the proposal is one undoable
rig edit; it is not a default export rewrite. Test the actual animation bank,
model size, contacts and special moves in Editor and Player before using the
result in a production resource.
The stock-clip verification summary now reports connected deform-bone length
drift across sampled preview frames. This is a more direct proportion check
than peak vertex travel, which includes ordinary locomotion and never proved
stretch by itself. The metric uses raw preview clip tracks; it does not assert
that native BSCR channel masks or cloth output were applied in the preview.
The DL1 Output viewport can also show an explicit reviewed-BSCR comparison
when the model has complete saved channel decisions. It holds omitted local
POS/ROT/SCL components at the authored bind frame while leaving the raw view
available. The stock-clip verification reports raw and reviewed-mask connected
deform-segment drift separately. This comparison is an authoring approximation:
helper motion, skin distortion, native blending, LOD and runtime binding still
require Editor and Player review. A missing or stale policy cannot silently
produce a reviewed result.
Playback and Retarget / Edit expose the same comparison as an opt-in target
diagnostic for a project-owned DL1 Output model. It masks the already evaluated
target pose, so retargeting and authored edits are not replaced by a resampled
source clip. Select DL1 profile and enable the reviewed BSCR control to compare
the rendered mesh and bones with Raw. The animation and export data are unchanged.
The target pane labels the result or explains why a complete matching policy is
unavailable. Actor/preset scale, bank selection, native blending and LOD,
attachments, FPP camera and physics are outside this visual comparison.
The separate exact-stock mask/LOD review can apply only rows without saved
decisions. Use that scope for missing helpers after reviewing a body policy;
applying all stock rows would replace existing body choices and requires a
separate intentional review. Unmatched extra bones remain unresolved.
For unmatched terminal helpers, Channel Policies offers a separate read-only
proposal after an exact stock comparison. It checks effective children,
render weights, and all included embedded tracks; only unweighted leaf helpers
with constant tracks become candidates. The preview shows any difference
between a constant track and the fitted bind, requires a chosen retained LOD
and explicit review, and applies only nodes without saved channel decisions.
It does not infer that constant tracks are safe to discard or set `LOD_OFF`
for a retained helper. The session edit is undoable and does not alter clip
payloads or the source model. Helpers & Hooks places this review first and
shows each candidate as a wrapped card so its evidence is readable in the
narrow inspector; the bounded list scrolls independently of its review and
Apply controls.

## Authored animation packs in Developer Tools

The installed DL1 Editor loads an authored animation library when its compiled
RPack is placed at the project-local `data/common_anims_sp_PC.rpack` path. In an
isolated flat-map test, the model's own ASCR/SCR binding listed the custom
sequence with its expected frame count and rate, and the Editor timeline
advanced through it without a manual SCR override. This is Editor evidence;
Player loading and coexistence with every retail animation remain separate
acceptance checks.

The Developer Tools **Animations only** export offers an explicit Editor-mount
checkbox. It writes the selected variants as one unified project pack and
keeps the portable `out/ReAnimated` copy. An unrelated pack already at the
conventional path blocks the mount. Re-exporting a pack still owned by the
same project preserves earlier resources while updating selected clips and
scripts, with transaction receipts and rollback. The single-model CLI has a
separate `--mount-editor-animation-pack` opt-in; multiple per-model batch
requests cannot each own that one path. Use an isolated project for native
review until the Player route and stock-bank coexistence are verified.

A target variant now retains a copied root-bone override only when that bone
exists in its selected target rig. Activating an older variant with a stale
source-rig root clears that invalid override within the successful project
transaction, leaves a review diagnostic, and lets the DL1 target-root policy
choose its semantic/default root. Preview and root-trail evaluation apply the
same target-rig check so an old saved name cannot cause a one-off evaluation
failure. This does not review retarget mappings or authorize export of a
draft cross-rig animation.

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
When exporting both proportion modes from one FBX into the same project, pass a
different stable `--model-id <guid>` for the intentionally separate identity.
Without it, both CLI outputs use the same deterministic source identity and the
second package is treated as a replacement. Keep each chosen GUID when
rebuilding its corresponding variant.

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

Verify & Export now has a read-only installed-model check. New deployment receipts
retain an optional model authoring identity: source, rig/morph signatures, complete
package input fingerprint, studio recipe/profile inputs and exporter contract.
Navigation and review-history changes do not alter this identity. Legacy receipts
remain readable but cannot prove a current authoring match. Lookup can select the
latest active receipt for one model/character instead of a newer unrelated deploy.

The check independently reports the authoring match, installed-file hashes and loose
project duplicates/legacy output. Edits clear the displayed result. Archive contents,
mount precedence, cached instances, referenced stock-bank binding and active Player
resource identity remain unverified; no loaded-resource or gameplay receipt is
created from this filesystem inspection. The panel calls out the fresh actor spawn
or reinitialization required for contact/IK acceptance. External prepared-animation
variants still need separate authoring evidence. This advances RS-031 without
closing native receipt or runtime acceptance requirements.

Installed-model inspection now also inventories project RP6L tables by exact
resource type and case-insensitive name. It scans root-level packs, data, assets_pc,
out and the deployment animation-runtime package folder. Receipt-owned retained
packs are identified separately from additional project copies. Matching archive
files receive container-byte hashes; these are not compared with logical resource
payload hashes. Payload decompression and semantic equivalence are not claimed.

File/directory/archive/match counts, table allocation, container hashing and error
output are bounded. Malformed or unreadable packs, refused reparse points and budget
exhaustion produce incomplete coverage rather than a clean result. The UI shows
matching entries and additional-copy warnings while keeping mounted precedence and
active-resource/gameplay facets unverified. Installation-wide providers and native
cache/load capture remain separate work.

Attachment authoring now supports an optional prop-owned grip calibration. The
existing character parent index/name guard and local offset remain authoritative;
the evaluator aligns a separately selected prop model-space frame using its exact
affine inverse. The project stores the prop asset ID, content hash, named/indexed
frame and matrix without embedding retail geometry. Rendering rejects changed
content, missing/reordered frames or frame-matrix drift instead of redirecting a prop.
Legacy origin-based attachments retain their original behavior.

A secondary character/prop frame pair can be saved with its own character-local
offset. The attachment panel can optionally drive an explicit root/joint/end chain
with a two-bone solver, pole point, pole space, orientation matching and weight.
The solver follows the evaluated prop after pose edits without modifying source
samples. The binding scope selects preview-only or authored/exportable solving;
preview is evaluated independently so partial weights are not applied twice.

Saved index/name identities remain guarded. Invalid ancestry, feedback into the
primary prop parent, competing attachment drivers, camera descendants and
unsupported scale or affine residuals preserve the incoming pose and report an
error. Targets beyond reach retain a measured gap and clamp warning. Selected-frame
overlays distinguish the primary frame and secondary prop/character contact.
Unresolved saved chain choices remain available for an explicit rebind.

ANM2 sampling rejects evaluator errors and requires descriptors on every driven
node, including nodes otherwise marked optional. Generic encode/readback tests
cover the solved output. This does not emit or verify native equipment configuration;
native holder consumers, runtime control ownership and real character/prop gameplay
acceptance remain required RS-019 work. The self-contained Blender variant handoff
continues to reject attachments because it cannot package their mesh/material data.

Camera calibration now has a dedicated Helpers and Hooks panel. Observed camera
nodes keep independent names and parents; reviewed parent-local translation and
rotation offsets preserve source geometry, weights, morphs, clips and channel
policies. Preview preparation runs as a cancelable job, source/draft changes
invalidate pending results, and apply uses the existing model transaction/undo path.
Direction and roll are displayed with a prepared-frame overlay. Independent global
frame overrides and camera branches carrying skin weights require separate review.

Reviewed camera recipes explicitly opt into `FollowPreparedParent`. Older helper
recipes default to their existing source-parent basis, preserving solved contact,
socket and structural footprints when preparation changes a parent basis. The
setting persists with the helper recipe and is consumed by the final rig preparer.
Camera creation accepts a separately chosen parent; it does not infer an eye midpoint
or invent native component ownership. Profile-driven creation, native lens/clipping acceptance,
camera motion on actual owner controls and native acceptance remain RS-020 work.

Camera review now shares one explicit view basis with its renderer-facing lens:
+Z forward and -Y up by default, matching the existing helper preview convention.
A diagnostic up-axis toggle changes the readout, frustum and look-through view
together. Vertical FOV, aspect, near/far clips and a separate frustum drawing depth
are session-only preview settings; they never modify the authored frame or clips.
The look-through override preserves the orbit camera and restores it when disabled.
Invalid lenses or unrepresentable camera positions fall back with an explanation.

The animation-review option samples the prepared camera from the same evaluated
skeleton used to render the model. Direction/roll and lens view update with timeline
scrubbing and playback; a rest-only review remains available. Generic workspace
tests compare camera motion to independent hierarchy composition and verify the
source keys and authored draft are unchanged. Projection tests cover frustum
corners and near/far clipping; synthetic D3D11 review images are editor evidence,
not native lens or gameplay acceptance.

Camera creation can now preview a named EyeCamera or RefCamera observation from
the resolved installed template. Player FPP resolves its own resource/fingerprint
rather than sharing the TPP identity. The target parent is explicit, with a unique
name-match suggestion only; frame copying never guesses an eye midpoint or scales
the character. Physical camera-row order cannot redirect a named selection.

Creation retains historical template origin: resource fingerprint, template ID,
profile/resource/node/parent names, observed local frame and creation offset. It
adds imported-source evidence without claiming a native profile rule. Existing
channel/LOD policies are preserved and missing policies stay unresolved. Creation
is previewed, explicitly reviewed and applied through one undoable model transaction.
Name collisions, foreign parents, stale source/reference input and cancellation
are guarded. Completed camera previews survive stage-only navigation; authoring
changes require a fresh preview. These mechanisms do not prove native parent or
component requirements, or native FPP/TPP/cinematic acceptance on actual actors.

Native model compilation defaults to a compact per-user staging root. In an
installed-toolchain comparison, the same two-surface morph model compiled and
passed shading read-back twice from a short root, but the old deep default root
made the texture stage exit with code 9. A realistic multi-surface FBX control
also compiled from the compact default. The 240-character source/object path
preflight remains an outer safety bound, not a guarantee that every shorter
path is accepted by the native compiler. Callers can still supply an explicit
private working root. A partial object with an unsupported exit status remains
ineligible for publication.

Structural helper review now scans all effective nodes, including unknown extras,
using palette-resolved positive vertex influences as well as weighted metadata.
The Conform Helpers stage shows saved role/frame/channel/LOD decisions and source
transform-track counts. Unverified native driver semantics stay explicit; names
such as normal or twist do not decide weight eligibility.

Unweighted helper branches support reviewed rigid local offsets. Weighted source
joints route into the surface-preserving rest editor with descendant frames held
fixed; weighted helper branches are rejected by the quick helper edit path.
Protection captures the current prepared frame/bounds when needed, and locks
name, parent, position, orientation, bounds or channels separately. Frame-policy
and source/prepared-parent-basis changes cannot bypass position/orientation/parent
protection. Unlocking is an explicit previewed transaction. Original imported
bones, geometry, weights, morphs and clips are retained.

The inspector uses a cancellable scan, explicit preview/review/apply, stage-only
preview preservation and the workspace's one-step undo/redo transaction. Frame
comparison uses the prepared output on both sides, with original and edited axes.
Detailed role/channel information and lock controls are expandable. Generic tests
cover palette use, stale work, protection, persistence and workspace transactions;
private synthetic compiler read-back is separate from native driver acceptance.
Profile-specific twist/share/normal placement/driver rules and actual actor tests
remain open; this inspector does not close RS-021 or its native release gates.

Capability profiles can now be loaded as portable `.dlrprofile.json` definitions
in Helpers & Hooks. Select behavior capabilities, explicitly assign existing nodes
to roles and select their owning assets, then preview and review before saving.
The full profile snapshot is stored in the model, so a reopened package does not
need the original file path. Legacy reference-only recipes remain loadable and
are reported as lacking the definition needed for role validation.

`RigCapabilityProfileSerializer.Seal` computes the canonical content fingerprint;
serialization and loading verify it. The profile hash field is zeroed while hashing
the typed camel-case JSON. This is content identity, not an authentication or
native-evidence certificate. The standalone and embedded schemas describe the
same profile data. Neither loading nor saving promotes native validation evidence.

The role report includes unselected families, capability prerequisites, missing
or competing assignments, owner/name/parent/type conflicts, actual skin-influence
conflicts and unresolved rules/consumer coverage. Previous assignments missing
from a replacement profile appear as migration items and retain their source
entities. Clicking a review row selects the corresponding assignment controls.
Unknown extras are retained. Decisions use a cancellable preview and one undoable
transaction; stale sources or changed drafts require another review.

The source writer rejects failed checks for an explicitly selected profile before
writing source outputs. Missing definitions and unresolved evidence remain explicit
manifest/compiler diagnostics, never inferred runtime acceptance. Complete native
profiles, frame/component/retention-rule evaluation, multi-asset authoring and live
family coverage remain open work.

Capability roles can now carry executable `validationRules` for the prepared
contract. Frame checks support allowed preparation policies, current source-frame
preservation, optional orthonormality, an origin in another role's local coordinates,
and an axis aimed at another role. Reference roles enter capability closure without
turning a normal parent/child direction relationship into a construction cycle.
Bounds checks declare allowed ownership policies and half-extent ranges. Channel
checks compare the explicit mask and ordered position/rotation/scale owners.
Retention checks compare prepared entity presence and the animation-LOD decision.

These checks consume the same prepared contract as output and do not change the
source or solve a new pose. A failed rule blocks source export through the profile
validator. Passed observations name only the checks actually requested. Missing
executable definitions remain unverified even when legacy rule IDs are present;
typed allowed frame policies take precedence over the legacy policy hint in the
review display. Other representations need their specialized resource validators.
Required variants/resource LODs remain unverified until their own compiled inventory
is inspected; animation LOD is not a substitute for those records. Local rule checks
and compiled read-back do not certify native consumers or actual actor behavior.

Profile edit admission is explicit and opt-in: a role's `validationRules.edits`
activates its `allowedEdits` flags and an independent removal permission. Legacy
profiles without this definition retain their authoring behavior and report the
missing permission definition as unverified. Name, parent, local position,
orientation/scale, bounds and channel/animation-LOD decisions are checked against
the previous profile; clearing assignments or replacing that profile in the same
edit cannot bypass an existing restriction. Helper locks remain independent.
Adding evidence without changing channel values or owners does not count as a
channel edit. Changing frame ownership/basis can require both position and
orientation permissions even when the current numeric frame looks similar.

Core recipe/document mutations and prepared-output comparisons are both covered.
The latter catches indirect frame/bounds changes caused by editing another node.
The main Studio model-event path reports a refusal before committing or recording
undo, and legacy helper fields use the same admission. Undo/redo and explicit model
open/replacement use their existing separate restore paths. Unchanged authored
helpers are no longer rewritten as a side effect of editing another helper.

The capability-profile panel exposes local authoring permissions under the selected
role. These controls change only the draft, preserve native rule definitions, retain
a UserOverride reference to the source profile hash, and require preview/review/apply
before saving. A refused change is shown in a bounded, expandable Conform banner.
Model-level field admission is not complete project/attachment or specialized
resource protection; multi-asset and native scenario acceptance remain open.

Conformance now projects authored helper rows back into the helper layer instead
of clearing them. IDs, kinds, helper branches, preview-camera selection and exact
affine frames are retained through the source rest-pose transfer. A helper absent
from an explicit drop-extra fit survives under its nearest mapped source ancestor;
its weights are not folded away merely because it is an authored helper. Split
hierarchies that would require a base bone to parent under an authored helper, or
promote that helper into a deform bone, are rejected for explicit reconciliation.

Retained base rows keep their source FBX object identities. Studio identities,
parents and active frame/component decisions are reconciled, and dependent reviews
are invalidated. A locked influence prevents dropping its source bone. Source
animation keys are reindexed by stable identity; they are not retargeted. A clip
that addresses an intentionally dropped node remains in the embedded source but
is unavailable on the changed rig until a derived version is created. Source-linked
layer replay uses source identities before name fallback to avoid accidental
same-index ownership after save/reopen. Motion acceptance still requires separate checks.

Conformance also reconciles saved secondary-motion references. PHX grid bones and
known collider attachment names follow the mapped hierarchy; only quoted name tokens
change, preserving comments, virtual endpoints, grid/seam declarations and native
coefficients. The first imported PHX text remains in the model as `originalText`.
An unknown statement referring to a renamed bone, or a used bone that would be
removed, stops the proposal with an explicit review error before commit.

Editor particle anchors and collider endpoints are transported into the final
bone frames, including authored-helper adjustments. Fixed roots, driven bindings,
constraint distances, radii and tuning retain their authored values. Review their
resulting shape and contact after a proportion change. MPCloth export similarly
replaces only resource-name tokens, retaining inline comments and activation flags.
This reference migration does not recalibrate native collision bounds, prove native
cloth movement, or retarget stock motion; those require compiled and Player checks.

Native companion export now requires every supplied PHX to appear exactly once in
its MPCloth wrapper. A deliberately disabled binding remains authored as such, but
the export notes explicitly say it cannot activate that garment. After official
mesh compilation, read-back compares each PHX grid-bone name with the compiled
hierarchy. A missing compiled node stops publication; a case-only difference is
reported for native review. Read-back also checks whether any movable grid node
influences rendered vertices and warns when none do. These checks establish
reference and skin-weight coverage, not Player cloth creation, synchronization,
collision response or visible secondary motion.

The conformance viewport now consumes the skeleton from the same prepared session
as its mesh, including the fit-to-effective selection map. Fit-frame changes
invalidate the paired preview rather than swapping in an unrelated skeleton.
Large-asset preview responsiveness remains a performance gate.

The master-plan scope remains all RS-001–038 and applicable VT-01–35. Installed
corpus/dependency records and native consumer evidence, frame/component rules,
role-aware preparation and repair, the staged UI, geometry-driven body/hand/eye
detection, automatic skinning and corrections, scale/motion behavior, compiled
semantic read-back, deployment freshness and actual-load/live scenario acceptance
remain release requirements. Passing foundation tests does not close those gates.


### Secondary motion during structural edits

A rest-frame refit that keeps the surface still also keeps editor particle anchors
and collider endpoints in their original model-space positions. Their bone-local
offsets are rebased into the edited joint frames. Baking a posed shape instead
retains those local offsets so attachments follow the posed bones. The rest-edit
preview explains the selected behavior before Apply, and the same undo/redo entry
owns the geometry, rig and secondary setup.

Native PHX/MPCloth text and coefficients remain unchanged for these non-renaming
edits. Native collision bounds, constraint lengths and resulting movement still
need review. A hierarchy reparent retains global rest frames and unchanged named
secondary attachments; palette remapping includes authored helpers that carry
weights, rather than rejecting them as out-of-range base bones.

The binary authored-surface layer now writes version 2, which can bind actual
helper influences using the helper's persistent ID. It still validates the base
rig signature, and unweighted helpers remain outside the surface payload. Version
1 layers remain readable and upgrade on the next capture. A same-named helper
with a different ID cannot silently inherit saved weights or inverse binds.


### Reviewed compiler retention proposals

Helpers and Hooks > Structural helpers now includes an explicit retention proposal
for a prepared bone branch with no actual mesh influences or helper dependencies.
The scanner reports this observed condition separately from compiled retention.
Preview adds one named helper child with a visible marker. Existing bones keep
their type, hierarchy, frames, bounds, weights and animation policies. The new
helper uses a bind-inherited NONE/LOD_OFF policy and does not add animation clips.

The `compiler.retention` helper role is a non-anatomical leaf dependency. Shape
solving ignores it when deriving bone axes and segment bounds, while the emitted
hierarchy keeps its real parent. Giving it mesh weights or children requires
reviewing its purpose first; preparation rejects those ambiguous uses.

The existing review checkbox, Apply, Cancel and undo/redo flow owns the proposal.
It is saved as an ordinary authored helper and recipe, rather than a hidden
export-only node. Official compiler readback must verify the exact candidate;
adding the helper is not a compiled pass or a native scenario validation result.
In a bounded compiler acceptance run, an unweighted branch was removed despite
being present in the source MSH, CHR and BSCR. Reviewed terminal helper
dependencies preserved that branch transitively, and compiled readback matched
the complete source policy table and component/LOD bits. This establishes a
checked repair pattern, not a universal rule that every unused branch will
survive or animate correctly.


### Removing a saved retention helper

Select a `compiler.retention` helper in the structural scan and use Preview
removal. The operation requires an unweighted leaf with no protected decisions,
model references, owned tracks or unresolved companion dependencies. Remaining
helper parents, draw palettes and decoded tracks are reindexed by identity.
Original source bytes and authored layers remain available; Apply and undo/redo
are the same reviewed model transaction.

Project references are checked before preview and again before Apply. Attachment
parents, secondary grips/IK, root-motion choices and mappings are protected. The
index guard covers both source and prepared ordering. Target edit/IK layers that
need reconciliation currently block removal instead of being silently changed.
Derived/auxiliary animation payloads and unresolved native includes/statements
also block removal pending dependency reconciliation.

Removing the dependency may allow the compiler to discard its parent again.
The build receipt is cleared and the changed rig must pass fresh compilation and
native scenario checks before deployment. This feature does not automatically
remove or rewrite project attachments or authored edit layers.


### Durable model-package batches

The shared `Dl1ModelBatchRunner` and `batch-models` command process exact approved
model packages through the existing complete package builder. Per-item outcomes,
source snapshots, output hashes and cancellation state are durable. Resume verifies
declared inputs and finished outputs; mismatches remain review-required. See
`DL1_MODEL_BATCH.md` for the manifest and recovery contract. This does not yet
supply cross-model recipe application, and does
not promote compiler evidence to runtime acceptance.

The Verify and Export stage includes an interactive reviewed queue over this
runner, with package approvals, JSON save/open, cancellation, result inspection
and resume. Compiler results remain explicitly runtime-unverified.

### Reusable profile and channel setup

The Helpers and Hooks stage now saves and opens .dlrsetup files. The shared
serializer and transfer service resolve semantic role/name selectors onto the
destination's own nodes, require explicit mapping review, and preserve fitted
coordinates, geometry, source clips and historical validation scope. See
DL1_RIG_SETUP.md for transfer rules and remaining geometry/guide reuse work.


### Reviewed setup application in batches

Queued packages can pin a `.dlrsetup` and reviewed destination binding list.
The App and headless runner apply through the same setup-transfer service, retain
original package/preset snapshots and the prepared authoring candidate, and check
both input hashes on build/resume. Changed setup inputs remain review-required;
invalid mappings and expected compiler data failures stay isolated per item.
This extends profile/channel reuse, not geometry or fitted-guide transplantation.

### Exact installed reference selection

Conform can resolve the mesh currently selected in Assets, and save its exact
decoded resource fingerprint as the reference identity. The family candidate
service inventories decoded roles and treats FPP/TPP pairing conflicts and
missing native consumer rules as separate diagnostics. See
DL1_RETAIL_RIG_PROFILES.md. This does not complete all family profiles or live
acceptance.
