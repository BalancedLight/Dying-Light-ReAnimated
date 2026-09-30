# Secondary preview collision review

The managed secondary-motion solver remains an MPC preview approximation. It
accepts explicit sphere/capsule endpoints and radii in the authored model domain;
its contact projection does not prove a native PHX/MPC collision configuration.

A particle exactly on a capsule's axis has no unique outward radial normal.
Falling back to a world axis can push it along the capsule, leaving it inside
after every solver iteration. The preview now retains a useful radial component
of its animated reference, or selects a deterministic perpendicular to the
capsule axis. A zero-length capsule retains the sphere fallback. Fixed anchors
and unrelated bones stay untouched, and reset/replay remains deterministic.

ODE's [capsule documentation](https://www.ode.org/wiki/index.php/Manual)
describes a cylinder with hemispherical caps: its length excludes the caps and
its native local axis is Z. ODE also exposes signed point-depth queries. These
are useful geometric references; they do not establish how a game's MPC script
properties map to ODE shapes, compiled bounds, object transforms or endpoints.
ReAnimated's explicit endpoint preview does not assume an ODE local-axis mapping.

This change independently implements elementary vector geometry. No ODE source
code or runtime library is incorporated. ODE is available under a choice of
[LGPL or BSD-style terms](https://ode.org/); future source reuse must retain the
selected license and required notices, and still needs native compatibility
evidence where game-specific behavior is claimed.

Review native MPC activation, collision construction, particle movement, driven
bone output and mesh deformation separately. Public ODE documentation and an
approximate preview cannot certify those runtime stages. Exact-build findings,
private compiler artifacts and character diagnostics remain outside this source
documentation and generic regression fixtures.
