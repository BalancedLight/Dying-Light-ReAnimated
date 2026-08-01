# DL1 installed-build fingerprint

The C# editor fingerprints an installed Windows Dying Light 1 executable at
startup without launching or loading the game. This is evidence for preview
fidelity labeling; it is not proof by itself that a preview behavior was
validated in game.

## Identity contract

`Dl1InstalledBuildFingerprintService` discovers a complete Steam install
through `SteamInstallDiscovery`, opens only `DyingLightGame.exe`, and records:

- executable SHA-256;
- executable byte length;
- numeric file version;
- numeric product version; and
- a versioned build fingerprint used by `PreviewProfile`.

The service reads the executable sequentially through one 1 MiB rented buffer.
It never copies the whole executable into memory and honors cancellation
between reads. The file is opened read-only and the game is never started.

Build fingerprint schema `dl-reanimated-dl1-windows-build-v1` hashes this
canonical UTF-8 payload:

```text
dl-reanimated-dl1-windows-build-v1
executable=DyingLightGame.exe
size=<invariant decimal byte count>
file-version=<major.minor.build.revision>
product-version=<major.minor.build.revision>
sha256=<lowercase executable SHA-256>
```

Absolute paths and filesystem timestamps are excluded. The same executable
copied to another Steam library therefore retains the same build identity,
while a byte, size, or embedded numeric-version change produces a different
identity.

## Fidelity badge behavior

The WPF toolbar and Fidelity tab show separate `Preview fidelity` and
`Installed DL1 build` rows.

- Built-in previews remain **DL1 profile** because this repository does not
  ship an independently trusted validation-capture registry.
- A saved **Game validated** profile is active only when its context and
  behavior settings equal the preview currently being evaluated, its exact
  64-hex build fingerprint matches the installed executable, and its exact
  64-hex validation-capture fingerprint matches an independently trusted
  registry entry.
- If the installed build fingerprint is missing, unreadable, still being
  detected, or different from the saved build fingerprint, the visible tier
  is downgraded to **DL1 profile** (or **Raw** for a raw context).
- Even a matching installed build remains downgraded when no trusted registry
  entry matches the profile's validation-capture fingerprint. Project metadata
  is evidence identity, not a trust source. Matching bytes are necessary, not
  a substitute for the associated capture and comparison record.

Executable and composite fingerprints are displayed in the Fidelity tab so a
validation report can record the exact evidence. No executable bytes are
stored in the project.

## Focused regression

```powershell
dotnet test .\tests\ReAnimated.Tests\ReAnimated.Tests.csproj `
  -c Debug `
  --filter FullyQualifiedName~Dl1InstalledBuildFingerprintTests `
  --no-restore
```

The tests cover bounded multi-buffer hashing, path-independent identity,
byte-change invalidation, cancellation, exact build/capture fingerprint
validation, case-insensitive hexadecimal matching, and WPF badge downgrade
behavior when trusted capture evidence is absent. They use generated files
only.

## Current local build identity

The read-only validation run on 2026-07-29 identified the configured Windows
installation as:

- file version `1.55.0.0`;
- executable length `1,255,560` bytes;
- executable SHA-256
  `f3f8a3b0841dcbc41a16a221da9935b6d70738700f5e46600f77f41d993d0835`;
  and
- composite build fingerprint
  `89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13`.

This records the installed binary used by the retail corpus and authoring
controls. No matching FPP/movie/gameplay capture profile has been approved, so
the built-in previews remain `DL1 profile`, not `Game validated`.
