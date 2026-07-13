from __future__ import annotations

import json
import struct
import zipfile
from dataclasses import asdict, dataclass
from pathlib import Path

from . import anm2
from .skeletons import read_chr_bytes


@dataclass(frozen=True, slots=True)
class TrackDescriptorMatch:
    track_index: int
    descriptor: int
    bone_names: list[str]


@dataclass(frozen=True, slots=True)
class TrackMapReport:
    anm2_path: str
    chr_path: str
    frame_count: int
    track_count: int
    matched_count: int
    unmatched_count: int
    matches: list[TrackDescriptorMatch]

    def to_json(self) -> str:
        return json.dumps(asdict(self), indent=2)


def dl_name_hash(name: str) -> int:
    """Engine hash used for animation descriptors and pose element names."""

    if not str(name).isascii():
        raise ValueError(
            f"Bone name {name!r} is non-ASCII. Chrome's implicit descriptor hash is "
            "ASCII-oriented; provide an explicit descriptor in a custom .crig."
        )
    value = 0
    for byte in name.lower().encode("ascii"):
        value = (byte + 41 * value) & 0xFFFFFFFF
    return value


def read_track_descriptors(path: str | Path) -> tuple[anm2.Anm2Header, list[int]]:
    data = Path(path).read_bytes()
    from .dl2_anm2 import detect_anm2_format
    detected = detect_anm2_format(data)
    if detected == 42:
        raise ValueError(
            "Dying Light 2 format-42 descriptors use the DL2 inspection path and cannot be "
            "read as a DL1 format-1 track table."
        )
    header = anm2.Anm2Header.parse(data)
    descriptors = list(struct.unpack_from(f"<{header.track_count}I", data, anm2.HEADER_LENGTH))
    return header, descriptors


def build_chr_hash_lookup(data: bytes, chr_name: str) -> dict[int, list[str]]:
    skeleton = read_chr_bytes(data, chr_name)
    lookup: dict[int, list[str]] = {}
    for bone in skeleton.bones:
        lookup.setdefault(dl_name_hash(bone.name), []).append(bone.name)
    return lookup


def trackmap_for_chr_bytes(anm2_path: str | Path, chr_data: bytes, chr_name: str) -> TrackMapReport:
    header, descriptors = read_track_descriptors(anm2_path)
    lookup = build_chr_hash_lookup(chr_data, chr_name)
    matches = [
        TrackDescriptorMatch(index, descriptor, lookup.get(descriptor, []))
        for index, descriptor in enumerate(descriptors)
    ]
    matched = sum(1 for item in matches if item.bone_names)
    return TrackMapReport(
        anm2_path=str(anm2_path),
        chr_path=chr_name,
        frame_count=header.frame_count,
        track_count=header.track_count,
        matched_count=matched,
        unmatched_count=len(matches) - matched,
        matches=matches,
    )


def trackmap_for_pak_chr(anm2_path: str | Path, pak_path: str | Path, chr_path: str) -> TrackMapReport:
    with zipfile.ZipFile(pak_path) as archive:
        by_lower = {name.lower(): name for name in archive.namelist()}
        actual = by_lower.get(chr_path.lower())
        if actual is None:
            raise FileNotFoundError(f"{chr_path} not found in {pak_path}")
        return trackmap_for_chr_bytes(anm2_path, archive.read(actual), f"{pak_path}!{actual}")
