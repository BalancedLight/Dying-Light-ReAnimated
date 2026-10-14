<div align="center">
  <img
    src="https://github.com/user-attachments/assets/c1ac55ac-c7ae-4698-9618-51a67050c43a"
    alt="EXPORTING header"
    width="1200"
  />
  <br />
  <a href="../README.md">Back to README</a> ·
  <a href="models.md">Models</a> ·
  <a href="animations.md">Animations</a>
</div>

<hr>

# Exporting and Developer Tools deployment

Save your model and project before exporting. Preview the intended model and animation variant, then choose the output route that matches your project.

## Choose an output

| Output | Use it for |
| --- | --- |
| `.dlraproj` / `.dlrmodel` | Continuing work in ReAnimated |
| `.anm2` | Individual body or mimic animation resources |
| `.scr` | Editable animation-script source, including sequence and event setup |
| `.msh`, `.chr`, `.bscr`, optional `.ascr` | Model geometry source, character definition, bone script, and animation-script alias |
| `.rpack` | Packaged model or animation resources |
| Complete compiled package | Model resources and associated output prepared with the Developer Tools compiler |
| Developer Tools deployment | Writing selected output into a specific editor project |

File names and companion files depend on the model and export settings. Use the export result to find the actual destinations.

## Loose ANM2 animations

**Needs:** imported motion, a resolved target, and export-ready animation variants.

1. Open **Export** and check the animation rows you want to export.
2. Review each **Output ANM2** name and its script assignment.
3. Choose **Export ANM2 files...** and select the output folder.
4. Review the result and any skipped rows.

For the active clip, **File → Export DL1 ANM2** also offers **Body...**, **Mimic...**, or **Body + mimic...**.

The output contains the chosen animation resources. Add them to your Developer Tools project's animation setup and reference the correct names in its script. Exporting a loose animation does not assign it to a character in the editor.

## Animation RPack

**Needs:** export-ready animation variants and the intended animation-script assignments.

1. Select the animation rows in **Export**.
2. Review the animation-script target. Use **Apply to SCR** when assigning selected motion to the script.
3. Choose **Export animation RPack...** and select a destination.
4. Check the result for exported and skipped entries.

The default animation RPack name is `common_anims_sp_PC.rpack`; standalone export lets you choose the destination file. New animations assigned to a stock player model default to `anims_player_dlc99`; existing saved assignments are retained. Custom characters can use their own scripts.

Use the pack with the matching character/script setup in your custom project. Keep track of its intended resource names when replacing an existing animation bank.

## Character and full-project export

**Needs:** checked, export-ready animation variants and their target models. Custom models require the Developer Tools compiler and required retail resources for compilation.

1. Open **Export** and check the animation variants you want to include.
2. Choose **Export character files...** for the associated character resources, or **Export full project...** to include the animation output as well.
3. Select an output location and read the result to find the generated files.

These routes compile custom models for the exported characters. Review the selected target models, resource names, materials, and script assignments before exporting. Use the resulting character resources with the matching animation setup in your editor project.

For a character without a selected animation row, use the model compilation route below or select the character directly in the Developer Tools export tab.

## Model source files

**Needs:** a prepared model, a Developer Tools project, the model compiler, and a complete game `Data0.pak`. Configure the game installation in **Workflow** and select the compiler in the Developer Tools tab.

1. Open **Export → Developer Tools** and choose the destination project.
2. Select **Character source files** and check the character in **Characters to compile**.
3. Choose **Export to Developer Tools** and review the destination details.

This route stages model source and related character files into the project. Output can include `.msh`, `.chr`, `.bscr`, and an optional `.ascr`, plus material and animation-script companions. Use the Developer Tools toolchain to compile and load the staged source, then connect it to your character setup. A prebuilt character RPack is a separate export mode.

## Compile a complete model package

**Needs:** a prepared model, the installed Developer Tools model compiler, and the retail resources required by the compiler.

1. Finish the model's Setup stages and review its animation clips or existing animation-bank choice.
2. Review the model's resource name, materials, and script alias.
3. Choose **Compile model RPack...** in Setup's export stage.
4. Select the compiler if prompted, then choose the output location.
5. Wait for the build and read its result before using the package.

This route compiles the model and assembles its package, including `.msh_obj`, a model RPack, and `local_dx11.mp`, with animation and companion output as required by the settings. `.skn` generation is currently unsupported.

Use the compiled resources with their matching character definition in your Developer Tools project. Test the model's materials, rig, and animations there before publishing your map.

## Deploy to Developer Tools

**Needs:** an existing Developer Tools project and prepared export selections. Compiled character deployment also needs the model compiler.

1. Open **Export → Developer Tools** and use **Choose project...**.
2. Choose the output mode for your task, such as **ANM2 files only** or **Character source files**. Review the selected models and animations.
3. Use **Choose compiler...** when the selected route needs compilation.
4. Choose **Export to Developer Tools**. Review destination details and any replacement conflicts before confirming.
5. Read **Latest result**, then open or reload the affected resources in the editor.

For a model you're editing in Setup, **Export project to Developer Tools...** provides the model deployment route. **Test the installed model → Check installed files** lets you inspect its deployed resources.

If the editor is already open, the **Refresh animations in Editor** section offers **Refresh animation** and **Check load result**. Review the result before relying on the updated animation; you may still need to reload content in the editor.

Deployment can report conflicts with existing or changed files. Review those destinations rather than replacing them blindly. Where a rollback is offered, it respects changes made after deployment; resolve a modified-file conflict before retrying.

For resource placement, character setup, custom maps, and editor use, follow the [Dying Light Developer Tools wiki](https://dldt.miraheze.org/wiki/).

## Troubleshooting

| Problem | What to check |
| --- | --- |
| **Select the Dying Light Developer Tools compiler first** | Install Developer Tools and choose its compiler executable. Check that the required retail resources are available. |
| **Complete package build failed** | Read the build status and Jobs / diagnostics. Fix the reported model, material, resource, or compiler problem, then rebuild. |
| Animations were skipped | Use **Review skipped animations** in the Developer Tools tab and resolve each binding or readiness issue. |
| Deployment reports a conflict | Check the existing destination files and decide whether to keep them or allow the offered replacement/backup. |
| Export succeeds but the editor shows old content | Check project selection, resource names, script assignment, and archive duplicates. Reload the affected resources and check the refresh/load result. |
| The character deforms badly in the editor | Revisit mapping, rest pose, and weights in the [model guide](models.md), then rebuild and test again. |
