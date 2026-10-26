<div align="center">
  <img
    src="https://github.com/user-attachments/assets/32fafce0-5d8b-4876-b1f3-1a9864db7cb6"
    alt="MODELS header"
    width="1200"
  />
  <br />
  <a href="../README.md">Back to README</a> ·
  <a href="exporting.md">Exporting</a> ·
  <a href="animations.md">Animations</a>
</div>

<hr>

Use **Models** when you want to import a character or prop, work on its rig, or prepare it for export. Choose **Models + Animations** if you also want to edit its motion.

## Import a model

1. Open **Models**. If the **Import rig** control is shown, choose the import mode before loading the file.
2. Choose **Import FBX...** and select a binary FBX containing mesh geometry. **Use FBX rig if present** is the default import mode. **Keep original rig** retains the source rig, **Prepare DL1 fit** prepares a humanoid for fitting, and **Static prop** is for a model that does not need a character rig. The import mode is locked once a model is loaded.
3. Inspect the model in the viewport. Check its size, orientation, mesh surfaces, skeleton, and materials before changing the rig.
4. Open **Setup** to choose how to prepare it.

To continue an earlier model, use **Open model...** or **Open .dlrmodel...**, depending on the panel you're in.

For a retail character, use the **Base-game model browser** in Models. Refresh the asset catalog through **File → Refresh asset catalog…**, selecting your game installation when prompted. Preview a model before choosing **Add to project**. **Import complete DL1 character...** brings in a character through the character import workflow.

## Choose a setup

| Choice | When to use it |
| --- | --- |
| **Adapt this rig** | The imported model has a rig that needs to fit a DL1 skeleton |
| **Repair an existing DL1 rig** | The model already follows a DL1 skeleton and needs corrections |
| **Build a rig from geometry** | You need a draft body rig for an unrigged model |

Follow the stages shown in Setup. Use **Previous stage** and **Next stage** to move between them.

For a rigged model, select a reference skeleton, choose **Use this skeleton**, and open **Review bone mapping**. Check that source bones map to the correct target parts, particularly the spine, hands, fingers, feet, and helpers. Automatic matches are a starting point: review them before fitting.

Use the fitting controls to adjust **Scale** and **Place joints**, then move to **Verify**. Imported rigs retain their weights until you edit them. Moving joints does not automatically fix every skinning problem.

**Player fit** offers two choices. **TPP – Keep proportions** uses the existing third-person fitting controls. **FPP – Stock player rig** fits the body and hands around the stock first-person skeleton and uses its animation channels. Stock joint, camera, and grip frames stay fixed; retained garment helpers remain editable.

When building from geometry, review the body guides before **Build draft body rig**. Check the generated binding and use stress poses to find areas that need weight adjustments.

## Review the result

- Check deformation around shoulders, elbows, hips, knees, and fingers.
- Review materials and texture assignments. If textures look upside down, check **Match DL1 texture origin (flip V)** against your source asset.
- Use **Check a stock animation...** to inspect the fitted model in motion.
- Review **Imported animation clips**. Select the takes you intend to use, and use **Prepare animation** when a clip needs preparation for the authored rig.
- Check the **Animation bank** choice. Referencing an existing bank and exporting your own clips are different setups; choose the one your character needs.

When you choose an existing bank for a newly fitted model, body joints keep their fitted distances and scale while using the bank's rotations. The body root uses bank position and rotation with bind scale. Existing channel choices take precedence; advanced root choices remain available in **Channel Policies**.

**DL1 Output** previews these saved channels by default. Clear **Use saved bone channels** under **Preview options** to inspect the raw clip keys. Reopening a model retains its channel choices; older packages are not changed automatically.

**Advanced controls** exposes more detailed helper, hierarchy, camera, and rig settings. Start with the guided stages and open those controls when you need a specific correction.

## Adjust hand grips

Applying a fit places the left and right hand grips from the fitted palm frames and the selected reference skeleton. Open **Setup → Helpers → Hand grips** to review them, or choose **Adjust hand grips** on the **Bones** tab.

Select a grip, adjust its position in metres and rotation in degrees relative to its hand, then choose **Save grip adjustment**. **Place grips in palms** keeps saved manual adjustments; **Recalibrate hand grips** replaces unlocked adjustments using the current fitted palms. Model undo/redo includes these changes, and saving the model retains them for reopening.

In Retarget/Edit, **Adjust target model** opens the current custom target's saved model package in Helpers. Check the grip with the intended prop and motion after adjusting it.

The rotation controls edit the grip's bind orientation. Existing-bank playback uses the saved grip position and scale with the bank's rotation. Review these choices in **Channel Policies** when your model needs different component ownership.

## Save your work

Use **Save model...** or **Save editable model...** to write a `.dlrmodel`. Use **File → Save project** to write a `.dlraproj`.

| File | Purpose |
| --- | --- |
| `.dlrmodel` | Editable model package containing the model's authoring data and setup |
| `.dlraproj` | Workspace project containing model and animation entries, bindings, and edits |
| `.fbx` | Source interchange file from your modeling or animation software |

Save both the model package and project when you need to continue the character and its wider animation workspace later. Keep referenced game assets and external source files available. Projects from the older Python app must be recreated in the C# app; supported older C# formats are handled by its readers.

When you're ready to produce game files, follow the [export guide](exporting.md).

## Troubleshooting

| Problem | What to check |
| --- | --- |
| **Import failed** | Read the model status and Jobs / diagnostics. Confirm the FBX is binary and contains mesh geometry. Re-export or bake unsupported animation stacks in your authoring software when the diagnostic requests it. |
| The model twists or collapses | Check the reference skeleton, bone mapping, rest pose, and skin weights. Test several poses rather than judging only the bind pose. |
| Missing or incorrect textures | Check source texture locations, material assignments, and the flip-V setting. Use the texture selection controls to replace an unresolved texture. |
| Game models are missing | Refresh the catalog and check that the game installation and any project RPack folders are still available. |
| **Source model changed** appears | Use **Re-match changed model** and review its mapping before fitting again. |

For placing and using the character in a custom map, see the [Developer Tools wiki](https://dldt.miraheze.org/wiki/).
