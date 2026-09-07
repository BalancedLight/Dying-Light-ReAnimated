# Existing animation banks

Models offers **Reference existing game animation bank** beside the animation-script alias. It is stored in model schema 5; schema-4 physics and facial presets survive migration and retain the previous authored-bank default. The Characters-only workflow uses this saved choice.

**Compile model RPack** honors the same saved choice when building a complete package. It validates the installed source graph and compiled bank, writes only the explicit ASCR dependency, and excludes imported review clips from animation output. The package manifest records the stock-bank evidence and leaves runtime binding verification false. Saved facial presets and explicit native physics companions follow the [native export workflow](secondary-motion-preview.md#native-export-from-the-model-workflow) in either animation mode.

In this mode the model ASCR references the selected bank's virtual `.scr` filename. Deployment validates the reachable stock script/include graph from the retail archive and, when required, sibling installed DLC archives. A differing loose project override blocks the operation. Deployment publishes no local animation SCR, ANM2, animation RPack or animation-refresh manifest. The official model/material compiler still runs in isolation; this is not a source-only dry run. Model sources, compiled objects, material database updates and rollback receipts follow the ordinary transaction.

The published `.msh_obj` is a standalone runtime resource container. Raw console-compiler units use compiler-only resource types and compiler-relative chunk addresses; those units are retained as `.msh_compiler_obj` diagnostics and must not be installed as runtime mesh objects. Publication normalizes the container tables while preserving mesh payload bytes, then validates the runtime layout and the compact mesh's embedded animation-script reference.

Converted compiler items also have their load-suppression flag cleared. The Player's per-item load dispatcher skips items with flag bit 0 set, even when their payload bytes and table addresses are otherwise valid. Conversion changes this bit only for items owned by converted compiler resources; unrelated flags, ordinary resource units and legal chunk loading modes remain unchanged. Published mesh validation rejects payloads that still carry the skip-loading flag.

Validation separates three claims: the selected bank exists on disk, the compiled model has a valid runtime container and the expected animation dependency, and Player has created a working animation binding. Only the first two are offline checks. Compiler receipts always leave runtime binding verification false; successful compilation does not establish that a particular Player session resolved and initialized the dependency.

Source availability and native bank availability are separate checks. The selected installation must contain exactly one readable, nonempty compiled type-322 bank of the requested name. `Dl1StockAnimationReference.CompiledBank` retains its archive/resource identity, compiled sequence count and SHA-256 values. A source-only include family is refused instead of producing an empty animation picker.

Existing-bank export preserves incoming bind frames for all bones outside explicitly declared secondary driven bones. Stock animation descriptors install local transforms directly, so reorienting their bind frames would twist the compiled skin even if a converted authoring preview looked correct. Only `SecondaryMotion.Groups.Particles.DrivenBoneName` entries receive the +X frame conversion used by native cloth; their locals and inverse references are derived against the preserved surrounding rig. They use compiled bind fallback when the stock animation has no corresponding track. The caller must supply a rig compatible with the selected bank; this mode does not retarget the bank's animations.

Include cycles are rejected; shared completed dependencies are checked once. The compiled **model** archive is retained at `out/ReAnimated/<resource>/model/<resource>_pc.rpack` as a required portable artifact, hash-recorded in the deployment receipt and covered by rollback. This separate directory avoids collisions when model and animation-bank names coincide.

The API uses `Dl1DeveloperToolsDeploymentRequest.ReferenceExistingAnimationLibrary = true` together with `DeployWithoutAnimations = true`, `InstallLooseAnm2 = false`, `ExportPortableAnimationRpack = false`, empty selections and no prepared animation library. Authored-bank behavior is unchanged. Stock-reference receipts use schema 3 and explicitly identify this mode; older receipt readers fail closed rather than assuming missing animation payloads.

The CLI exposes the same path:

```text
DLReAnimated deploy-model <model.dlrmodel> <project-root> <compiler.exe> <retail-Data0.pak> <character-id> <resource-name> <animation-bank> --stock-bank --preflight
```

Remove `--preflight` to compile and deploy. Conflicting owned/unowned project files are never silently replaced by this CLI.

Compiled morph checks retain exact names, dimensions and finite nearest-HALF quantization. Either nearest HALF is accepted at an exact midpoint; other deviations remain errors. Speech names present in the source must resolve to the intended compiled morph under Windows Player's prefix-first ordering (for example, `w` before `wide`). These checks are compiler/static evidence, not runtime acceptance or proof that morph-normal shading survives native compilation.

The inspected native DX11 morph shader changes position only. It consumes HALF4 XYZ deltas; the fourth component is zero padding, and no morph normal or tangent channel is consumed. Imported FBX normal deltas remain available in ReAnimated's preview, but are not exported as invented native payloads. Base normals and tangents are compiled normally. Native material and lighting appearance still requires its own visual review.

The inspected Windows Player builds the shader's active list after skipping weights whose absolute value is at most `0.001f`. It packs up to 64 active targets into pairs. A complete FED pose may therefore contain more than 64 rows when inactive rows are zero; the inventory length does not itself consume active shader slots. More than 64 simultaneous active targets is outside this native preview/export fidelity claim. This boundary is static Player evidence; it is not a retail or live validation claim.
