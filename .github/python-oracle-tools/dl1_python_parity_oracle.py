from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from typing import Any


FORMAT = "dl-reanimated-python-csharp-parity-oracle-v1"
MAX_ANM2_BYTES = 64 * 1024 * 1024
STOCK_ANM2_FILES = (
    "infected_turn_90r.template.anm2",
    "stock_writer_control.anm2",
)
SAMPLE_TIMES = (0.0, 0.5, 1.0, 14.5, 15.0, 29.25)
NAME_HASH_INPUTS = (
    "bip01",
    "BIP01",
    "EyeCamera",
    "l_hand",
    "r_hand",
    "Bip01 Motion",
    "w",
    "fv",
)
NON_ASCII_NAME_HASH_INPUTS = ("éye", "骨")


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def _read_bounded(path: Path) -> bytes:
    size = path.stat().st_size
    if size > MAX_ANM2_BYTES:
        raise ValueError(
            f"{path} is {size} bytes; parity inputs are limited to {MAX_ANM2_BYTES}."
        )
    return path.read_bytes()


def _header_payload(header: Any) -> dict[str, int]:
    return {
        "formatVersion": int(header.format_version),
        "samplerVersion": int(header.unknown06),
        "frameCount": int(header.frame_count),
        "trackCount": int(header.track_count),
        "pageCount": int(header.unknown12),
        "pageOffset": int(header.unknown14),
        "declaredLength": int(header.declared_length),
        "durationKeyCount": int(header.unknown20),
        "unknown24": int(header.unknown24),
        "unknown28": int(header.unknown28),
    }


def _selected_track_indices(track_count: int) -> list[int]:
    candidates = (
        0,
        1,
        2,
        track_count // 3,
        track_count // 2,
        (2 * track_count) // 3,
        track_count - 2,
        track_count - 1,
    )
    return sorted({index for index in candidates if 0 <= index < track_count})


def _sample_times(frame_count: int) -> list[float]:
    candidates = (*SAMPLE_TIMES, float(frame_count - 1))
    return sorted({value for value in candidates if 0.0 <= value <= frame_count - 1})


def _anm2_payload(data: bytes, name: str) -> dict[str, Any]:
    from dlanm2_gui import anm2
    from dlanm2_gui.anm2_components import decode_samples

    clip = anm2.decode(data, name)
    layout = anm2.probe_v1_layout(clip.header, data)
    if layout is None or layout.validation_errors:
        errors = [] if layout is None else layout.validation_errors
        raise ValueError(f"{name} is not a valid DL1 sampler-v1 payload: {errors}")

    selected_indices = _selected_track_indices(clip.header.track_count)
    decoded = decode_samples(data, _sample_times(clip.header.frame_count))
    samples: list[dict[str, Any]] = []
    for frame in decoded.frames:
        samples.append(
            {
                "requestedTime": float(frame.requested_time),
                "pageIndex": int(frame.page_index),
                "tableIndex": int(frame.table_index),
                "frameInSlot": int(frame.in_segment_frame),
                "fraction": float(frame.fraction),
                "tracks": [
                    [float(value) for value in frame.tracks[index]]
                    for index in selected_indices
                ],
            }
        )

    preserving_bytes = anm2.encode_preserving_body(clip)
    return {
        "name": name,
        "sourceSha256": _sha256(data),
        "preservingRoundTripSha256": _sha256(preserving_bytes),
        "byteLength": len(data),
        "header": _header_payload(clip.header),
        "descriptors": [f"0x{value:08X}" for value in decoded.descriptors],
        "pageFrameSpans": [int(value) for value in layout.page_frame_spans],
        "selectedTrackIndices": selected_indices,
        "samples": samples,
    }


def _packed_groups() -> list[dict[str, Any]]:
    from dlanm2_gui.anm2_packed import encode_group_8

    cases = (
        (
            "flat",
            [[0] * 8 for _ in range(16)],
        ),
        (
            "signed_ramp",
            [
                [
                    frame * 10,
                    -frame * 5,
                    3,
                    0,
                    frame,
                    -frame,
                    1,
                    -2,
                ]
                for frame in range(16)
            ],
        ),
        (
            "quadratic",
            [
                [
                    frame * frame * (lane + 1) - (70 * lane)
                    for lane in range(8)
                ]
                for frame in range(16)
            ],
        ),
    )
    payloads: list[dict[str, Any]] = []
    for name, frames in cases:
        encoded = encode_group_8(frames)
        payloads.append(
            {
                "name": name,
                "frames": frames,
                "encodedHex": encoded.hex().upper(),
                "sha256": _sha256(encoded),
            }
        )
    return payloads


