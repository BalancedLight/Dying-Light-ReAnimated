# Reflected FBX wrapper geometry is not bilateral semantics

The current FBX → ANM2 sampler removes a static, uniform reflected scene
wrapper before bind and animation globals are evaluated. This keeps sampled
bone matrices finite and proper. The removed wrapper matrix remains useful for
diagnostics and for observing where named bind-pose pivots are physically
located, but it does not rename animation channels.

In particular, none of the following proves that a target `l_*` row should
consume a source `r_*` row:

- a negative wrapper determinant;
- the wrapper name `Armature`;
- `mirror` in a filename or animation-stack name.

The corrected current contract is `dlr_current_normalized_global_v2`. Its
default production normalizer does not mirror-conjugate globals after the
wrapper has already been canonicalized.

Bilateral source ownership is a separate policy:

- `auto` compares trusted clavicle, arm, hand, thigh, calf, and foot bind-pose
  pairs in the physical observation frame. Strong same-side agreement
  preserves names; strong opposite-side agreement swaps verified automatic
  rows. Missing or inconsistent evidence preserves names and warns.
- `preserve_source_names` always keeps named source ownership.
- `swap_bilateral_explicit` swaps only verified bilateral automatic or exact
  rows and emits a warning. Manual target overrides remain untouched.

For a removed reflected wrapper, Auto may reapply the removed scene basis to
bind pivots only while measuring physical side. That diagnostic observation
does not re-enter animation sampling and is not a second transform
compensation.

