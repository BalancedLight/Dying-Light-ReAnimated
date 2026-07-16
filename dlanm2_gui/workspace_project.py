from __future__ import annotations

from dataclasses import asdict, dataclass, field, fields
from copy import deepcopy
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Mapping
import json
import os
import tempfile
import uuid

from . import __version__
from .game_profiles import DL1_GAME_ID, SUPPORTED_GAME_IDS, infer_game_id, project_coherence_errors

PROJECT_FORMAT = "dl-reanimated-project"
PROJECT_EXTENSION = ".dlraproj"
CURRENT_PROJECT_SCHEMA_VERSION = 8


def now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


def _field_names(cls: type) -> set[str]:
    return {row.name for row in fields(cls)}


def filtered(cls: type, payload: Mapping[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in payload.items() if key in _field_names(cls)}


def _with_unknown_fields(cls: type, payload: Mapping[str, Any]) -> dict[str, Any]:
    result = filtered(cls, payload)
    unknown = {key: value for key, value in payload.items() if key not in _field_names(cls)}
    extensions = dict(result.get("extensions", {}) or {})
    if unknown:
        extensions.setdefault("unknown_fields", {}).update(unknown)
    if "extensions" in _field_names(cls):
        result["extensions"] = extensions
    return result


def _restore_unknown_fields(payload: dict[str, Any]) -> dict[str, Any]:
    """Re-emit preserved extension fields at their original object level."""

    extensions = payload.get("extensions")
    if isinstance(extensions, Mapping):
        unknown = extensions.get("unknown_fields")
        if isinstance(unknown, Mapping):
            for key, value in unknown.items():
                payload.setdefault(str(key), deepcopy(value))
    return payload


def _portable_path(value: str, project_path: Path | None) -> str:
    if not value or project_path is None:
        return value
    candidate = Path(value)
    if not candidate.is_absolute():
        return value
    try:
        return candidate.resolve().relative_to(project_path.resolve().parent).as_posix()
    except ValueError:
        return str(candidate)


def _resolved_path(value: str, project_path: Path | None) -> str:
    if not value or project_path is None or Path(value).is_absolute():
        return value
    return str((project_path.resolve().parent / Path(value)).resolve())


@dataclass(slots=True)
class ProjectAnimation:
    animation_id: str
    source_fbx: str
    display_name: str
    resource_name: str
    source_animation_stack: str = ""
    enabled: bool = True
    script_target: str = ""
    root_policy: str = "inplace"
    ik_preset: str = "runtime"
    mapping_profile_id: str = ""
    # Empty values inherit the project-level target.  Keeping the override on
    # the clip lets one RPack contain resources for several custom CRIGs.
    target_rig_ref: str = ""
    target_rig_path: str = ""
    source_root_bone: str = ""
    target_root_bone: str = ""
    fps: int = 30
    start_frame: int | None = None
    end_frame: int | None = None
    notes: str = ""
    tags: list[str] = field(default_factory=list)
    extensions: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def create(cls, source_fbx: str | Path, resource_name: str | None = None, animation_stack: str = "") -> "ProjectAnimation":
        path = Path(source_fbx)
        return cls(str(uuid.uuid4()), str(path), path.stem, resource_name or path.stem, animation_stack)


@dataclass(slots=True)
class RigSettings:
    target_rig_ref: str = "builtin:male_npc_infected"
    target_rig_path: str = ""
    retarget_mode: str = "humanoid"
    use_imported_animation_bind_pose: bool = True
    source_rest_fbx: str = ""
    trusted_source_rest_json: str = ""
    canonical_smd: str = "reference/player_1_tpp.smd"
    target_template_anm2: str = "reference/infected_turn_90r.template.anm2"
    stock_writer_control_anm2: str = "reference/stock_writer_control.anm2"
    target_rig_name: str = "Dying Light male humanoid"
    extensions: dict[str, Any] = field(default_factory=dict)

    @property
    def default_target_rig_ref(self) -> str:
        return self.target_rig_ref

    @default_target_rig_ref.setter
    def default_target_rig_ref(self, value: str) -> None:
        self.target_rig_ref = str(value)

    @property
    def default_target_rig_path(self) -> str:
        return self.target_rig_path

    @default_target_rig_path.setter
    def default_target_rig_path(self, value: str) -> None:
        self.target_rig_path = str(value)


@dataclass(slots=True)
class ExportSettings:
    mode: str = "new"
    output_directory: str = "build"
    pack_filename: str = "common_anims_sp_pc.rpack"
    existing_rpack: str = ""
    collision_policy: str = "error"
    default_script_target: str = "male_npc_infected_dlc60"
    custom_script_resource: str = ""
    resource_prefix: str = "dl_reanimated"
    include_validation_controls: bool = False
    write_intermediate_anm2: bool = False
    extensions: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class Anm2ToFbxItem:
    conversion_id: str
    source_anm2: str
    output_name: str
    source_rig_ref: str = "builtin:male_npc_infected"
    source_rig_path: str = ""
    enabled: bool = True
    fps: int = 30
    start_frame: int | None = None
    end_frame: int | None = None
    extensions: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def create(cls, path: str | Path, output_name: str | None = None) -> "Anm2ToFbxItem":
        source = Path(path)
        return cls(str(uuid.uuid4()), str(source), output_name or source.stem)


@dataclass(slots=True)
class Anm2ToFbxSettings:
    mode: str = "native"
    target_fbx: str = ""
    output_directory: str = "build/fbx"
    translation_scale: str = "auto"
    selected_mapping_profile_id: str = ""
    items: list[Anm2ToFbxItem] = field(default_factory=list)
    bone_mapping_profiles: dict[str, dict[str, Any]] = field(default_factory=dict)
    extensions: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class DlReanimatedProject:
    project_id: str
    name: str
    created_utc: str
    modified_utc: str
    game_id: str = DL1_GAME_ID
    rig: RigSettings = field(default_factory=RigSettings)
    export: ExportSettings = field(default_factory=ExportSettings)
    animations: list[ProjectAnimation] = field(default_factory=list)
    mapping_profiles: dict[str, dict[str, Any]] = field(default_factory=dict)
    user_script_targets: list[dict[str, Any]] = field(default_factory=list)
    anm2_to_fbx: Anm2ToFbxSettings = field(default_factory=Anm2ToFbxSettings)
    notes: str = ""
    extensions: dict[str, Any] = field(default_factory=dict)
    schema_version: int = CURRENT_PROJECT_SCHEMA_VERSION
    minimum_reader_version: int = 1
    format: str = PROJECT_FORMAT
    created_with: str = __version__

    @classmethod
    def new(cls, name: str = "Untitled Project") -> "DlReanimatedProject":
        timestamp = now()
        return cls(str(uuid.uuid4()), name, timestamp, timestamp)

    def animation_by_id(self, animation_id: str) -> ProjectAnimation | None:
        return next((row for row in self.animations if row.animation_id == animation_id), None)

    def touch(self) -> None:
        self.modified_utc = now()
        self.created_with = __version__

    def validate(self) -> list[str]:
        errors: list[str] = []
        if not self.name.strip():
            errors.append("Project name cannot be empty.")
        if self.game_id not in SUPPORTED_GAME_IDS:
            errors.append(f"Unsupported game identifier {self.game_id!r}.")
        if self.rig.retarget_mode not in {"humanoid", "exact"}:
            errors.append("Retarget mode must be humanoid or exact.")
        if self.rig.retarget_mode == "exact" and any(
            row.enabled
            and not row.target_rig_ref
            and not row.target_rig_path
            for row in self.animations
        ) and not self.rig.target_rig_path:
            errors.append(
                "Exact mode requires a default .crig target for animations that inherit the "
                "project target; alternatively select a target rig on every enabled animation."
            )
        if (
            self.rig.retarget_mode == "humanoid"
            and not self.rig.use_imported_animation_bind_pose
            and not self.rig.source_rest_fbx.strip()
        ):
            errors.append(
                "Choose a valid source rest/T-pose FBX when imported animation bind pose is disabled."
            )
        if not self.export.output_directory:
            errors.append("Choose an output folder.")
        if not self.export.pack_filename.casefold().endswith(".rpack"):
            errors.append("Pack filename must end in .rpack.")
        seen_resources: dict[str, str] = {}
        for row in self.animations:
            if not row.enabled:
                continue
            key = row.resource_name.casefold()
            if key in seen_resources:
                errors.append(
                    f"Duplicate animation resource name {row.resource_name!r}; resource names "
                    "must be unique across all animation script targets."
                )
            else:
                seen_resources[key] = row.animation_id
        errors.extend(project_coherence_errors(self))
        return list(dict.fromkeys(errors))

    def to_dict(self, project_path: str | Path | None = None) -> dict[str, Any]:
        self.touch()
        result = asdict(self)
        destination = Path(project_path) if project_path is not None else None
        rig = result["rig"]
        for key in (
            "target_rig_path", "source_rest_fbx", "trusted_source_rest_json", "canonical_smd",
            "target_template_anm2", "stock_writer_control_anm2",
        ):
            rig[key] = _portable_path(str(rig.get(key, "")), destination)
        # Schema-v8 aliases must mirror the already-portable legacy storage
        # fields.  Writing the alias first would reintroduce an absolute path
        # during load because migration gives the explicit default alias
        # precedence.
        rig["default_target_rig_ref"] = str(rig.get("target_rig_ref", ""))
        rig["default_target_rig_path"] = str(rig.get("target_rig_path", ""))
        export = result["export"]
        for key in ("output_directory", "existing_rpack"):
            export[key] = _portable_path(str(export.get(key, "")), destination)
        reverse = result["anm2_to_fbx"]
        for key in ("target_fbx", "output_directory"):
            reverse[key] = _portable_path(str(reverse.get(key, "")), destination)
        for row in result["animations"]:
            row["source_fbx"] = _portable_path(str(row.get("source_fbx", "")), destination)
            row["target_rig_path"] = _portable_path(
                str(row.get("target_rig_path", "")), destination
            )
        for row in reverse["items"]:
            row["source_anm2"] = _portable_path(str(row.get("source_anm2", "")), destination)
            row["source_rig_path"] = _portable_path(str(row.get("source_rig_path", "")), destination)
        _restore_unknown_fields(rig)
        _restore_unknown_fields(export)
        _restore_unknown_fields(reverse)
        for row in result["animations"]:
            _restore_unknown_fields(row)
        for row in reverse["items"]:
            _restore_unknown_fields(row)
        return _restore_unknown_fields(result)

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "DlReanimatedProject":
        raw = dict(payload)
        schema_version = int(raw.get("schema_version", 0) or 0)
        minimum_reader = int(raw.get("minimum_reader_version", 1) or 1)
        if schema_version > CURRENT_PROJECT_SCHEMA_VERSION or minimum_reader > CURRENT_PROJECT_SCHEMA_VERSION:
            raise ValueError(
                f"This project requires a newer DL ReAnimated reader (schema {schema_version}, "
                f"minimum reader {minimum_reader})."
            )
        if raw.get("format", PROJECT_FORMAT) != PROJECT_FORMAT:
            raise ValueError(f"Unsupported project format {raw.get('format')!r}.")

        game_id = infer_game_id(raw)
        rig_raw = dict(raw.get("rig", {}) or {})
        default_ref = str(rig_raw.pop("default_target_rig_ref", "") or "")
        default_path = str(rig_raw.pop("default_target_rig_path", "") or "")
        if default_ref:
            rig_raw["target_rig_ref"] = default_ref
        if default_path:
            rig_raw["target_rig_path"] = default_path
        if schema_version <= 1 and "use_imported_animation_bind_pose" not in rig_raw:
            rig_raw["use_imported_animation_bind_pose"] = not bool(rig_raw.get("source_rest_fbx"))
        if schema_version <= 2:
            rig_extensions = dict(rig_raw.get("extensions", {}) or {})
            rig_extensions.setdefault(
                "legacy_target_files",
                {
                    key: rig_raw.get(key, "")
                    for key in (
                        "canonical_smd", "target_template_anm2", "stock_writer_control_anm2"
                    )
                },
            )
            rig_raw["extensions"] = rig_extensions
        export_raw = dict(raw.get("export", {}) or {})
        reverse_raw = dict(raw.get("anm2_to_fbx", {}) or {})
        item_rows = reverse_raw.pop("items", []) or []
        animations = []
        for source_row in (raw.get("animations", []) or []):
            animation_raw = dict(source_row)
            extensions = dict(animation_raw.get("extensions", {}) or {})
            legacy_root = extensions.get("root_mapping_v1", {})
            if isinstance(legacy_root, Mapping):
                animation_raw.setdefault(
                    "source_root_bone", str(legacy_root.get("source_bone", "") or "")
                )
                animation_raw.setdefault(
                    "target_root_bone", str(legacy_root.get("target_bone", "") or "")
                )
            animations.append(
                ProjectAnimation(
                    **_with_unknown_fields(ProjectAnimation, animation_raw)
                )
            )
        reverse_items = [
            Anm2ToFbxItem(**_with_unknown_fields(Anm2ToFbxItem, dict(row)))
            for row in item_rows
        ]
        top = _with_unknown_fields(cls, raw)
        top.pop("rig", None)
        top.pop("export", None)
        top.pop("animations", None)
        top.pop("anm2_to_fbx", None)
        top.update(
            {
                "project_id": str(raw.get("project_id") or uuid.uuid4()),
                "name": str(raw.get("name", "Imported Project")),
                "created_utc": str(raw.get("created_utc", now())),
                "modified_utc": str(raw.get("modified_utc", now())),
                "game_id": game_id,
                "rig": RigSettings(**_with_unknown_fields(RigSettings, rig_raw)),
                "export": ExportSettings(**_with_unknown_fields(ExportSettings, export_raw)),
                "animations": animations,
                "mapping_profiles": {
                    str(key): dict(value) for key, value in dict(raw.get("mapping_profiles", {})).items()
                },
                "user_script_targets": [dict(row) for row in raw.get("user_script_targets", [])],
                "anm2_to_fbx": Anm2ToFbxSettings(
                    **_with_unknown_fields(Anm2ToFbxSettings, reverse_raw), items=reverse_items
                ),
                "notes": str(raw.get("notes", "")),
                "schema_version": CURRENT_PROJECT_SCHEMA_VERSION,
                "minimum_reader_version": 1,
                "format": PROJECT_FORMAT,
                "created_with": str(raw.get("created_with", "legacy")),
            }
        )
        return cls(**filtered(cls, top))

    def save(self, path: str | Path) -> Path:
        destination = Path(path)
        if destination.suffix.casefold() != PROJECT_EXTENSION:
            destination = destination.with_suffix(PROJECT_EXTENSION)
        destination.parent.mkdir(parents=True, exist_ok=True)
        payload = (json.dumps(self.to_dict(destination), indent=2, ensure_ascii=False) + "\n").encode("utf-8")
        handle, temporary = tempfile.mkstemp(
            prefix=destination.name + ".", suffix=".tmp", dir=destination.parent
        )
        try:
            with os.fdopen(handle, "wb") as stream:
                stream.write(payload)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, destination)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
        return destination

    @classmethod
    def load(cls, path: str | Path) -> "DlReanimatedProject":
        source = Path(path)
        project = cls.from_dict(json.loads(source.read_text(encoding="utf-8-sig")))
        for key in (
            "target_rig_path", "source_rest_fbx", "trusted_source_rest_json", "canonical_smd",
            "target_template_anm2", "stock_writer_control_anm2",
        ):
            setattr(project.rig, key, _resolved_path(getattr(project.rig, key), source))
        for key in ("output_directory", "existing_rpack"):
            setattr(project.export, key, _resolved_path(getattr(project.export, key), source))
        for row in project.animations:
            row.source_fbx = _resolved_path(row.source_fbx, source)
            row.target_rig_path = _resolved_path(row.target_rig_path, source)
        project.anm2_to_fbx.target_fbx = _resolved_path(project.anm2_to_fbx.target_fbx, source)
        project.anm2_to_fbx.output_directory = _resolved_path(project.anm2_to_fbx.output_directory, source)
        for row in project.anm2_to_fbx.items:
            row.source_anm2 = _resolved_path(row.source_anm2, source)
            row.source_rig_path = _resolved_path(row.source_rig_path, source)
        return project


__all__ = [
    "Anm2ToFbxItem", "Anm2ToFbxSettings", "CURRENT_PROJECT_SCHEMA_VERSION",
    "DlReanimatedProject", "ExportSettings", "PROJECT_EXTENSION", "PROJECT_FORMAT",
    "ProjectAnimation", "RigSettings",
]
