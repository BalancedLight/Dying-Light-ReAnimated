# Reviewed model-package batches

`DLReAnimated batch-models <manifest.json> <output-parent>` builds reviewed
`.dlrmodel` packages sequentially through the same complete package builder as
the Models workspace. It writes source files, official compiler products,
materials/textures, companions and selected authored animations or existing-bank
references according to each saved package. It does not deploy or launch a game.

Each manifest has `format: "dl-reanimated-model-batch"`, `version: 1`, a nonempty
GUID `id`, a `compiler` file declaration, optional `retailData0`, optional
`compilerWorkingDirectory`, optional `additionalToolInputs`, and 1–64 `items`.
A file declaration contains `path` and its exact `sha256`. Paths in a manifest
are resolved against the manifest directory. Each item contains a distinct GUID
`id`, a display `name`, a package file declaration `package`, and `approved`.
Approval applies to those exact package bytes; its default is false.

Use a short explicit compiler working directory when native compiler path limits
require one. No workstation path is built into the batch contract. Include
additional tool/configuration files whose identities must be pinned. The receipt
states that this is declared-input provenance, not proof of a complete native
toolchain dependency closure.

The output parent receives `batch-<id>/receipt.json`, an ownership marker, a job
lock, and isolated per-item attempt folders. Each attempt retains an immutable
source snapshot. Failure of one item does not stop later independent items.
Cancellation records the current item and leaves later entries pending. A second
invocation resumes after acquiring the job lock; concurrent runs cannot mutate
the same job.

Resume rechecks source and declared-tool hashes plus every completed output file,
including unexpected additions. Changed sources or outputs require review and
are not overwritten. Changing the manifest or exporter contract requires a new
batch identity. A materialized result marked NeedsReview cannot silently become
accepted on a later resume. Failed/cancelled attempts may be retried with the
same approved inputs; prior attempt directories remain available.

`--inspect` reads the recorded receipt without revalidating current files. Normal
execution returns 0 only if every item is CompilerValidated, 2 for failed or
review-required items, and 130 after cancellation. CompilerValidated is not a
runtime binding, loaded-resource or gameplay acceptance result.

In Conform's **Verify and Export** stage, expand **Reviewed model batches**.
Add saved packages, inspect the selected file and revision, and mark individual
items **Approved**. Choose the compiler, optional retail Data0 and working
directory, and an output parent. **Save queue** writes a portable JSON manifest;
**Run / resume batch** uses the same durable runner as the CLI. **Cancel** retains
completed attempts. Open that saved queue after restarting the app to resume.
If a manifest was moved, select the original output parent; this preserves its
batch ID. Changing source approvals or tool settings creates a new identity.
**Refresh selected** rereads a modified package and clears its approval.
**New batch** retains the visible entries and their source approvals while
assigning a new identity on save. **Inspect receipt** reads historical results;
it does not revalidate current artifacts. Additional tool inputs from a CLI
manifest are preserved; editing those advanced pins currently requires JSON.

Each item may include an optional `setup` with a pinned `preset` file declaration
and a `bindings` array of `{ key, destinationEntityId }`. These IDs belong to the
exact destination package revision; they are never physical indices or copied
donor IDs. Approval covers the package hash, preset file hash and binding choices.
Absent setups are omitted from canonical JSON so existing v1 job hashes remain
unchanged. Older builds reject the added field rather than silently ignoring it.

In **Selected item: reusable setup**, choose a `.dlrsetup`, review the destination
mapping and requirements, preview, acknowledge review and attach. Then approve the
item in the queue. Repeat for each target package; each can use its own preset and
mapping. Attaching/removing a setup clears item approval and creates a new batch
identity on save. Refreshing a package also removes its old setup mapping.
**Review linked setup** reopens saved bindings for inspection or revision.

The runner snapshots the preset alongside the approved package and applies it
through the shared `FbxRigSetupTransfer` service before invoking the existing
package builder. `setup-applied.dlrmodel` retains the prepared authoring candidate
inside the attempt folder. Original inputs are never rewritten. Changed preset
bytes before building or resuming require review; changes during a build retain
the output as NeedsReview. Setup/profile failures are isolated per item, and
unverified profile diagnostics stay in the receipt. Expected compiler data
rejections and timeouts likewise do not abort independent later items.

The same optional setup contract works through `batch-models`; the CLI does not
duplicate setup application. The entire resolved manifest is limited to 1 MiB,
with at most 64 packages and 4096 mappings per setup. Split large mapping sets
into smaller reviewed batches. Broader geometry, hand/eye guide and skin-correction
recipe reuse remains separate work; this format transfers profile/channel choices
while keeping fitted anatomy and native acceptance destination-owned.
