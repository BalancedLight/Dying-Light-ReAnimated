# DL1 installed-retail authoring regression

The optional installed-retail regression connects the hermetic authoring
regression to a complete Steam Dying Light 1 installation without copying
retail payloads into the repository or test output. It never starts the game.

Run it from the repository root:

```powershell
.\tools\validate_dl1_installed_authoring_e2e.ps1 -Configuration Release
```

The detailed test output reports `EXERCISED` with the selected resource,
pack name, decoded bone/morph counts, and content hash when production Steam
discovery finds a complete install. It reports `NOT EXERCISED` and returns
cleanly when no complete install is available.

On a configured installation the test uses production code to:

- discover the Steam install and build the base/DLC retail provider set;
- enumerate the complete provider catalog into a temporary SQLite index;
- resolve an exact physical type-272 identity and reopen it by both logical
  and physical `RetailAssetId`;
- prefer the stable `armored` control, falling back to known player/head
  controls only when the candidate is not skinned, morph-capable, and backed
  by a fully decoded position-delta target;
- decode real hierarchy, geometry, skin palettes, morph inventory, rig, and
  mapped entity/LOD SHORT4 position deltas;
- bind the resource content SHA-256 and full retail identity into the target
  rig and retarget-map fingerprint;
- round-trip generated body and mimic ANM2 through import on one rational
  timeline;
- manually retarget one generated source track and evaluate non-destructive
  authored bone and facial corrections;
- export body+mimic through the authoritative evaluator, re-import both, and
  compare every emitted frame value; and
- atomically write a generated animation-library RPack, reopen it, and check
  animation bytes and script metadata.

Only the mesh input comes from the user's installation. Generated ANM2 and
RPack outputs are written under a temporary test directory and removed.
No retail mesh, texture, animation, FED, or other proprietary bytes are
embedded in source, artifacts, or releases.

The compact row contains position deltas only; it does not contain a separate
normal-delta payload. This test proves synchronized mimic authoring, edit
ownership, descriptor export, value preservation, and that the selected
retail channel has decoded position vectors. It is not a game-validation
result and does not claim exact shaders, normal reconstruction, or
pixel-identical live facial rendering.
