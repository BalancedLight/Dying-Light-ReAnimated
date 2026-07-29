"""Native ANM2/FBX round-trip metadata and companion-sidecar handling."""

from __future__ import annotations

import base64
import hashlib
import json
import os
from pathlib import Path
import tempfile
from typing import Any
import zlib

from .fbx_core import FbxDocument, _properties70


ROUNDTRIP_FORMAT = "dl-reanimated-fbx-roundtrip"
ROUNDTRIP_SCHEMA_VERSION = 1
ROUNDTRIP_GUARD_PREFIX = "DLR_RoundTripGuard_"


def roundtrip_sidecar_path(fbx_path: str | Path) -> Path:
    return Path(str(Path(fbx_path)) + ".dlrroundtrip.json")


def _canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def roundtrip_contract_id(contract: dict[str, Any]) -> str:
    """Return the canonical identity for a contract, excluding its identity fields."""

    payload = dict(contract)
    payload.pop("contract_id", None)
    payload.pop("guard_name", None)
    return hashlib.sha256(_canonical_bytes(payload)).hexdigest()


def finalize_roundtrip_contract(
    contract: dict[str, Any],
) -> dict[str, Any]:
    payload = dict(contract)
    identity = roundtrip_contract_id(payload)
    payload["contract_id"] = identity
    payload["guard_name"] = ROUNDTRIP_GUARD_PREFIX + identity[:24]
    return payload


def validate_roundtrip_contract_identity(contract: dict[str, Any]) -> str:
    identity = str(contract.get("contract_id", "") or "")
    expected = roundtrip_contract_id(contract)
    if len(identity) != 64 or identity.lower() != expected:
        raise ValueError("FBX round-trip contract identity is stale or malformed")
    guard_name = str(contract.get("guard_name", "") or "")
    if guard_name != ROUNDTRIP_GUARD_PREFIX + identity[:24]:
        raise ValueError("FBX round-trip guard name does not match its contract")
    return identity


def embedded_native_metadata(document: FbxDocument) -> dict[str, Any]:
    for object_id in getattr(document, "null_models", {}).values():
        node = document.object_by_id.get(object_id)
        if node is None:
            continue
        encoded = (
            _properties70(node).get("dlr_native_metadata_zlib_b64") or [""]
        )[0]
        if not encoded:
            continue
        try:
            decoded = zlib.decompress(base64.b64decode(str(encoded))).decode(
                "utf-8"
            )
            payload = json.loads(decoded)
        except (OSError, ValueError, TypeError, json.JSONDecodeError) as exc:
            raise ValueError(
                "FBX contains malformed DL ReAnimated native metadata"
            ) from exc
        if not isinstance(payload, dict):
            raise ValueError(
                "FBX DL ReAnimated native metadata must contain an object"
            )
        return payload
    return {}


def write_roundtrip_sidecar(
    fbx_path: str | Path,
    native_metadata: dict[str, Any],
) -> Path:
    contract = native_metadata.get("roundtrip_contract")
    if not isinstance(contract, dict) or not contract:
        raise ValueError(
            "Native FBX metadata does not contain a round-trip contract"
        )
    destination = roundtrip_sidecar_path(fbx_path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "format": ROUNDTRIP_FORMAT,
        "schema_version": ROUNDTRIP_SCHEMA_VERSION,
        "native_metadata": native_metadata,
    }
    handle, temporary = tempfile.mkstemp(
        prefix=destination.name + ".",
        suffix=".tmp",
        dir=destination.parent,
    )
    try:
        with os.fdopen(handle, "wb") as stream:
            stream.write(
                json.dumps(
                    payload,
                    ensure_ascii=False,
                    sort_keys=True,
                    indent=2,
                ).encode("utf-8")
                + b"\n"
            )
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, destination)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    return destination.resolve()


