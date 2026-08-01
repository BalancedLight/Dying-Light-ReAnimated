# DL1 FBX animation-domain import

Normal C# FBX animation import uses `FbxReadOptions.Animation`. The bounded
binary reader retains:

- object headers and connections;
- model hierarchy, authoritative `Pose::BindPose` matrices, and Model-transform
  fallback data;
- animation stacks, layers, curve nodes, and curve values; and
- blend-shape channel objects needed by a future facial-curve adapter.

It deliberately does not materialize the children of known model-domain
objects: `Geometry`, skin/cluster/base-blendshape `Deformer`, `Material`,
`Texture`, `LayeredTexture`, and `Video`. `BlendShapeChannel` remains decoded
because it can own `DeformPercent` animation without requiring shape topology.
Vertex, polygon, normal, UV, tangent, weight, material, texture, and shape-delta
arrays therefore cannot consume the animation import allocation budget or make
a valid skeletal clip unreadable. Every light object header remains available
in `FbxBinaryDocument.SkippedObjectPayloads`, and every skipped node is
explicitly marked with `ChildPayloadSkipped`.

This is a domain boundary, not relaxed whole-document validation.
`inspect-fbx` and strict Blender-output inspection continue to use the complete
document profile and validate requested geometry normally. The animation reader
walks skipped child headers and property framing without inflating arrays, then
requires the first child-list terminator to equal the object's declared end.
An invalid end offset therefore cannot silently consume a following skeleton or
animation object. Invalid FBX structure, unsupported binary versions, malformed
selected-stack data, ambiguous changing takes, and invalid skeleton transforms
still fail closed.

`Pose::BindPose` globals take precedence per bone and are transposed once from
the FBX row-vector array convention into Core's column-vector convention.
Uncovered bones fall back to evaluated unanimated Model globals. Cluster
topology, weights, and `TransformLink` arrays remain outside this animation-only
profile.

When an FBX contains multiple takes and the user did not select one, normal
animation import automatically chooses a take only when exactly one one-layer
stack owns changing limb transform channels (`Lcl Translation`, `Lcl
Rotation`, or `Lcl Scaling`). Visibility or custom curves cannot make an
otherwise static take win selection because the transform evaluator does not
apply them. A static rest-pose file is accepted only when exactly one one-layer
stack owns any evaluated limb transform channels. Multiple plausible takes
remain an actionable error instead of being selected by name or order.
Malformed curves are retained as stack-local diagnostics: they make their
owning take unusable, do not make an unrelated take unreadable, and still fail
explicit import when that malformed take is selected.

Semantic parsing, take analysis, timebase inference, hierarchy evaluation, and
per-frame sampling all honor the import cancellation token. Curve time ranges
and cadence deltas are reduced without flattening duplicate arrays. In addition
to the per-stack frame limit, import rejects the aggregate `bone count x frame
count` before track allocation; the default bound is 1,000,000 sampled
transform keys and can be explicitly configured.

## Evidence

`FbxBinaryReaderTests` proves that a geometry array which exceeds the complete
reader's configured array limit is skipped by the animation profile while its
object identity and sibling skeleton objects remain available. It also proves
that a forged skipped-object end offset cannot swallow the sibling skeleton.
The semantic tests lock authoritative BindPose priority, stack-local malformed
curve handling, cancellation, aggregate-key rejection, explicit stack curve
isolation, selected nonzero sample ticks, and the rule that only
evaluator-supported transform curves influence automatic take selection. A
compatibility test retains the original complete-reader overload and the
pre-existing positional record deconstruction shapes.

The animation-only profile intentionally does not report polygon counts or
model-payload semantic errors because doing so would require parsing the domain
it excludes. Python preflight nodes that require exact quad inventory or
geometry-error text remain pending rather than being counted as C# parity.

`FbxAnimationDomainCompatibilityTests` runs the same optional external
11-file Mixamo corpus used by the Python animation-domain tests. When
`DLR_FBX_ANIMATION_CORPUS_ROOT` is unset it uses
`F:\Fbx\AnimationTests`. Every exercised control is pinned by exact byte
length and SHA-256 before it is decoded; a same-named replacement cannot
satisfy the expected frame, curve, and topology-exclusion checks. When a file
is unavailable the corresponding xUnit control is reported as skipped, never as
a green pass. No FBX from this corpus is copied into the repository or release.
