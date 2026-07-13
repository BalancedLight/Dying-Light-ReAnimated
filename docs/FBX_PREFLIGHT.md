# FBX preflight checks

DL ReAnimated validates an animation FBX when it is imported and immediately before a build. Import and build readiness are intentionally different: a readable cross-rig clip can be added so its mapping can be repaired, while export remains blocked until the saved mapping is usable.

## Blocking errors

A build is stopped for conditions that cannot produce a reliable animation:

- the file cannot be parsed as the supported binary FBX form;
- no usable skeleton exists;
- the selected animation stack does not exist;
- required target deform bones are missing and no reviewed `.crig` map is being used;
- bind matrices are non-finite or singular;
- two bone names collapse to the same Unicode-normalized identifier;
- the selected game, target rig, and ANM2 template are incoherent.

## Warnings

Warnings do not necessarily make the FBX invalid:

- multiple animation stacks are present;
- source skeleton contains extra bones;
- multiple roots or helper-like roots are present;
- the animation contains no moving skeletal channels;
- bind-pose coverage is partial and a fallback was used;
- a non-bone Armature/null carries axis or scale conversion;
- the unit scale is unusual;
- optional target helpers are missing;
- non-ASCII names require explicit target descriptors rather than implicit Chrome hashing.

## Reading the report

The GUI displays what was detected, why it matters, and the next action. A source-data failure such as an unreadable FBX still blocks import. A target mismatch adds the clip, generates an editable map, and directs you to **Root & .crig Mapping**. The full report remains available in build output.

Extra source bones are normally safe. In a reviewed mapped-rig build, unmapped target helpers stay at bind pose. Map every body/deform bone whose motion is required; use the filter in the mapping editor to find unresolved rows. A hierarchy warning should be reviewed when the affected node lies inside a mapped deform chain.
