from __future__ import annotations

import argparse
import base64
import hashlib
import json
import math
import struct
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence


FORMAT = "dl-reanimated-python-csharp-fed-parity-oracle-v1"
MAX_ORACLE_BYTES = 512 * 1024


@dataclass(frozen=True)
class FedLimits:
    maximum_file_bytes: int = 16 * 1024 * 1024
    maximum_expressions: int = 100_000
    maximum_weights_per_expression: int = 100_000
    maximum_total_weights: int = 2_000_000
    maximum_string_bytes: int = 4096
    maximum_total_string_bytes: int = 8 * 1024 * 1024
    reject_trailing_bytes: bool = True
    reject_duplicate_names: bool = False


class FedOracleError(ValueError):
    pass


class _FedReader:
    def __init__(self, payload: bytes, limits: FedLimits) -> None:
        if len(payload) > limits.maximum_file_bytes:
            raise FedOracleError(
                "FED payload size is outside the configured limit."
            )
        self._payload = payload
        self._limits = limits
        self._offset = 0
        self._string_bytes = 0

    def read_exactly(self, length: int) -> bytes:
        end = self._offset + length
        if end > len(self._payload):
            raise FedOracleError("FED payload ended unexpectedly.")
        value = self._payload[self._offset:end]
        self._offset = end
        return value

    def read_bounded_int32(self, label: str, maximum: int) -> int:
        value = struct.unpack("<i", self.read_exactly(4))[0]
        if value < 0 or value > maximum:
            raise FedOracleError(f"FED {label} {value} is unsafe.")
        return value

    def read_string(self, label: str) -> str:
        length = struct.unpack("<H", self.read_exactly(2))[0]
        if length <= 0 or length > self._limits.maximum_string_bytes:
            raise FedOracleError(
                f"FED {label} length {length} is unsafe."
            )
        self._string_bytes += length
        if self._string_bytes > self._limits.maximum_total_string_bytes:
            raise FedOracleError(
                "FED total string bytes exceed the configured limit."
            )
        encoded = self.read_exactly(length)
        if b"\0" in encoded:
            raise FedOracleError(f"FED {label} contains an embedded NUL.")
        try:
            return encoded.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exception:
            raise FedOracleError(
                f"FED {label} is not valid UTF-8."
            ) from exception

    def read_single(self, label: str) -> float:
        value = struct.unpack("<f", self.read_exactly(4))[0]
        if not math.isfinite(value):
            raise FedOracleError(f"FED {label} is not finite.")
        return value

    def require_end(self) -> None:
        if (
            self._limits.reject_trailing_bytes
            and self._offset != len(self._payload)
        ):
            raise FedOracleError("FED payload contains trailing bytes.")


def _name_key(value: str) -> str:
    # Every case-folded comparison fixture is ASCII. This intentionally avoids
    # claiming that Python casefold and .NET OrdinalIgnoreCase are equivalent
    # for the full Unicode range.
    if not value.isascii():
        raise FedOracleError(
            "FED parity case-insensitive controls must use ASCII names."
        )
    return value.casefold()


def _diagnostic(
    code: str,
    message: str,
    expression_index: int,
    weight_index: int | None = None,
) -> dict[str, Any]:
    return {
        "code": code,
        "severity": "Warning",
        "message": message,
        "expressionIndex": expression_index,
        "weightIndex": weight_index,
    }


