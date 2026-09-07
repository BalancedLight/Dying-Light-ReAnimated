# Secondary motion preview

The Secondary motion panel evaluates cloth and secondary strands after authored animation/edits/IK and actor placement. TPP uses the final display pose. FPP uses the authored model pose before view-only HSpine/projection correction, then transfers only simulated secondary corrections to each presentation. The solver is labeled **MPC preview approximation**: it is not the native engine solver and does not establish compiled or in-game compatibility.

## Authoring and persistence

Custom-model schema 4 contains optional `SecondaryMotion` and `FacialPresets` data. Versions 1–3 migrate in memory with empty optional features; embedded source FBX and texture bytes are preserved. A group declares fixed anchors, free or virtual particles, explicit structural/shear/bend links, and sphere/capsule colliders. Only explicitly named dynamic output bones change. Anchor bones, other body bones, camera helpers and weapon helpers are retained.

Load/save setup exchanges the strict JSON representation exposed by `SecondaryMotionSetupSerializer`. All bone references must resolve, every free particle must connect to an anchor, output bones cannot be driven twice, and values must remain finite. The editor offers group enable, stiffness, bend stiffness, damping, animation follow, reset, and anchor/collision overlays. These are preview parameters, independent of native PHX coefficients.

**Save model copy** includes current secondary settings and facial presets in a new content-addressed `.dlrmodel`. It does not overwrite the existing model or silently change the active project's target. Open the copy in Models and save the project to use the existing immutable project-asset transaction. Setup edits in the preview panel are otherwise pending and are not automatically persisted.

## Evaluation API

```csharp
var session = new SecondaryMotionSession(definition, orderedBoneNames,
    initializationPose: bindFrame,
    parentIndices: orderedParentIndices);
SecondaryMotionResult result = session.Sample(timeSeconds, seconds =>
{
    // Sample the same clip/context deterministically at the requested seconds.
    // SecondaryMotionDomain.SelectPhysicsPose chooses authored FPP or display TPP.
    // Keep the original imported/source bone frames: model-owned offsets use them.
    return new SecondaryMotionFrame(physicalModelGlobals, actorWorldTransform);
});
// result.Globals is a new model-space matrix array; inputs remain immutable.
```

Each timeline owns its session. Sampling uses fixed 120 Hz ticks and eight constraint iterations. Fractional requests run on disposable state so they cannot affect later fixed ticks. Backward seeks replay from the clip start; pause consumes no wall time. Reset/recreate a session when the clip, model, settings, editing layers, display context or actor-motion policy changes. A pure random-access sampler is mandatory. The current replay limit is one hour; large seeks can be expensive.

Actor placement must be a unit-scale rigid rotation/translation. Scaled, sheared or mirrored actor transforms are rejected with an actionable error, including during bind initialization. Author geometry and physics dimensions at their intended model size; this preview does not implement actor-scale conversion of explicit spring lengths.

The optional bind initializer first settles for 0.25 seconds, then eases anchors and colliders into the first animation pose over 0.5 seconds. With parent indices it blends local TRS through the hierarchy, preserving rigid rotation paths. This hidden, deterministic preparation establishes the garment outside the limbs before a run/crouch starts; it never changes published body/helper matrices. Both the desktop adapter and private validation helpers supply it. Callers without a bind initializer retain the prior direct-first-frame behavior, and a zero initialization duration disables it.

The optional animated-shape spring (`RestShapeStiffness`, 0–1000 inverse seconds squared) accelerates particles toward their current animated positions after motion transport. Its default is zero, retaining the free simulation behavior. It can restrain rigid decorative strands without freezing their motion; tune damping alongside it. This setting has no implied native PHX equivalent.

Local corrections are first derived in source-bone frames as `inverse(physicalAnimatedGlobal) * physicalSimulatedGlobal`. Chrome preview may reorder bones and author different local axes. The adapter uses the explicit source-to-physical map and converts each correction through `B = inverse(sourceBindGlobal) * emittedBindGlobal`, applying `inverse(B) * sourceDelta * B` to the emitted bone. This keeps nonzero virtual-tip/collider offsets in their original frame and preserves the world-space skinning effect.

