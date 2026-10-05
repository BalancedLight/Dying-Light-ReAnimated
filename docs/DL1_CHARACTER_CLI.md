# Character commands

The Character CLI inspects a model package, exports a neutral face for sculpting, and imports a reviewed sculpt. Run these commands with the packaged executable or the development app entry point. They execute before the editor opens.

## Inspect a character

```text
DLReAnimated character inspect character.dlrmodel
```

The JSON report includes the source SHA-256, surface and triangle counts, expression names and descriptors, target slots, original channel and LOD bindings, bone bounds, subsystem and resource status, saved reviews, validation records, and export blockers. Gathered effect entries also report their original bundle identity, stored name, kind, and source-text extent. Use the reported surface IDs and expression names in the commands below.

## Add hair or an accessory

```text
DLReAnimated character add-attachment character.dlrmodel --attachment accessory.fbx --bone head --reviewed --output character-with-accessory.dlrmodel
```

Position the accessory in the character's neutral model space before importing. Choose an exact bone name from the inspection report for rigid binding. Omit `--bone` for an existing skin whose bone names and bind frames match the character. Review placement and binding before passing `--reviewed`.

The command appends the FBX surfaces and their materials to a new package while retaining the original character surfaces, facial channels and deltas, rig, variants and companion resources. Conflicting material identities, mesh or LOD names, and incompatible skin frames are rejected. The JSON report identifies the added surfaces and source/output hashes. Existing outputs cannot be overwritten.

## Reuse a retained material

```text
DLReAnimated character assign-material character-with-accessory.dlrmodel --target-material <appended-material-guid> --use-material <retained-material-guid> --reviewed --output character-with-retained-material.dlrmodel
```

`inspect` lists the material GUIDs and each surface's material binding. Select the accessory material and the original native material to reuse. The command checks its retained material and provider records, rebinds the accessory surfaces to that existing slot, and removes the unused accessory material row. Original surfaces and material slots remain intact, so preserved skins keep their original mappings. The replaced accessory material and any texture bytes it no longer uses are retained in the package archive.

The original material and provider must have their payloads in the package. Missing or ambiguous records and edited original slots are rejected. The operation saves a new package and clears the previous build and game acceptance records.

## Hide an accessory with a body region

```text
DLReAnimated character body-hide character.dlrmodel --resource <body-resource-id> --region <exact-region-token> --entity <exact-element-name> --reviewed --output character-with-damage-visibility.dlrmodel
```

Choose the retained body-element resource, one exact body-region token and a bone or mesh element from the model. The command adds one `AddMesh2Disable` relationship to that region. It keeps the original source in the package archive and preserves the other regions, comments, helpers and detached-part hide lists. Existing identical entries, stale selections and ambiguous model elements are rejected.

Use this after adding hair or another accessory that must follow a region's damage visibility. The editor offers the same operation through **Hide with region** and **Add hide relationship**, with its normal undo/redo and package save behavior.

## Bone bounds

```text
DLReAnimated character fit-bounds character.dlrmodel --bone bone_name
DLReAnimated character set-bounds character.dlrmodel --bone bone_name --center 0,0,0 --size 0.1,0.2,0.3 --reviewed --output edited-character.dlrmodel
DLReAnimated character bounds-controls character.dlrmodel --bone bone_name --scale 1.5 --output-dir bounds-controls
```

`fit-bounds` reports a candidate from retained weighted points without editing the package. `set-bounds` applies reviewed bone-local center coordinates and full X/Y/Z sizes in meters to a new package. The rig frames, geometry, weights, morphs and companions remain intact; previous build and game-acceptance records are cleared.

`bounds-controls` writes the unchanged baseline and three independent packages that scale only X, Y or Z. It requires a positive scale different from one and positive original bounds on all three axes. Its JSON manifest records each package hash and bounds. The new folder appears only after all four packages and the manifest are saved and checked. Use these controls for compiler readback and native shape measurements before assigning Height/Width labels.

## Ragdoll shape inputs

```text
DLReAnimated character shape-inputs capsule --spans 0.1,0.2,0.5 --scale 0.75
```

Supply full min/max corner spans already expressed in the bone frame. The JSON report gives padded spans, capsule axis, radius and cylinder length. Box output keeps all three padded spans; sphere output uses half the longest span. Equal longest spans select X first, then Y, then Z.

The calculation uses single-precision scale and padding arithmetic from the DL1 Player shape creation path. Capsule length has a separate scale adjustment. Use this report when comparing the compiler controls with measured collision shapes; the command does not load a model or convert its coordinate frame.

