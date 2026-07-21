from __future__ import annotations

"""Explicit source/target skeletal-root mapping shared by UI and builders.

Dying Light's stock male target contains a bone named ``bip01``, but custom SMD
and CRIG targets do not have to use that literal name.  The UI stores one
per-animation mapping with two independent choices:

``source_bone``
    FBX bone whose motion should drive the target's skeletal root.  Empty means
    automatic: the humanoid Hips mapping, a conventional Hips/root name, then
    the hierarchy root with the largest descendant tree.

``target_bone``
    Target SMD/CRIG bone that receives skeletal-root motion.
    Empty means automatic: a real ``bip01``, then ``pelvis``, then the best
    descriptor-backed hierarchy root.

The setting is intentionally stored in ``ProjectAnimation.extensions`` so old
``.dlraproj`` files remain valid and forward-compatible.
"""

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Iterator, Mapping, Sequence


ROOT_MAPPING_EXTENSION_KEY = "root_mapping_v1"


def dl_name_hash(name: str) -> int:
    """Chrome descriptor hash used for target bone track lookup."""

    if not str(name).isascii():
        raise ValueError(
            f"Bone name {name!r} is non-ASCII and requires an explicit .crig descriptor."
        )
    value = 0
    for byte in str(name).lower().encode("ascii"):
        value = (byte + 41 * value) & 0xFFFFFFFF
    return value


@dataclass(frozen=True, slots=True)
class RootMappingSelection:
    source_bone: str = ""
    target_bone: str = ""

    @classmethod
    def from_animation(cls, animation: Any) -> "RootMappingSelection":
        extensions = dict(getattr(animation, "extensions", {}) or {})
        payload = extensions.get(ROOT_MAPPING_EXTENSION_KEY, {})
        if not isinstance(payload, Mapping):
            payload = {}
        return cls(
            source_bone=str(
                getattr(animation, "source_root_bone", "")
                or payload.get("source_bone", "")
                or ""
            ),
            target_bone=str(
                getattr(animation, "target_root_bone", "")
                or payload.get("target_bone", "")
                or ""
            ),
        )

    def store(self, animation: Any) -> None:
        extensions = dict(getattr(animation, "extensions", {}) or {})
        extensions[ROOT_MAPPING_EXTENSION_KEY] = {
            "source_bone": self.source_bone,
            "target_bone": self.target_bone,
        }
        animation.extensions = extensions
        if hasattr(animation, "source_root_bone"):
            animation.source_root_bone = self.source_bone
        if hasattr(animation, "target_root_bone"):
            animation.target_root_bone = self.target_bone


@dataclass(frozen=True, slots=True)
class SmdHierarchyBone:
    index: int
    name: str
    parent_index: int


@dataclass(frozen=True, slots=True)
class TargetRootResolution:
    bone_name: str
    descriptor: int
    track_index: int
    method: str
    literal_bip01_present: bool


class DescriptorNameAliasMap(dict[int, str]):
    """Descriptor-name mapping whose ``items`` also exposes name aliases.

    The legacy humanoid builder creates ``track_index_by_name`` from
    ``names_by_descriptor.items()`` but later also uses normal descriptor lookup.
    Yielding an extra ``(descriptor, 'bip01')`` pair lets a custom target bone act
    as the Bip01/root track without renaming the SMD bone or losing its real name.
    """

    def __init__(self, source: Mapping[int, str], aliases: Mapping[str, int]) -> None:
        super().__init__((int(key), str(value)) for key, value in source.items())
        self._aliases = {str(name): int(descriptor) for name, descriptor in aliases.items()}

    def items(self) -> Iterator[tuple[int, str]]:  # type: ignore[override]
        yield from super().items()
        for alias, descriptor in self._aliases.items():
            yield descriptor, alias


# ---------------------------------------------------------------------------
# SMD and hierarchy helpers


