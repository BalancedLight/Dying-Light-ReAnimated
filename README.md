# DL ReAnimated

DL ReAnimated is a Windows C# authoring application for Dying Light 1
animation work. It provides a WPF/MVVM editor, a Direct3D 11 preview viewport,
retail RPack asset browsing, FBX/ANM2 import, retargeting, non-destructive bone
edits, facial preview, FPP/EyeCamera preview, ANM2 export, and animation-library
RPack output.

This repository contains the C# application only. It has no Python runtime, packaging, validation, or GitHub Actions dependency. The checked-in JSON compatibility fixtures are static C# test inputs that preserve reviewed historical behavior.

## Build

The SDK is pinned by `global.json`.

```powershell
.\build_csharp.ps1 -Configuration Debug
```

Every solution build also publishes one self-contained `win-x64` executable
to:

```text
artifacts\csharp\solution-build\<Configuration>\win-x64\DLReAnimated.exe
```

The CLI is hosted by that same executable; no second executable is shipped.

## Validate

Run the fast development gates:

```powershell
.\tools\validate_csharp.ps1 -Tier Focused -Configuration Release
```

Run the hermetic C# gates:

```powershell
.\tools\validate_csharp.ps1 -Tier Hermetic -Configuration Release
```

Release validation runs portable C# gates: renderer goldens and, when supplied, the optional Blender handoff:

```powershell
.\tools\validate_csharp.ps1 `
    -Tier Release `
    -Configuration Release
```

Create the self-contained release folder and ZIP with:

```powershell
.\package_csharp.ps1
```

Validation is content-addressed and fail-closed. `-ForceAll` on the validation script and `-ForceAllValidation` on the packaging script deliberately bypass reusable receipts.

## Scope

- Dying Light 1 PC only.
- Fresh C# schema-1 `.dlraproj` projects only.
- Legacy Python schema 1-10 projects are detected and refused without being
  modified.
- Retail meshes, textures, animations, FED files, and other proprietary game
  assets are referenced locally and are never embedded in projects or
  releases.
- Blender remains optional and is used only for the reverse FBX writer.
- The independent **Models** workspace accepts user-owned binary FBX models,
  previews custom rigs/materials/textures, stores deterministic schema-1
  `.dlrmodel` packages, builds evidence-backed DL1 source `.msh`/`.bscr` and
  optional `.ascr`, and exports selected FBX animation stacks as one animation
  RPack. With an installed DL1 Developer Tools compiler and retail compiler
  bootstrap, it also creates a compiled `.msh_obj` and standalone type-272
  model RPack in an isolated job, then reopens and validates both hierarchy and
  RP6L identity before atomic publication. It never embeds retail game assets.
  Unproven `.chr` and `.skn` generation fails closed instead of emitting
  placeholders.

See [the C# implementation status](docs/CSHARP_REWRITE.md),
[the first-release support matrix](docs/DL1_FIRST_RELEASE_SUPPORT_MATRIX.md),
[the ANM2 format notes](docs/ANM2_FORMAT.md), and
[the stability gates](docs/DL1_STABILITY_ACCEPTANCE.md).

## License

See [LICENSE](LICENSE).
