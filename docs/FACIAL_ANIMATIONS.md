# Facial animations and mimic export

This local prototype adds FBX blendshape animation to DL ReAnimated without changing the existing skeletal retargeter. Dying Light facial animation uses ordinary ANM2 tracks whose `tx` component is interpreted as a morph weight by the mimic consumer.

## Normal workflow

1. Add an animation FBX as usual.
2. Leave **Facial animations** on **Auto-detect from target and FBX**.
3. In the **Animations** table, leave **Body / face** on **Auto**.
4. Press **Face auto** when a manual review is needed.
5. Review or edit the source-blendshape to target-morph mapping.
6. Build the RPack.

Auto mode writes a normal body animation when no changing blendshape curve is present. When the selected target has a facial profile and the FBX contains animated `BlendShapeChannel` / `DeformPercent` curves, Auto writes both:

```text
my_animation
my_animation_mimic
```

The `_mimic` naming is intentional. It follows the game's separate body-plus-mimic workflow and lets the face performance be reused or replaced independently.

## Per-clip Body / face modes

The selector beside **Root motion** offers:

- **Auto** — body only unless animated facial curves and a target profile are detected.
- **Body only** — excludes facial resources.
- **Mimic only** — writes only `<resource>_mimic`.
- **Body + mimic** — writes a body ANM2 and a separate synchronized mimic ANM2.

The prototype does not physically merge body and face tracks into one combined ANM2. Stock combined clips exist, but separate resources are safer for the first authoring release and match the player mimic lookup path.

## Model facial setting

The Project tab offers:

- **Auto-detect from target and FBX** — recommended.
- **Model supports facial animations** — allows facial export whenever a profile is available.
- **Model has no facial animations** — suppresses facial export project-wide.

Auto detection does not inspect a retail game mesh. It uses two portable facts:

1. the selected target rig has a mimic profile; and
2. the imported FBX contains changing blendshape curves.

The bundled **Human / infected common 46** profile is suitable for the player, common human NPCs, and the infected models validated during testing. Volatiles and other model families can use additional `.dlrmimic.json` profiles. A custom `.crig` can embed its own mimic profile.

## Facial retargeting dialog

The dialog shows:

- every animated FBX blendshape;
- the selected Dying Light target descriptor;
- a contribution weight;
- mapping confidence and method;
- a generic procedural preview with a frame slider.

The preview is deliberately asset-free. It draws a generic face from simple shapes and approximates eyelid, jaw, smile, and funnel semantics. It is useful for catching obvious mappings, but it is not a substitute for testing the actual target mesh in the editor.

### Consolidating a richer source face

Multiple source blendshapes may map to one Dying Light target:

```text
jawOpen + mouthOpen + viseme_AA
    -> morph_jaw_open

mouthSmileLeft + cheekSquintLeft
    -> morph_lips_L_smile
```

Use **Duplicate selected mapping** when one source curve should also contribute to another target, such as an eye blink driving both upper and lower eyelids.

The build report records:

- animated source shapes;
- mapped and unmapped source shapes;
- many-to-one target consolidation;
- approximate captured source activity;
- the final mapping and weights.

## Values and clamping

Dying Light stock mimic tracks may be negative or greater than `1.0`. The default is therefore **no clamp**. Hard and soft clamping are retained as profile/build settings for special target meshes, but should not be enabled merely because a source uses values outside 0–1.

## Manual mouth animation and SPB

SPB files remain the normal speech/phoneme system. This prototype does not edit SPB files.

Manual mouth animation is still supported: animate jaw, lips, visemes, or custom shape keys in Blender or another DCC, bake them to FBX blendshape curves, and map those curves in the facial dialog. This is useful for cinematics, non-speech vocalization, chewing, snarling, or deliberately authored lip motion.

## Advanced root-motion source

With **Show advanced settings** enabled, clips using **Skeletal root (bip01)** or **Motion accumulator** expose **Source bone for bip01 motion**.

This changes which source FBX bone position drives target `bip01` translation. In motion-accumulator mode, it also drives the horizontal OffsetHelper displacement. It does not rename the target `bip01` track and does not use an arbitrary source-bone roll as actor yaw; the established stable body frame still owns orientation.

Leave **Default mapped Hips** selected for normal humanoid animation.

## Custom targets and future meshes

A facial profile is separate from mesh geometry. It records descriptor semantics and mapping aliases, not proprietary vertex data. A future custom mesh can:

- preserve a large custom target set through an embedded `.crig` profile;
- use a separate shareable `.dlrmimic.json` profile; or
- deliberately consolidate a richer source set into a smaller stock target set.

The tool should never force a custom mesh into the common-46 profile when that mesh exposes additional valid morph descriptors.

## Project portability

Per-clip facial mode and mappings are stored in the existing `ProjectAnimation.extensions` dictionary. Project-level facial settings are stored in `RigSettings.extensions`. No project schema bump is required, and older application versions preserve these unknown fields.

When a custom profile is selected, the project stores both its path and an embedded declarative copy. Moving the project therefore does not require the original profile file to remain at the same path.

## Current prototype limitations

- Binary FBX input only, matching the current body importer.
- Facial curves must be baked into one selected animation layer.
- Import currently recognizes animated FBX `BlendShapeChannel` `DeformPercent` curves.
- The bundled common-46 profile has exact names for a subset of descriptors and broad video-derived regions for the remaining channels.
- The procedural preview is approximate and cannot reproduce target-mesh skinning or morph geometry.
- Speech/SPB generation, FED expression editing, and physically combined body+face ANM2 output are not part of this prototype.
