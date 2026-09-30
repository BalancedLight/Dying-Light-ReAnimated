# Reusable rig setups

Conform's **Helpers and Hooks → Reusable rig setup** panel saves and opens
`.dlrsetup` files. Start a Studio session and save a capability profile definition
before capturing a setup. Open it on another model, inspect each destination
mapping, preview the requirements report, acknowledge the review and apply.
The change is one normal workspace undo/redo transaction.

A setup contains the profile definition and selected capabilities, character
owner-role declarations, semantic node selectors and channel/LOD choices.
Selectors have opaque preset-local keys and proposed native names/kinds/roles.
They contain no donor physical node indices, model IDs or source entity IDs.
Their keys are deterministic for unchanged input decisions. A canonical SHA-256
detects accidental edits; it is an integrity check, not a trust signature or
native compatibility certification.

Proposals use unique roles from the same declared profile family, or exact
native names and kinds. Conflicting name/role results and ambiguous or absent
matches remain unresolved. Every selector needs a distinct destination-owned
node of the same kind before a preview can be applied. Review the actual parent,
owner and consumer diagnostics; a unique name alone is not compatibility proof.
External asset nodes must be handled on their own asset. A setup cannot silently
take over an owner role already bound to another destination asset.

The destination retains geometry, weights, morphs, source clips, source units,
runtime scale and motion strategy, helper placement, bounds, and existing nodes.
Coordinates fitted to a donor are not reusable placement instructions. Helpers
and frame decisions are listed for destination calibration review. Missing
helpers must be created/fitted through the existing helper tools; setup transfer
does not invent a placement for them. Detection, hand/eye guide fitting and skin
correction recipes are not yet transferred by this format.

Applying replaces assignments for the setup's named roles, selected capabilities
and profile definition, plus mapped channel policies. Other node decisions stay
in place. Existing locks and profile edit constraints still apply. Inspect the
report for previous-profile assignments that now require migration.

Donor channel evidence and validation receipts are not copied. The candidate
records proposed channel choices against the destination source hash with
`UserOverride` provenance and the setup revision; applying requires author
review. Existing destination receipts remain historical, stage approvals are
invalidated through the normal session machinery, and the previous build receipt
is cleared. None of this establishes native consumer, compiled-load or gameplay
acceptance. Missing/unknown ownership still blocks native export where required.

The Core serializer and Codecs proposal/preview service are shared with the App.
Transferred decisions persist in the destination `.dlrmodel`, which can then be
reviewed and queued by the existing model batch runner. Alternatively, attach a
reviewed setup and explicit mapping directly to each queued package; the same
transfer service applies it to an owned snapshot before compilation. See
`DL1_MODEL_BATCH.md` for approvals, source/setup hashes, cancellation and resume.
Broader geometry/guide recipe reuse and native scenario acceptance remain open.
