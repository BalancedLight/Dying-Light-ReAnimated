# Troubleshooting

## The GUI does not start

Run:

```
run_gui.bat --setup
```

Confirm Python 3.11 or newer is installed and available through `py` or `python`. Delete `.venv` and rerun setup when the environment is corrupt.

## PySide6 is missing

The launcher installs the `gui` extra automatically. Manual installation:

```bash
python -m pip install -e ".[gui]"
```

## Build fails with `Permission denied: '.'`

This means a required file or output field was blank and was interpreted as the current directory. Current releases validate file fields and identify the missing input directly. Check:

- embedded-bind mode is enabled, or a real source rest/T-pose FBX is selected;
- the target SMD/template/control fields are valid in Advanced mode;
- an output folder is selected.

## Do I need a separate T-pose FBX?

Usually no. Leave **Use imported animation FBX bind pose (recommended)** enabled. Disable it only for FBXs with missing or unreliable bind transforms, then choose a matching neutral/rest FBX.

## A required humanoid role is unmapped

Open Retargeting, run Auto-map, then fill every required role manually. Do not map two roles to the same source bone. Extra helpers can remain ignored.

## The mapping came from a different skeleton

A `.dlrmap.json` profile stores a skeleton hash. Accept the warning only when the new FBX genuinely uses a compatible hierarchy/naming scheme, then review all assignments.

## Player/female animation looks broken

Changing the `_ANIMATION_SCR_` target does not change the target skeleton. Use target SMD/template/reference assets matching the player/female rig.

## The resource is not visible in the editor

Check:

- the correct `common_anims_sp_pc.rpack` is installed;
- the selected `_ANIMATION_SCR_` resource is appropriate;
- the resource/sequence name is unique;
- the editor project was fully reloaded;
- `common_anims_PC.rpack` was not replaced.

## Append refuses the existing RPack

When a sidecar exists, its SHA-256 must match the pack. Restore the original pair or deliberately create a new project pack. Do not delete the manifest merely to bypass an unexplained mismatch.

## A locomotion loop snaps back

`inplace` and `bip01` raw loops reset to frame zero. Use the `motion` policy and configure the movie/graph to apply OffsetHelper or motion accumulation. See [ROOT_MOTION_AND_IK.md](ROOT_MOTION_AND_IK.md).

## The body is correct but fingers are not

Finger retargeting remains under active editor validation. Confirm the source finger roles and test a finger-light build when necessary. Do not change the validated body/ANM2 writer settings to compensate for a hand-only issue.

## Build log is needed for a bug report

Save:

```
project .dlraproj
mapping .dlrmap.json (when external)
dl_reanimated_build/build_report.json
<pack>.dlrmanifest.json
GUI build log text
source FBX name and tested frame
```

Do not upload commercial/game assets publicly without checking redistribution rights.


## The EXE build fails

Build Windows executables on Windows with Python 3.11 or newer:

```
build_exe.bat
```

Delete `.venv-build`, `build`, and `dist` before retrying a damaged build. The script runs a frozen `--self-test`; inspect `dist\DL-ReAnimated\exe_self_test.json` when packaging succeeds but the app does not start.

## Long animation appears in the timeline but stays in bind pose

Rebuild the RPack with the current writer. Dying Light supports very long clips, but their packed stream slots must be split across physical 64 KiB pages.

A corrected build report includes fields similar to:

```json
"page_count": 3,
"page_frame_spans": [210, 210, 145]
```

The spans must sum to `frame_count - 1`. Rebuilding is required for a malformed payload; changing the AnimationScr or target model will not repair its page layout.
