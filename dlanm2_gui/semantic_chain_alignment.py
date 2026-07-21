"""Deterministic source/target semantic-chain alignment primitives.

The source-skeleton analyzer deliberately does not live in this module.  This
keeps the alignment policy usable by the production analyzer, synthetic tests,
and reviewed mapping recipes without importing any particular FBX parser or
name classifier.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable, Sequence


CHAIN_MAPPING_MODES = frozenset(
    {
        "direct",
        "composed",
        "distributed",
        "inherit_bind",
        "static_bind",
        "manual_required",
    }
)


@dataclass(frozen=True, slots=True)
class SemanticChainNode:
    """One ordered joint in a semantic chain.

    ``parent`` is optional because callers may already have sliced a chain out
    of a larger skeleton.  When supplied for a node after the first, it must
    name the preceding node; this prevents an unordered candidate list from
    being treated as a safe anatomical chain.
    """

    name: str
    semantic_role: str = ""
    side: str = ""
    parent: str | None = None
    optional: bool = False
    static: bool = False


@dataclass(frozen=True, slots=True)
class ChainAlignmentDecision:
    """Deterministic disposition of one target-chain node."""

    target_bone: str
    mode: str
    source_bones: tuple[str, ...]
    semantic_role: str
    side: str
    confidence: float
    confidence_margin: float
    source_weights: tuple[float, ...]
    reason: str

    def __post_init__(self) -> None:
        if self.mode not in CHAIN_MAPPING_MODES:
            raise ValueError(f"Unsupported semantic-chain mapping mode {self.mode!r}")
        if self.source_weights and len(self.source_weights) != len(self.source_bones):
            raise ValueError("source_weights must correspond one-to-one with source_bones")


def _canonical_side(value: str) -> str:
    normalized = str(value or "").strip().casefold()
    aliases = {
        "l": "left",
        "left": "left",
        "r": "right",
        "right": "right",
        "c": "center",
        "centre": "center",
        "center": "center",
        "mid": "center",
        "middle": "center",
        "": "",
    }
    return aliases.get(normalized, normalized)


def _coerce_nodes(
    values: Sequence[SemanticChainNode | str] | Iterable[SemanticChainNode | str],
    *,
    default_side: str,
) -> tuple[SemanticChainNode, ...]:
    side = _canonical_side(default_side)
    rows: list[SemanticChainNode] = []
    for value in values:
        if isinstance(value, SemanticChainNode):
            node_side = _canonical_side(value.side) or side
            rows.append(
                SemanticChainNode(
                    str(value.name),
                    str(value.semantic_role),
                    node_side,
                    None if value.parent is None else str(value.parent),
                    bool(value.optional),
                    bool(value.static),
                )
            )
        else:
            name = str(value)
            rows.append(SemanticChainNode(name, name, side))
    return tuple(rows)


def _chain_problem(nodes: tuple[SemanticChainNode, ...]) -> str:
    names = [row.name for row in nodes]
    if any(not name for name in names):
        return "chain contains an empty bone name"
    if len(set(names)) != len(names):
        return "chain contains a duplicate bone"
    explicit_sides = {
        _canonical_side(row.side)
        for row in nodes
        if _canonical_side(row.side) not in {"", "center"}
    }
    if len(explicit_sides) > 1:
        return "chain contains conflicting left/right declarations"
    for index, row in enumerate(nodes[1:], start=1):
        if row.parent is not None and row.parent != nodes[index - 1].name:
            return (
                f"chain order is not parent-consistent at {row.name!r}: "
                f"expected parent {nodes[index - 1].name!r}, got {row.parent!r}"
            )
    return ""


def _manual_decisions(
    targets: tuple[SemanticChainNode, ...],
    *,
    confidence: float,
    confidence_margin: float,
    reason: str,
) -> tuple[ChainAlignmentDecision, ...]:
    return tuple(
        ChainAlignmentDecision(
            target_bone=row.name,
            mode="static_bind" if row.static else "manual_required",
            source_bones=(),
            semantic_role=row.semantic_role,
            side=_canonical_side(row.side),
            confidence=float(confidence),
            confidence_margin=float(confidence_margin),
            source_weights=(),
            reason=("target is declared static" if row.static else reason),
        )
        for row in targets
    )


def _partition(items: Sequence[SemanticChainNode], group_count: int) -> list[tuple[SemanticChainNode, ...]]:
    """Split ordered values into stable, non-empty, nearly-even groups."""

    if group_count <= 0 or group_count > len(items):
        raise ValueError("group_count must be between one and the item count")
    quotient, remainder = divmod(len(items), group_count)
    groups: list[tuple[SemanticChainNode, ...]] = []
    cursor = 0
    for index in range(group_count):
        size = quotient + (1 if index < remainder else 0)
        groups.append(tuple(items[cursor : cursor + size]))
        cursor += size
    return groups


def align_semantic_chains(
    source_chain: Sequence[SemanticChainNode | str] | Iterable[SemanticChainNode | str],
    target_chain: Sequence[SemanticChainNode | str] | Iterable[SemanticChainNode | str],
    *,
    source_side: str = "",
    target_side: str = "",
    confidence: float = 1.0,
    confidence_margin: float = 1.0,
    minimum_confidence: float = 0.65,
    minimum_margin: float = 0.10,
) -> tuple[ChainAlignmentDecision, ...]:
    """Align two already-identified anatomical chains without spatial guessing.

    The input order is authoritative topology.  Unique semantic-role matches
    become anchors first.  Remaining ordered segments are mapped directly,
    composed when the source is longer, or distributed when the target is
    longer.  Optional missing target nodes use bind-local inheritance.  A side,
    topology, duplicate-role, or confidence ambiguity fails closed with
    ``manual_required`` decisions.
    """

    sources = _coerce_nodes(source_chain, default_side=source_side)
    targets = _coerce_nodes(target_chain, default_side=target_side)
    if not targets:
        return ()

    source_problem = _chain_problem(sources)
    target_problem = _chain_problem(targets)
    source_declared_side = _canonical_side(source_side) or next(
        (_canonical_side(row.side) for row in sources if _canonical_side(row.side)),
        "",
    )
    target_declared_side = _canonical_side(target_side) or next(
        (_canonical_side(row.side) for row in targets if _canonical_side(row.side)),
        "",
    )
    side_conflict = (
        source_declared_side not in {"", "center"}
        and target_declared_side not in {"", "center"}
        and source_declared_side != target_declared_side
    )
    if source_problem or target_problem or side_conflict:
        reason = source_problem or target_problem or (
            f"source side {source_declared_side!r} conflicts with target side "
            f"{target_declared_side!r}"
        )
        return _manual_decisions(
            targets,
            confidence=confidence,
            confidence_margin=confidence_margin,
            reason=reason,
        )
    if confidence < minimum_confidence or confidence_margin < minimum_margin:
        return _manual_decisions(
            targets,
            confidence=confidence,
            confidence_margin=confidence_margin,
            reason=(
                "semantic-chain candidate does not meet the minimum confidence and "
                "runner-up margin"
            ),
        )

    decisions: dict[str, ChainAlignmentDecision] = {}
    for target in targets:
        if target.static:
            decisions[target.name] = ChainAlignmentDecision(
                target.name,
                "static_bind",
                (),
                target.semantic_role,
                _canonical_side(target.side),
                confidence,
                confidence_margin,
                (),
                "target is declared static",
            )

    active_targets = [row for row in targets if not row.static]
    if not sources:
        for target in active_targets:
            decisions[target.name] = ChainAlignmentDecision(
                target.name,
                "inherit_bind",
                (),
                target.semantic_role,
                _canonical_side(target.side),
                confidence,
                confidence_margin,
                (),
                "source chain is absent; retain target bind-local transform",
            )
        return tuple(decisions[row.name] for row in targets)

    source_roles: dict[str, list[int]] = {}
    target_roles: dict[str, list[int]] = {}
    for index, row in enumerate(sources):
        if row.semantic_role:
            source_roles.setdefault(row.semantic_role, []).append(index)
    for index, row in enumerate(active_targets):
        if row.semantic_role:
            target_roles.setdefault(row.semantic_role, []).append(index)

    ambiguous_roles = {
        role
        for role in source_roles.keys() & target_roles.keys()
        if len(source_roles[role]) != 1 or len(target_roles[role]) != 1
    }
    if ambiguous_roles:
        return _manual_decisions(
            targets,
            confidence=confidence,
            confidence_margin=confidence_margin,
            reason="semantic roles are duplicated within the chain: "
            + ", ".join(sorted(ambiguous_roles)),
        )

    anchors: list[tuple[int, int]] = sorted(
        (
            source_roles[role][0],
            target_roles[role][0],
        )
        for role in source_roles.keys() & target_roles.keys()
    )
    if any(
        left[1] >= right[1]
        for left, right in zip(anchors, anchors[1:])
    ):
        return _manual_decisions(
            targets,
            confidence=confidence,
            confidence_margin=confidence_margin,
            reason="semantic anchors would cross and violate chain order",
        )

    used_source: set[int] = set()
    anchored_target: set[int] = set()
    for source_index, target_index in anchors:
        source = sources[source_index]
        target = active_targets[target_index]
        used_source.add(source_index)
        anchored_target.add(target_index)
        decisions[target.name] = ChainAlignmentDecision(
            target.name,
            "direct",
            (source.name,),
            target.semantic_role,
            _canonical_side(target.side),
            confidence,
            confidence_margin,
            (1.0,),
            "unique semantic role and ordered topology agree",
        )

    remaining_sources = [
        row for index, row in enumerate(sources) if index not in used_source
    ]
    remaining_targets = [
        row for index, row in enumerate(active_targets) if index not in anchored_target
    ]

    if len(remaining_sources) < len(remaining_targets):
        deficit = len(remaining_targets) - len(remaining_sources)
        # Missing optional/terminal targets are a normal bind-inheritance case.
        # Prefer the most terminal optional rows so thigh->calf can safely feed
        # a target foot/toe suffix without manufacturing a relationship.
        optional_to_inherit = {
            row.name
            for row in reversed(remaining_targets)
            if row.optional
            for _unused in (0,)
        }
        selected_optional: set[str] = set()
        for row in reversed(remaining_targets):
            if len(selected_optional) >= deficit:
                break
            if row.name in optional_to_inherit:
                selected_optional.add(row.name)
        for row in remaining_targets:
            if row.name in selected_optional:
                decisions[row.name] = ChainAlignmentDecision(
                    row.name,
                    "inherit_bind",
                    (),
                    row.semantic_role,
                    _canonical_side(row.side),
                    confidence,
                    confidence_margin,
                    (),
                    "optional target segment has no independent source; inherit bind-local",
                )
        remaining_targets = [
            row for row in remaining_targets if row.name not in selected_optional
        ]

    if remaining_sources and remaining_targets:
        if len(remaining_sources) == len(remaining_targets):
            for source, target in zip(remaining_sources, remaining_targets):
                decisions[target.name] = ChainAlignmentDecision(
                    target.name,
                    "direct",
                    (source.name,),
                    target.semantic_role,
                    _canonical_side(target.side),
                    confidence,
                    confidence_margin,
                    (1.0,),
                    "ordered one-to-one chain segment",
                )
        elif len(remaining_sources) > len(remaining_targets):
            for source_group, target in zip(
                _partition(remaining_sources, len(remaining_targets)),
                remaining_targets,
            ):
                mode = "direct" if len(source_group) == 1 else "composed"
                weights = tuple(1.0 for _row in source_group)
                decisions[target.name] = ChainAlignmentDecision(
                    target.name,
                    mode,
                    tuple(row.name for row in source_group),
                    target.semantic_role,
                    _canonical_side(target.side),
                    confidence,
                    confidence_margin,
                    weights,
                    (
                        "ordered one-to-one chain segment"
                        if mode == "direct"
                        else "compose the ordered source segment between semantic anchors"
                    ),
                )
        else:
            for source, target_group in zip(
                remaining_sources,
                _partition(remaining_targets, len(remaining_sources)),
            ):
                mode = "direct" if len(target_group) == 1 else "distributed"
                weight = 1.0 / float(len(target_group))
                for target in target_group:
                    decisions[target.name] = ChainAlignmentDecision(
                        target.name,
                        mode,
                        (source.name,),
                        target.semantic_role,
                        _canonical_side(target.side),
                        confidence,
                        confidence_margin,
                        ((1.0,) if mode == "direct" else (weight,)),
                        (
                            "ordered one-to-one chain segment"
                            if mode == "direct"
                            else "distribute one source segment across the longer target chain"
                        ),
                    )
    elif remaining_targets:
        for target in remaining_targets:
            decisions[target.name] = ChainAlignmentDecision(
                target.name,
                "inherit_bind" if target.optional else "manual_required",
                (),
                target.semantic_role,
                _canonical_side(target.side),
                confidence,
                confidence_margin,
                (),
                (
                    "optional target segment has no independent source; inherit bind-local"
                    if target.optional
                    else "required target segment has no remaining source segment"
                ),
            )

    return tuple(decisions[row.name] for row in targets)


__all__ = [
    "CHAIN_MAPPING_MODES",
    "ChainAlignmentDecision",
    "SemanticChainNode",
    "align_semantic_chains",
]