def _parse_fed(payload: bytes, limits: FedLimits) -> dict[str, Any]:
    reader = _FedReader(payload, limits)
    expression_count = reader.read_bounded_int32(
        "expression count",
        limits.maximum_expressions,
    )
    expressions: list[dict[str, Any]] = []
    diagnostics: list[dict[str, Any]] = []
    expression_names: set[str] = set()
    total_weights = 0

    for expression_index in range(expression_count):
        expression_name = reader.read_string(
            f"expression {expression_index} name"
        )
        if not expression_name.strip():
            raise FedOracleError(
                f"FED expression {expression_index} has an empty name."
            )
        expression_key = _name_key(expression_name)
        if expression_key in expression_names:
            if limits.reject_duplicate_names:
                raise FedOracleError(
                    "FED contains duplicate expression "
                    f"'{expression_name}'."
                )
            diagnostics.append(
                _diagnostic(
                    "FED001",
                    f"Expression '{expression_name}' duplicates an earlier "
                    "expression; source order is preserved and name lookup "
                    "returns the first occurrence.",
                    expression_index,
                )
            )
        else:
            expression_names.add(expression_key)

        weight_count = reader.read_bounded_int32(
            f"expression {expression_index} weight count",
            limits.maximum_weights_per_expression,
        )
        total_weights += weight_count
        if total_weights > limits.maximum_total_weights:
            raise FedOracleError(
                "FED total morph weight count exceeds the configured limit."
            )

        weights: list[dict[str, Any]] = []
        morph_names: set[str] = set()
        for weight_index in range(weight_count):
            morph_name = reader.read_string(
                "expression "
                f"{expression_index} morph {weight_index} name"
            )
            if not morph_name.strip():
                raise FedOracleError(
                    f"FED expression '{expression_name}' has an empty "
                    "morph name."
                )
            morph_key = _name_key(morph_name)
            if morph_key in morph_names:
                if limits.reject_duplicate_names:
                    raise FedOracleError(
                        f"FED expression '{expression_name}' contains "
                        f"duplicate morph '{morph_name}'."
                    )
                diagnostics.append(
                    _diagnostic(
                        "FED002",
                        f"Expression '{expression_name}' contains duplicate "
                        f"morph '{morph_name}'; both ordered weights are "
                        "preserved.",
                        expression_index,
                        weight_index,
                    )
                )
            else:
                morph_names.add(morph_key)

            weight = reader.read_single(
                f"expression {expression_index} morph "
                f"{weight_index} weight"
            )
            weight_bits = struct.unpack("<I", struct.pack("<f", weight))[0]
            weights.append(
                {
                    "morphName": morph_name,
                    "weight": weight,
                    "weightBitsHex": f"{weight_bits:08X}",
                }
            )

        expressions.append(
            {
                "name": expression_name,
                "weights": weights,
            }
        )

    reader.require_end()
    return {
        "expressions": expressions,
        "diagnostics": diagnostics,
    }


def _pack_string(value: str) -> bytes:
    encoded = value.encode("utf-8", errors="strict")
    if not 0 < len(encoded) <= 0xFFFF:
        raise ValueError("Synthetic FED string length is outside UInt16.")
    return struct.pack("<H", len(encoded)) + encoded


def _build_fed(
    expressions: Sequence[
        tuple[str, Sequence[tuple[str, float]]]
    ],
) -> bytes:
    output = bytearray(struct.pack("<i", len(expressions)))
    for expression_name, weights in expressions:
        output.extend(_pack_string(expression_name))
        output.extend(struct.pack("<i", len(weights)))
        for morph_name, weight in weights:
            output.extend(_pack_string(morph_name))
            output.extend(struct.pack("<f", weight))
    return bytes(output)


def _lookup_payload(
    document: dict[str, Any],
    query: str,
) -> dict[str, Any]:
    query_key = _name_key(query)
    for index, expression in enumerate(document["expressions"]):
        if _name_key(expression["name"]) == query_key:
            return {
                "query": query,
                "expressionIndex": index,
                "name": expression["name"],
            }
    raise FedOracleError(
        f"Synthetic lookup expression '{query}' was not found."
    )


def _layer_normalization(
    document: dict[str, Any],
    expression_index: int,
    target_morphs: Sequence[str],
    mapping: dict[str, str],
) -> dict[str, Any]:
    expression = document["expressions"][expression_index]
    target_by_key = {
        _name_key(target): target for target in target_morphs
    }
    mapping_by_key = {
        _name_key(source): target
        for source, target in mapping.items()
    }
    values: dict[str, float] = {}
    resolved_weight_count = 0
    missing: list[str] = []
    diagnostics: list[dict[str, Any]] = []
    for weight in expression["weights"]:
        source_name = weight["morphName"]
        target_name = mapping_by_key.get(
            _name_key(source_name),
            source_name,
        )
        target_key = _name_key(target_name)
        if target_key not in target_by_key:
            diagnostics.append(
                {
                    "code": "FED101",
                    "severity": "Warning",
                    "expressionIndex": expression_index,
                    "weightIndex": None,
                }
            )
            if all(
                _name_key(existing) != _name_key(source_name)
                for existing in missing
            ):
                missing.append(source_name)
            continue
        resolved_weight_count += 1
        values[target_key] = (
            values.get(target_key, 0.0) + float(weight["weight"])
        )

    tracks = [
        {
            "morphName": target,
            "value": values[_name_key(target)],
        }
        for target in target_morphs
        if _name_key(target) in values
    ]
    return {
        "expressionIndex": expression_index,
        "targetMorphs": list(target_morphs),
        "mapping": mapping,
        "tracks": tracks,
        "diagnostics": diagnostics,
        "compatibility": {
            "sourceWeightCount": len(expression["weights"]),
            "resolvedWeightCount": resolved_weight_count,
            "resolvedTargetCount": len(values),
            "missingSourceMorphNames": missing,
            "isComplete": (
                resolved_weight_count == len(expression["weights"])
                and not missing
            ),
        },
    }


