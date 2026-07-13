# Model import and installation

The Models workspace imports static or skinned FBX models and prepares Dying Light model assets. Model entries and their manual bone mappings are stored in the same `.dlraproj` file as animation work.

## Tabs

### Models

Add one or more FBX files, choose the import mode and orientation policy, and run **Analyze models**. Leave orientation on **Auto** for ordinary Y-up FBX files; imported models are intended to be placed at identity rotation in ChromeEd.

### Bone Mapping

This tab applies to **Dying Light Humanoid** imports. Auto-map provides a starting point, while the final-target dropdowns store manual overrides for the project. Review helper, twist, face, costume, and accessory bones before building.

### Build & Install

Choose the output folder, material handling, physical surface, skeleton options, and animation script. **Build source MSH** creates source files without running the Developer Tools compiler. **Build, compile & install** also compiles and copies the results into the configured project.

Model builds run in the background. Progress appears in the build log, and other workspaces remain responsive until the task completes.

Use **Exact Rig** when the imported model and its animations share the same skeleton. If **Create/install .crig** is enabled, the model build also creates a reusable custom rig target.

### DevTools

Configure the ResPack compiler, `Data0.pak`, workshop root, active project, and Developer Tools `Engine\Data` folder. **Auto-detect** checks common Steam locations; **Validate** reports missing or incompatible paths before a build starts.

## Typical model workflow

1. Add the model FBX in **Models** and analyze it.
2. Select Static, Exact Rig, or Dying Light Humanoid mode.
3. Review **Bone Mapping** for humanoid imports.
4. Configure output and material options in **Build & Install**.
5. Validate the Developer Tools paths.
6. Build the source or compile and install it into the active project.

For custom rig packages and exact-skeleton animation targets, see [Chrome Rig custom targets](CHROME_RIGS.md).