def read_smd_hierarchy(path: str | Path) -> tuple[SmdHierarchyBone, ...]:
    source = Path(path)
    try:
        text = source.read_text(encoding="utf-8-sig")
    except UnicodeDecodeError:
        text = source.read_text(encoding="latin-1")
    rows: list[SmdHierarchyBone] = []
    in_nodes = False
    for line_number, raw in enumerate(text.splitlines(), start=1):
        line = raw.strip()
        if line == "nodes":
            in_nodes = True
            continue
        if in_nodes and line == "end":
            break
        if not in_nodes or not line:
            continue
        try:
            index_text, remainder = line.split(None, 1)
            first_quote = remainder.index('"')
            second_quote = remainder.index('"', first_quote + 1)
            name = remainder[first_quote + 1 : second_quote]
            parent_text = remainder[second_quote + 1 :].strip()
            rows.append(SmdHierarchyBone(int(index_text), name, int(parent_text)))
        except (ValueError, IndexError) as exc:
            raise ValueError(
                f"Could not parse SMD node declaration at {source}:{line_number}: {raw!r}"
            ) from exc
    if not rows:
        raise ValueError(f"SMD does not contain a nodes section: {source}")
    indices = {row.index for row in rows}
    for row in rows:
        if row.parent_index >= 0 and row.parent_index not in indices:
            raise ValueError(
                f"SMD bone {row.name!r} references missing parent index {row.parent_index}: {source}"
            )
    return tuple(rows)


def parent_names_from_smd(rows: Sequence[SmdHierarchyBone]) -> dict[str, str | None]:
    by_index = {row.index: row.name for row in rows}
    return {
        row.name: (by_index.get(row.parent_index) if row.parent_index >= 0 else None)
        for row in rows
    }


def descendant_counts(names: Iterable[str], parents: Mapping[str, str | None]) -> dict[str, int]:
    names = tuple(str(value) for value in names)
    result = {name: 0 for name in names}
    for name in names:
        seen: set[str] = set()
        cursor = parents.get(name)
        while cursor in result and cursor not in seen:
            result[str(cursor)] += 1
            seen.add(str(cursor))
            cursor = parents.get(str(cursor))
    return result


def _normalized(value: str) -> str:
    return "".join(ch for ch in value.casefold() if ch.isalnum())


def _preferred_name(
    names: Iterable[str],
    preferences: Sequence[str],
) -> str | None:
    rows = tuple(str(value) for value in names)
    by_normal = {_normalized(name): name for name in rows}
    for candidate in preferences:
        found = by_normal.get(_normalized(candidate))
        if found is not None:
            return found
    return None


def choose_hierarchy_root(
    names: Iterable[str],
    parents: Mapping[str, str | None],
    *,
    allowed: Iterable[str] | None = None,
) -> str:
    all_names = tuple(str(value) for value in names)
    allowed_set = set(all_names if allowed is None else (str(value) for value in allowed))
    candidates = [name for name in all_names if name in allowed_set]
    if not candidates:
        raise ValueError("No usable bones are available for automatic root selection.")

    preferred = _preferred_name(
        candidates,
        ("bip01", "pelvis", "hips", "root", "rootbone", "armature"),
    )
    if preferred is not None:
        return preferred

    counts = descendant_counts(all_names, parents)
    roots = [name for name in candidates if parents.get(name) not in set(all_names)]
    pool = roots or candidates

    def penalty(name: str) -> int:
        normal = _normalized(name)
        return int(any(token in normal for token in ("iktarget", "helper", "shadowcaster", "mesh", "model")))

    scores = {
        name: (counts.get(name, 0), -penalty(name))
        for name in pool
    }
    best_score = max(scores.values())
    winners = [name for name in pool if scores[name] == best_score]
    if len(winners) != 1:
        available_roots = roots or pool
        raise ValueError(
            "Automatic root selection is ambiguous: equally suitable candidates are "
            + ", ".join(repr(name) for name in winners)
            + ". Available hierarchy roots: "
            + ", ".join(repr(name) for name in available_roots)
            + ". Choose the intended Source root/Target root explicitly in "
            "Animations > Root & .crig Mapping; no first-bone fallback was used."
        )
    return winners[0]


