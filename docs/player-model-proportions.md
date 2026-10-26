# Player model proportions

Fit the third-person and first-person models against their respective player references. Use **Keep this model's proportions** to retain the character's limb lengths. Uniformly scaling the whole character changes height while retaining its shape.

## Hand grips

After fitting, ReAnimated places the left and right grips using the fitted palm landmarks and the reference grip frames. Their offsets follow the hand size. Open **Helpers → Hand grips** to adjust their position and bind orientation. **Place grips in palms** preserves saved manual placements; **Recalibrate hand grips** replaces unlocked placements.

The native hand holders use animation-bank rotation with their calibrated bind position and scale. A bank rotation key can replace the displayed bind orientation during playback. Adjusting the palm or rotating the whole character is not a substitute for selecting the correct holder channels.

From **Retarget / Edit**, use **Adjust target model** to open the selected custom target's saved model setup. Save the model package after editing its grips.

## Vanilla first-person rigs

DL1 has a separate first-person arm mixer that samples local transforms independently of the mesh's normal animation component mask. Keeping bind positions in the bone script alone does not preserve different joint distances through that mixer.

For a model using the stock player animation bank, prepare the FPP skeleton with the native joint spacing, local frames, palm pivots, and camera helpers. Fit the visible arms and hands around that skeleton while keeping natural widths and finger shapes. The TPP model can retain its own proportions independently.

Use a working FPP character as a reference for the complete rig and channel setup. Bone debug shapes alone do not establish the parent hierarchy or bind-frame compatibility.

Compare one-handed and two-handed weapons, normal and charged fists, and the same climbing route in the normal Player. The final Workshop package should contain the compiled model, textures, and supported animation data without a runtime code dependency.
