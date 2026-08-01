[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild,
    [switch]$UpdateFixture,
    [switch]$InstalledEvidence,
    [string]$PythonOracleRoot = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($PythonOracleRoot)) {
    $PythonOracleRoot = Join-Path `
        (Split-Path -Parent $repositoryRoot) `
        "ReAnimated - Python"
}
$resolvedPythonOracleRoot = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables(
        $PythonOracleRoot.Trim().Trim('"')))
if (-not (Test-Path -LiteralPath (
        Join-Path $resolvedPythonOracleRoot "dlanm2_gui") `
        -PathType Container)) {
    throw (
        "The external Python oracle package is missing below " +
        "$resolvedPythonOracleRoot")
}
$temporaryRoot = [IO.Path]::GetFullPath(
    (Join-Path ([IO.Path]::GetTempPath()) (
        "dl-reanimated-scr-event-parity-" +
        [Guid]::NewGuid().ToString("N"))))
$expectedTemporaryPrefix = [IO.Path]::GetFullPath(
    ([IO.Path]::GetTempPath())).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $temporaryRoot.StartsWith(
        $expectedTemporaryPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use an SCR parity directory outside the OS temp directory."
}

$checkedFixture = Join-Path $repositoryRoot `
    "tests/fixtures/dl1_animation_scr_event_parity_v1.json"
$generatedFixture = Join-Path $temporaryRoot `
    "dl1_animation_scr_event_parity_v1.json"
$probePath = Join-Path $temporaryRoot "scr_event_oracle_probe.py"
$previousOracle = [Environment]::GetEnvironmentVariable(
    "DLR_PYTHON_SCR_EVENT_PARITY_ORACLE",
    [EnvironmentVariableTarget]::Process)
$previousInstalledEvidence = [Environment]::GetEnvironmentVariable(
    "DLR_RUN_INSTALLED_ANIMATION_SCR_EVENT_EVIDENCE",
    [EnvironmentVariableTarget]::Process)

$pythonProbe = @'
from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
import struct
import sys


FORMAT = "dl-reanimated-python-csharp-animation-scr-event-parity-v1"


def sha256(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest().upper()


def sections_payload(sections, parse_animation_scr_sections):
    section0, section1 = sections
    parsed = parse_animation_scr_sections(sections)
    return {
        "section0Base64": base64.b64encode(section0).decode("ascii"),
        "section0Sha256": sha256(section0),
        "section1Base64": base64.b64encode(section1).decode("ascii"),
        "section1Sha256": sha256(section1),
        "parsed": {
            "declaredSequenceCount": parsed.sequence_count,
            "nameTableOffset": parsed.name_table_offset,
            "sequences": [
                {
                    "name": sequence.name,
                    "nameOffset": sequence.name_offset,
                    "recordOffset": sequence.record_offset,
                    "enabled": sequence.enabled,
                    "blend": sequence.blend,
                    "framesPerSecond": sequence.fps,
                    "startFrame": sequence.start_frame,
                    "endFrame": sequence.end_frame,
                    "eventCount": sequence.event_count,
                }
                for sequence in parsed.sequences
            ],
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository-root", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    repository_root = Path(args.repository_root).resolve()
    sys.path.insert(0, str(repository_root))

    from dlanm2_gui.animation_scr import (
        ANIMATION_SCR_RECORD_MAGIC,
        ANIMATION_SCR_RECORD_SENTINEL,
        ANIMATION_SCR_RECORD_SIZE,
        AnimationScrSequence,
        append_animation_scr_sequences,
        parse_animation_scr_sections,
        patch_animation_scr_sequence_ranges,
    )

    event_record_size = 12
    sequence_rows = (
        ("idle_event", 1, 0.25, 30.0, 0.0, 59.0, 2),
        ("run_event", 1, 0.5, 60.0, 5.0, 125.0, 0),
        ("turn_event", 0, 0.75, 24.0, 2.5, 48.5, 1),
    )
    names = b"".join(
        name.encode("ascii") + b"\0"
        for name, *_ in sequence_rows
    )
    name_offsets = []
    cursor = 0
    for name, *_ in sequence_rows:
        name_offsets.append(cursor)
        cursor += len(name.encode("ascii")) + 1

    records = bytearray()
    for name_offset, row in zip(name_offsets, sequence_rows):
        _, enabled, blend, fps, start, end, event_count = row
        records.extend(struct.pack(
            "<IIIIIffffIIIII",
            name_offset,
            ANIMATION_SCR_RECORD_MAGIC,
            0,
            0,
            enabled,
            blend,
            fps,
            start,
            end,
            0,
            0,
            0,
            event_count,
            ANIMATION_SCR_RECORD_SENTINEL,
        ))

    # Runtime/decompile evidence establishes only the 12-byte row stride here.
    # The individual fields deliberately remain opaque.
    event_rows = (
        bytes.fromhex("03 00 00 00 11 22 33 44 00 00 40 3F"),
        bytes.fromhex("07 00 00 00 99 88 77 66 00 00 20 C0"),
        bytes.fromhex("0B 00 00 00 DE AD BE EF 01 02 03 00"),
    )
    event_table = b"".join(event_rows)
    original = (
        bytes(records) + event_table + names,
        struct.pack("<II", len(sequence_rows), 0) + names,
    )
    parsed = parse_animation_scr_sections(original)
    expected_name_offset = (
        len(sequence_rows) * ANIMATION_SCR_RECORD_SIZE +
        len(event_table)
    )
    if parsed.name_table_offset != expected_name_offset:
        raise AssertionError(
            "Python did not locate the canonical event-bearing name table."
        )
    if [row.event_count for row in parsed.sequences] != [2, 0, 1]:
        raise AssertionError("Python event-count metadata changed.")

    patch_override = {
        "name": "RUN_EVENT",
        "startFrame": 6.25,
        "endFrame": 96.5,
        "framesPerSecond": 48.0,
    }
    patched = patch_animation_scr_sequence_ranges(
        original,
        {
            patch_override["name"]: (
                patch_override["startFrame"],
                patch_override["endFrame"],
                patch_override["framesPerSecond"],
            )
        },
    )
    event_offset = len(sequence_rows) * ANIMATION_SCR_RECORD_SIZE
    event_end = event_offset + len(event_table)
    if patched[0][event_offset:event_end] != event_table:
        raise AssertionError("Python range patch changed opaque event bytes.")
    if patched[1] != original[1]:
        raise AssertionError("Python range patch changed section 1.")
    changed_offsets = [
        index
        for index, (before, after) in enumerate(zip(original[0], patched[0]))
        if before != after
    ]

    rejection_recipes = []

    def capture(case_id, operation, sections, action):
        try:
            action()
        except (ValueError, NotImplementedError) as exception:
            rejection_recipes.append({
                "id": case_id,
                "operation": operation,
                "section0Base64": base64.b64encode(
                    sections[0]
                ).decode("ascii"),
                "section1Base64": base64.b64encode(
                    sections[1]
                ).decode("ascii"),
                "pythonExceptionType": type(exception).__name__,
                "pythonMessage": str(exception),
            })
        else:
            raise AssertionError(
                f"Python AnimationScr unexpectedly accepted {case_id}."
            )

    capture(
        "append-event-layout",
        "append",
        original,
        lambda: append_animation_scr_sequences(
            original,
            (
                AnimationScrSequence(
                    "new_event_clip",
                    "new_event_clip.anm2",
                    0.0,
                    1.0,
                    30.0,
                ),
            ),
        ),
    )
    capture(
        "patch-missing-event-sequence",
        "patch",
        original,
        lambda: patch_animation_scr_sequence_ranges(
            original,
            {"not_present": (0.0, 1.0, 30.0)},
        ),
    )

    outside = bytearray(original[0])
    struct.pack_into("<I", outside, 0, 0x7FFFFFFF)
    outside_sections = (bytes(outside), original[1])
    capture(
        "event-name-offset-outside",
        "parse",
        outside_sections,
        lambda: parse_animation_scr_sections(outside_sections),
    )

    unterminated_sections = (original[0][:-1], original[1])
    capture(
        "event-name-unterminated",
        "parse",
        unterminated_sections,
        lambda: parse_animation_scr_sections(unterminated_sections),
    )

    missing_names_sections = (
        original[0][:expected_name_offset],
        original[1],
    )
    capture(
        "event-name-table-missing",
        "parse",
        missing_names_sections,
        lambda: parse_animation_scr_sections(missing_names_sections),
    )

    payload = {
        "format": FORMAT,
        "scope": {
            "game": "Dying Light 1",
            "layout": (
                "56-byte sequence records, contiguous opaque 12-byte event "
                "rows, then duplicated lowercase ASCII sequence names"
            ),
            "eventSemantics": "opaque",
            "eventEncodingSupported": False,
            "retailPayloadEmbedded": False,
            "notes": [
                "The fixture is synthetic and redistributable.",
                "Python remains unchanged and is invoked through its existing parser, range patcher, and append rejection.",
                "This proves metadata parsing and byte preservation, not event field semantics or event authoring.",
            ],
        },
        "recordSize": ANIMATION_SCR_RECORD_SIZE,
        "eventRecordSize": event_record_size,
        "canonical": {
            "recipe": (
                "Three synthetic stock-layout sequences with event counts "
                "2/0/1 and three opaque 12-byte rows"
            ),
            "eventTableOffset": event_offset,
            "eventTableLength": len(event_table),
            "eventTableBase64": base64.b64encode(
                event_table
            ).decode("ascii"),
            "eventTableSha256": sha256(event_table),
            "original": sections_payload(
                original,
                parse_animation_scr_sections,
            ),
            "patchOverride": patch_override,
            "patched": sections_payload(
                patched,
                parse_animation_scr_sections,
            ),
            "patchChangedOffsets": changed_offsets,
        },
        "rejectedInputs": rejection_recipes,
    }
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(payload, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
'@

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    [IO.File]::WriteAllText(
        $probePath,
        $pythonProbe,
        [Text.UTF8Encoding]::new($false))

    $pythonCommand = Get-Command "py" -ErrorAction SilentlyContinue
    if ($null -ne $pythonCommand) {
        & $pythonCommand.Source -3 $probePath `
            --repository-root $resolvedPythonOracleRoot `
            --output $generatedFixture
    }
    else {
        $pythonCommand = Get-Command "python" -ErrorAction Stop
        & $pythonCommand.Source $probePath `
            --repository-root $resolvedPythonOracleRoot `
            --output $generatedFixture
    }
    if ($LASTEXITCODE -ne 0) {
        throw (
            "The Python AnimationScr event-layout probe failed with exit " +
            "code $LASTEXITCODE.")
    }

    if ($UpdateFixture) {
        $checkedDirectory = Split-Path -Parent $checkedFixture
        [IO.Directory]::CreateDirectory($checkedDirectory) | Out-Null
        $replacement = Join-Path $checkedDirectory (
            ".dl1_animation_scr_event_parity_v1." +
            [Guid]::NewGuid().ToString("N") +
            ".tmp")
        $backup = Join-Path $checkedDirectory (
            ".dl1_animation_scr_event_parity_v1." +
            [Guid]::NewGuid().ToString("N") +
            ".bak")
        $committed = $false
        try {
            [IO.File]::WriteAllBytes(
                $replacement,
                [IO.File]::ReadAllBytes($generatedFixture))
            if ([IO.File]::Exists($checkedFixture)) {
                [IO.File]::Replace(
                    $replacement,
                    $checkedFixture,
                    $backup)
            }
            else {
                [IO.File]::Move($replacement, $checkedFixture)
            }
            $committed = $true
        }
        finally {
            if ([IO.File]::Exists($replacement)) {
                [IO.File]::Delete($replacement)
            }
            if ($committed -and [IO.File]::Exists($backup)) {
                [IO.File]::Delete($backup)
            }
            elseif ([IO.File]::Exists($backup)) {
                Write-Warning (
                    "The previous AnimationScr event fixture backup was " +
                    "preserved at $backup")
            }
        }
    }

    if (-not [IO.File]::Exists($checkedFixture)) {
        throw (
            "The checked AnimationScr event-layout fixture is missing. " +
            "Run this tool once with -UpdateFixture and review the result.")
    }

    $generatedHash = (
        Get-FileHash -LiteralPath $generatedFixture -Algorithm SHA256).Hash
    $checkedHash = (
        Get-FileHash -LiteralPath $checkedFixture -Algorithm SHA256).Hash
    if ($generatedHash -ne $checkedHash) {
        throw (
            "The checked AnimationScr event-layout fixture is stale. " +
            "Generated SHA256: $generatedHash; checked SHA256: " +
            "$checkedHash. Review the evidence recipe before updating it.")
    }

    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_SCR_EVENT_PARITY_ORACLE",
        $generatedFixture,
        [EnvironmentVariableTarget]::Process)
    $testFilter = "FullyQualifiedName~AnimationScrEventParityTests"
    if ($InstalledEvidence) {
        [Environment]::SetEnvironmentVariable(
            "DLR_RUN_INSTALLED_ANIMATION_SCR_EVENT_EVIDENCE",
            "1",
            [EnvironmentVariableTarget]::Process)
        $testFilter += (
            "|FullyQualifiedName~" +
            "InstalledAnimationScrEventEvidenceTests")
    }
    $testArguments = @(
        "test",
        (Join-Path $repositoryRoot "tests/ReAnimated.Tests/ReAnimated.Tests.csproj"),
        "--configuration",
        $Configuration,
        "--filter",
        $testFilter
    )
    if ($NoBuild) {
        $testArguments += "--no-build"
    }

    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw (
            "The C# AnimationScr event-layout parity tests failed with exit " +
            "code $LASTEXITCODE.")
    }

    Write-Host (
        "DL1 AnimationScr event-layout Python/C# parity passed. " +
        "Fixture SHA256: $generatedHash")
    if ($InstalledEvidence) {
        Write-Host (
            "Installed DL1 1.55 stock AnimationScr event-layout " +
            "evidence passed.")
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_SCR_EVENT_PARITY_ORACLE",
        $previousOracle,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        "DLR_RUN_INSTALLED_ANIMATION_SCR_EVENT_EVIDENCE",
        $previousInstalledEvidence,
        [EnvironmentVariableTarget]::Process)
    if ([IO.Directory]::Exists($temporaryRoot)) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        if ($resolvedTemporaryRoot.StartsWith(
                $expectedTemporaryPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
        }
    }
}