# ---------------------------------------------------------------------------
# Resolution


def resolve_target_smd_root(
    smd_path: str | Path,
    descriptors: Sequence[int],
    *,
    requested_bone: str = "",
) -> TargetRootResolution:
    rows = read_smd_hierarchy(smd_path)
    names = [row.name for row in rows]
    parents = parent_names_from_smd(rows)
    descriptor_to_index = {int(value): index for index, value in enumerate(descriptors)}
    trackable = [name for name in names if dl_name_hash(name) in descriptor_to_index]

    if requested_bone:
        if requested_bone not in names:
            raise ValueError(
                f"Selected target skeletal root {requested_bone!r} is not present in target SMD "
                f"{Path(smd_path).name}. Choose another target root in Animations > Root & .crig Mapping."
            )
        descriptor = dl_name_hash(requested_bone)
        if descriptor not in descriptor_to_index:
            raise ValueError(
                f"Selected target skeletal root {requested_bone!r} exists in target SMD, but its "
                f"descriptor 0x{descriptor:08X} is absent from the selected target ANM2 template. "
                "Use a template for this skeleton or choose a descriptor-backed target bone."
            )
        return TargetRootResolution(
            requested_bone,
            descriptor,
            descriptor_to_index[descriptor],
            "manual",
            "bip01" in names,
        )

    if not trackable:
        raise ValueError(
            f"Target SMD {Path(smd_path).name} has no bones whose descriptor exists in the target "
            "ANM2 template. The retargeter cannot choose a skeletal-root track automatically."
        )
    chosen = choose_hierarchy_root(names, parents, allowed=trackable)
    descriptor = dl_name_hash(chosen)
    method = "literal_bip01" if _normalized(chosen) == "bip01" else "automatic_fallback"
    return TargetRootResolution(
        chosen,
        descriptor,
        descriptor_to_index[descriptor],
        method,
        any(_normalized(name) == "bip01" for name in names),
    )


def resolve_source_root(
    source_names: Iterable[str],
    parents: Mapping[str, str | None],
    *,
    requested_bone: str = "",
    humanoid_aliases: Mapping[str, str] | None = None,
) -> tuple[str, str]:
    names = tuple(str(value) for value in source_names)
    if requested_bone:
        if requested_bone not in names:
            raise ValueError(
                f"Selected source root bone {requested_bone!r} is not present in the animation "
                "FBX. Re-open Animations > Root & .crig Mapping and choose an existing source bone."
            )
        return requested_bone, "manual"

    aliases = dict(humanoid_aliases or {})
    mapped_hips = aliases.get("mixamorig:Hips")
    if mapped_hips in names:
        return str(mapped_hips), "mapped_humanoid_hips"

    preferred = _preferred_name(
        names,
        ("mixamorig:Hips", "Hips", "pelvis", "bip01", "root", "rootbone", "armature"),
    )
    if preferred is not None:
        return preferred, "conventional_name"
    return choose_hierarchy_root(names, parents), "hierarchy_root"


def root_mapping_summary(selection: RootMappingSelection) -> str:
    source = selection.source_bone or "Automatic"
    target = selection.target_bone or "Automatic"
    return f"Skeletal-root mapping — source: {source}; target: {target}"


__all__ = [
    "DescriptorNameAliasMap",
    "ROOT_MAPPING_EXTENSION_KEY",
    "RootMappingSelection",
    "SmdHierarchyBone",
    "TargetRootResolution",
    "choose_hierarchy_root",
    "descendant_counts",
    "dl_name_hash",
    "parent_names_from_smd",
    "read_smd_hierarchy",
    "resolve_source_root",
    "resolve_target_smd_root",
    "root_mapping_summary",
]
