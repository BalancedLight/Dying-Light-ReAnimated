<p align="center">
  <img
    src="https://github.com/user-attachments/assets/51f5eba4-3e85-4436-8b81-48c9151d6ab9"
    alt="ReAnimated logo"
    width="628"
  />
</p>

Create custom cutscenes, make zombies do the Thriller dance, have Kyle Crane twerk, mocap yourself and import it; Create whatever you want and import it directly into your custom projects!

## What is ReAnimated?

DL ReAnimated is a Windows app for making and editing models and animations for **Dying Light 1**. Import your own FBX files, work with assets from your installed game, set up a rig, adapt animations to another character, and export files for your custom project.

You can:

- Import models, materials, and animation takes from binary FBX files.
- Adapt or repair a rig, or build a draft rig from geometry.
- Browse game models and animations from local RPack archives.
- Retarget motion, edit bones and keyframes, and work with animation events.
- Preview movement, facial expressions, first-person cameras, and secondary motion.
- Save editable models and projects, export ANM2 animations, build RPack packages, and deploy to a Developer Tools project.

This branch contains the C# application. It targets Dying Light 1; the older Python application's Dying Light 2 support does not carry over to this version -- *yet*.

## Before you start

ReAnimated is in early development. Expect errors, bugs, and unfinished features.

It isn't a miracle product. You'll still need to use the outputs in the editor or game, and some models will need work on their rigs, weights, textures, or animation setup. Check your results in the Developer Tools editor before using them in your project.

## Get started

1. Open the repository's [GitHub Actions builds](https://github.com/BalancedLight/Dying-Light-ReAnimated/actions/workflows/dotnet.yml).
2. Choose a successful run for the **main** branch. Download the **DL-ReAnimated-CSharp-win-x64** artifact at the bottom of the run page. Sign in to GitHub if it asks.
3. Extract the download. If you use the included `DL-ReAnimated-CSharp-win-x64.zip`, extract that too.
4. Run `DLReAnimated.exe`.
5. Read the startup notice and choose **Models**, **Animations**, or **Models + Animations**.

The packaged app is self-contained. You don't need the .NET SDK or Python to run it. Building from source is covered below.

## Requirements

| What you want to do | What you need |
| --- | --- |
| Run ReAnimated | 64-bit Windows and graphics support for Direct3D 11 |
| Import your own models or animations | Binary FBX files; ANM2 is also supported for animation import |
| Browse retail assets | A local Dying Light 1 installation and its RPack archives |
| Compile complete model packages or deploy compiled character output | Dying Light Developer Tools and its model compiler; compilation also needs the required retail game resources |
| Edit or test exported content in a custom map | A Developer Tools project |
| Export supported mesh/animation FBX files | Blender, selected through the app's **Blender…** button |
| Build the application | The .NET SDK selected by `global.json`, PowerShell, and access to restore NuGet packages |

Retail assets are read from your installation and aren't bundled with the app. Keep the game files available when reopening projects that reference them.

## Choose your workflow

| Starting point | Where to begin |
| --- | --- |
| Import, adapt, or repair a model | **Models** — [model guide](docs/models.md) |
| Import, retarget, or edit animation | **Animations** — [animation guide](docs/animations.md) |
| Work on a character and its animations together | **Models + Animations** — start with the model guide, then prepare its clips in Animations |
| Produce files for the editor or game | **Export** — [export and deployment guide](docs/exporting.md) |

Save early with **File → Save project**. A `.dlraproj` saves the overall workspace; a `.dlrmodel` saves an editable model package. Neither is a replacement for exporting game resources.

## Build and develop

Clone this repository and switch to the `main` branch. Run the following commands from the repository root. Install the SDK pinned by [global.json](global.json), currently **.NET 10.0.401**; later patches in that SDK feature band are permitted.

### Build

```powershell
.\build_csharp.ps1 -Configuration Debug
```

For a Release build:

```powershell
.\build_csharp.ps1 -Configuration Release
```

The script restores locked dependencies, builds the solution, and runs the tests. Use `-SkipTests` when you only need a build.

Every solution build also publishes a self-contained executable:

```text
artifacts/csharp/solution-build/<Configuration>/win-x64/DLReAnimated.exe
```

The regular development output is under `src/ReAnimated.App/bin/<Configuration>/net10.0-windows10.0.19041.0/`. Close any app running from an output directory before rebuilding it. The same executable hosts the desktop app and CLI.

### Validate

```powershell
.\tools\validate_csharp.ps1 -Tier Focused -Configuration Release
.\tools\validate_csharp.ps1 -Tier Hermetic -Configuration Release
.\tools\validate_csharp.ps1 -Tier Release -Configuration Release
```

**Focused** runs a smaller set of checks for development. **Hermetic** runs the broader checks without a local game installation. **Release** adds renderer checks and optional installed-Blender checks. Supply `-BlenderExecutable` to select Blender, or `-SkipUnavailableOptionalBlender` when that optional check cannot run.

Validation can reuse results for unchanged inputs. Add `-ForceAll` to rerun all checks for the selected tier. These commands check the application; test your exported content in the editor separately.

### Package

```powershell
.\package_csharp.ps1
```

This builds and validates a self-contained Release package. Outputs go to:

```text
artifacts/csharp/win-x64/DLReAnimated.exe
artifacts/csharp/DL-ReAnimated-CSharp-win-x64.zip
artifacts/csharp/SHA256SUMS.txt
```

Use `-OutputRoot` to choose another output folder. For machines without the optional Blender check, use `-SkipUnavailableOptionalBlenderOracle`. Use `-ForceAllValidation` to rerun packaging validation instead of reusing previous results.

See [C# application architecture](docs/CSHARP_REWRITE.md) for the solution's main components.

## Help and license

- [Models and rig setup](docs/models.md)
- [Animations and editing](docs/animations.md)
- [Exporting and Developer Tools deployment](docs/exporting.md)
- [Dying Light Developer Tools wiki](https://dldt.miraheze.org/wiki/)
- [Report a bug](https://github.com/BalancedLight/Dying-Light-ReAnimated/issues) — include the app version or build, what you were doing, and the message from Jobs / diagnostics. Attach a small example you can share if possible.
- [License](LICENSE)

## AI usage disclosure

ReAnimated was helped developed with use of AI tools.
