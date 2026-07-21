"""Coherent game-level target packages for DL ReAnimated projects."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any


DL1_GAME_ID = "dying_light_1"
DL2_GAME_ID = "dying_light_2"
SUPPORTED_GAME_IDS = (DL1_GAME_ID, DL2_GAME_ID)

DL1_RIG_REF = "builtin:male_npc_infected"
DL2_ADVANCED_RIG_REF = "builtin:dl2_player_advanced"
DL2_LEGACY_RIG_REF = "builtin:dl2_player_shadow_caster"
# Backward-compatible public name.  It has always represented the current DL2
# default; new callers should use the explicit advanced/legacy constants.
DL2_RIG_REF = DL2_ADVANCED_RIG_REF


@dataclass(frozen=True, slots=True)
class TargetPackageDescriptor:
    """Immutable files and identity that make up one bundled target preset."""

    rig_ref: str
    rig_name: str
    rig_relative_path: str
    canonical_smd_relative_path: str
    reference_anm2_relative_path: str
    primary_root: str
    provenance: str = "bundled"

    def paths(self, root: str | Path) -> dict[str, str]:
        base = Path(root)
        return {
            "target_rig_path": (
                str(base / self.rig_relative_path) if self.rig_relative_path else ""
            ),
            "canonical_smd": str(base / self.canonical_smd_relative_path),
            "target_template_anm2": str(base / self.reference_anm2_relative_path),
        }


@dataclass(frozen=True, slots=True)
class GameProfile:
    game_id: str
    display_name: str
    default_target_rig_ref: str
    compatible_builtin_rig_refs: tuple[str, ...]
    target_packages: tuple[TargetPackageDescriptor, ...]
    stock_control_relative_path: str
    anm2_format_label: str
    output_status: str
    finger_policy: str
    world_up_axis: tuple[float, float, float] = (0.0, 1.0, 0.0)

    def package_for_rig_ref(
        self, rig_ref: str | None = None
    ) -> TargetPackageDescriptor | None:
        selected = str(rig_ref or self.default_target_rig_ref)
        return next(
            (package for package in self.target_packages if package.rig_ref == selected),
            None,
        )

    @property
    def default_target_package(self) -> TargetPackageDescriptor:
        package = self.package_for_rig_ref(self.default_target_rig_ref)
        if package is None:  # pragma: no cover - guarded by the static inventory below
            raise ValueError(
                f"Game profile {self.game_id!r} has no package for its default target "
                f"{self.default_target_rig_ref!r}."
            )
        return package

    # Compatibility properties for existing integrations.  These deliberately
    # describe only the default package; package_for_rig_ref() is unambiguous
    # when a project explicitly selects a compatible legacy preset.
    @property
    def target_rig_ref(self) -> str:
        return self.default_target_rig_ref

    @property
    def target_rig_name(self) -> str:
        return self.default_target_package.rig_name

    @property
    def target_rig_relative_path(self) -> str:
        return self.default_target_package.rig_relative_path

    @property
    def canonical_smd_relative_path(self) -> str:
        return self.default_target_package.canonical_smd_relative_path

    @property
    def reference_anm2_relative_path(self) -> str:
        return self.default_target_package.reference_anm2_relative_path

    @property
    def primary_root(self) -> str:
        return self.default_target_package.primary_root

    def paths(self, root: str | Path, *, rig_ref: str | None = None) -> dict[str, str]:
        package = self.package_for_rig_ref(rig_ref)
        if package is None:
            raise ValueError(
                f"{self.display_name} has no bundled target package for {rig_ref!r}."
            )
        result = package.paths(root)
        result["stock_writer_control_anm2"] = (
            str(Path(root) / self.stock_control_relative_path)
            if self.stock_control_relative_path
            else ""
        )
        return result


_DL1_PACKAGE = TargetPackageDescriptor(
    DL1_RIG_REF,
    "Dying Light player_1_tpp / male humanoid",
    "",
    "reference/player_1_tpp.smd",
    "reference/infected_turn_90r.template.anm2",
    "bip01",
)

_DL2_ADVANCED_PACKAGE = TargetPackageDescriptor(
    DL2_ADVANCED_RIG_REF,
    "Dying Light 2 Player — Advanced",
    "reference/dl2/player_skeleton.crig",
    "reference/dl2/player_skeleton.smd",
    "reference/dl2/0_m_fpp_farjump.anm2",
    "pelvis",
    "supplied 271-node advanced player skeleton",
)

_DL2_LEGACY_PACKAGE = TargetPackageDescriptor(
    DL2_LEGACY_RIG_REF,
    "Dying Light 2 Player — Shadow Caster [Legacy]",
    "reference/dl2/player_shadow_caster.crig",
    "reference/dl2/player_shadow_caster.smd",
    "reference/dl2/0_m_fpp_farjump.anm2",
    "pelvis",
    "legacy bundled 81-node shadow-caster skeleton",
)


GAME_PROFILES = {
    DL1_GAME_ID: GameProfile(
        DL1_GAME_ID,
        "Dying Light 1",
        DL1_RIG_REF,
        (DL1_RIG_REF,),
        (_DL1_PACKAGE,),
        "reference/stock_writer_control.anm2",
        "format 1",
        "validated",
        "dl1_hand1_parent_policy",
    ),
    DL2_GAME_ID: GameProfile(
        DL2_GAME_ID,
        "Dying Light 2",
        DL2_ADVANCED_RIG_REF,
        (DL2_ADVANCED_RIG_REF, DL2_LEGACY_RIG_REF),
        (_DL2_ADVANCED_PACKAGE, _DL2_LEGACY_PACKAGE),
        "",
        "Header_Version2 (signature 42)",
        "native Header_Version2 read / FBX; native Header_Version2 writing unavailable",
        "dl2_explicit_finger10_20_30_40_roots",
    ),
}


def get_game_profile(game_id: str | None) -> GameProfile:
    value = str(game_id or DL1_GAME_ID)
    if value not in GAME_PROFILES:
        raise ValueError(
            f"Unsupported game identifier {value!r}. Choose Dying Light 1 or Dying Light 2."
        )
    return GAME_PROFILES[value]


def infer_game_id(payload: dict[str, Any]) -> str:
    """Infer only unmistakable DL2 projects; all old/ambiguous projects stay DL1."""

    explicit = payload.get("game_id")
    if explicit in SUPPORTED_GAME_IDS:
        return str(explicit)
    extensions = payload.get("extensions", {})
    if isinstance(extensions, dict) and extensions.get("game_id") in SUPPORTED_GAME_IDS:
        return str(extensions["game_id"])
    rig = payload.get("rig", {})
    if isinstance(rig, dict):
        values = "\n".join(
            str(rig.get(key, ""))
            for key in (
                "target_rig_ref",
                "default_target_rig_ref",
                "target_rig_path",
                "canonical_smd",
                "target_template_anm2",
            )
        ).casefold()
        if "builtin:dl2_" in values or "reference/dl2/" in values.replace("\\", "/"):
            return DL2_GAME_ID
    return DL1_GAME_ID


def path_looks_dl2(value: str) -> bool:
    normalized = str(value or "").replace("\\", "/").casefold()
    return "reference/dl2/" in normalized or "/dl2/" in normalized or "dl2_" in normalized


def project_coherence_errors(project: Any) -> list[str]:
    profile = get_game_profile(getattr(project, "game_id", DL1_GAME_ID))
    rig = project.rig
    errors: list[str] = []
    selected_ref = str(rig.target_rig_ref or "")
    if (
        selected_ref.startswith("builtin:")
        and selected_ref not in profile.compatible_builtin_rig_refs
    ):
        compatible = ", ".join(repr(value) for value in profile.compatible_builtin_rig_refs)
        errors.append(
            f"{profile.display_name} cannot use built-in target {selected_ref!r}; "
            f"select one of {compatible} or a compatible custom .crig."
        )
    selected_package = profile.package_for_rig_ref(selected_ref)
    if selected_package is not None:
        package_path_fields = (
            ("target CRIG", str(rig.target_rig_path or ""), "rig_relative_path"),
            ("canonical SMD", str(rig.canonical_smd or ""), "canonical_smd_relative_path"),
        )
        for label, value, package_field in package_path_fields:
            if not value:
                continue
            expected_relative = str(getattr(selected_package, package_field))
            if _matches_profile_path(value, expected_relative, Path()):
                continue
            conflicting = next(
                (
                    package
                    for package in profile.target_packages
                    if package.rig_ref != selected_ref
                    and _matches_profile_path(
                        value, str(getattr(package, package_field)), Path()
                    )
                ),
                None,
            )
            if conflicting is not None:
                errors.append(
                    f"Built-in target {selected_ref!r} is paired with the {label} from "
                    f"{conflicting.rig_ref!r}; keep each bundled topology with its own package."
                )
    target_values = (rig.target_rig_path, rig.canonical_smd, rig.target_template_anm2)
    if profile.game_id == DL1_GAME_ID and any(path_looks_dl2(value) for value in target_values):
        errors.append("Dying Light 1 is selected, but a DL2 target SMD/CRIG/reference ANM2 is configured.")
    if profile.game_id == DL2_GAME_ID:
        if selected_ref == DL1_RIG_REF:
            errors.append("Dying Light 2 is selected, but the DL1 male NPC target is configured.")
        template = Path(str(rig.target_template_anm2 or ""))
        if template.name and template.name in {"infected_turn_90r.template.anm2", "stock_writer_control.anm2"}:
            errors.append("Dying Light 2 is selected, but a DL1 reference ANM2 is configured.")
    custom_rig = Path(str(rig.target_rig_path or ""))
    if custom_rig.is_file() and custom_rig.suffix.casefold() == ".crig":
        try:
            from .chrome_rig import ChromeRig
            rig_game = str(ChromeRig.load(custom_rig).extensions.get("game_id", "") or "")
            if rig_game in SUPPORTED_GAME_IDS and rig_game != profile.game_id:
                errors.append(
                    f"The selected custom .crig identifies {rig_game!r}, but the project "
                    f"identifies {profile.game_id!r}."
                )
        except (OSError, ValueError):
            pass
    template = Path(str(rig.target_template_anm2 or ""))
    if template.is_file() and template.stat().st_size >= 8:
        try:
            from .dl2_anm2 import detect_anm2_format
            detected = detect_anm2_format(template)
            expected = 42 if profile.game_id == DL2_GAME_ID else 1
            if detected != expected:
                errors.append(
                    f"{profile.display_name} expects ANM2 {profile.anm2_format_label}, but "
                    f"{template.name!r} was detected as format {detected}."
                )
        except ValueError as exc:
            errors.append(f"The target reference ANM2 is unsupported: {exc}")
    return list(dict.fromkeys(errors))


def _matches_profile_path(value: str, relative: str, root: Path) -> bool:
    if not relative:
        return not str(value or "")
    normalized = str(value or "").replace("\\", "/").casefold()
    rel = relative.replace("\\", "/").casefold()
    absolute = str(root / relative).replace("\\", "/").casefold()
    return normalized in {rel, absolute} or normalized.endswith("/" + rel)


def _matches_any_package_path(
    value: str,
    profile: GameProfile,
    field_name: str,
    root: Path,
) -> bool:
    relatives = {
        str(getattr(package, field_name))
        for package in profile.target_packages
    }
    return any(_matches_profile_path(value, relative, root) for relative in relatives)


def apply_target_package_selection(
    project: Any,
    root: str | Path,
    rig_ref: str,
) -> bool:
    """Apply one explicitly selected immutable built-in target package.

    This is a user-selection operation, not a load-time migration.  Keeping it
    separate from :func:`apply_game_profile_defaults` lets old projects retain
    an explicit legacy preset while ensuring a later GUI preset change updates
    the CRIG, canonical SMD, and reference ANM2 as one coherent package.
    """

    profile = get_game_profile(getattr(project, "game_id", DL1_GAME_ID))
    package = profile.package_for_rig_ref(rig_ref)
    if package is None:
        return False
    paths = profile.paths(root, rig_ref=rig_ref)
    rig = project.rig
    rig.target_rig_ref = package.rig_ref
    rig.target_rig_path = paths["target_rig_path"]
    rig.target_rig_name = package.rig_name
    rig.canonical_smd = paths["canonical_smd"]
    rig.target_template_anm2 = paths["target_template_anm2"]
    rig.stock_writer_control_anm2 = paths["stock_writer_control_anm2"]
    # Bundled targets are curated policies, not ordinary custom CRIGs.  Auto
    # selects the appropriate internal engine after source compatibility is
    # known; expert/custom selection remains the only normal ExactRig surface.
    rig.retarget_mode = "auto"
    return True


def apply_game_profile_defaults(
    project: Any,
    root: str | Path,
    *,
    previous_game_id: str | None = None,
    force: bool = False,
) -> dict[str, list[str]]:
    """Switch coherent defaults without overwriting an explicit compatible preset.

    In particular, merely loading or refreshing a DL2 project that explicitly
    stores the legacy shadow-caster ID does not migrate it to the advanced rig.
    ``force=True`` represents the user's explicit "reset to current defaults"
    action and may select the advanced package.
    """

    base = Path(root)
    current = get_game_profile(project.game_id)
    previous = get_game_profile(previous_game_id or project.game_id)
    changing_game = previous.game_id != current.game_id
    changed: list[str] = []
    retained: list[str] = []
    rig = project.rig
    previous_managed_ref = (
        rig.target_rig_ref == previous.default_target_rig_ref
        or (
            changing_game
            and rig.target_rig_ref in previous.compatible_builtin_rig_refs
        )
    )
    if force or previous_managed_ref:
        rig.target_rig_ref = current.default_target_rig_ref
        rig.target_rig_path = current.paths(base)["target_rig_path"]
        rig.target_rig_name = current.target_rig_name
        rig.retarget_mode = "auto"
        changed.append("target rig")
    else:
        retained.append("custom or explicit compatible target rig")
    path_fields = {
        "canonical_smd": "canonical_smd_relative_path",
        "target_template_anm2": "reference_anm2_relative_path",
    }
    current_paths = current.paths(base)
    for field_name, package_field in path_fields.items():
        old_value = str(getattr(rig, field_name, "") or "")
        if (
            force
            or (
                previous_managed_ref
                and _matches_any_package_path(old_value, previous, package_field, base)
            )
        ):
            setattr(rig, field_name, current_paths[field_name])
            changed.append(field_name)
        elif old_value:
            retained.append(field_name)
    old_control = str(rig.stock_writer_control_anm2 or "")
    if (
        force
        or (
            previous_managed_ref
            and _matches_profile_path(
                old_control, previous.stock_control_relative_path, base
            )
        )
    ):
        rig.stock_writer_control_anm2 = current_paths["stock_writer_control_anm2"]
        changed.append("stock_writer_control_anm2")
    elif old_control:
        retained.append("stock_writer_control_anm2")
    for item in project.anm2_to_fbx.items:
        item_previous_managed = (
            item.source_rig_ref == previous.default_target_rig_ref
            or (
                changing_game
                and item.source_rig_ref in previous.compatible_builtin_rig_refs
            )
        )
        if force or item_previous_managed:
            item.source_rig_ref = current.default_target_rig_ref
            item.source_rig_path = current_paths["target_rig_path"]
    extensions = dict(project.extensions or {})
    extensions["game_id"] = current.game_id
    extensions["game_profile_status"] = {
        "expected_anm2_format": current.anm2_format_label,
        "primary_root": current.primary_root,
        "finger_policy": current.finger_policy,
        "world_up_axis": list(current.world_up_axis),
        "output_status": current.output_status,
        "default_target_rig_ref": current.default_target_rig_ref,
        "compatible_builtin_rig_refs": list(current.compatible_builtin_rig_refs),
    }
    project.extensions = extensions
    return {"changed": changed, "retained": retained}


__all__ = [
    "DL1_GAME_ID",
    "DL2_GAME_ID",
    "DL1_RIG_REF",
    "DL2_ADVANCED_RIG_REF",
    "DL2_LEGACY_RIG_REF",
    "DL2_RIG_REF",
    "GAME_PROFILES",
    "GameProfile",
    "TargetPackageDescriptor",
    "SUPPORTED_GAME_IDS",
    "get_game_profile",
    "infer_game_id",
    "apply_game_profile_defaults",
    "path_looks_dl2",
    "project_coherence_errors",
]
