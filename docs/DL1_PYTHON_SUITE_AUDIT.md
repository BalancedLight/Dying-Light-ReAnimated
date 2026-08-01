# DL1 Python suite audit

## Outcome

The checked Python regression-reference suite contains exactly **616 pytest
node IDs**. It is an important drift/fallback control, but installed DL1 1.55
assets, matching-build decompiles, and captured game behavior take precedence
over a conflicting Python result. The first reviewed C# audit classifies every
node without promoting a whole test file or feature family on partial
evidence:

| Classification | Exact nodes | Meaning |
| --- | ---: | --- |
| `applicable_mapped` | 92 | A direct bounded C# regression or differential control is named for this exact Python behavior. |
| `explicit_exclusion` | 317 | The node is outside the stated DL1 first-release contract for a recorded reason. |
| `still_pending` | 207 | The behavior remains an applicable or unresolved parity gap. |
| **Total** | **616** | Every collected node appears exactly once in the checked manifest. |

This is an inventory and drift gate, not a parity-complete claim. In
particular, the 92 mapped nodes do not mark their surrounding files or feature
families complete. The 207 pending nodes remain release work until direct C#
evidence supports reviewing their exact classifications.

Four FBX animation-preflight nodes were deliberately returned to pending after
review. They require exact 20,500/23,770-quad inventory, ignored geometry-error
text, or a dedicated static-rest-pose diagnostic. The C# animation reader
correctly excludes those model payloads and imports the pinned corpus, but it
does not parse or claim those exact inventory/diagnostic behaviors. BindPose
priority, stack-local malformed-curve handling, arbitrary stack isolation,
selected nonzero sample ticks, and the exact 11-file corpus remain mapped by
direct controls.

This review maps eight formerly pending facial/mimic nodes to direct bounded
C# controls: facial scalar import without topology, declared and fallback
facial timing, percent-scale handling, active-unmapped reporting, and the three
Common46 profile behaviors. It also corrects earlier overclaims. Two optional
hierarchy nodes load a DL2 `.crig` and are excluded from the DL1 release; two
mimic-project nodes exercise legacy Python extension/copy mechanics and are
excluded rather than treated as native C# project parity; and the Blender
first-sample BindPose node is pending because it finishes with an FBX-to-ANM2
rebuild/value comparison that the C# handoff does not implement.

The latest review maps seven additional exact nodes. The cached decoder now
matches random-access sampling bit-for-bit on the pinned DL1 infected-turn
clip, decodes a unique packed slot only once while retaining an ordered track
subset, and cancels from inside active work under a materialized-output bound.
The selection/cancellation Python controls happen to use a DL2 payload, so the
C# evidence deliberately locks their target-neutral behavior with DL1
sampler-v1 data rather than adding a DL2 reader. A separate parented-pelvis
control proves world-space heading removal reconstructs a finite local
transform without overwriting the animated parent. End-to-end body import also
proves 257 Cayley samples across 720 degrees remain scalar-equivalent while
adjacent quaternions stay in one hemisphere. Finally, the Blender handoff now
uses separate ordered selected-track decodes for Action bones/motion and for
unresolved original-cadence sidecars instead of materializing all tracks at
once. Its embedded Blender helper is also locked to bulk FCurve writes, one
dependency update, non-forced endpoint/all-bone baking, and one armature link.
The cumulative root-heading reporting case remains pending.

Three mixed-file nodes are now classified by their actual first-release scope
rather than left unresolved: the DL2 cached sample, the generated
3,343-frame/271-bone DL2 job, and the legacy-project test that switches into
both a built-in DL2 target and a custom CRIG target are exact exclusions. The
C# application continues to reject DL2 ANM2 and legacy projects and does not
gain a DL2 or custom-model code path from this audit correction.

Three `test_anm2_to_fbx.py` nodes now have direct bounded C# evidence. The
strict 2,210-frame control crosses physical 64 KiB page boundaries and matches
cached versus random-access samples on both sides. The actual Blender staging
service bakes active motion into the unique Root while retaining the original
helper sidecar and proves a present but static accumulator changes neither
Root translation nor its reported bake state.

## Machine-readable contract

