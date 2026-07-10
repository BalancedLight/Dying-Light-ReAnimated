# Root motion, looping, and IK

Pose correctness and actor/world accumulation are separate layers.

The builder exposes three root policies for every imported clip.

## `inplace`

```
bip01 translation: fixed
0xCCC3CDDF: fixed
```

Use this for pose-only playback, static movie placement, or gameplay systems that already control actor movement.

## `bip01`

```
source Hips displacement -> bip01 translation
0xCCC3CDDF: fixed
```

This visibly moves the skeleton during one playback. A repeated raw sequence returns to frame zero, so a strafe or turn restarts instead of continuing.

## `motion`

```
vertical / pose placement -> bip01
horizontal displacement -> 0xCCC3CDDF translation
body orientation delta -> 0xCCC3CDDF rotation
```

Manual movie testing showed that this is the useful accumulation-oriented form. Continuous movement still depends on the consuming movie/graph/gameplay system applying the helper transform rather than resetting the actor.

For movie keys, the known editor-facing control is:

```
CKeyAnimation.m_UseOffsetHelper = true
```

Animation-workspace pose objects expose related controls:

```
CPoseObject.AccumulateMotion
CPoseObject.MotionAccumulatorBone
```

Expected accumulated behavior:

```
strafe-left repetition:
  each loop continues farther sideways

90-degree turn repetition:
  first loop reaches roughly 90 degrees
  second loop continues toward roughly 180 degrees
```

If a `*_motion` resource still resets, the remaining issue is the consumer setup—not the body pose or ANM2 codec.

## IK

IK is consumer-side. ANM2 stores sampled local transforms; it does not contain one universal IK-enable bit.

The CLI accepts:

```
--ik-authoring-preset runtime
--ik-authoring-preset off
```

The GUI stores this choice per animation, and builds record it in `movie_authoring_presets.json`. It does not modify a movie/graph automatically and does not fabricate ANM2 data.