def _accepted_case(
    case_id: str,
    payload: bytes,
    lookup: str,
    *,
    layer: dict[str, Any] | None = None,
) -> dict[str, Any]:
    normalized = _parse_fed(payload, FedLimits())
    result = {
        "id": case_id,
        "payloadBase64": base64.b64encode(payload).decode("ascii"),
        "payloadBytes": len(payload),
        "payloadSha256": hashlib.sha256(payload).hexdigest().upper(),
        "rejectDuplicateNames": False,
        "normalized": normalized,
        "lookup": _lookup_payload(normalized, lookup),
    }
    if layer is not None:
        result["layerNormalization"] = _layer_normalization(
            normalized,
            layer["expressionIndex"],
            layer["targetMorphs"],
            layer["mapping"],
        )
    return result


def _rejected_case(
    case_id: str,
    payload: bytes,
    diagnostic_fragment: str,
    *,
    reject_duplicate_names: bool = False,
) -> dict[str, Any]:
    limits = FedLimits(
        reject_duplicate_names=reject_duplicate_names,
    )
    try:
        _parse_fed(payload, limits)
    except FedOracleError as exception:
        return {
            "id": case_id,
            "payloadBase64": base64.b64encode(payload).decode("ascii"),
            "payloadBytes": len(payload),
            "payloadSha256": hashlib.sha256(payload)
                .hexdigest()
                .upper(),
            "rejectDuplicateNames": reject_duplicate_names,
            "pythonExceptionType": type(exception).__name__,
            "pythonMessage": str(exception),
            "diagnosticFragment": diagnostic_fragment,
        }
    raise AssertionError(f"Python FED oracle accepted '{case_id}'.")


