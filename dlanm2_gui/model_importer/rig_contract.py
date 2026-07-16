from __future__ import annotations

"""Immutable contract for the hierarchy authored into a Chrome source MSH.

The contract is created from the exact in-memory ``SourceMsh`` immediately
before serialization.  MSH reporting, generated Chrome Rigs and later
animation-target selection can therefore fingerprint one bind owner instead of
independently reconstructing similar-looking skeletons.
"""

from dataclasses import asdict, dataclass, replace
from typing import Any, Mapping, Sequence
import hashlib
import json
import math
import unicodedata

import numpy as np

from ..trackmap import dl_name_hash
from .vendor.chrome_mesh_tools.math3d import matrix4_from_matrix3x4
from .vendor.chrome_mesh_tools.writer import MSH_NODE_FLAG_ANIMATED, SourceMsh


AUTHORED_RIG_CONTRACT_FORMAT = "dl-reanimated-authored-rig-contract"
AUTHORED_RIG_CONTRACT_SCHEMA_VERSION = 1

NODE_TYPE_NAMES = {
    1: "MESH",
    2: "MESH_VBLEND",
    4: "HELPER",
    8: "BONE",
}
ANIMATION_NODE_TYPES = frozenset({"BONE", "HELPER"})


def _normalized_name(value: str) -> str:
    return unicodedata.normalize("NFKC", str(value)).casefold()


def _matrix16(matrix: np.ndarray) -> tuple[float, ...]:
    value = np.asarray(matrix, dtype=float)
    if value.shape != (4, 4):
        raise ValueError("Authored rig matrices must be 4x4.")
    return tuple(float(item) for item in value.reshape(16))


def _matrix4(values: Sequence[float]) -> np.ndarray:
    if len(values) != 16:
        raise ValueError("Authored rig global matrices must contain 16 values.")
    return np.asarray(values, dtype=float).reshape((4, 4))


