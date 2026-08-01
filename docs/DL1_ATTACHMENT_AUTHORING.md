# DL1 prop and weapon attachment authoring

The C# editor supports one animated DL1 target with rigid props or weapons
decoded from the active retail catalog. This is an authoring-preview workflow,
not a multi-actor scene format.

## Workflow

1. Index the Dying Light 1 installation and load the animated retail target.
2. Open **Attachments**, filter and select a type-272 retail mesh, then choose a
   decoded target bone, helper, or prop socket.
3. Set a local translation, quaternion-backed Euler rotation, and non-zero
   scale. Choose **Preview only** only when the binding must not appear in
   authoritative export evaluation.
4. Add the binding. Playback and timeline scrubbing reevaluate the target pose,
   parent transform, and local offset through the same `AnimationEvaluator`
   used by export.
5. Select a document attachment to apply another local offset or remove it.
   Project edits use the normal immutable commit path, so undo, redo, dirty
   state, recovery autosave, and atomic `.dlraproj` save all include them.

Projects store the retail asset identity and SHA-256, parent index and parent
name guard, local TRS, scope, and binding ID. They never store mesh buffers,
textures, or any other proprietary retail bytes.

## Resolution and rendering contract

- A saved asset must resolve to the same DL1 install, provider, resource type,
  resource row/name, and precedence. Its decoded SHA-256 must still match.
- New bindings store the canonical parent bone/helper name as well as its
  numeric index. If a different rig places another bone at that index,
  evaluation reports `attachment_parent_bone_mismatch` and omits the binding.
- The D3D handoff composes immutable target and attachment mesh snapshots.
  Static surfaces retain their decoded entity transform beneath the evaluated
  attachment world transform.
- A skinned prop is CPU-evaluated once in its own decoded bind pose, converted
  to a rigid mesh, and then follows the target parent. It is never fed the
  animated actor's unrelated skin palette.
- Missing assets, malformed surfaces, fingerprint changes, invalid parents,
  and surface/count limits produce visible diagnostics. No replacement mesh or
  guessed socket is substituted.

The document limit is 32 bindings and the composed scene limit is 8,192 draw
surfaces. Catalog pickers publish at most 5,000 filtered rows to WPF at once.

## Deliberate first-release boundaries

- Attachment local offsets are constant across the animation. Bone motion on
  the shared timeline supplies the animation; keyed attachment-offset curves
  are deferred.
- Attached props do not play an independent animation or morph track.
- Material-resource and texture fidelity follow the main retail-mesh preview
  status.
- There is no multi-actor graph, movie serializer, constraint graph, physics,
  cloth, collision, or gameplay weapon-state emulation.
- An authored/exportable binding is present in authoritative export evaluation,
  but DL1 ANM2 itself contains bone/mimic tracks rather than embedded prop
  meshes. The project binding is the durable assembly instruction.

## Focused regression evidence

`AttachmentAuthoringTests` covers the retail-mesh picker and prop-helper
default, schema-1 identity/parent/TRS persistence without retail bytes, strict
parent-name failure, static composition, independently skinned bind-pose
baking, and missing-asset diagnostics. `EvaluationPipelineTests` continues to
prove authored versus preview-only attachment ownership.
