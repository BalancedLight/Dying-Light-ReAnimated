# GUI quick start

## Launch

On Windows, double-click `run_gui.bat`. The first run creates `.venv`, installs dependencies, verifies the environment, and opens the application.

```
run_gui.bat --setup     repair/update the environment only
build_exe.bat           build the portable Windows EXE folder and ZIP
```

## Simple mode

Simple mode hides target templates, diagnostic controls, and intermediate-file options. A normal workflow is:

1. Add one or more animation FBXs.
2. Leave **Use imported animation FBX bind pose (recommended)** enabled.
3. Choose the animation-script target.
4. Review the automatic humanoid mapping.
5. Choose root motion per clip.
6. Build the RPack.

A separate T-pose is only required when an FBX has unreliable or missing bind transforms. Disable embedded-bind mode and select the matching neutral/rest FBX in that case.

## Advanced mode

Enable **Show advanced settings** to expose:

- `.crig` import and model-FBX rig creation;
- trusted source-rest matrix JSON;
- custom target SMD/template/control files;
- mapping profile load/save/clear controls;
- ignored source-bone inventory;
- collision policy;
- diagnostic stock/bind controls;
- intermediate ANM2 and report output;
- developer documentation.

Advanced mode is a local UI preference and does not change animation data by itself.

### Custom target in four steps

1. Enable **Show advanced settings**.
2. Click **Create .crig from model FBX…** and choose the target model's binary FBX.
3. Save the shareable package; it is installed and selected automatically.
4. Add animation FBXs that use the same bone names and parent hierarchy, then build normally.

The model must already be available to the game/mod. A `.crig` defines animation tracks; it does not compile a mesh, CHR, skin, physics, or AI resources. Rigid objects still need at least one root bone with the object skinned to it.

## Animations

Each row is one output animation. Rows are intentionally tall enough to edit names and options without clipping.

- **Use** — include/exclude without removing the clip.
- **Display name** — project label.
- **FBX source** — imported file.
- **Resource name** — final sequence and `_ANIMATION_` name before the prefix.
- **Animation SCR** — project default or per-clip override.
- **Root motion** — in-place, `bip01`, or motion accumulator.
- **IK** — consumer-side authoring recommendation.
- **Edit mapping** — opens the Retargeting tab for that clip.


For **Root Motion**, use motion accumulator for clips that will involve movements like walking, as this will move the entire object itself and not just the mesh.

## Retargeting

Click **Auto-map humanoid**, then review the required roles. Change a role by choosing the correct source FBX bone. The status line stays compact, and the mapping table uses the available window height.

With a custom `.crig`, the tab reports **Exact skeleton mode** instead. No humanoid mapping is required; every target bone is checked by exact name and parent during the build.

**Apply to compatible clips** copies the mapping only to clips with the exact same source-skeleton hash.

## ANM2 to FBX

The dedicated workspace batches extracted ANM2 files into skeleton-and-animation FBXs through Blender. Select the matching Chrome Rig because ANM2 does not contain bone names, hierarchy, or bind transforms. Native mode requires no mapping. Cross-rig mode accepts a target skeleton FBX and provides conservative automatic suggestions plus an editable mapping table.

For a door or prop, create/import its `.crig` from the matching model FBX, then use native export. Reverse jobs and generic mapping profiles are saved with the project. Blender is detected automatically when possible and can be selected manually.

## Export

**Export ANM2 only…** retargets every enabled clip and writes just the generated `.anm2`
files to the folder you choose. It does not leave an RPack, manifest, or build report there.

### Create new

Writes a new tool-owned animation RPack.

### Append/replace

Loads a previous DL ReAnimated pack, preserves its resources, and writes an updated pack to the output folder.

The advanced checkbox **Include stock writer and bind-pose controls** adds two development-only diagnostic animations. It is not needed in ordinary user packs.

## Installation

Install the result under the working DLC/workshop data route as:

```
common_anims_sp_pc.rpack
```

Do not replace `common_anims_PC.rpack`. Reload the editor project to clear cached resources.


## Facial tab

The **Facial** tab is always visible. Select an imported animation, then use **Scan facial curves** to inspect FBX BlendShapeChannel animation or **Open facial retargeting** to map source shapes to Dying Light mimic descriptors. The **Body / face** control beside Root motion chooses Auto, Body only, Mimic only, or Body + mimic. FBXs without facial curves continue to export normally as body animation.