## Export a neutral face

```text
DLReAnimated character export-neutral character.dlrmodel --surface surface/0 --output neutral-face.obj
```

Choose a face surface from the inspection report. The OBJ uses meters and retains the control-point and triangle order needed for sculpt import.

Sculpt that face without adding, removing, reordering, or merging vertices or changing its triangles. Save the result as OBJ or binary FBX.

## Import a sculpt

```text
DLReAnimated character import-sculpt character.dlrmodel --reference reference.dlrmodel --target-surface surface/0 --reference-surface surface/0 --expression face_expression --sculpt sculpt.obj --reviewed --output authored-character.dlrmodel
```

Load the target and reference packages, choose the corresponding face surfaces, and use an expression name from the reference. Review the sculpt before passing `--reviewed`. The command calculates the expression deltas and saves its source identity, descriptor, original channel index, target slot, and accepted review in the output package.

The target neutral geometry, embedded source, rig, and companion data are retained. Changes invalidate the existing compilation and runtime acceptance records.

If the target already contains that expression, choose a conflict policy:

| Option | Behavior |
| --- | --- |
| `--conflict reject` | Reject an existing expression. This is the default. |
| `--conflict keep` | Keep the existing expression and its review. |
| `--conflict replace` | Replace the expression with the reviewed sculpt. |

Use a new output filename for each operation. Export uses `.obj`; import uses `.dlrmodel`. Inputs and existing outputs cannot be overwritten. Completed outputs appear through an atomic rename.

## Export gathered effects

```text
DLReAnimated character export-effects character.dlrmodel --output character-effects.rpack --compression zlib
```

The command writes the required current packed effect definitions as one FX resource, retaining their exact stored names, signed kinds and source text. Compression is `none` by default or `zlib`. It checks the original bundle receipts, all quoted nested `.fx` identities, and semantic readback before publishing a new output. Nested names resolve globally; explicit relative paths remain unsupported. Missing, ambiguous or edited definitions without their original packed kind are rejected. The JSON report includes definition counts and archive/payload hashes. The character package remains unchanged.

This command produces the gathered effect resource for inspection and compiler integration. Complete character output also requires its material, texture and deployment checks.

## Attach a supplemental source

Select one exact member from a ZIP and provide its canonical virtual resource name and content SHA-256:

```text
DLReAnimated character attach-source character.dlrmodel --archive sources.zip --member sources/effects/effect.fx --virtual-name data/effects/effect.fx --subsystem damage --expected-sha256 <content-sha256> --reviewed --output character-with-source.dlrmodel
```

The output preserves the member bytes with a receipt containing the archive SHA-256, exact member name, virtual name, content SHA-256, byte length, and source review. Pass `--archive-sha256 <hash>` to pin the archive as well as the member content.

The added resource is a required candidate awaiting character-association review. Existing missing references and subsystem checks remain in the inspection report. Exact missing references may coexist with their new candidate; payload duplicates, case aliases, root replacement, and ambiguous providers are rejected.

Subsystem names are `geometry`, `rig`, `skinning`, `materials`, `textures`, `lods`, `morphs`, `variants`, `facial-definitions`, `ragdoll`, `cloth`, `damage`, `helpers`, and `detached-parts`. Choose the subsystem explicitly.

ZIP member and virtual paths must use forward slashes without absolute paths, empty segments, or dot segments, and their extensions must match. The reader checks directory counts and sizes, duplicate paths, the selected member length, and expected hashes. It reads only the selected member's expanded bytes. Archive limits are 16 GiB, 100,000 entries, and 64 MiB of directory metadata; a selected member may be at most 64 MiB. Known script types require valid UTF-8 and at most 4 million characters. FED inputs use the FED reader's limits. Other binary and unknown types retain their bytes.

Attachment preserves geometry, rig, morphs, source data, and existing reviews. It clears the prior build and runtime acceptance records. Later companion edits and renames archive the original source with its receipt; the authored derivative uses its updated content identity.

## Automation

Each successful operation writes one JSON report to standard output. Errors go to standard error.

| Exit code | Meaning |
| --- | --- |
| `0` | Completed |
| `2` | Invalid arguments, input, review, or output |
| `130` | Cancelled |

Unknown options, duplicate options, missing values, and extra positional arguments are rejected. Ctrl+C cancels the operation. Package loading and sculpt readers enforce the supported file and geometry limits.

For command syntax, run:

```text
DLReAnimated character --help
```