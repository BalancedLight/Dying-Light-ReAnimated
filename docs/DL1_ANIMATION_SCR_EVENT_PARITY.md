# Historical DL1 AnimationScr event-layout fixture provenance

> **Historical background only.** The fixture below is a checked-in static C# compatibility input. The C# application, package, validation scripts, and GitHub Actions do not install or execute Python; the former live differential wrapper was retired.

This gate covers the event-bearing layout used by Dying Light 1 animation
scripts without claiming to understand or author event semantics. The legacy
Python implementation remains unchanged.

## Proven layout

The symbolized engine decompile establishes the serialized relationship:

- `AnimBank::ResPackRegister` reads the declared sequence count, places the
  first event row immediately after `56 * sequenceCount` bytes of sequence
  records, reads each record's unsigned event count at record offset `48`, and
  advances the event cursor by exactly `12 * eventCount`.
- The same function resolves each record's name offset relative to the byte
  immediately after the complete event table.
- `CResourceLoadingRuntime::RegisterAnimationScrResource` supplies the two
  resource sections to `AnimMixer::ResPackRegisterScr`.

That evidence proves the 56-byte record stride, the event-count location, the
12-byte event-row stride, and the location of the section-0 name table. It does
not prove the meaning of any field inside a 12-byte event row.

The separate installed-PC control below ties that structural interpretation to
the exact fingerprinted Windows 1.55 resources; the decompile alone is not
presented as Windows-build identity evidence.

`AnimationScrCodec.Parse` therefore derives the canonical name-table offset as:

```text
56 * declaredSequenceCount + 12 * sum(rawEventCount)
```

It accepts that location only when declared name offsets resolve to bounded
names there. Noncanonical auxiliary layouts retain the bounded heuristic
fallback and are visibly reported through
`HasCanonicalEventTableLayout == false`.

The parser exposes:

- each record's unsigned `RawEventCount`;
- `TotalDeclaredEventCount`;
- the opaque payload offset and length;
- the expected event-table length when it fits the supported bounds; and
- whether the observed layout is canonical.

The C# reader accepts both the authoring marker pair already emitted by the
Python/C# writers (`471`, `0x7FFA`) and the exact stock Windows 1.55 marker pair
(`588`, `0x7FF9`). Other marker pairs continue to be skipped instead of being
guessed.

## Differential control

`tests/fixtures/dl1_animation_scr_event_parity_v1.json` is a reviewed,
redistributable synthetic oracle. It contains no retail bytes. The live Python
probe creates:

- three 56-byte sequence records with event counts `2`, `0`, and `1`;
- three deliberately opaque 12-byte event rows;
- the canonical section-0 event table and name table;
- the duplicated section-1 names; and
- one case-insensitive timing patch.

`AnimationScrEventParityTests` requires Python and C# to agree on all parsed
sequence metadata and the exact original and patched section bytes. The patch
may change only the encoded FPS/start/end bytes of the selected record; the
opaque event table and all of section 1 must remain byte-identical.

The fixture also locks rejection decisions and actionable C# diagnostics for:

- append into an event/auxiliary layout;
- patch of a missing sequence;
- a name offset outside section 0;
- an unterminated name; and
- a missing name table.

A separate hermetic test applies the stock 1.55 marker pair to the same
canonical layout. This keeps the installed-build extension covered even on a
machine without Dying Light.

## Installed 1.55 evidence

The opt-in control is read-only, requires build fingerprint
`89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13`,
and opens `DW\Data\common_anims_PC.rpack` without launching the game. It locks
these exact type-322 resources:

| Resource | Index | Sequences | Event rows | Event bytes |
| --- | ---: | ---: | ---: | ---: |
| `anims_man_all` | 6849 | 7,698 | 64,092 | 769,104 |
| `anims_player` | 6850 | 5,925 | 5,511 | 66,132 |
| `anims_player_man_all` | 6851 | 12,689 | 68,679 | 824,148 |

For each resource the control proves that all declared records parse, every
marker pair is the exact stock 1.55 pair, the event byte count equals
`12 * sum(rawEventCount)`, and an in-memory timing patch preserves the complete
event table and second section. No installed payload or derived name list is
written to the repository.

## Current validation

The Hermetic and Release C# validation tiers exercise the checked-in event-layout fixture without requiring installed DL1 data:

```powershell
.\tools\validate_csharp.ps1 -Tier Hermetic -Configuration Release
```

The former live Python probe and fixture-update command are no longer part of this repository. Changes to this JSON fixture require an ordinary reviewed C# source change.## Explicit exclusions

- Event rows are opaque. Event names, arguments, frame semantics, ordering
  rules, and runtime dispatch are not decoded.
- Building new event rows and appending into event/auxiliary scripts remain
  unsupported and fail closed.
- The second section's action payload is preserved but not semantically
  decoded by this gate.
- The installed control is exact to the fingerprinted Windows 1.55 build and
  the three listed base resources. It is not broad DLC/script corpus proof.
- This is offline parser and byte-preservation evidence. It does not replace a
  bounded live-game animation-event acceptance pass.
