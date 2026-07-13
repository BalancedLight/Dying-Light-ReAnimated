# Model import and installation

The Models workspace imports static or skinned FBX models and prepares Dying Light model assets. Model entries and their manual bone mappings are stored in the same `.dlraproj` file as animation work.

## Tabs

### Models

Add one or more FBX files, choose the import mode and orientation policy, and run **Analyze models**. Leave orientation on **Auto** for ordinary Y-up FBX files. Auto respects the evaluated FBX scene and its Model/BindPose transforms instead of adding another axis rotation; imported models are intended to be placed at identity rotation in ChromeEd. The legacy Y-up conversion remains available as an explicit manual policy for older projects.

### Bone Mapping

This tab applies to **Dying Light Humanoid** imports. Auto-map provides a starting point, while the final-target dropdowns store manual overrides for the project. Review helper, twist, face, costume, and accessory bones before building.

Humanoid conversion preserves the FBX's evaluated bind-pose surface byte-for-byte at the authored vertex positions and remaps only its skin weights onto stock-named Dying Light bones. It does not pre-deform vertices with a weighted `target_bind * inverse(source_bind)` pass: that operation changes triangle lengths before compilation and caused compressed torsos, elongated necks, detached clothing, and collapsing limbs on stylised characters.

The humanoid palette emits the useful stock Dying Light animation prefix: 69 `BONE` elements and 18 `HELPER` elements. The 19 stock clothing/head mesh roots in `player_1_tpp.smd` are omitted and replaced by the imported skinned geometry. Cameras, normals, holders, and the two `hspine` rows are therefore no longer mislabeled or retained as skin bones.

Stock-named joints are fitted to the mapped FBX pivots, then re-framed so Chrome's local `+X` axis follows each visible bone segment. Every reference matrix is the inverse of that exact authored global bind. This produces the same bind identity used by Chrome skinning while preserving arbitrary proportions and giving ChromeEd the bone direction it expects.

This fitted hierarchy is a custom bind even though its bone names are familiar. Raw `anims_man_all` tracks must not be attached automatically: their absolute local translations belong to the stock player bind. The model importer creates a `.crig` from the exact emitted MSH hierarchy; retarget stock or custom animation FBXs to that rig, then enter only the script containing those retargeted clips. The BSCR retains position and rotation channels (plus root scale) for that path.

Humanoid and Exact Rig bone extents use compact local bind-segment proxies. They are not calculated from every influenced vertex: weighted-vertex AABBs make terminal twists, hands, and head bones enormous and point leaf visualizations in arbitrary directions. Transverse radius is estimated only from dominantly owned vertices and capped relative to segment length.

The model box is independent from bone display bounds. The importer appends a non-rendering ordinary `MESH` entity after all `MESH_SKINNED` elements and stores the exact emitted-vertex AABB on it. This ordering follows `CMeshFileBase::InitNumAnimEntities` and `CMeshFileBase::CalculateBoundingBox`: the carrier contributes to the cyan reference box without entering the animation prefix, while skinned geometry remains excluded by the engine's normal bounds pass.

In Exact Rig mode, LimbNodes with real skin-cluster weights are emitted as `BONE`; unweighted armature ancestors, end markers, facial controls, and other transform-only nodes are emitted as animated `HELPER`. They remain addressable by custom animation but no longer create a false ground-to-pelvis bone or receive retention skin weights.

Use **Exact Rig** when the original skeleton, bone names, and proportions must remain exact. Both skinned modes now create their `.crig` from the authored MSH hierarchy rather than the raw FBX, so custom animations use the same Chrome `+X` frames and fitted pivots that were compiled into the model.

### Build & Install

Choose the output folder, material handling, physical surface, skeleton options, and animation script. **Build source MSH** creates source files without running the Developer Tools compiler. **Build, compile & install** also compiles and copies the results into the configured project.

Model builds run in the background. Progress appears in the build log, and other workspaces remain responsive until the task completes.

Use **Exact Rig** when the imported model and its animations share the same skeleton. If **Create/install .crig** is enabled, every skinned model build creates a reusable target from its exact emitted bind.

### DevTools

Configure the ResPack compiler, `Data0.pak`, workshop root, active project, and Developer Tools `Engine\Data` folder. **Auto-detect** checks common Steam locations; **Validate** reports missing or incompatible paths before a build starts.

## Typical model workflow

1. Add the model FBX in **Models** and analyze it.
2. Select Static, Exact Rig, or Dying Light Humanoid mode.
3. Review **Bone Mapping** for humanoid imports.
4. Configure output and material options in **Build & Install**.
5. Validate the Developer Tools paths.
6. Build the source or compile and install it into the active project.

Compiled skinned imports are rejected if expected bones/helpers are pruned, no skinned mesh survives, the animation prefix changes, render flags are lost, any compiled bone bound collapses, or the compiled ordinary-mesh carrier differs from the emitted geometry AABB. A successful compiler exit by itself is not treated as a valid model.

For custom rig packages and exact-skeleton animation targets, see [Chrome Rig custom targets](CHROME_RIGS.md).