- `tests/fixtures/dl1_python_suite_audit_rules_v1.json` contains ordered,
  reviewed classification rules, rationales, and named C# evidence for mapped
  behavior.
- `tests/fixtures/dl1_python_suite_audit_v1.json` stores all 616 exact node IDs
  in pytest collection order, their classifications, rule IDs, areas, source
  file identities, and summary counts.
- `F:\DyingLightTools\ReAnimated - Python\tools\audit_dl1_python_suite.py`
  recollects the archived suite and compares it with the checked manifest.
- `PythonSuiteAuditTests` independently checks the manifest, rules, hard-coded
  reviewed totals, node/rule matches, and collection identity. When
  `DLR_PYTHON_ORACLE_ROOT` is set by the live parity wrapper it also checks
  every archived Python test source identity.

The live audit fails closed when a Python node is added, removed, reordered,
or reclassified; when any Python test source changes; or when the reviewed
rules change without deliberate manifest regeneration. A newly collected node
is reported as unclassified even if the conservative default rule would
eventually place it in `still_pending`.

The reviewed collection identity is
`9A2C1B71F098AB29709EF68D7F4AEE3BF5698902656C8FFFA1E1034646E692EF`.
The aggregate Python test-source identity is
`BCD023BEA1542ED58E612133775696FB8DFB6AE898319FD339CFBD90940AA7A5`.

## Quantified pending inventory

The largest current pending areas are:

| Area | Pending nodes |
| --- | ---: |
| Retargeting, mapping, helpers, and rig analysis | 106 |
| ANM2 sparse decode and root behavior outside the mapped provenance/cadence controls | 15 |
| WPF behavior corresponding to legacy GUI regressions | 27 |
| FBX evaluation outside the bounded mapped controls | 26 |
| Animation-library and export workflows | 19 |
| ANM2 to FBX and Blender helper round trips | 14 |

The ANM2-to-FBX area has 21 total nodes: seven bounded handoff/cadence nodes are
mapped and 14 remain pending. All 15 schema-1 ANM2 provenance nodes are also
mapped separately to the public bounded C# codec and Blender integration.
The six Python mimic-builder/profile nodes are now directly mapped; the four
remaining nodes in `test_mimic_project_extensions.py` are exact DL2,
legacy-project, or Python-implementation exclusions rather than C# parity
claims.

The largest individual pending files are
`test_unified_gui_regressions.py` (27),
`test_universal_skeleton_analysis.py` (27),
`test_automatic_retarget_plan.py` (20),
`test_fbx_transform_contract.py` (19 pending and 6 mapped),
`test_custom_fbx_release_candidate_editor_rpack.py` (10).

Explicit exclusions are also exact and reviewable. Their current groups are
DL2-only behavior (132), deferred custom-model/CRIG authoring (117),
legacy Python application or packaging surfaces (30), intentionally
unsupported legacy projects (29), obsolete Python compatibility behavior (5),
and the deferred legacy SMD target path (4).

## Validation

Run the live audit alone:

```powershell
python "F:\DyingLightTools\ReAnimated - Python\tools\audit_dl1_python_suite.py" `
  --repository-root "F:\DyingLightTools\ReAnimated - Python"
```

Run the live audit and focused C# integrity test:

```powershell
.\tools\validate_dl1_python_suite_audit.ps1 `
  -Configuration Release `
  -PythonOracleRoot "F:\DyingLightTools\ReAnimated - Python"
```

The normal bounded parity gate also runs both checks:

```powershell
.\tools\validate_dl1_parity.ps1 `
  -Configuration Release `
  -PythonOracleRoot "F:\DyingLightTools\ReAnimated - Python"
```

After intentionally changing the Python suite or reviewing a classification,
inspect collection and rule changes first, then deliberately replace the
manifest:

```powershell
python "F:\DyingLightTools\ReAnimated - Python\tools\audit_dl1_python_suite.py" `
  --repository-root "F:\DyingLightTools\ReAnimated - Python" `
  --write-manifest
```

Manifest regeneration is not proof of parity. Any node moved from pending to
mapped must name direct C# evidence in the reviewed rules. Any exclusion must
remain within the documented DL1 first-release boundaries.