def _generated_anm2() -> dict[str, Any]:
    from dlanm2_gui import anm2
    from dlanm2_gui.anm2_writer import build_payload_from_values
    from dlanm2_gui.trackmap import dl_name_hash

    frame_count = 37
    descriptors = [dl_name_hash("bip01"), dl_name_hash("l_hand")]
    values: list[list[list[float]]] = []
    for frame in range(frame_count):
        values.append(
            [
                [
                    (frame - 18) * 0.03125,
                    ((frame % 9) - 4) * 0.0625,
                    0.125,
                    frame * 0.015625,
                    -frame * 0.0078125,
                    2.0,
                    1.0,
                    1.0,
                    1.0,
                ],
                [
                    0.0,
                    0.0,
                    0.0,
                    2.0,
                    -3.0,
                    4.0,
                    1.0 + frame / 128.0,
                    1.0 - frame / 256.0,
                    0.5 + (frame % 5) / 512.0,
                ],
            ]
        )
    packed_flags = [
        [True, True, False, True, True, False, False, False, False],
        [False, False, False, False, False, False, True, True, True],
    ]
    header = anm2.Anm2Header(
        format_version=anm2.FORMAT_VERSION,
        unknown06=1,
        frame_count=frame_count,
        track_count=len(descriptors),
        unknown12=1,
        unknown14=0,
        declared_length=0,
        unknown20=1,
        unknown24=0,
        unknown28=0,
    )
    encoded = build_payload_from_values(
        header,
        descriptors,
        values,
        packed_flags,
    )
    payload = _anm2_payload(encoded, "generated_direct_packed_scale.anm2")
    payload.update(
        {
            "recipe": "two-track-direct-packed-scale-v1",
            "packedComponentMasks": [0x1B, 0x1C0],
        }
    )
    return payload


def build_oracle(repository_root: Path) -> dict[str, Any]:
    from dlanm2_gui.trackmap import dl_name_hash

    reference = repository_root / "reference"
    stock: list[dict[str, Any]] = []
    for file_name in STOCK_ANM2_FILES:
        path = reference / file_name
        stock.append(_anm2_payload(_read_bounded(path), file_name))

    rejected: list[str] = []
    for value in NON_ASCII_NAME_HASH_INPUTS:
        try:
            dl_name_hash(value)
        except ValueError:
            rejected.append(value)
        else:
            raise AssertionError(f"Python unexpectedly accepted non-ASCII name {value!r}.")

    return {
        "format": FORMAT,
        "scope": {
            "game": "Dying Light 1",
            "semanticAbsoluteTolerance": 0.00001,
            "maximumInputBytes": MAX_ANM2_BYTES,
            "notes": [
                "Exact comparisons cover implicit ASCII name hashes, packed-group bytes, source bytes, preserving round trips, descriptors, headers, and page spans.",
                "Numeric comparisons cover only the listed ANM2 sampler-v1 tracks and times.",
                "This corpus does not prove FBX, retargeting, mimic mapping, RPack, renderer, or live-game parity.",
            ],
        },
        "nameHashes": [
            {"name": name, "hashHex": f"0x{dl_name_hash(name):08X}"}
            for name in NAME_HASH_INPUTS
        ],
        "rejectedNonAsciiNameHashes": rejected,
        "packedGroups": _packed_groups(),
        "generatedAnm2": _generated_anm2(),
        "stockAnm2": stock,
    }


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Emit the bounded DL1 Python parity oracle consumed by C# tests."
    )
    parser.add_argument(
        "--repository-root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
    )
    parser.add_argument("--output", type=Path)
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    repository_root = args.repository_root.resolve()
    sys.path.insert(0, str(repository_root))
    payload = build_oracle(repository_root)
    text = json.dumps(
        payload,
        ensure_ascii=False,
        indent=2,
        sort_keys=True,
        allow_nan=False,
    ) + "\n"
    if args.output is None:
        sys.stdout.write(text)
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text, encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