For FPP, the camera pane receives its own display global multiplied by the converted correction; external orbit receives its authored presentation global multiplied by the same correction. Camera/weapon helpers remain untouched. Lens/view-only correction changes do not reset or drive the physical session. Physical debug overlays belong to FPP external orbit, while TPP overlays remain in its display pane.

## Native export from the model workflow

Import one MPCloth wrapper and every PHX source it references with **Import native scripts**. Save a model copy, open that copy in Models, and use **Export source**, **Compile model**, or **Deploy to Developer Tools**. Saved facial presets are emitted as a matching-basename FED automatically. Their names are exact native pose names; authored speech variants use the preset name plus ` speech`. Every pose includes all morph rows, resetting unspecified targets to zero. A pose with more than 64 simultaneously active targets is rejected; more than 64 inventory rows is supported.

To provide native expressions, save explicit presets named `normal`, `alarmed`, `dead`, `_NONE`, `BLINK`, or `EYES_UP`, `EYES_DOWN`, `EYES_LEFT`, `EYES_RIGHT` as appropriate. The exporter does not guess which artistic controls mean closed eyes or which expression should be the default. Speech variant names must not collide with another saved preset.

Native script export preserves PHX text, coefficients, `Mode3D`, virtual endpoints, and MPCloth binding flags. Each PHX is assigned a deterministic name under the exported model identity; the MPCloth references are updated, so another model exported from the same saved setup cannot silently take ownership of its physics files. A missing PHX or bone, duplicate binding, malformed grid, or unpackaged include prevents export. Inline custom includes before importing; the installed `MeshPartCloth.def` header is supported.

The deployment transaction installs FED/MPCloth beside the model and PHX under `data/odephysics/meshpartcloth`. All files participate in the ordinary hash inventory, conflict review, backups and rollback. **Compile model** also writes the loose companions under `native-companions/data/...` beside its output, since an RPack alone does not supply these loose native scripts.

Retained scripts are explicit authoring data, not a conversion of the preview solver. Export does not re-fit their endpoints or collision declarations to newly compiled bone frames/bounds. Recheck those whenever the rig changes and inspect the actual garment in Player. The manifest, compiler receipt and deployment preflight distinguish source-inventory validation from that remaining native validation. A preview-only setup exports no PHX and reports the missing native setup explicitly.

## Native scripts

`Dl1ClothCodec.ReadPhx` recognizes bounded grids, normal/fixed bone nodes, Clip seam bindings, numeric script variables, scalar native parameters, and sphere/capsule declarations. Clip cells retain their exact source/target coordinates and distances; missing targets, duplicate cells, self-links and cycles fail validation. `ReadMpCloth` reads resource bindings and flags. `NativeClothSyntax.Write()` retains the original text exactly, including unknown calls, comments, spacing and expressions. `ReplaceCommand` changes only a selected call. `WritePhx` and `WriteMpCloth` author the supported subset and validate their own output.

Includes are retained but never executed. Missing bone/resource references and malformed calls are explicit errors. Unknown statements, hook behavior, virtual endpoints and mesh-bound collision centers remain explicit diagnostics and are never silently converted into guessed simulation settings. Clip parsing verifies the documented binding contract, not native seam dynamics. A valid supported parse is not a native compiler receipt. In particular, native sphere centers may use mesh bounds rather than bone pivots; the editor's explicit collider offsets do not claim to reproduce them.

Contacts include particles, quarter/midpoint samples of structural edges and shear diagonals, and the closest point between each such link and a collider. Corrections are distributed only to free endpoints, with bounded projection near fixed attachments and no artificial contact-normal rebound. A fixed attachment embedded in a collision proxy remains an authoring conflict; the solver does not move its anchor to conceal it.

These contacts are a diagnostic tool, not a mesh-level penetration test. The current approximation does not implement cloth self-collision, external world collision, native wind generators, native adaptive-physics blending or mesh-bound native endpoint inference. Inspect the rendered garment through the actual motion suite before accepting it.