def build_oracle() -> dict[str, Any]:
    canonical = _build_fed(
        (
            ("_NONE", ()),
            (
                "Blink.Mixed",
                (
                    ("morph_l_eye_close", 1.25),
                    ("morph_r_eye_close", -0.5),
                    ("jaw_open", -0.0),
                ),
            ),
            ("smile", (("mouth_smile", 0.75),)),
        )
    )
    duplicate_compatibility = _build_fed(
        (
            (
                "mixed",
                (
                    ("blink_l", 0.75),
                    ("BLINK_L", -0.25),
                    ("source_smile", 0.6),
                    ("missing", 0.2),
                ),
            ),
            ("MIXED", (("blink_l", 0.125),)),
        )
    )
    duplicate_expression = _build_fed(
        (
            ("blink", (("left_eye", 1.0),)),
            ("BLINK", (("right_eye", 1.0),)),
        )
    )
    duplicate_morph = _build_fed(
        (
            (
                "blink",
                (
                    ("eye", 1.0),
                    ("EYE", 0.5),
                ),
            ),
        )
    )
    one_expression_prefix = (
        struct.pack("<i", 1) + _pack_string("blink")
    )
    valid_single = _build_fed(
        (("blink", (("eye", 1.0),)),)
    )
    nan_weight = (
        struct.pack("<i", 1)
        + _pack_string("blink")
        + struct.pack("<i", 1)
        + _pack_string("eye")
        + struct.pack("<I", 0x7FC00000)
    )

    rejected = (
        _rejected_case(
            "truncated-header",
            b"\x01\x00\x00",
            "ended unexpectedly",
        ),
        _rejected_case(
            "negative-expression-count",
            struct.pack("<i", -1),
            "unsafe",
        ),
        _rejected_case(
            "excessive-expression-count",
            struct.pack("<i", 100_001),
            "unsafe",
        ),
        _rejected_case(
            "empty-expression-name",
            struct.pack("<iH", 1, 0),
            "length",
        ),
        _rejected_case(
            "whitespace-expression-name",
            struct.pack("<i", 1) + _pack_string(" "),
            "empty name",
        ),
        _rejected_case(
            "invalid-utf8-expression-name",
            struct.pack("<iH", 1, 1) + b"\xFF",
            "valid UTF-8",
        ),
        _rejected_case(
            "nul-expression-name",
            struct.pack("<iH", 1, 3) + b"a\0b",
            "embedded NUL",
        ),
        _rejected_case(
            "negative-weight-count",
            one_expression_prefix + struct.pack("<i", -1),
            "unsafe",
        ),
        _rejected_case(
            "excessive-weight-count",
            one_expression_prefix + struct.pack("<i", 100_001),
            "unsafe",
        ),
        _rejected_case(
            "empty-morph-name",
            one_expression_prefix
            + struct.pack("<iH", 1, 0),
            "length",
        ),
        _rejected_case(
            "nonfinite-weight",
            nan_weight,
            "not finite",
        ),
        _rejected_case(
            "truncated-weight",
            valid_single[:-1],
            "ended unexpectedly",
        ),
        _rejected_case(
            "trailing-byte",
            valid_single + b"\xA5",
            "trailing bytes",
        ),
        _rejected_case(
            "excessive-string-length",
            struct.pack("<iH", 1, 4097),
            "length",
        ),
        _rejected_case(
            "duplicate-expression-strict",
            duplicate_expression,
            "duplicate expression",
            reject_duplicate_names=True,
        ),
        _rejected_case(
            "duplicate-morph-strict",
            duplicate_morph,
            "duplicate morph",
            reject_duplicate_names=True,
        ),
    )

    return {
        "format": FORMAT,
        "scope": {
            "game": "Dying Light 1",
            "maximumOracleBytes": MAX_ORACLE_BYTES,
            "formatContract": (
                "Little-endian Int32 expression count; repeated UInt16 "
                "UTF-8 expression name, Int32 weight count, and repeated "
                "UInt16 UTF-8 morph name plus IEEE-754 float32 weight."
            ),
            "notes": [
                "All FED payloads are generated by this oracle and are safe to redistribute.",
                "The tracked legacy Python application has no FED parser; this is an independent standard-library review oracle.",
                "Ordered names and exact float32 bits are compared without clamping authored values.",
                "ASCII-only duplicate controls avoid claiming full Unicode equivalence between Python casefold and .NET OrdinalIgnoreCase.",
                "No retail or proprietary game payload is embedded.",
            ],
        },
        "acceptedInputs": [
            _accepted_case(
                "canonical-ordered-values",
                canonical,
                "BLINK.MIXED",
            ),
            _accepted_case(
                "duplicate-compatibility-and-layer-normalization",
                duplicate_compatibility,
                "MiXeD",
                layer={
                    "expressionIndex": 0,
                    "targetMorphs": (
                        "smile",
                        "blink_l",
                        "unused",
                    ),
                    "mapping": {
                        "source_smile": "smile",
                    },
                },
            ),
        ],
        "rejectedInputs": list(rejected),
    }


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Emit the bounded redistributable DL1 FED Python/C# parity oracle."
        )
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
    if not (
        (repository_root / "pyproject.toml").is_file()
        and (repository_root / "dlanm2_gui").is_dir()
    ):
        raise FileNotFoundError(
            f"Archived DL ReAnimated Python root was not found: "
            f"{repository_root}"
        )
    payload = build_oracle()
    text = json.dumps(
        payload,
        ensure_ascii=False,
        indent=2,
        sort_keys=True,
        allow_nan=False,
    ) + "\n"
    encoded = text.encode("utf-8")
    if len(encoded) > MAX_ORACLE_BYTES:
        raise ValueError(
            f"FED parity oracle is {len(encoded)} bytes; "
            f"maximum is {MAX_ORACLE_BYTES}."
        )
    if args.output is None:
        sys.stdout.write(text)
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(
            text,
            encoding="utf-8",
            newline="\n",
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
