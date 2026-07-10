"""Versioned DL ReAnimated project files.

The project format is deliberately JSON, path-portable, migration-driven, and
extension-friendly.  Old project versions are migrated on load; unknown fields
are retained under ``extensions.unknown_fields`` instead of silently discarded.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import tempfile
from typing import Any, Mapping
import uuid

from . import __version__
from .script_targets import DEFAULT_SCRIPT_TARGET_ID


PROJECT_FORMAT = "dl-reanimated-project"
PROJECT_EXTENSION = ".dlraproj"
CURRENT_PROJECT_SCHEMA_VERSION = 4
MINIMUM_READER_VERSION = 1


def _now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


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
    fps: int = 30
    start_frame: int | None = None
    end_frame: int | None = None
    notes: str = ""
    tags: list[str] = field(default_factory=list)
    extensions: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def create(
        cls,
        source_fbx: str,
        *,
        resource_name: str | None = None,
        animation_stack: str = "",
    ) -> "ProjectAnimation":
        path = Path(source_fbx)
        stem = _safe_resource_name(resource_name or path.stem)
        return cls(
            animation_id=str(uuid.uuid4()),
            source_fbx=str(source_fbx),
            display_name=path.stem,
            resource_name=stem,
            source_animation_stack=animation_stack,
        )


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
    target_rig_name: str = "Dying Light player_1_tpp / male humanoid"
    extensions: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class ExportSettings:
    mode: str = "new"  # new | append
    output_directory: str = "build"
    pack_filename: str = "common_anims_sp_pc.rpack"
    existing_rpack: str = ""
    collision_policy: str = "error"  # error | replace
    default_script_target: str = DEFAULT_SCRIPT_TARGET_ID
    custom_script_resource: str = ""
    resource_prefix: str = "dl_reanimated"
    include_validation_controls: bool = False
    write_intermediate_anm2: bool = False
    extensions: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class DlReanimatedProject:
    project_id: str
    name: str
    created_utc: str
    modified_utc: str
    rig: RigSettings = field(default_factory=RigSettings)
    export: ExportSettings = field(default_factory=ExportSettings)
    animations: list[ProjectAnimation] = field(default_factory=list)
    mapping_profiles: dict[str, dict[str, Any]] = field(default_factory=dict)
    user_script_targets: list[dict[str, Any]] = field(default_factory=list)
    notes: str = ""
    extensions: dict[str, Any] = field(default_factory=dict)
    schema_version: int = CURRENT_PROJECT_SCHEMA_VERSION
    minimum_reader_version: int = MINIMUM_READER_VERSION
    format: str = PROJECT_FORMAT
    created_with: str = __version__

    @classmethod
    def new(cls, name: str = "Untitled Animation Project") -> "DlReanimatedProject":
        now = _now()
        return cls(
            project_id=str(uuid.uuid4()),
            name=name,
            created_utc=now,
            modified_utc=now,
        )

    def animation_by_id(self, animation_id: str) -> ProjectAnimation | None:
        return next(
            (row for row in self.animations if row.animation_id == animation_id),
            None,
        )

    def touch(self) -> None:
        self.modified_utc = _now()
        self.created_with = __version__

    def validate(self) -> list[str]:
        errors: list[str] = []
        if not self.name.strip():
            errors.append("Project name cannot be empty.")
        if self.rig.retarget_mode not in {"humanoid", "exact"}:
            errors.append("Retarget mode must be 'humanoid' or 'exact'.")
        if self.rig.retarget_mode == "exact" and not self.rig.target_rig_path.strip():
            errors.append("Exact retarget mode requires an installed or selected .crig file.")
        if self.export.mode not in {"new", "append"}:
            errors.append("Export mode must be 'new' or 'append'.")
        if self.export.collision_policy not in {"error", "replace"}:
            errors.append("Collision policy must be 'error' or 'replace'.")
        if self.export.mode == "append" and not self.export.existing_rpack:
            errors.append("Append mode requires an existing tool-created RPack.")
        if not self.export.output_directory.strip():
            errors.append("Choose an output folder before building.")
        if (
            self.rig.retarget_mode == "humanoid"
            and not self.rig.use_imported_animation_bind_pose
            and not self.rig.source_rest_fbx.strip()
        ):
            errors.append(
                "Choose a source rest/T-pose FBX or enable imported-animation bind pose."
            )
        if not self.export.pack_filename.lower().endswith(".rpack"):
            errors.append("Pack filename must end in .rpack.")
        seen_resources: set[str] = set()
        for row in self.animations:
            if not row.source_fbx:
                errors.append(f"Animation {row.display_name!r} has no FBX source.")
            if not row.resource_name:
                errors.append(f"Animation {row.display_name!r} has no resource name.")
            if row.root_policy not in {"inplace", "bip01", "motion"}:
                errors.append(f"Animation {row.display_name!r} has an invalid root policy.")
            if row.ik_preset not in {"runtime", "off"}:
                errors.append(f"Animation {row.display_name!r} has an invalid IK preset.")
            key = row.resource_name.lower()
            if row.enabled and key in seen_resources:
                errors.append(
                    f"Duplicate animation resource name: {row.resource_name!r}. "
                    "_ANIMATION_ resource names are global within an RPack even when "
                    "the sequences use different animation-script targets."
                )
            if row.enabled:
                seen_resources.add(key)
        return errors

    def to_dict(self, *, project_path: str | Path | None = None) -> dict[str, Any]:
        self.touch()
        payload = asdict(self)
        if project_path is not None:
            base = Path(project_path).resolve().parent
            _relativize_project_paths(payload, base)
        return payload

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "DlReanimatedProject":
        migrated = _migrate_payload(dict(payload))
        allowed = {
            "project_id",
            "name",
            "created_utc",
            "modified_utc",
            "rig",
            "export",
            "animations",
            "mapping_profiles",
            "user_script_targets",
            "notes",
            "extensions",
            "schema_version",
            "minimum_reader_version",
            "format",
            "created_with",
        }
        unknown = {key: value for key, value in migrated.items() if key not in allowed}
        extensions = dict(migrated.get("extensions", {}))
        if unknown:
            extensions.setdefault("unknown_fields", {}).update(unknown)
        rig_payload = dict(migrated.get("rig", {}))
        export_payload = dict(migrated.get("export", {}))
        animations = [
            ProjectAnimation(**_filtered_dataclass_payload(ProjectAnimation, dict(row)))
            for row in migrated.get("animations", [])
        ]
        return cls(
            project_id=str(migrated.get("project_id") or uuid.uuid4()),
            name=str(migrated.get("name", "Imported Project")),
            created_utc=str(migrated.get("created_utc", _now())),
            modified_utc=str(migrated.get("modified_utc", _now())),
            rig=RigSettings(**_filtered_dataclass_payload(RigSettings, rig_payload)),
            export=ExportSettings(**_filtered_dataclass_payload(ExportSettings, export_payload)),
            animations=animations,
            mapping_profiles={
                str(key): dict(value)
                for key, value in dict(migrated.get("mapping_profiles", {})).items()
            },
            user_script_targets=[dict(row) for row in migrated.get("user_script_targets", [])],
            notes=str(migrated.get("notes", "")),
            extensions=extensions,
            schema_version=int(migrated.get("schema_version", CURRENT_PROJECT_SCHEMA_VERSION)),
            minimum_reader_version=int(migrated.get("minimum_reader_version", MINIMUM_READER_VERSION)),
            format=str(migrated.get("format", PROJECT_FORMAT)),
            created_with=str(migrated.get("created_with", "unknown")),
        )

    def save(self, path: str | Path) -> Path:
        destination = Path(path)
        if destination.suffix.lower() != PROJECT_EXTENSION:
            destination = destination.with_suffix(PROJECT_EXTENSION)
        destination.parent.mkdir(parents=True, exist_ok=True)
        payload = self.to_dict(project_path=destination)
        text = json.dumps(payload, indent=2, ensure_ascii=False) + "\n"
        _atomic_write_text(destination, text)
        return destination

    @classmethod
    def load(cls, path: str | Path) -> "DlReanimatedProject":
        source = Path(path)
        payload = json.loads(source.read_text(encoding="utf-8"))
        if not isinstance(payload, dict):
            raise ValueError("project root must be a JSON object")
        project = cls.from_dict(payload)
        _resolve_project_paths(project, source.resolve().parent)
        return project


def resolve_project_path(value: str, project_path: str | Path | None) -> Path:
    path = Path(value).expanduser()
    if path.is_absolute() or project_path is None:
        return path
    return Path(project_path).resolve().parent / path


def _migrate_payload(payload: dict[str, Any]) -> dict[str, Any]:
    format_name = str(payload.get("format", PROJECT_FORMAT))
    if format_name != PROJECT_FORMAT:
        raise ValueError(f"unsupported project format: {format_name}")
    version = int(payload.get("schema_version", 0))
    minimum_reader = int(payload.get("minimum_reader_version", 1))
    if minimum_reader > CURRENT_PROJECT_SCHEMA_VERSION:
        raise ValueError(
            f"project requires a newer reader schema {minimum_reader}, but this build supports "
            f"{CURRENT_PROJECT_SCHEMA_VERSION}"
        )
    if version > CURRENT_PROJECT_SCHEMA_VERSION:
        raise ValueError(
            f"project schema {version} is newer than supported schema "
            f"{CURRENT_PROJECT_SCHEMA_VERSION}"
        )
    while version < CURRENT_PROJECT_SCHEMA_VERSION:
        migrator = _MIGRATIONS.get(version)
        if migrator is None:
            raise ValueError(f"no migration path exists from project schema {version}")
        payload = migrator(payload)
        version = int(payload["schema_version"])
    return payload


def _migrate_v0_to_v1(payload: dict[str, Any]) -> dict[str, Any]:
    migrated = dict(payload)
    migrated.setdefault("format", PROJECT_FORMAT)
    migrated.setdefault("project_id", str(uuid.uuid4()))
    migrated.setdefault("created_utc", _now())
    migrated.setdefault("modified_utc", migrated["created_utc"])
    migrated.setdefault("rig", {})
    migrated.setdefault("export", {})
    migrated.setdefault("animations", [])
    migrated.setdefault("mapping_profiles", {})
    migrated.setdefault("user_script_targets", [])
    migrated.setdefault("extensions", {})
    migrated.setdefault("minimum_reader_version", 1)
    migrated.setdefault("created_with", "legacy")
    migrated["schema_version"] = 1
    return migrated


def _migrate_v1_to_v2(payload: dict[str, Any]) -> dict[str, Any]:
    """Add explicit source-bind policy without breaking alpha.1/alpha.2 projects.

    Existing projects that already chose a separate rest FBX keep that behavior.
    Projects whose source-rest field was blank switch to the new embedded-bind mode.
    """

    migrated = dict(payload)
    rig = dict(migrated.get("rig", {}))
    rig.setdefault(
        "use_imported_animation_bind_pose",
        not bool(str(rig.get("source_rest_fbx", "")).strip()),
    )
    migrated["rig"] = rig
    migrated["schema_version"] = 2
    return migrated


def _migrate_v2_to_v3(payload: dict[str, Any]) -> dict[str, Any]:
    """Route legacy targets through an explicit rig reference and engine mode."""

    migrated = dict(payload)
    rig = dict(migrated.get("rig", {}))
    rig.setdefault("target_rig_ref", "builtin:male_npc_infected")
    rig.setdefault("target_rig_path", "")
    rig.setdefault("retarget_mode", "humanoid")
    extensions = dict(rig.get("extensions", {}))
    extensions.setdefault(
        "legacy_target_files",
        {
            key: rig.get(key, "")
            for key in (
                "canonical_smd",
                "target_template_anm2",
                "stock_writer_control_anm2",
            )
        },
    )
    rig["extensions"] = extensions
    migrated["rig"] = rig
    migrated["schema_version"] = 3
    return migrated


def _migrate_v3_to_v4(payload: dict[str, Any]) -> dict[str, Any]:
    """Add explicit FBX animation-stack selection to every project clip."""

    migrated = dict(payload)
    animations = []
    for raw in migrated.get("animations", []):
        row = dict(raw)
        row.setdefault("source_animation_stack", "")
        animations.append(row)
    migrated["animations"] = animations
    migrated["schema_version"] = 4
    return migrated


_MIGRATIONS = {
    0: _migrate_v0_to_v1,
    1: _migrate_v1_to_v2,
    2: _migrate_v2_to_v3,
    3: _migrate_v3_to_v4,
}


def _filtered_dataclass_payload(data_class: type, payload: dict[str, Any]) -> dict[str, Any]:
    fields = data_class.__dataclass_fields__
    unknown = {key: value for key, value in payload.items() if key not in fields}
    result = {key: value for key, value in payload.items() if key in fields}
    if unknown and "extensions" in fields:
        extensions = dict(result.get("extensions", {}))
        extensions.setdefault("unknown_fields", {}).update(unknown)
        result["extensions"] = extensions
    return result


def _relativize_project_paths(payload: dict[str, Any], base: Path) -> None:
    rig = payload.get("rig", {})
    for key in (
        "source_rest_fbx",
        "trusted_source_rest_json",
        "target_rig_path",
        "canonical_smd",
        "target_template_anm2",
        "stock_writer_control_anm2",
    ):
        rig[key] = _portable_path(rig.get(key, ""), base)
    export = payload.get("export", {})
    for key in ("output_directory", "existing_rpack"):
        export[key] = _portable_path(export.get(key, ""), base)
    for row in payload.get("animations", []):
        row["source_fbx"] = _portable_path(row.get("source_fbx", ""), base)


def _portable_path(value: str, base: Path) -> str:
    if not value:
        return ""
    path = Path(value).expanduser()
    try:
        absolute = path.resolve() if path.is_absolute() else (Path.cwd() / path).resolve()
        return os.path.relpath(absolute, base).replace("\\", "/")
    except (OSError, ValueError):
        return str(path).replace("\\", "/")


def _resolve_project_paths(project: DlReanimatedProject, base: Path) -> None:
    def resolve(value: str) -> str:
        if not value:
            return ""
        path = Path(value).expanduser()
        return str(path if path.is_absolute() else (base / path).resolve())

    project.rig.source_rest_fbx = resolve(project.rig.source_rest_fbx)
    project.rig.trusted_source_rest_json = resolve(project.rig.trusted_source_rest_json)
    project.rig.target_rig_path = resolve(project.rig.target_rig_path)
    project.rig.canonical_smd = resolve(project.rig.canonical_smd)
    project.rig.target_template_anm2 = resolve(project.rig.target_template_anm2)
    project.rig.stock_writer_control_anm2 = resolve(project.rig.stock_writer_control_anm2)
    project.export.output_directory = resolve(project.export.output_directory)
    project.export.existing_rpack = resolve(project.export.existing_rpack)
    for row in project.animations:
        row.source_fbx = resolve(row.source_fbx)


def _atomic_write_text(path: Path, text: str) -> None:
    handle, temp_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(text)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temp_name, path)
    finally:
        if os.path.exists(temp_name):
            os.unlink(temp_name)


def _safe_resource_name(value: str) -> str:
    safe = []
    for char in value.strip().lower():
        if char.isalnum() or char == "_":
            safe.append(char)
        elif char in {"-", " ", "."}:
            safe.append("_")
    result = "".join(safe).strip("_")
    while "__" in result:
        result = result.replace("__", "_")
    return result or "custom_animation"


__all__ = [
    "CURRENT_PROJECT_SCHEMA_VERSION",
    "DlReanimatedProject",
    "ExportSettings",
    "MINIMUM_READER_VERSION",
    "PROJECT_EXTENSION",
    "PROJECT_FORMAT",
    "ProjectAnimation",
    "RigSettings",
    "resolve_project_path",
]
