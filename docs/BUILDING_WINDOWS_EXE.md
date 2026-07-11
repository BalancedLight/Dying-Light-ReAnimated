# Building the Windows EXE

DL ReAnimated ships as Python source and can also be built as a portable Windows GUI application.

## Requirements

- Windows 10 or newer
- 64-bit Python 3.11 or newer
- Internet access for the first dependency installation

## One-command build

From the repository root:

```
build_exe.bat
```

PowerShell alternative:

```powershell
./build_exe.ps1
```

A normal build does **not** install or run pytest. The build script:

1. creates `.venv-build`;
2. installs the GUI and PyInstaller dependencies;
3. invokes PyInstaller with `DL-ReAnimated.spec`;
4. runs `DL-ReAnimated.exe --self-test` against the frozen application;
5. creates a portable ZIP.

Outputs:

```
dist/DL-ReAnimated/DL-ReAnimated.exe
dist/DL-ReAnimated/exe_self_test.json
dist/DL-ReAnimated-Windows-x64.zip
```

## Optional release validation

Run the source test subset only when preparing or checking a release:

```
build_exe.bat --with-tests
```

That option installs pytest into `.venv-build` and runs the release/project/EXE-surface tests before invoking PyInstaller.

The frozen executable self-test remains enabled by default because it is a lightweight packaging check, not the project test suite. It verifies that the built application can start and locate its bundled modules and assets.

To skip even the frozen self-test for troubleshooting antivirus or sandbox interference:

```
build_exe.bat --skip-smoke
```

The old `--skip-tests` option is still accepted for compatibility, but it is no longer necessary because tests are skipped by default.

## Why this is a one-folder build

PySide6, the Dying Light reference skeleton/template files, help documents, and example assets must remain available at runtime. A one-folder distribution keeps those files stable and inspectable instead of extracting them into a temporary directory on every launch.

Distribute the entire `DL-ReAnimated` folder or the generated ZIP, not the EXE by itself.

## Rebuilding cleanly

Delete these directories and run the command again:

```
.venv-build
build
dist
```

## Release caution

The repository includes game-derived reference files and an example RPack. Review `docs/project/THIRD_PARTY_ASSETS.md` before public redistribution.

## Optional Blender integration

Blender is not embedded in the portable application or ZIP. ANM2 → FBX users install Blender separately and select `blender.exe`; all FBX → ANM2 and RPack features remain self-contained. The PyInstaller build includes only DL ReAnimated's small background export helper.
