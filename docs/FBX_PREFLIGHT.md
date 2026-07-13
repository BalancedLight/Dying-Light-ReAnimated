# FBX preflight checks

DL ReAnimated validates an animation FBX when it is imported and immediately before a build.

## Blocking errors

A build is stopped for conditions that cannot produce a reliable animation:

- the file cannot be parsed as the supported binary FBX form;
- no usable skeleton exists;
- the selected animation stack does not exist;
- required target deform bones are missing;
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

The GUI displays a short summary and provides the full report in the build output. Each finding includes a code, severity, explanation, and suggested correction where possible.

Extra source bones are normally safe when using exact/subset retargeting. Missing target bones are more serious. A hierarchy warning should be reviewed when the affected node lies inside a mapped deform chain.
