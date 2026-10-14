<div align="center">
  <img
    src="https://github.com/user-attachments/assets/16994c3e-29e5-40c9-a424-b6576fc453d5"
    alt="ANIMATIONS header"
    width="1200"
  />
  <br />
  <a href="../README.md">Back to README</a> ·
  <a href="models.md">Models</a> ·
  <a href="exporting.md">Exporting</a>
</div>

<hr>

Use **Animations** to import clips, choose the characters they belong to, and prepare their motion. Use **Playback** to inspect them and **Retarget / Edit** to change mapping and animation.

## Bring in an animation

1. Save the project, then choose **File → Import FBX / ANM2...** or **Import animation...** in Animations. If the project is new, the app asks you to save it first.
2. Select a binary FBX or ANM2 file. If the FBX has several takes, choose the stacks you want to import.
3. Check the source model or rig associated with the clip. The source describes the skeleton the motion was authored for.
4. Choose **Add target to selected clip...** to assign the model that should receive the motion.
5. Open **Retarget / edit** and review the mapping, scale, and root-motion settings.
6. Play the result in **Playback**, then save the project.

You can also browse animations from your local game archives. Refresh the catalog through **File → Refresh asset catalog…** and use the asset browser to find and preview the clip you need. Keep the matching model or rig available: an animation file alone does not supply a rendered character.

For motion imported with a model, review **Imported animation clips** in its setup. **Play selected clip** previews a take; **Prepare animation** prepares the selected take for the model's authored rig. Enable the animation workspace if the app offers it after a Models-only import.

## Review source and target

Retargeting adapts movement from one skeleton to another. Similar bone names help, but they do not guarantee the same proportions, hierarchy, or rest pose.

Check the major body parts first, then hands, fingers, helpers, and any first-person camera bones. Watch the feet and root movement during playback. If a clip is bound to the wrong source, use **Rebind source** on its animation entry and review the mapping again.

An animation can have more than one target. Check the active target variant before editing or exporting so you're working on the intended character.

## Edit keyframes and curves

1. Open **Retarget / Edit → Timeline / curves** and select a track.
2. Choose **Dope sheet** or **Curves**.
3. Use **Create edit layer** to start editing the mapped motion for the clip.
4. Select a key to change its frame or value, or drag it in the graph.
5. Use **Snap** for whole-frame dragging. Numeric frame edits also accept fractional frames.
6. Preview your change and save the project.

Changing a key's time keeps its value. The edit layer starts with the existing motion and edits across the clip. Use **Undo** and **Redo** to review changes.

## Work with events

Open **Events**, choose a sequence, and use **+ Event** to add a notification. A sequence gives a clip its range, playback rate, and event timing.

Events support fractional timing, multiple selection, dragging, duplication, and undo/redo. Use **Range → Review** before applying range changes. Open **Details** for slots, action parameters, and other event settings.

**Import** reads script source or a compiled animation bank. **Export script** writes the edited `.scr` source. A compiled export may require an action-bank serializer when you create child actions; follow the diagnostic if that operation is unavailable. You can audition a local sound file for an audio event, but scrubbing does not replay sounds.

## Body and facial motion

Normal animation import handles body motion. For facial motion, use **File → Import mimic ANM2...** or **Import facial FBX for review...** and inspect the facial setup separately.

For the active animation, **File → Export DL1 ANM2** offers **Body...**, **Mimic...**, and **Body + mimic...**. Use the matching route for the channels you've prepared. Facial FBX review does not mean every source facial channel can be exported directly as mimic animation.

## Export to FBX with Blender

1. Select Blender with **Blender…** in the asset browser's export area.
2. For a retail mesh, select it in the model browser and use **Export mesh FBX...**.
3. For animated output, load an export-ready target variant with a target model, then choose **Active variant to FBX…**.
4. Pick the destination and wait for the export job. Review its status before opening the FBX in Blender.

The animated route uses the active variant and its target geometry. If the button is disabled, check that both are loaded and that the variant is ready for export.

For ANM2 or RPack output, follow the [export guide](exporting.md).

## Troubleshooting

| Problem | What to check |
| --- | --- |
| Motion looks wrong on another character | Review source binding, target mapping, scale, rest pose, and root-motion settings. |
| A clip or model cannot be reopened | Check referenced files, the game installation, and configured RPack roots. Read the missing-resource diagnostic. |
| Export is disabled | Check the active target variant and its status. Resolve incomplete mapping or required reviews first. |
| Blender export fails | Select an existing `blender.exe`, check the export status, and inspect Jobs / diagnostics. |
| An event behaves differently in the editor | Check the script assignment, sequence range, event timing, and action parameters in the exported script. |

Use the [Developer Tools wiki](https://dldt.miraheze.org/wiki/) for animation setup in the editor and custom maps.
