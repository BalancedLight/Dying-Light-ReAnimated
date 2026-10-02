# ReAnimated ODE collision backend

This is a small ODE-backed narrow-phase collision library for secondary-motion
preview contacts. It does not replace the preview's particle integration,
constraints, native MPC implementation, or game runtime. The C ABI exposes
only finite scalar sphere/capsule queries and contact data; ODE handles and
structures stay inside the DLL.

The source lock pins official Open Dynamics Engine 0.16.6. The build script
requires a caller-provided copy of the upstream source archive and verifies its
SHA-256 before extracting or building it. It discovers the Visual Studio 2022
x64 C++ tools and CMake without depending on a particular machine path. The
ODE static library and wrapper outputs, CTest results, and a receipt containing
source and wrapper hashes are written under the caller's artifact directory.

Example from the repository root:

```powershell
.\tools\native\ode-collision\build.ps1 `
  -OdeSourceArchive '<path-to-verified-ode-0.16.6.tar.gz>' `
  -ArtifactsDirectory '<writable-artifacts-directory>' `
  -StageForEvaluation
```

The optional staging switch copies the tested Win64 wrapper into the Evaluation
project's embedded-resource directory. Release builds require that resource;
Debug builds can still run on platforms where it is absent, using the explicitly
identified analytic development fallback.

The native smoke executable calls the wrapper built against ODE itself. It
checks sphere contacts and separation, capsule shaft and end-cap contacts,
finite-input rejection, repeatability, and concurrent per-thread calls. The
managed preview reports the active contact backend identity and the number of
native contact queries; it never labels the analytic fallback as ODE.

The wrapper uses ODE's collision geometry API only. Each worker thread allocates
ODE collision TLS once and reuses one particle sphere plus one solid sphere and
capsule. This avoids allocating ODE geoms for each particle/collider pair while
keeping all calls independent of Dying Light binaries, addresses, or ABIs.
