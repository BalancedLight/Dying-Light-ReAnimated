from __future__ import annotations

"""Windows-only Techland DevTools compiler and loose-project installer."""

from dataclasses import dataclass
from pathlib import Path
from typing import Any
import json
import math
import os
import shutil
import subprocess
import time

from .vendor.chrome_mesh_tools.compact_mesh import inspect_msh_obj
from .vendor.chrome_mesh_tools.source_contract import audit_source_msh_for_compiler
from .rig_contract import AuthoredRigContract
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
    # Source and generated-rig integrity are checked before settings validation
    # creates an output directory or a compiler staging project.
    source_preflight = preflight_model_compile(
        source_msh=source_msh,
        source_report=source_report,
    )
    settings.validate()
    source = Path(source_msh)
    if not source.is_file():
        raise ModelCompileError(f"source MSH was not found: {source}")
    resource_name = str(source_report["resource_name"])
    effective_mode = str(source_report["effective_mode"])
    expected_bones = int(source_report.get("bone_count", 0))
    expected_helpers = int(source_report.get("helper_count", 0))
    expected_model_bounds = source_report.get("model_bounds")
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
        expected_helpers=expected_helpers,
        expected_model_bounds=(
            expected_model_bounds if isinstance(expected_model_bounds, dict) else None
        ),
    )
    if not validation["ready"]:
        raise ModelCompileError(
            "Compiled mesh did not satisfy the expected entity contract: "
            + "; ".join(validation["errors"])
        )
    mesh_audit = compact["mesh_resources"][0]
    aggregate = mesh_audit.get("bone_bounds_global_aggregate", {})
    if effective_mode != "static":
        _log(
            log_callback,
            "Compiled rig validation: "
            f"{int(mesh_audit.get('bone_count', 0))} bones, "
            f"{int(mesh_audit.get('skinned_mesh_count', 0))} skinned meshes, "
            f"{int(aggregate.get('contributing_bone_count', 0))} usable bone bounds, "
            f"aggregate diagonal {float(aggregate.get('diagonal_length', 0.0)):.3f} m",
        )

    data_target = active_project / Path(virtual_path)
    assets_target = (
        active_project
        / "assets_pc"
        / Path(virtual_path).relative_to("data")
    ).with_suffix(".msh_obj")
    data_target.parent.mkdir(parents=True, exist_ok=True)
    assets_target.parent.mkdir(parents=True, exist_ok=True)
    backup_dir = out_root / "install_backup" / time.strftime("%Y%m%d_%H%M%S")
    backed_up_files: list[str] = []
    for existing in (
        data_target,
        data_target.with_suffix(".ascr"),
        data_target.with_suffix(".bscr"),
        assets_target,
    ):
        if existing.is_file():
            backup_dir.mkdir(parents=True, exist_ok=True)
            backup = backup_dir / existing.name
            shutil.copy2(existing, backup)
            backed_up_files.append(str(backup))
    removed_stale_companions: list[str] = []
    for suffix in (".msh", ".ascr", ".bscr"):
        staged = staged_msh.with_suffix(suffix)
        target = data_target.with_suffix(suffix)
        if staged.is_file():
            shutil.copy2(staged, target)
        elif suffix != ".msh" and target.is_file():
            # A previous build may have auto-attached anims_man_all.ascr.  If
            # the current exact/fitted bind intentionally has no alias, leaving
            # that stale file beside the new MSH makes the next editor compile
            # silently reintroduce incompatible stock tracks.
            target.unlink()
            removed_stale_companions.append(str(target))
    shutil.copy2(object_path, assets_target)

    report = {
        "format": "dl_reanimated_model_import_compile_v1",
        "resource_name": resource_name,
        "effective_mode": effective_mode,
        "source_msh": str(source),
        "source_preflight": source_preflight,
        "compiler": str(compiler),
        "workshop_root": str(workshop),
        "compiler_project": str(compiler_project),
        "active_project": str(active_project),
        "virtual_path": virtual_path,
        "compiled_object": str(object_path),
        "installed_source": str(data_target),
        "installed_object": str(assets_target),
        "install_backups": backed_up_files,
        "removed_stale_companions": removed_stale_companions,
        "bootstrap": bootstrap,
        "commands": command_results,
        "compact_audit": compact,
        "validation": validation,
        "status": "compiled_and_installed",
        "timestamp_unix": time.time(),
    }
    report_path = out_root / "compile_and_install_report.json"
    # Report contributors should return JSON-native values, but accept
    # os.PathLike diagnostics defensively so a successful compile can never be
    # reported as failed merely because metadata contains a Path.
    report_path.write_text(
        json.dumps(report, indent=2, default=os.fspath) + "\n",
        encoding="utf-8",
    )
    report["report_path"] = str(report_path)
    _log(log_callback, f"Installed source: {data_target}")
    _log(log_callback, f"Installed compiled object: {assets_target}")
    return report