def _json_bytes(value: Any) -> bytes:
    return json.dumps(
        value,
        sort_keys=True,
        ensure_ascii=False,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")


def _sha256(value: Any) -> str:
    return hashlib.sha256(_json_bytes(value)).hexdigest()


def _semantic_role(name: str) -> str:
    try:
        from ..retarget_mapping import canonical_humanoid_role

        return str(canonical_humanoid_role(name) or "")
    except Exception:
        return ""


@dataclass(frozen=True, slots=True)
class AuthoredRigNode:
    physical_index: int
    name: str
    normalized_name: str
    parent_physical_index: int
    node_type: str
    local_matrix3x4: tuple[float, ...]
    global_matrix4x4: tuple[float, ...]
    inverse_global_reference3x4: tuple[float, ...]
    descriptor: int | None
    animated: bool
    deform: bool
    helper: bool
    semantic_role: str = ""
    aliases: tuple[str, ...] = ()

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "AuthoredRigNode":
        return cls(
            physical_index=int(payload["physical_index"]),
            name=str(payload["name"]),
            normalized_name=str(payload.get("normalized_name") or _normalized_name(str(payload["name"]))),
            parent_physical_index=int(payload.get("parent_physical_index", -1)),
            node_type=str(payload.get("node_type", "MESH")),
            local_matrix3x4=tuple(float(value) for value in payload["local_matrix3x4"]),
            global_matrix4x4=tuple(float(value) for value in payload["global_matrix4x4"]),
            inverse_global_reference3x4=tuple(
                float(value) for value in payload["inverse_global_reference3x4"]
            ),
            descriptor=(
                int(payload["descriptor"])
                if payload.get("descriptor") is not None
                else None
            ),
            animated=bool(payload.get("animated", False)),
            deform=bool(payload.get("deform", False)),
            helper=bool(payload.get("helper", False)),
            semantic_role=str(payload.get("semantic_role", "")),
            aliases=tuple(str(value) for value in payload.get("aliases", ())),
        )


def _identity_hashes(
    nodes: Sequence[AuthoredRigNode],
) -> tuple[str, str, str]:
    skeleton_payload = [
        {
            "physical_index": row.physical_index,
            "name": row.name,
            "normalized_name": row.normalized_name,
            "parent": row.parent_physical_index,
            "node_type": row.node_type,
            "deform": row.deform,
            "helper": row.helper,
            "aliases": list(row.aliases),
        }
        for row in nodes
        if row.node_type in ANIMATION_NODE_TYPES
    ]
    bind_payload = [
        {
            "physical_index": row.physical_index,
            "local": list(row.local_matrix3x4),
            "global": list(row.global_matrix4x4),
            "reference": list(row.inverse_global_reference3x4),
        }
        for row in nodes
    ]
    descriptor_payload = [
        {"physical_index": row.physical_index, "descriptor": row.descriptor}
        for row in nodes
        if row.descriptor is not None
    ]
    return (
        _sha256(bind_payload),
        _sha256(skeleton_payload),
        _sha256(descriptor_payload),
    )


def _composite_contract_id(
    bind_hash: str,
    skeleton_hash: str,
    descriptor_hash: str,
) -> str:
    digest = _sha256(
        {
            "bind_hash": str(bind_hash),
            "skeleton_hash": str(skeleton_hash),
            "descriptor_hash": str(descriptor_hash),
        }
    )
    return f"authored:{digest[:24]}"


def _legacy_bind_only_contract_id(bind_hash: str) -> str:
    return f"authored:{str(bind_hash)[:24]}"


@dataclass(frozen=True, slots=True)
class AuthoredRigContract:
    contract_id: str
    source_fbx_sha256: str
    source_model_name: str
    authored_msh_resource_name: str
    coordinate_contract: dict[str, Any]
    nodes: tuple[AuthoredRigNode, ...]
    roots: tuple[int, ...]
    primary_root: int
    animation_entity_prefix_length: int
    deform_node_indexes: tuple[int, ...]
    helper_node_indexes: tuple[int, ...]
    mesh_node_indexes: tuple[int, ...]
    bind_hash: str
    skeleton_hash: str
    descriptor_hash: str
    generated_crig_ref: str = ""
    generated_crig_path: str = ""
    format: str = AUTHORED_RIG_CONTRACT_FORMAT
    schema_version: int = AUTHORED_RIG_CONTRACT_SCHEMA_VERSION

    @property
    def animation_nodes(self) -> tuple[AuthoredRigNode, ...]:
        return tuple(row for row in self.nodes if row.node_type in ANIMATION_NODE_TYPES)

    def with_generated_crig(self, *, rig_ref: str, path: str) -> "AuthoredRigContract":
        return replace(
            self,
            generated_crig_ref=str(rig_ref),
            generated_crig_path=str(path),
        )

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "AuthoredRigContract":
        if payload.get("format", AUTHORED_RIG_CONTRACT_FORMAT) != AUTHORED_RIG_CONTRACT_FORMAT:
            raise ValueError("Not a DL ReAnimated authored-rig contract.")
        if int(payload.get("schema_version", 0)) != AUTHORED_RIG_CONTRACT_SCHEMA_VERSION:
            raise ValueError("Unsupported authored-rig contract schema version.")
        nodes = tuple(
            AuthoredRigNode.from_dict(row) for row in payload.get("nodes", ())
        )
        computed_bind, computed_skeleton, computed_descriptor = _identity_hashes(
            nodes
        )
        bind_hash = str(payload.get("bind_hash", "") or computed_bind)
        skeleton_hash = str(payload.get("skeleton_hash", "") or computed_skeleton)
        descriptor_hash = str(
            payload.get("descriptor_hash", "") or computed_descriptor
        )
        contract_id = str(
            payload.get("contract_id", "")
            or _composite_contract_id(
                bind_hash,
                skeleton_hash,
                descriptor_hash,
            )
        )
        result = cls(
            contract_id=contract_id,
            source_fbx_sha256=str(payload.get("source_fbx_sha256", "")),
            source_model_name=str(payload.get("source_model_name", "")),
            authored_msh_resource_name=str(payload.get("authored_msh_resource_name", "")),
            coordinate_contract=dict(payload.get("coordinate_contract", {}) or {}),
            nodes=nodes,
            roots=tuple(int(value) for value in payload.get("roots", ())),
            primary_root=int(payload.get("primary_root", -1)),
            animation_entity_prefix_length=int(
                payload.get("animation_entity_prefix_length", 0)
            ),
            deform_node_indexes=tuple(
                int(value) for value in payload.get("deform_node_indexes", ())
            ),
            helper_node_indexes=tuple(
                int(value) for value in payload.get("helper_node_indexes", ())
            ),
            mesh_node_indexes=tuple(
                int(value) for value in payload.get("mesh_node_indexes", ())
            ),
            bind_hash=bind_hash,
            skeleton_hash=skeleton_hash,
            descriptor_hash=descriptor_hash,
            generated_crig_ref=str(payload.get("generated_crig_ref", "")),
            generated_crig_path=str(payload.get("generated_crig_path", "")),
        )
        result.validate()
        return result

    @classmethod
    def from_source_msh(
        cls,
        source: SourceMsh,
        *,
        source_fbx_sha256: str,
        source_model_name: str,
        authored_msh_resource_name: str,
        coordinate_contract: Mapping[str, Any],
        aliases_by_name: Mapping[str, Sequence[str]] | None = None,
    ) -> "AuthoredRigContract":
        source.validate()
        alias_rows = {
            _normalized_name(str(name)): tuple(
                str(value) for value in values if str(value).strip()
            )
            for name, values in dict(aliases_by_name or {}).items()
        }
        globals_by_index: list[np.ndarray] = []
        nodes: list[AuthoredRigNode] = []
        for physical_index, source_node in enumerate(source.nodes):
            local = np.asarray(matrix4_from_matrix3x4(source_node.local_matrix), dtype=float)
            parent = int(source_node.parent_index)
            global_matrix = (
                globals_by_index[parent] @ local if parent >= 0 else local.copy()
            )
            globals_by_index.append(global_matrix)
            node_type = NODE_TYPE_NAMES.get(int(source_node.node_type), f"UNKNOWN_{int(source_node.node_type)}")
            animated = bool(
                node_type in ANIMATION_NODE_TYPES
                or int(source_node.tail_words[0]) & int(MSH_NODE_FLAG_ANIMATED)
            )
            name = str(source_node.name)
            nodes.append(
                AuthoredRigNode(
                    physical_index=physical_index,
                    name=name,
                    normalized_name=_normalized_name(name),
                    parent_physical_index=parent,
                    node_type=node_type,
                    local_matrix3x4=tuple(float(value) for value in source_node.local_matrix),
                    global_matrix4x4=_matrix16(global_matrix),
                    inverse_global_reference3x4=tuple(
                        float(value) for value in source_node.reference_matrix
                    ),
                    descriptor=(dl_name_hash(name) if animated else None),
                    animated=animated,
                    deform=node_type == "BONE",
                    helper=node_type == "HELPER",
                    semantic_role=_semantic_role(name),
                    aliases=alias_rows.get(_normalized_name(name), ()),
                )
            )

        roots = tuple(row.physical_index for row in nodes if row.parent_physical_index < 0)
        animation_roots = [
            row.physical_index
            for row in nodes
            if row.node_type in ANIMATION_NODE_TYPES
            and (
                row.parent_physical_index < 0
                or nodes[row.parent_physical_index].node_type not in ANIMATION_NODE_TYPES
            )
        ]
        prefix_length = 0
        for row in nodes:
            if row.node_type not in ANIMATION_NODE_TYPES:
                break
            prefix_length += 1

        bind_hash, skeleton_hash, descriptor_hash = _identity_hashes(nodes)
        result = cls(
            contract_id=_composite_contract_id(
                bind_hash,
                skeleton_hash,
                descriptor_hash,
            ),
            source_fbx_sha256=str(source_fbx_sha256),
            source_model_name=str(source_model_name),
            authored_msh_resource_name=str(authored_msh_resource_name),
            coordinate_contract=dict(coordinate_contract),
            nodes=tuple(nodes),
            roots=roots,
            primary_root=(animation_roots[0] if animation_roots else roots[0] if roots else -1),
            animation_entity_prefix_length=prefix_length,
            deform_node_indexes=tuple(row.physical_index for row in nodes if row.deform),
            helper_node_indexes=tuple(row.physical_index for row in nodes if row.helper),
            mesh_node_indexes=tuple(
                row.physical_index
                for row in nodes
                if row.node_type in {"MESH", "MESH_VBLEND"}
            ),
            bind_hash=bind_hash,
            skeleton_hash=skeleton_hash,
            descriptor_hash=descriptor_hash,
        )
        result.validate()
        return result

    def validate(self, *, tolerance: float = 5.0e-5) -> dict[str, Any]:
        if not self.nodes:
            raise ValueError("Authored rig contract contains no source-MSH nodes.")
        if tuple(row.physical_index for row in self.nodes) != tuple(range(len(self.nodes))):
            raise ValueError("Authored rig physical node indexes must be contiguous and ordered.")
        if len(self.nodes) > 32_768:
            raise ValueError(
                f"Authored rig has {len(self.nodes)} nodes; source-MSH parent indexes are "
                "signed int16 and support at most 32768 physical nodes."
            )
        reconstructed: list[np.ndarray] = []
        maximum_reconstruction_error = 0.0
        maximum_reference_error = 0.0
        seen_animation_names: dict[str, str] = {}
        seen_descriptors: dict[int, str] = {}
        for row in self.nodes:
            if row.normalized_name != _normalized_name(row.name):
                raise ValueError(
                    f"Authored rig node {row.name!r} has stale normalized-name identity. "
                    "Rebuild the authored rig contract from its source MSH."
                )
            if row.parent_physical_index >= row.physical_index:
                raise ValueError(
                    f"Authored rig node {row.name!r} ({row.physical_index}) has parent "
                    f"{row.parent_physical_index}; parents must precede children."
                )
            if row.parent_physical_index < -1:
                raise ValueError(
                    f"Authored rig node {row.name!r} has invalid parent "
                    f"{row.parent_physical_index}."
                )
            local = np.asarray(matrix4_from_matrix3x4(row.local_matrix3x4), dtype=float)
            recorded_global = _matrix4(row.global_matrix4x4)
            reference = np.asarray(
                matrix4_from_matrix3x4(row.inverse_global_reference3x4), dtype=float
            )
            if not (
                np.isfinite(local).all()
                and np.isfinite(recorded_global).all()
                and np.isfinite(reference).all()
            ):
                raise ValueError(
                    f"Authored rig node {row.name!r} contains a non-finite local/global/reference matrix."
                )
            calculated = (
                reconstructed[row.parent_physical_index] @ local
                if row.parent_physical_index >= 0
                else local.copy()
            )
            reconstruction_error = float(np.max(np.abs(calculated - recorded_global)))
            maximum_reconstruction_error = max(
                maximum_reconstruction_error, reconstruction_error
            )
            if reconstruction_error > tolerance:
                raise ValueError(
                    f"Authored rig node {row.name!r} global bind does not reconstruct from "
                    f"its parent/local matrix (max error {reconstruction_error:.6g})."
                )
            determinant = float(np.linalg.det(recorded_global[:3, :3]))
            if not math.isfinite(determinant) or abs(determinant) <= 1.0e-12:
                raise ValueError(
                    f"Authored rig node {row.name!r} has a singular global bind matrix."
                )
            identity = recorded_global @ reference
            reference_error = float(np.max(np.abs(identity - np.eye(4, dtype=float))))
            maximum_reference_error = max(maximum_reference_error, reference_error)
            if reference_error > tolerance:
                raise ValueError(
                    f"Authored rig node {row.name!r} violates the Chrome inverse-global "
                    f"reference rule: global * reference max error {reference_error:.6g}."
                )
            reconstructed.append(calculated)
            if row.node_type in ANIMATION_NODE_TYPES:
                previous = seen_animation_names.get(row.normalized_name)
                if previous is not None and previous != row.name:
                    raise ValueError(
                        f"Authored animation entities {previous!r} and {row.name!r} collide "
                        "after Unicode NFKC/casefold normalization."
                    )
                seen_animation_names[row.normalized_name] = row.name
                if row.descriptor is None:
                    raise ValueError(
                        f"Authored animation entity {row.name!r} has no descriptor."
                    )
                previous_descriptor = seen_descriptors.get(int(row.descriptor))
                if previous_descriptor is not None and previous_descriptor != row.name:
                    raise ValueError(
                        f"Authored animation entities {previous_descriptor!r} and {row.name!r} "
                        f"collide at descriptor 0x{int(row.descriptor):08X}."
                    )
                seen_descriptors[int(row.descriptor)] = row.name
        bind_hash, skeleton_hash, descriptor_hash = _identity_hashes(self.nodes)
        for label, recorded, calculated in (
            ("bind", self.bind_hash, bind_hash),
            ("skeleton", self.skeleton_hash, skeleton_hash),
            ("descriptor", self.descriptor_hash, descriptor_hash),
        ):
            if recorded != calculated:
                raise ValueError(
                    f"Authored rig {label} identity hash is stale: recorded {recorded!r}, "
                    f"calculated {calculated!r}. Rebuild the contract from its source MSH."
                )
        composite_contract_id = _composite_contract_id(
            bind_hash,
            skeleton_hash,
            descriptor_hash,
        )
        legacy_contract_id = _legacy_bind_only_contract_id(bind_hash)
        if self.contract_id not in {composite_contract_id, legacy_contract_id}:
            raise ValueError(
                f"Authored rig contract ID {self.contract_id!r} does not match its bind, "
                "skeleton, and descriptor identity. Rebuild the contract from its source MSH."
            )
        return {
            "status": "pass",
            "node_count": len(self.nodes),
            "animation_entity_count": len(self.animation_nodes),
            "maximum_global_reconstruction_error": maximum_reconstruction_error,
            "maximum_inverse_global_reference_error": maximum_reference_error,
            "contract_identity_scheme": (
                "bind_skeleton_descriptor_v2"
                if self.contract_id == composite_contract_id
                else "legacy_bind_only_v1"
            ),
            "canonical_contract_id": composite_contract_id,
            "tolerance": tolerance,
        }


__all__ = [
    "ANIMATION_NODE_TYPES",
    "AUTHORED_RIG_CONTRACT_FORMAT",
    "AUTHORED_RIG_CONTRACT_SCHEMA_VERSION",
    "AuthoredRigContract",
    "AuthoredRigNode",
]
