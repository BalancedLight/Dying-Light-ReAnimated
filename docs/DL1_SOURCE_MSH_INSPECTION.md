# Inspecting Chrome source MSH

Run `DLReAnimated inspect-source-msh model.msh` to inspect a loose Chrome source MSH produced by the C# model writer. The command reads the file without modifying it and writes a JSON report to standard output.

The report identifies its evidence layer, file hash, physical hierarchy, local and reference matrices, bounds, material and surface names, and each LOD's geometry, subsets, skinning and morph counts. A subset palette contains global physical-node indices; a vertex's bone bytes are local indices into the palette used by that subset. The reader validates these relationships for the vertices referenced by each subset and retains the local values in the decoded document.

This command inspects the writer's emitted source format. It does not decode retail compact MSH resources or establish what a running Editor or Player loaded. The report keeps `compiledResourceValidated` and `runtimeValidated` false. Official compiler readback and native loaded-resource verification remain separate checks.

The source parser is bounded and rejects unsupported chunk layouts, invalid counts or framing, malformed hierarchy, non-finite transforms and geometry, invalid palettes or triangle indices, and invalid quantized skin weights. It supports the writer's rigid and skinned geometry, helper and bone nodes, multiple LODs and subsets, one-to-four skin influences, and named morph deltas. The vertex-format declaration is retained as bytes; its undocumented fields are not assigned invented meanings.

An example report capture is:

```text
DLReAnimated inspect-source-msh model.msh > source-inspection.json
```

Inspection errors use the standard CLI error exit code. Cancellation uses the standard cancellation exit code.
