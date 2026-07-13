# Dying Light 2 preview workflow

## Start a DL2 project

1. Create or open a project.
2. Select **Dying Light 2** in the Project workspace.
3. Import the animation FBX.
4. Review the FBX diagnostics.
5. Use the bundled **DL2 player shadow-caster** target, or select a compatible custom `.crig`.
6. Keep the root mapping on `pelvis` unless the model family requires another target.
7. Build and inspect the generated report before testing in DevTools.

Changing the game profile changes the complete target package. Do not mix a DL1 target CRIG/SMD with the DL2 reference ANM2 or vice versa.

## Why the bind pose matters

A source FBX may store its actual skinned bind pose in `Pose::BindPose` or skin-cluster `TransformLink` matrices. Its ordinary unanimated local transforms are not always equivalent. Blender FBXs may also carry axis and scale conversion on a non-bone `Armature` Model node.

DL ReAnimated therefore calculates animation in global space:

```text
correction[bone] = inverse(sourceBindGlobal[bone]) × targetBindGlobal[bone]
correctedGlobal[bone, frame] = sourceAnimatedGlobal[bone, frame] × correction[bone]
correctedLocal[bone, frame] = inverse(correctedGlobal[parent, frame]) × correctedGlobal[bone, frame]
```

This allows the source FBX to contain extra bones while the target uses a smaller descriptor-backed animation skeleton.

## Superset skeletons

The supplied DL2 player FBX contains many more nodes than the target shadow-caster SMD. Facial, cloth, accessory, camera, weapon, and secondary-animation bones can remain unused. Required target deform bones must still exist and have a usable ancestry.

## Multiple roots

DL2 player skeletons can contain independent roots such as left/right IK targets and a shadow-caster helper. They remain independent. The preview target uses `pelvis` as the primary animation/root role.

## Format-42 limitation

The supplied DL2 ANM2 uses format 42, not the DL1 format-1 stream. The preview safely recognizes and inventories format-42 files, but animated format-42 ANM2-to-FBX conversion and native writing are still unavailable. The GUI will explain this rather than produce a misleading static export.