def load_roundtrip_sidecar(fbx_path: str | Path) -> dict[str, Any]:
    path = roundtrip_sidecar_path(fbx_path)
    if not path.is_file():
        return {}
    try:
        payload = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        raise ValueError(f"Round-trip sidecar is malformed: {path}") from exc
    if not isinstance(payload, dict):
        raise ValueError(f"Round-trip sidecar must contain an object: {path}")
    if payload.get("format") != ROUNDTRIP_FORMAT:
        raise ValueError(
            f"Unsupported round-trip sidecar format in {path}: "
            f"{payload.get('format')!r}"
        )
    if int(payload.get("schema_version", 0) or 0) != ROUNDTRIP_SCHEMA_VERSION:
        raise ValueError(
            f"Unsupported round-trip sidecar schema in {path}: "
            f"{payload.get('schema_version')!r}"
        )
    metadata = payload.get("native_metadata")
    if not isinstance(metadata, dict):
        raise ValueError(
            f"Round-trip sidecar has no native_metadata object: {path}"
        )
    contract = metadata.get("roundtrip_contract")
    if not isinstance(contract, dict) or not contract:
        raise ValueError(
            f"Round-trip sidecar has no canonical contract: {path}"
        )
    return metadata


def resolve_native_roundtrip_metadata(
    fbx_path: str | Path,
    document: FbxDocument,
) -> tuple[dict[str, Any], str]:
    """Resolve embedded metadata first, using the sidecar as a strict fallback."""

    embedded = embedded_native_metadata(document)
    sidecar = load_roundtrip_sidecar(fbx_path)
    if embedded and sidecar:
        if _canonical_bytes(embedded) != _canonical_bytes(sidecar):
            raise ValueError(
                "Embedded FBX round-trip metadata and the adjacent sidecar do "
                "not agree. Restore the matching .dlrroundtrip.json file."
            )
        return embedded, "embedded_and_sidecar"
    if embedded:
        return embedded, "embedded"
    if sidecar:
        return sidecar, "sidecar"
    return {}, ""


def detect_native_helper_roundtrip_target(
    fbx_path: str | Path,
    document: FbxDocument | None = None,
) -> dict[str, Any]:
    """Return validated helper-contract evidence suitable for route selection.

    With no document, only the adjacent canonical sidecar is inspected. GUI
    import passes its already parsed document so embedded-only metadata is also
    recognized without another FBX parse.
    """

    try:
        if document is None:
            metadata = load_roundtrip_sidecar(fbx_path)
            metadata_source = "sidecar" if metadata else ""
        else:
            metadata, metadata_source = resolve_native_roundtrip_metadata(
                fbx_path,
                document,
            )
        contract = metadata.get("roundtrip_contract", {})
        if not isinstance(contract, dict) or not contract:
            return {}
        contract_id = validate_roundtrip_contract_identity(contract)
        if int(metadata.get("version", 0) or 0) < 5:
            return {}
        if not bool(contract.get("roundtrip_capable", False)):
            return {}
        rig_ref = str(contract.get("rig_id", "") or "")
        if not rig_ref:
            return {}
        helper_descriptors = {
            int(row.get("descriptor", 0)) & 0xFFFFFFFF
            for row in contract.get("expected_skeleton", ())
            if isinstance(row, dict) and bool(row.get("helper", False))
        }
    except (OSError, TypeError, ValueError):
        return {}
    if not helper_descriptors:
        return {}
    helper_tracks = tuple(
        dict.fromkeys(
            str(row.get("node_name", "") or "")
            for row in contract.get("source_track_nodes", ())
            if isinstance(row, dict)
            and row.get("semantic") == "named_helper_bone"
            and str(row.get("node_name", "") or "")
        )
    )
    return {
        "status": "confirmed",
        "rig_ref": rig_ref,
        "contract_id": contract_id,
        "metadata_source": metadata_source,
        "helper_tracks": list(helper_tracks),
    }


__all__ = [
    "ROUNDTRIP_FORMAT",
    "ROUNDTRIP_GUARD_PREFIX",
    "ROUNDTRIP_SCHEMA_VERSION",
    "detect_native_helper_roundtrip_target",
    "embedded_native_metadata",
    "finalize_roundtrip_contract",
    "load_roundtrip_sidecar",
    "resolve_native_roundtrip_metadata",
    "roundtrip_contract_id",
    "roundtrip_sidecar_path",
    "validate_roundtrip_contract_identity",
    "write_roundtrip_sidecar",
]
