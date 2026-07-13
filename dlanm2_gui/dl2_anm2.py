"""Safe Dying Light 2 ANM2 format-42 inspection.

DL1 and DL2 both carry the historical ``42`` signature at offset 4.  The
secondary version at offset 6 dispatches the proven DL1 layout (1) versus the
new DL2 layout (2).  Naming the public paths format 1 and format 42 matches the
editor terminology while retaining the exact raw header fields in reports.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Sequence
import hashlib
import struct

MAGIC = b"ANM2"
DL2_SIGNATURE = 42
DL2_SECONDARY_VERSION = 2
DL2_HEADER_STRUCT = struct.Struct("<4s12H")
DL2_HEADER_SIZE = DL2_HEADER_STRUCT.size


@dataclass(frozen=True, slots=True)
class Dl2Anm2Header42:
    magic: str
    signature: int
    secondary_version: int
    frame_count: int
    flags: int
    active_track_count: int
    data_offset: int
    flags2: int
    active_descriptor_end: int
    reference_descriptor_offset: int
    flags3: int
    total_descriptor_count: int
    layout_flags: int
    active_descriptors: tuple[int, ...]
    reference_descriptors: tuple[int, ...]
    file_size: int
    sha256: str
    validation_errors: tuple[str, ...] = ()

    @property
    def reference_track_count(self) -> int:
        return len(self.reference_descriptors)

    @property
    def format_label(self) -> str:
        return "format 42"

    def to_dict(self) -> dict[str, Any]:
        result = asdict(self)
        result["active_descriptors"] = list(self.active_descriptors)
        result["reference_descriptors"] = list(self.reference_descriptors)
        result["layout_valid"] = not self.validation_errors
        result["native_curve_decoder"] = "incomplete"
        result["native_writer"] = "disabled"
        return result


def detect_anm2_format(data_or_path: bytes | bytearray | str | Path) -> int:
    data = bytes(data_or_path) if isinstance(data_or_path, (bytes, bytearray)) else Path(data_or_path).read_bytes()
    if len(data) < 8:
        raise ValueError("ANM2 payload is too small to contain magic and format fields.")
    magic, signature, secondary = struct.unpack_from("<4sHH", data, 0)
    if magic != MAGIC:
        raise ValueError(f"Expected ANM2 magic, found {magic!r}.")
    if signature != DL2_SIGNATURE:
        raise ValueError(f"Unsupported ANM2 signature {signature}; expected 42.")
    if secondary == 1:
        return 1
    if secondary == DL2_SECONDARY_VERSION:
        return 42
    raise ValueError(
        f"Unsupported ANM2 format: signature {signature}, secondary version {secondary}. "
        "Only DL1 format 1 and DL2 format 42 are supported."
    )


def parse_dl2_header42(path_or_data: str | Path | bytes | bytearray) -> Dl2Anm2Header42:
    data = bytes(path_or_data) if isinstance(path_or_data, (bytes, bytearray)) else Path(path_or_data).read_bytes()
    if detect_anm2_format(data) != 42:
        raise ValueError("The selected ANM2 is DL1 format 1, not DL2 format 42.")
    if len(data) < DL2_HEADER_SIZE:
        raise ValueError(f"DL2 format-42 ANM2 is smaller than its {DL2_HEADER_SIZE}-byte header.")
    values = DL2_HEADER_STRUCT.unpack_from(data, 0)
    (
        magic, signature, secondary, frame_count, flags, active_count, data_offset,
        flags2, active_end, reference_offset, flags3, total_count, layout_flags,
    ) = values
    errors: list[str] = []
    descriptor_start = DL2_HEADER_SIZE
    descriptor_end = descriptor_start + int(total_count) * 4
    expected_active_end = descriptor_start + int(active_count) * 4
    if total_count < active_count:
        errors.append("total descriptor count is smaller than the active descriptor count")
    if descriptor_end > len(data):
        errors.append("descriptor table extends past the end of the file")
    if data_offset < descriptor_end or data_offset > len(data):
        errors.append("animation data offset does not follow the descriptor table inside the file")
    if active_end not in {0, expected_active_end}:
        errors.append(
            f"active descriptor end is {active_end}, expected {expected_active_end} from the active count"
        )
    if reference_offset not in {0, expected_active_end, active_end}:
        errors.append("reference descriptor offset is inconsistent with the active descriptor table")
    readable_count = max(0, min(int(total_count), (len(data) - descriptor_start) // 4))
    descriptors = tuple(struct.unpack_from(f"<{readable_count}I", data, descriptor_start))
    active = descriptors[: min(int(active_count), len(descriptors))]
    reference = descriptors[min(int(active_count), len(descriptors)) :]
    if len(active) != active_count:
        errors.append("active descriptor table is truncated")
    if len(descriptors) != total_count:
        errors.append("reference descriptor table is truncated")
    return Dl2Anm2Header42(
        magic.decode("ascii"), signature, secondary, frame_count, flags, active_count,
        data_offset, flags2, active_end, reference_offset, flags3, total_count,
        layout_flags, active, reference, len(data), hashlib.sha256(data).hexdigest().upper(),
        tuple(dict.fromkeys(errors)),
    )


def inspect_anm2(path: str | Path, target_descriptors: Sequence[int] | None = None) -> dict[str, Any]:
    source = Path(path)
    detected = detect_anm2_format(source)
    if detected == 42:
        header = parse_dl2_header42(source)
        report = header.to_dict()
        report.update({"path": str(source), "detected_format": 42, "game_id": "dying_light_2"})
        if target_descriptors is not None:
            target = {int(value) for value in target_descriptors}
            active = set(header.active_descriptors)
            report["target_profile_compatibility"] = {
                "matched_active_descriptors": len(active & target),
                "missing_active_descriptors": sorted(active - target),
                "extra_target_descriptors": sorted(target - active),
                "compatible_for_inspection": not header.validation_errors,
                "compatible_for_curve_decode": False,
            }
        return report
    from . import anm2
    clip = anm2.decode_file(source)
    report = clip.diagnostics()
    report.update({"path": str(source), "detected_format": 1, "game_id": "dying_light_1"})
    return report


__all__ = [
    "DL2_HEADER_SIZE", "DL2_SECONDARY_VERSION", "DL2_SIGNATURE", "Dl2Anm2Header42",
    "detect_anm2_format", "inspect_anm2", "parse_dl2_header42",
]
