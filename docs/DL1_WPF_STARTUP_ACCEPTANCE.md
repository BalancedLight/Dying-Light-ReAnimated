# Packaged WPF/D3D11 startup acceptance

This gate runs the real shipped application entry point, WPF XAML, main
window, both `HwndHost` viewport children, and their D3D11 presentation loops.
It is separate from the headless package self-test.

## Run

From the repository root after packaging:

```powershell
.\tools\validate_dl1_wpf_startup.ps1
```

To exercise a particular build:

```powershell
.\tools\validate_dl1_wpf_startup.ps1 `
  -ExecutablePath .\artifacts\csharp\win-x64\DLReAnimated.exe `
  -TimeoutSeconds 30
```

Repeated open/close acceptance uses the same isolated contract and records
every executable-bound run:

```powershell
.\tools\validate_dl1_wpf_startup_stress.ps1 -Iterations 5
```

It publishes
`artifacts/validation/dl1-wpf-startup-stress.json` only after every iteration
passes and proves all iterations used one exact executable SHA-256 and
informational version.

The executable handles the private `--wpf-startup-smoke` switch before normal
CLI or interactive dispatch. The switch is not a public CLI verb.

## Contract

The runner creates one bounded stage under `artifacts/validation`, then starts
the selected executable with:

- isolated `LOCALAPPDATA`, `APPDATA`, `TEMP`, and single-file extraction roots;
- developer .NET SDK and NuGet environment paths removed;
- a minimal Windows system `PATH`;
- a private empty receipt directory and working directory; and
- no computer-control session or simulated input.

The WPF window remains visible to Windows so real `HwndHost` children can be
created, but it is positioned outside the virtual desktop, is not activated,
and does not appear on the taskbar. After initial readiness, the same real
window follows a deterministic six-step compact/expanded resize schedule.
Both real viewport hosts must:

- be present exactly once for the source and target panes;
- reach `RendererLifecycleState.Ready`;
- select Hardware or the documented WARP startup fallback;
- present at least three frames;
- have positive arranged dimensions and DPI scales;
- remain Ready and advance their presented-frame count after every resize;
- publish live swap-chain pixel dimensions equal to the ceiling of each
  arranged host size multiplied by its WPF DPI scale after every resize; and
- report no unexpected renderer diagnostics.

The isolated profile must contain no crash report. A timeout, nonzero exit,
missing/partial receipt, faulted viewport, unexpected diagnostic, wrong
architecture, or evidence-write failure fails closed and preserves the stage
for inspection.

On success, the tool atomically publishes:

```text
artifacts/validation/dl1-wpf-startup-smoke.json
```

The schema-2 receipt binds the result to the executable path, byte length,
SHA-256, assembly informational version, process/runtime/OS identity, adapter
choices, and the hash of the in-process source receipt. It also retains each
resize target, actual window and host DIP dimensions, DPI scales, expected and
renderer-published pixels, before/after frame counts, states, and diagnostics.
The disposable isolated profile is then removed.

## Boundary

This is strong evidence that the exact executable can start the real WPF and
D3D11 presentation stack on the current Windows session without relying on
the developer environment. The repeated runner additionally proves bounded
startup, repeated hosted swap-chain resize, viewport presentation, and orderly
shutdown across consecutive fresh processes. It is not:

- a clean-machine test;
- a Remote Desktop transition;
- physical device-removal, display-change, adapter-change, or forced-WARP
  evidence;
- a multi-hour editor longevity run;
- a screenshot-pixel or retail-material comparison; or
- a live Dying Light validation.

Those gates remain separate. The smoke path never reads or embeds retail game
assets and never launches Dying Light.
