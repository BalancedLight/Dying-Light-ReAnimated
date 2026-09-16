# Native size source studies

This offline research tool creates additional HumanAI presets for isolated body-size trials. It reads a caller-supplied preset file, copies selected control presets, and changes only their names and `m_ForcedBodyScaleMin` / `m_ForcedBodyScaleMax`. Fixed equal bounds remove scale randomization from each trial. All other source statements and the original controls remain intact.

Run `dotnet run --project tools/ScaleStudyTool -- <request.json> <empty-output-directory>`. The UTF-8 request has `sourcePath`, `definitionName`, and `controls`; each control has `presetName` and `trials`, and each trial has `presetName` and `bodyScale`. Paths and control names come from the local caller. The source must be valid UTF-8, optionally with a BOM.

The result contains a complete preset source and a manifest with hashes, unchanged control fields and explicit trial inputs. It does not overwrite the supplied source, copy mesh/animation assets, deploy anything, or claim native scale support. A full output file should not be installed over later project edits: merge only the reviewed trial presets into the current project.

The trials are for spawn-time scale. Post-spawn changes, exact Player resource resolution, root composition, local pose, contacts, collision/reach, IK calibration and supported limits still require their own evidence. Use the Character Rig Studio master plan's S1 through S6 experiments; S1 source generation does not close the other studies or their native gates. Never commit private game presets or generated retail-based output to the repository.
