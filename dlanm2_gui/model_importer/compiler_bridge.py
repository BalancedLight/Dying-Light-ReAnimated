from __future__ import annotations

"""Windows-only Techland DevTools compiler and loose-project installer."""

from dataclasses import dataclass
from pathlib import Path
from typing import Any
import json
import os
import shutil
import subprocess
import time

from .vendor.chrome_mesh_tools.compact_mesh import inspect_msh_obj
from .compiler_support import (
    GENERATED_PROJECT_MARKER,
    MeshResourceSpec,
    common_override_text,
    compiler_commands,
    mesh_resource_pack_folders_text,
    mesh_rsrc_text,
    mesh_rules_text,
    stage_compiler_bootstrap,
)


class ModelCompileError(RuntimeError):
    pass


@dataclass(slots=True)
class CompilerSettings:
    compiler: str
    data0_pak: str
    workshop_root: str
    active_project: str
    output_directory: str
    devtools_data_directory: str = ""
    platform: str = "PC"

    def validate(self) -> None:
        compiler = Path(self.compiler)
        data0 = Path(self.data0_pak)
        workshop = Path(self.workshop_root)
        project = Path(self.active_project)
        if not compiler.is_file():
            raise ModelCompileError(f"ResPack compiler was not found: {compiler}")
        if not data0.is_file():
            raise ModelCompileError(f"Dying Light Data0.pak was not found: {data0}")
        if not workshop.is_dir():
            raise ModelCompileError(f"DevTools workshop root was not found: {workshop}")
        if not project.is_dir():
            raise ModelCompileError(f"Active DevTools project was not found: {project}")
        try:
            project.resolve().relative_to(workshop.resolve())
        except ValueError as exc:
            raise ModelCompileError(
                "The active project must be a folder inside the selected DevTools workshop root."
            ) from exc
        Path(self.output_directory).mkdir(parents=True, exist_ok=True)


