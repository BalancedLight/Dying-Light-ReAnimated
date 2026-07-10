<p align="center">
  <img
    src="https://github.com/user-attachments/assets/51f5eba4-3e85-4436-8b81-48c9151d6ab9"
    alt="ReAnimated logo"
    width="628"
  />
</p>

DL ReAnimated is a project-based **FBX → ANM2 → RPack** authoring tool for Dying Light. It provides a desktop GUI, humanoid retarget mapping, shareable `.crig` custom targets, selectable animation-script targets, root-motion policies, reusable projects, and safe new/append RPack export.

## Start

Windows source build:

```
run_gui.bat
```

Repair/update the local environment:

```
run_gui.bat --setup
```

Build the portable Windows application:

```
build_exe.bat
```

## Normal workflow

1. Add one or more animation FBXs.
2. Leave **Use imported animation FBX bind pose (recommended)** enabled.
3. Choose the target animation script and root-motion policy.
4. Review the automatic humanoid mapping.
5. Save the `.dlraproj` project.
6. Build a new RPack or append to a previous DL ReAnimated RPack.

Advanced target files, diagnostic controls, intermediate reports, and developer options are hidden unless **Show advanced settings** is enabled.

For a custom object, animal, or model that already exists in a mod, Advanced mode can create a shareable `.crig` from one binary model FBX. Animations using that exact skeleton can then be exported without humanoid roles or additional target files.

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
- [Project compatibility](docs/PROJECT_FORMAT.md)
- [Building the Windows EXE](docs/BUILDING_WINDOWS_EXE.md)

Contributor material is kept under `docs/project/`; generated diagnostics and scratch research are not shipped in the release tree.

## Current status

The full-body Mixamo retarget, model-only exact-rig object workflow, deterministic `.crig` packages, embedded-bind workflow, Cayley rotation encoding, packed ANM2 writer, multi-page long-clip output, animation-script packaging, and three humanoid root-motion modes are implemented. Finger retargeting and semantic quadruped retargeting remain active/future validation areas.

## Disclaimer
<<<<<<< HEAD
=======

Dying Light ReAnimated was helped developed with AI tools
>>>>>>> b25156355dcf6a8e49111f1ec772ee22820933eb

Dying Light ReAnimated was developed with assistance from AI tools.

<<<<<<< HEAD
*Yes, ReAnimated is a pun.*
=======



*Yes, the ReAnimated is a pun.*
>>>>>>> b25156355dcf6a8e49111f1ec772ee22820933eb
