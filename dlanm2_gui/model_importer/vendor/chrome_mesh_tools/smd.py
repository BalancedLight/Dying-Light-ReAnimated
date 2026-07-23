from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

_NODE_RE = re.compile(r'^\s*(-?\d+)\s+"(.*)"\s+(-?\d+)\s*$')
_POSE_RE = re.compile(
    r"^\s*(-?\d+)\s+"
    r"([-+0-9.eE]+)\s+([-+0-9.eE]+)\s+([-+0-9.eE]+)\s+"
    r"([-+0-9.eE]+)\s+([-+0-9.eE]+)\s+([-+0-9.eE]+)\s*$"
)


@dataclass(frozen=True)
class SmdNode:
    index: int
    name: str
    parent_index: int


@dataclass(frozen=True)
class SmdPose:
    position: tuple[float, float, float]
    rotation_radians: tuple[float, float, float]


@dataclass(frozen=True)
class SmdFile:
    source_path: str | None
    version: int
    nodes: tuple[SmdNode, ...]
    poses_by_time: dict[int, dict[int, SmdPose]]

    @classmethod
    def parse(cls, text: str, source_path: str | None = None) -> "SmdFile":
        lines = text.splitlines()
        version = None
        nodes: list[SmdNode] = []
        poses_by_time: dict[int, dict[int, SmdPose]] = {}
        section = None
        current_time: int | None = None
        for line_number, raw_line in enumerate(lines, 1):
            line = raw_line.strip()
            if not line:
                continue
            if line.startswith("version "):
                version = int(line.split()[1])
                continue
            if line == "nodes":
                section = "nodes"
                continue
            if line == "skeleton":
                section = "skeleton"
                continue
            if line == "triangles":
                section = "triangles"
                continue
            if line == "end":
                section = None
                current_time = None
                continue
            if section == "nodes":
                match = _NODE_RE.match(raw_line)
                if not match:
                    raise ValueError(
                        f"{source_path or 'SMD'}:{line_number}: invalid node line {raw_line!r}"
                    )
                nodes.append(
                    SmdNode(int(match.group(1)), match.group(2), int(match.group(3)))
                )
            elif section == "skeleton":
                if line.startswith("time "):
                    current_time = int(line.split()[1])
                    poses_by_time.setdefault(current_time, {})
                    continue
                match = _POSE_RE.match(raw_line)
                if not match or current_time is None:
                    raise ValueError(
                        f"{source_path or 'SMD'}:{line_number}: invalid skeleton line {raw_line!r}"
                    )
                values = tuple(float(match.group(i)) for i in range(2, 8))
                poses_by_time[current_time][int(match.group(1))] = SmdPose(
                    (values[0], values[1], values[2]),
                    (values[3], values[4], values[5]),
                )
        if version is None:
            raise ValueError(f"{source_path or 'SMD'}: missing version line")
        nodes.sort(key=lambda node: node.index)
        return cls(source_path, version, tuple(nodes), poses_by_time)

    @classmethod
    def from_path(cls, path: str | Path) -> "SmdFile":
        p = Path(path)
        return cls.parse(p.read_text(encoding="utf-8-sig", errors="replace"), str(p))

    @property
    def root_nodes(self) -> tuple[SmdNode, ...]:
        return tuple(node for node in self.nodes if node.parent_index < 0)

    def node_by_name(self, name: str) -> SmdNode | None:
        lowered = name.casefold()
        return next((node for node in self.nodes if node.name.casefold() == lowered), None)

    def depth(self, node_index: int) -> int:
        by_index = {node.index: node for node in self.nodes}
        seen: set[int] = set()
        depth = 0
        current = by_index.get(node_index)
        while current is not None and current.parent_index >= 0:
            if current.index in seen:
                raise ValueError(f"SMD hierarchy cycle at node {current.index}")
            seen.add(current.index)
            depth += 1
            current = by_index.get(current.parent_index)
        return depth

    def to_dict(self) -> dict[str, Any]:
        bind = self.poses_by_time.get(0, {})
        return {
            "source_path": self.source_path,
            "version": self.version,
            "node_count": len(self.nodes),
            "root_count": len(self.root_nodes),
            "root_nodes": [node.name for node in self.root_nodes],
            "times": sorted(self.poses_by_time),
            "nodes": [
                {
                    "index": node.index,
                    "name": node.name,
                    "parent_index": node.parent_index,
                    "depth": self.depth(node.index),
                    "bind_pose": (
                        {
                            "position": list(bind[node.index].position),
                            "rotation_radians": list(bind[node.index].rotation_radians),
                        }
                        if node.index in bind
                        else None
                    ),
                }
                for node in self.nodes
            ],
        }

    def write_json(self, path: str | Path) -> None:
        Path(path).write_text(json.dumps(self.to_dict(), indent=2), encoding="utf-8")
