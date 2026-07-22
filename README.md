<p align="center">
  <img
    src="https://github.com/user-attachments/assets/51f5eba4-3e85-4436-8b81-48c9151d6ab9"
    alt="ReAnimated logo"
    width="628"
  />
</p>

DL ReAnimated is a fully open source, project-based **FBX ↔ ANM2 → RPack** authoring tool for Dying Light, and `.anm2` generation support for Dying Light 2. It provides a desktop GUI for animation authoring, model import, Blender-assisted ANM2-to-FBX export, humanoid and generic bone mapping, shareable `.crig` custom targets, selectable animation-script targets, root-motion policies, reusable projects, and safe new/append RPack export.

## Start

All you need to get started is a recent build of Python installed and a download of the code from this repo.

Run on Windows:

```
run_gui.bat
```

This should handle installing everything you need easily, and then run the app.

Repair/update the local environment:

```
run_gui.bat --setup
```

Build the portable Windows application:

```
build_exe.bat
```

## Normal workflow

1. Select Dying Light 1 or Dying Light 2 in the Project workspace.
2. Add one or more animation FBXs.
3. Leave **Use imported animation FBX bind pose (recommended)** enabled.
4. Choose the target animation script and root-motion policy.
5. Review the automatic or exact/subset mapping report.
6. Save the `.dlraproj` project.
7. Build a new RPack or append to a previous DL ReAnimated RPack.
*Note: Dying Light 2 drop-in RPack appends are not yet supported!*

Advanced target files, diagnostic controls, intermediate reports, and developer options are hidden unless **Advanced settings** is enabled in the top-right corner or the View menu. **Root & .crig Mapping** appears only for a custom/imported target rig or a deliberate expert override; bundled player rigs stay in the semantic **Retargeting** workflow.

For a custom object, animal, or model that already exists in a mod, Advanced mode can create a shareable `.crig` from one binary model FBX. Animations using that exact skeleton can then be exported without humanoid roles or additional target files.

The dedicated **ANM2 → FBX** workspace converts extracted animations back into editable skeleton FBXs. Native export supports any matching `.crig`, including doors and props; cross-rig mode provides conservative automatic mapping plus manual review. FBX-declared source cadence, ANM2 sampling cadence, and playback cadence are tracked separately; reverse export can resample between independent ANM2/FBX rates and consumes hash-validated `.anm2.dlrmeta.json` timing provenance.

## Workspaces and tabs

- **Animations:** Project, Animations, Retargeting, Facial, Export, and animation Help. Advanced settings adds Root & `.crig` Mapping when a custom target owns that workflow.
- **Models:** Models, Bone Mapping, Build & Install, DevTools, and model Help.
- **ANM2 → FBX:** Convert and conversion Help.

The File, Import, Build, Workspace, View, and Help menus contain project-wide commands. Each workspace Help tab contains only the documentation for that workspace; general setup, troubleshooting, and project compatibility are in the top Help menu.

Builds and exports run in the background. Their workspace log and status bar report progress while the rest of the interface remains available.

## Output

The editor-loadable output is normally:

```
common_anims_sp_pc.rpack
```


## Main files

```
run_gui.bat                  first-run setup and GUI launcher
build_exe.bat                portable Windows EXE/ZIP builder
*.dlraproj                   versioned multi-animation project
*.dlrmap.json                reusable humanoid mapping profile
*.crig                       shareable custom target-rig package
common_anims_sp_pc.rpack     example output
```

## Documentation

- [GUI quick start](docs/GUI_GUIDE.md)
- [Humanoid retargeting](docs/RETARGETING.md)
- [Chrome Rig custom targets](docs/CHROME_RIGS.md)
- [Animation-script targets](docs/ANIMATION_SCRIPT_TARGETS.md)
- [New and append RPack export](docs/RPACK_WORKFLOW.md)
- [Root motion and IK](docs/ROOT_MOTION_AND_IK.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [ANM2 format](docs/ANM2_FORMAT.md)
- [ANM2 to FBX](docs/ANM2_TO_FBX.md)
- [Model import and installation](docs/MODEL_IMPORT.md)
- [Project compatibility](docs/PROJECT_FORMAT.md)
- [Building the Windows EXE](docs/BUILDING_WINDOWS_EXE.md)
- [Dying Light 2 preview workflow](docs/DYING_LIGHT_2.md)
- [FBX preflight checks](docs/FBX_PREFLIGHT.md)

Contributor material is kept under `docs/project/`; generated diagnostics and scratch research are not shipped in the release tree.

More technical documentation can also be found in the `docs` folder.

## Current status

DL1 full-body retargeting, mimic, root motion, exact-rig objects, deterministic `.crig` packages, ANM2 decoding, Blender-assisted ANM2-to-FBX export, generic cross-rig mapping, the packed multi-page writer, and RPack packaging remain validated. The validated PC Dying Light 2 Header_Version2 block/sampler layout now supports native ANM2 decoding and Blender-assisted ANM2-to-FBX export with the bundled advanced player skeleton. Native DL2 ANM2 writing remains unavailable. Custom models and rigs are currently very experimental.

## Disclaimer
Dying Light ReAnimated was developed with assistance from AI tools.

## Contributing
All I ask is that you keep forks or commits of this project open-source. I believe modding should be for the community and a benefit to everyone, regardless of skill or background. 


*Yes, ReAnimated is a pun.*