def compile_and_install_model(
    *,
    source_msh: str | Path,
    source_report: dict[str, Any],
    settings: CompilerSettings,
    log_callback=None,
) -> dict[str, Any]:
    settings.validate()
    source = Path(source_msh)
    if not source.is_file():
        raise ModelCompileError(f"source MSH was not found: {source}")
    resource_name = str(source_report["resource_name"])
    effective_mode = str(source_report["effective_mode"])
    expected_bones = int(source_report.get("bone_count", 0))
    compiler = Path(settings.compiler)
    workshop = Path(settings.workshop_root)
    active_project = Path(settings.active_project)
    out_root = Path(settings.output_directory) / resource_name
    compiler_output = out_root / "compiler_output"
    compiler_output.mkdir(parents=True, exist_ok=True)

    project_name = "_DLReAnimatedModelImporter"
    compiler_project = workshop / project_name
    _replace_generated_project(compiler_project, kind="model_importer")
    virtual_directory = f"data/characters/dl_reanimated/imported/{resource_name}"
    virtual_path = f"{virtual_directory}/{resource_name}.msh"
    staged_msh = compiler_project / Path(virtual_path)
    staged_msh.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, staged_msh)
    for suffix in (".ascr", ".bscr"):
        companion = source.with_suffix(suffix)
        if companion.is_file():
            shutil.copy2(companion, staged_msh.with_suffix(suffix))

    _log(log_callback, f"Compiler project: {compiler_project}")
    bootstrap = stage_compiler_bootstrap(
        compiler=compiler,
        data0_pak=settings.data0_pak,
        project_dir=compiler_project,
        devtools_data_dir=settings.devtools_data_directory or None,
    )
    data_dir = compiler_project / "data"
    data_dir.mkdir(parents=True, exist_ok=True)
    # Restore the narrow project-owned folder script after copying Techland's
    # broad bootstrap version.
    (data_dir / "resourcepackfolders.scr").write_text(
        mesh_resource_pack_folders_text(), encoding="ascii"
    )
    (data_dir / "common_ovr.scr").write_text(common_override_text(), encoding="ascii")
    (compiler_project / "common_ovr.scr").write_text(common_override_text(), encoding="ascii")

    spec = MeshResourceSpec(
        resource_name=resource_name,
        source_path=staged_msh,
        virtual_path=virtual_path,
        skins="Default",
        instances_limit=1,
        pos_compression=0,
        used_by_code=True,
    )
    rsrc = compiler_project / "model_resources.rsrc"
    rules = compiler_project / "model_resource.rules"
    rsrc.write_text(mesh_rsrc_text((spec,)), encoding="utf-8")
    rules.write_text(mesh_rules_text(), encoding="utf-8")

    commands = compiler_commands(
        compiler=compiler,
        project_name=project_name,
        platform=settings.platform,
        out_dir=compiler_output,
        compiler_workshop_dir=workshop,
        script_rules=rules,
        rsrc_path=rsrc,
        specs=(spec,),
        output_name=f"{resource_name}_pc.rpack",
    )
    command_results: list[dict[str, Any]] = []
    for index, command in enumerate(commands, 1):
        _log(log_callback, f"Running compiler stage {index}/{len(commands)}...")
        completed = subprocess.run(
            command,
            cwd=compiler_project,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        command_results.append(
            {
                "command": command,
                "returncode": completed.returncode,
                "stdout": completed.stdout,
                "stderr": completed.stderr,
            }
        )
        if completed.stdout:
            _log(log_callback, completed.stdout.rstrip())
        if completed.stderr:
            _log(log_callback, completed.stderr.rstrip())
        if completed.returncode != 0:
            raise ModelCompileError(
                f"Techland compiler stage {index} exited with code {completed.returncode}"
            )

    object_path = _find_compiled_object(compiler_output, resource_name)
    if object_path is None:
        raise ModelCompileError(
            f"Techland's mesh frontend completed but did not emit {resource_name}.msh_obj"
        )
    compact = inspect_msh_obj(object_path)
    validation = _validate_compact_result(
        compact,
        mode=effective_mode,
        expected_bones=expected_bones,
    )
    if not validation["ready"]:
        raise ModelCompileError(
            "Compiled mesh did not satisfy the expected entity contract: "
            + "; ".join(validation["errors"])
        )

    data_target = active_project / Path(virtual_path)
    assets_target = (
        active_project
        / "assets_pc"
        / Path(virtual_path).relative_to("data")
    ).with_suffix(".msh_obj")
    data_target.parent.mkdir(parents=True, exist_ok=True)
    assets_target.parent.mkdir(parents=True, exist_ok=True)
    for suffix in (".msh", ".ascr", ".bscr"):
        staged = staged_msh.with_suffix(suffix)
        if staged.is_file():
            shutil.copy2(staged, data_target.with_suffix(suffix))
    shutil.copy2(object_path, assets_target)

    report = {
        "format": "dl_reanimated_model_import_compile_v1",
        "resource_name": resource_name,
        "effective_mode": effective_mode,
        "source_msh": str(source),
        "compiler": str(compiler),
        "workshop_root": str(workshop),
        "compiler_project": str(compiler_project),
        "active_project": str(active_project),
        "virtual_path": virtual_path,
        "compiled_object": str(object_path),
        "installed_source": str(data_target),
        "installed_object": str(assets_target),
        "bootstrap": bootstrap,
        "commands": command_results,
        "compact_audit": compact,
        "validation": validation,
        "status": "compiled_and_installed",
        "timestamp_unix": time.time(),
    }
    report_path = out_root / "compile_and_install_report.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    report["report_path"] = str(report_path)
    _log(log_callback, f"Installed source: {data_target}")
    _log(log_callback, f"Installed compiled object: {assets_target}")
    return report


def _replace_generated_project(path: Path, *, kind: str) -> None:
    if path.exists():
        marker = path / GENERATED_PROJECT_MARKER
        if not marker.is_file():
            raise ModelCompileError(
                f"Refusing to replace unmarked workshop project: {path}"
            )
        shutil.rmtree(path)
    path.mkdir(parents=True, exist_ok=True)
    (path / GENERATED_PROJECT_MARKER).write_text(
        json.dumps(
            {
                "format": "dl_reanimated_model_importer_generated_project_v1",
                "kind": kind,
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )


def _find_compiled_object(output: Path, resource_name: str) -> Path | None:
    exact = output / f"{resource_name}.msh_obj"
    if exact.is_file():
        return exact
    rows = sorted(
        output.rglob("*.msh_obj"),
        key=lambda path: path.stat().st_mtime_ns,
        reverse=True,
    )
    for row in rows:
        if row.stem == resource_name:
            return row
    return rows[0] if len(rows) == 1 else None


def _validate_compact_result(
    report: dict[str, Any],
    *,
    mode: str,
    expected_bones: int,
) -> dict[str, Any]:
    errors: list[str] = []
    resources = report.get("mesh_resources", [])
    if len(resources) != 1:
        errors.append(f"compiled object contains {len(resources)} mesh resources; expected 1")
    for row in resources:
        counts = row.get("type_counts", {})
        if mode == "static":
            if int(counts.get("MESH", 0)) < 1:
                errors.append("static import contains no runtime MESH entity")
        else:
            bones = int(counts.get("BONE", 0))
            skinned = int(counts.get("MESH_SKINNED", 0))
            if bones != expected_bones:
                errors.append(f"compiled object retained {bones} bones; expected {expected_bones}")
            if skinned < 1:
                errors.append("compiled object contains no MESH_SKINNED entity")
            body_flags = [
                int(entity["flags"], 16)
                for entity in row.get("entities", [])
                if entity.get("element_type_name") == "MESH_SKINNED"
            ]
            if body_flags and not all(value & 0x4700 == 0x4700 for value in body_flags):
                errors.append("one or more skinned mesh entities lost the 0x4700 render/animation defaults")
    return {
        "mode": mode,
        "expected_bones": expected_bones,
        "errors": errors,
        "ready": not errors,
    }


def _log(callback, message: str) -> None:
    if callback is not None:
        callback(str(message))
