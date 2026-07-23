"""Selectable FBX-to-ANM2 transform sampling contracts."""

from __future__ import annotations


CURRENT = "current"
LEGACY_5_0 = "legacy_5_0"
FBX_ANM2_EXPORT_BEHAVIORS = (CURRENT, LEGACY_5_0)


def coerce_fbx_anm2_export_behavior(value: str | None) -> str:
    """Return one supported, stable project export-behavior identifier."""
    resolved = str(value or CURRENT).strip().casefold()
    if resolved not in FBX_ANM2_EXPORT_BEHAVIORS:
        choices = ", ".join(FBX_ANM2_EXPORT_BEHAVIORS)
        raise ValueError(
            f"Unsupported FBX-to-ANM2 export behavior {value!r}; expected one of: {choices}."
        )
    return resolved


__all__ = [
    "CURRENT",
    "LEGACY_5_0",
    "FBX_ANM2_EXPORT_BEHAVIORS",
    "coerce_fbx_anm2_export_behavior",
]
