# Automatic skin binding candidates

The current `AutomaticSkinBinder` is a working, deterministic C# volume-distance
candidate. It produces weights and diagnostics for caller-validated source
control-point identities. Source-linked rig/binding persistence and surface
replay and the first generated-body/binder workflow now exist; the studio still
needs complete hand/eye integration, correction controls and the full deformation
challenge set before this can be accepted as its automatic binding workflow.

## Algorithm and constraints

The input contains a complete `SourceVolumeGrid`, stable deform-influence IDs and
segments, source-linked sample points, optional per-cell region labels, explicit
handle eligibility and fixed influence fractions.

Each influence seeds the interior cells crossed by its segment. Multi-source
Dijkstra distances propagate through six-neighbor interior cells within that
influence's allowed regions. Exterior, unknown and excluded-region cells remain
unreachable. Surface samples use the nearest interior cell within a declared
metric radius, restricted to the union of eligible automatic influence regions.
Unreachable points retain an explicit unbound fraction.

For a sampled point and influence, let `d` be the interior path distance plus the
surface-to-cell offset, and `h` the cell size. Its provisional score is
`max(d/h, 0.25)^(-p)`, where `p` is the selected falloff power. The implementation
retains the strongest eligible scores within the remaining influence slots and
normalizes only their remaining automatic fraction. Positive fixed weights are
preserved exactly. A zero fixed weight excludes that influence. Fully fixed
assignments can bind detached rigid accessories without a volume sample.

Regions restrict automatic propagation. Fixed fractions remain explicit authored
decisions independently of those regions. A positive fixed fraction conflicting
with a point's explicit handle whitelist is refused.

Every result row reports its sample, unresolved fraction and provisional weight
removed by influence truncation. `AllPointsAssigned` reports assignment coverage;
`RequiresDeformationReview` remains true. Partial segment coverage, unseeded
influences, disconnected parts and truncation remain inspectable diagnostics.
Actual native weight quantization is handled later by the existing exporter.

## Reproducibility and limits

The fingerprint includes the grid identity, segments, source point identities and
coordinates, eligibility, fixed fractions, regions and all execution settings.
Influences and equal-score ties have stable GUID ordering. Cancellation and cell,
point, influence, expanded-node and cross-product work limits refuse partial
publication. Source geometry, topology, morphs and imported weight data are never
modified by the solver.

The grid is an approximation. Thin regions, diagonal paths, touching anatomy,
ambiguous surface sampling and open geometry require better analysis or explicit
correction. This implementation currently requires the complete field produced
by the existing volume service. It does not implement the robust voxelization
described by the production-mesh geodesic binding paper.

## Research comparison and release selection

| Candidate | Basis | Current integration |
| --- | --- | --- |
| Volume geodesic distances | Geodesic voxel binding derives influence weights using distances inside a voxelized character. [Dionne and de Lasa, 2013](https://diglib.eg.org/items/3d3458d9-bdf2-41c7-8b84-5da16b5cd637) | Own C# distance/constraint implementation using existing grid services; no external solver code or checkpoints copied. |
| Bounded biharmonic weights | Bounded biharmonic weights minimize a Laplacian energy with bound constraints, providing a shape-aware optimization approach. [Jacobson et al., 2011](https://igl.ethz.ch/projects/bbw/) | Comparison candidate; solver, meshing dependencies, licenses and performance acceptance remain to be evaluated. |

The current generic tests cover concave gaps, separate islands, explicit region
barriers, eligibility-aware sample transfer, fixed positive and zero weights,
rigid assignments, influence truncation, malformed rows and cancellation/work
budgets. Private stress studies compare falloff and region choices with explicit
fixture joints. They do not establish hand/eye detection, automatic rig creation,
deformation quality on held-out production meshes, compiled binding fidelity or
native animation acceptance. RS-024 and RS-025 remain in progress.