def preflight_model_compile(
    *,
    source_msh: str | Path,
    source_report: dict[str, Any],
) -> dict[str, Any]:
    """Validate model/compiler inputs without creating or changing any output."""

    source = Path(source_msh)
    if not source.is_file():
        raise ModelCompileError(
            f"Model compile preflight could not read source MSH: {source}. Build Source MSH "
            "from the model FBX first; no compiler or install output was created."
        )
    missing = [
        key for key in ("resource_name", "effective_mode") if key not in source_report
    ]
    if missing:
        raise ModelCompileError(
            "Model compile preflight received an incomplete build report (missing "
            + ", ".join(missing)
            + "). Rebuild the source from its FBX before compiling; no output was created."
        )
    try:
        audit = audit_source_msh_for_compiler(source)
    except (OSError, ValueError) as exc:
        raise ModelCompileError(
            f"Model compile preflight could not parse {source.name!r}: {exc}. Rebuild the "
            "source from a supported binary FBX; Exact Rig is viable when humanoid fitting "
            "is the unsupported step. No compiler or install output was created."
        ) from exc
    if not audit.get("ready", False):
        raise ModelCompileError(
            f"Model compile preflight rejected {source.name!r} before output:\n- "
            + "\n- ".join(str(value) for value in audit.get("errors", ()))
            + "\nSafe action: rebuild the source MSH from the original FBX after correcting "
            "the named geometry/hierarchy issue. Exact Rig remains an alternative to fitted "
            "humanoid mode where applicable."
        )

    contract_payload = source_report.get("authored_rig_contract")
    generated = source_report.get("generated_crig")
    contract: AuthoredRigContract | None = None
    if isinstance(contract_payload, dict):
        try:
            contract = AuthoredRigContract.from_dict(contract_payload)
        except (KeyError, TypeError, ValueError) as exc:
            raise ModelCompileError(
                "Model compile preflight found an invalid authored rig identity in the build "
                f"report: {exc} Rebuild the Source MSH and CRIG together from the model FBX; "
                "no compiler output was created."
            ) from exc
        contract_validation = contract.validate()
        audit["authored_rig_identity"] = {
            "status": "pass",
            "contract_id": contract.contract_id,
            "canonical_contract_id": contract_validation["canonical_contract_id"],
            "contract_identity_scheme": contract_validation[
                "contract_identity_scheme"
            ],
            "bind_hash": contract.bind_hash,
            "skeleton_hash": contract.skeleton_hash,
            "descriptor_hash": contract.descriptor_hash,
        }
    if isinstance(generated, dict) and contract is None:
        raise ModelCompileError(
            "Model compile preflight found generated CRIG metadata without an authored MSH "
            "rig contract. Rebuild the model and CRIG together before compiling; no output "
            "was created."
        )
    if contract is not None and isinstance(generated, dict):
        expected_identity = {
            "bind": contract.bind_hash,
            "skeleton": contract.skeleton_hash,
            "descriptor": contract.descriptor_hash,
        }
        generated_identity = {
            "bind": str(
                generated.get("bind_hash", generated.get("authored_bind_hash", ""))
                or ""
            ),
            "skeleton": str(
                generated.get(
                    "skeleton_hash", generated.get("authored_skeleton_hash", "")
                )
                or ""
            ),
            "descriptor": str(
                generated.get(
                    "descriptor_hash", generated.get("authored_descriptor_hash", "")
                )
                or ""
            ),
        }
        for label, expected_value in expected_identity.items():
            generated_value = generated_identity[label]
            if generated_value and generated_value != expected_value:
                raise ModelCompileError(
                    "Model compile preflight found a stale generated CRIG selection: "
                    f"build-report authored {label} identity {generated_value!r} does not "
                    f"match the current MSH contract {expected_value!r}. Rebuild/install "
                    "this model's CRIG and use that generated rig in Animations; no compiler "
                    "output was created."
                )
        contract_validation = contract.validate()
        allowed_contract_ids = {
            contract.contract_id,
            str(contract_validation["canonical_contract_id"]),
            f"authored:{contract.bind_hash[:24]}",
        }
        generated_contract = str(generated.get("contract_id", "") or "")
        if generated_contract and generated_contract not in allowed_contract_ids:
            raise ModelCompileError(
                "Model compile preflight found a CRIG from a different authored rig contract. "
                "Its contract ID is not the composite or legacy ID for the current bind, "
                "skeleton, and descriptor identity. Rebuild the source and CRIG together "
                "before compiling; no output was created."
            )
        crig_value = str(generated.get("path", "") or "")
        if crig_value:
            crig_path = Path(crig_value)
            if not crig_path.is_file():
                raise ModelCompileError(
                    f"Model compile preflight expected generated CRIG {crig_path}, but it is "
                    "missing. Rebuild/install the generated rig before compiling; no output "
                    "was created."
                )
            try:
                from ..chrome_rig import ChromeRig

                rig = ChromeRig.load(crig_path)
            except (OSError, ValueError) as exc:
                raise ModelCompileError(
                    f"Model compile preflight could not load generated CRIG {crig_path.name!r}: "
                    f"{exc}. Rebuild it from this model source; no output was created."
                ) from exc
            actual_identity = {
                "bind": str(rig.extensions.get("authored_bind_hash", "") or ""),
                "skeleton": str(
                    rig.extensions.get("authored_skeleton_hash", "") or ""
                ),
                "descriptor": str(
                    rig.extensions.get("authored_descriptor_hash", "") or ""
                ),
            }
            actual_contract = str(
                rig.extensions.get("authored_rig_contract_id", "") or ""
            )
            for label, expected_value in expected_identity.items():
                actual_value = actual_identity[label]
                if actual_value != expected_value:
                    detail = (
                        f"declares {actual_value!r}"
                        if actual_value
                        else "does not declare that identity"
                    )
                    raise ModelCompileError(
                        f"Generated CRIG {crig_path.name!r} has stale authored {label} "
                        f"identity: it {detail}, but the current MSH contract requires "
                        f"{expected_value!r}. Rebuild/install the CRIG generated with this "
                        "model and reselect it in Animations; no output was created."
                    )
            if actual_contract not in allowed_contract_ids:
                raise ModelCompileError(
                    f"Generated CRIG {crig_path.name!r} belongs to contract "
                    f"{actual_contract!r}, which is neither the composite nor legacy ID for "
                    "the current bind, skeleton, and descriptor identity. Rebuild the model "
                    "and CRIG as one unit; no output was created."
                )
            audit["authored_rig_identity"]["generated_crig_verified"] = True
        elif not all(generated_identity.values()):
            missing_identity = [
                label for label, value in generated_identity.items() if not value
            ]
            raise ModelCompileError(
                "Model compile preflight cannot prove generated CRIG coherence because the "
                "build report has no CRIG path and is missing authored "
                + ", ".join(missing_identity)
                + " identity metadata. Rebuild the model and CRIG together before compiling; "
                "no output was created."
            )
        else:
            audit["authored_rig_identity"]["generated_crig_verified"] = True
    return audit


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
    expected_helpers: int = 0,
    expected_model_bounds: dict[str, Any] | None = None,
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
            if row.get("global_transform_error"):
                errors.append(
                    "compiled compact hierarchy cannot reconstruct global transforms: "
                    + str(row["global_transform_error"])
                )
            bones = int(counts.get("BONE", 0))
            helpers = int(counts.get("HELPER", 0))
            skinned = int(counts.get("MESH_SKINNED", 0))
            if bones != expected_bones:
                errors.append(f"compiled object retained {bones} bones; expected {expected_bones}")
            if skinned < 1:
                errors.append("compiled object contains no MESH_SKINNED entity")
            if helpers != expected_helpers:
                errors.append(
                    f"compiled object retained {helpers} helpers; expected {expected_helpers}"
                )
            animation_entities = expected_bones + expected_helpers
            if (
                "animation_entity_count_candidate" in row
                and int(row.get("animation_entity_count_candidate", -1)) != animation_entities
            ):
                errors.append(
                    "compiled animation-entity prefix is "
                    f"{int(row.get('animation_entity_count_candidate', -1))}; "
                    f"expected {animation_entities} BONE/HELPER entities before geometry"
                )
            body_flags = [
                int(entity["flags"], 16)
                for entity in row.get("entities", [])
                if entity.get("element_type_name") == "MESH_SKINNED"
            ]
            if body_flags and not all(value & 0x4700 == 0x4700 for value in body_flags):
                errors.append("one or more skinned mesh entities lost the 0x4700 render/animation defaults")
            if body_flags and not all(value & 0x1 for value in body_flags):
                errors.append("one or more skinned mesh entities lost the animated-node flag")
            bone_entities = [
                entity
                for entity in row.get("entities", [])
                if entity.get("element_type_name") == "BONE"
            ]
            helper_entities = [
                entity
                for entity in row.get("entities", [])
                if entity.get("element_type_name") == "HELPER"
            ]
            nonanimated_bones = [
                str(entity.get("name", "<unnamed>"))
                for entity in bone_entities
                if not (int(str(entity.get("flags", "0")), 16) & 0x1)
            ]
            if nonanimated_bones:
                names = ", ".join(nonanimated_bones[:8])
                suffix = "..." if len(nonanimated_bones) > 8 else ""
                errors.append(
                    f"compiled object contains {len(nonanimated_bones)} bones without the "
                    f"animated-node flag ({names}{suffix})"
                )
            reference_errors = [
                float(entity.get("global_reference_identity_max_abs_error", math.inf))
                for entity in [*bone_entities, *helper_entities]
            ]
            if (
                not reference_errors
                or not all(math.isfinite(value) for value in reference_errors)
                or max(reference_errors) > 1.0e-3
            ):
                errors.append(
                    "compiled animation globals and inverse-global reference matrices are not "
                    "mutual inverses"
                )
            invalid_bounds: list[str] = []
            for entity in bone_entities:
                values = entity.get("bounds_center_half_extents", [])
                if len(values) != 6:
                    invalid_bounds.append(str(entity.get("name", "<unnamed>")))
                    continue
                try:
                    numbers = [float(value) for value in values]
                except (TypeError, ValueError):
                    invalid_bounds.append(str(entity.get("name", "<unnamed>")))
                    continue
                if (
                    not all(math.isfinite(value) for value in numbers)
                    or max(numbers[3:]) <= 1.0e-6
                ):
                    invalid_bounds.append(str(entity.get("name", "<unnamed>")))
            if invalid_bounds:
                names = ", ".join(invalid_bounds[:8])
                suffix = "..." if len(invalid_bounds) > 8 else ""
                errors.append(
                    f"compiled object contains {len(invalid_bounds)} bones with collapsed "
                    f"bounds ({names}{suffix}); ChromeEd would show tiny bone markers and "
                    "an invalid aggregate model box"
                )
            aggregate = row.get("bone_bounds_global_aggregate")
            if not isinstance(aggregate, dict):
                errors.append("compiled object is missing the aggregate bone-bound audit")
            else:
                contributing = int(aggregate.get("contributing_bone_count", 0))
                collapsed = int(aggregate.get("collapsed_bone_count", 0))
                invalid = int(aggregate.get("invalid_bone_count", 0))
                diagonal = float(aggregate.get("diagonal_length", 0.0))
                if (
                    contributing != expected_bones
                    or collapsed
                    or invalid
                    or not math.isfinite(diagonal)
                    or diagonal <= 0.1
                ):
                    errors.append(
                        "compiled aggregate bone bounds are unusable "
                        f"(contributing={contributing}/{expected_bones}, "
                        f"collapsed={collapsed}, invalid={invalid}, diagonal={diagonal:.6g} m)"
                    )
            if mode == "dying_light_humanoid":
                invalid_animation_flags = [
                    str(entity.get("name", "<unnamed>"))
                    for entity in bone_entities
                    if (
                        int(str(entity.get("flags", "0")), 16) & 0x700
                    ) not in {0x200, 0x300, 0x700}
                ]
                if invalid_animation_flags:
                    names = ", ".join(invalid_animation_flags[:8])
                    suffix = "..." if len(invalid_animation_flags) > 8 else ""
                    errors.append(
                        f"compiled fitted humanoid contains {len(invalid_animation_flags)} "
                        f"bones with unsupported animation flags ({names}{suffix}); expected "
                        "the stock ROT, POS|ROT, or root POS|ROT|SCL policies"
                    )
            if expected_model_bounds is not None:
                expected_name = str(expected_model_bounds.get("node_name", "")).casefold()
                entities = list(row.get("entities", []))
                carrier = next(
                    (
                        entity
                        for entity in entities
                        if str(entity.get("name", "")).casefold() == expected_name
                        and entity.get("element_type_name") == "MESH"
                    ),
                    None,
                )
                if carrier is None:
                    errors.append(
                        "compiled object is missing the ordinary-MESH model-bounds carrier"
                    )
                else:
                    first_skinned = min(
                        (
                            int(entity.get("index", 1 << 30))
                            for entity in entities
                            if entity.get("element_type_name") == "MESH_SKINNED"
                        ),
                        default=1 << 30,
                    )
                    if int(carrier.get("index", -1)) <= first_skinned:
                        errors.append(
                            "model-bounds carrier appears before skinned geometry and would "
                            "inflate the animation-entity prefix"
                        )
                    actual = [
                        float(value)
                        for value in carrier.get("bounds_center_half_extents", [])
                    ]
                    expected = [
                        *[float(value) for value in expected_model_bounds.get("center_xyz", [])],
                        *[float(value) for value in expected_model_bounds.get("half_extents_xyz", [])],
                    ]
                    if len(actual) != 6 or len(expected) != 6:
                        errors.append("model-bounds carrier has an incomplete compact AABB")
                    elif max(abs(left - right) for left, right in zip(actual, expected)) > 1.0e-3:
                        errors.append(
                            "compiled model-bounds carrier differs from the emitted geometry AABB"
                        )
                reference_bounds = row.get("reference_bounds_global_aggregate", {})
                actual_diagonal = float(reference_bounds.get("diagonal_length", 0.0))
                expected_diagonal = float(expected_model_bounds.get("diagonal_m", 0.0))
                if (
                    not math.isfinite(actual_diagonal)
                    or actual_diagonal < expected_diagonal * 0.95
                ):
                    errors.append(
                        "compiled reference bounds do not enclose the emitted mesh "
                        f"(actual diagonal={actual_diagonal:.6g} m, "
                        f"mesh diagonal={expected_diagonal:.6g} m)"
                    )
    return {
        "mode": mode,
        "expected_bones": expected_bones,
        "expected_helpers": expected_helpers,
        "errors": errors,
        "ready": not errors,
    }


def _log(callback, message: str) -> None:
    if callback is not None:
        callback(str(message))
