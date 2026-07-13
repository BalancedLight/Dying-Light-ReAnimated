from __future__ import annotations

"""Techland DevTools mesh compiler staging used by the Models workspace."""

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence
import hashlib
import json
import os
import posixpath
import shutil
import subprocess
import time
import zipfile



DEFAULT_VIRTUAL_ROOT = "data/characters/dl_reanimated/mannequin"
DEFAULT_PACK_NAME = "common_anims_sp_pc.rpack"
DEFAULT_MESH_PACK_NAME = "mesh_resources_pc.rpack"
GENERATED_PROJECT_MARKER = ".chrome_mesh_tools_generated_project.json"

_BOOTSTRAP_EXACT = {
    "data/enginedefs.mth",
    "data/resourcepackcfg.scr",
    "data/varlist_descriptions.scr",
    "data/varlist_descriptions_game.scr",
    "data/quickaccessvarsdefault.scr",
    "data/map_creator_varlist.scr",
}
_BOOTSTRAP_PREFIXES = ("data/scripts/varlist",)

# The official Developer Tools install keeps compiler bootstrap files under
# <DevTools>/Engine/Data rather than inside the retail game's Data0.pak.
# Target keys are the paths expected by imports inside the staged compiler
# project. Candidate values are relative to Engine/Data.
_DEVTOOLS_BOOTSTRAP_CANDIDATES: dict[str, tuple[str, ...]] = {
    "data/enginedefs.mth": (
        "Shaders/Common/EngineDefs.mth",
        "EngineDefs.mth",
    ),
    "data/resourcepackcfg.scr": ("ResourcePackCfg.scr",),
    "data/varlist_descriptions.scr": ("varlist_descriptions.scr",),
    "data/varlist_descriptions_game.scr": ("varlist_descriptions_game.scr",),
    "data/quickaccessvarsdefault.scr": ("QuickAccessVarsDefault.scr",),
    "data/map_creator_varlist.scr": ("map_creator_varlist.scr",),
    "data/defaultrespackcompiler.rules": ("DefaultResPackCompiler.rules",),
    "data/resourcepackfolders.scr": ("ResourcePackFolders.scr",),
}


class MeshStageError(RuntimeError):
    pass


@dataclass(frozen=True)
class MeshResourceSpec:
    resource_name: str
    source_path: Path
    virtual_path: str
    skins: str = "Default"
    instances_limit: int = 1
    pos_compression: int = 0
    used_by_code: bool = True

    @property
    def frontend_options(self) -> str:
        return (
            f"skins={self.skins};"
            f"instances_limit={self.instances_limit};"
            f"pos_compression={self.pos_compression}"
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "resource_name": self.resource_name,
            "source_path": str(self.source_path),
            "virtual_path": self.virtual_path,
            "skins": self.skins,
            "instances_limit": self.instances_limit,
            "pos_compression": self.pos_compression,
            "frontend_options": self.frontend_options,
            "expected_compiled_object": str(
                compiled_mesh_object_path(
                    Path("compiler_project"), self.virtual_path
                )
            ),
        }


def _quote(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


def mesh_rsrc_text(specs: Sequence[MeshResourceSpec]) -> str:
    if not specs:
        raise MeshStageError("at least one mesh resource is required")
    lines = [
        'import "ResourcePackCfg.scr"',
        "",
        "sub main()",
        "{",
        "  configuration(cfg_common)",
        "  {",
    ]
    for spec in specs:
        lines.append(
            '    res( _MESH_, "{name}", "{path}", "{options}", {used});'.format(
                name=_quote(spec.resource_name),
                path=_quote(spec.virtual_path.replace("\\", "/")),
                options=_quote(spec.frontend_options),
                used="true" if spec.used_by_code else "false",
            )
        )
    lines.extend(
        [
            "  }",
            "",
            "  configuration(cfg_PC)",
            "  {",
            "  }",
            "}",
            "",
        ]
    )
    return "\n".join(lines)


def mesh_resource_pack_folders_text() -> str:
    return "\n".join(
        [
            'import "enginedefs.mth"',
            'import "ResourcePackCfg.scr"',
            "",
            "sub main()",
            "{",
            "    default(",
            "        MF_DEFAULT,",
            "        MF_SKINNING,",
            "        MF_SKINNING | MF_SKINNING_ONE_BONE,",
            "        MF_SKINNING | MF_MORPH_TARGETS",
            "    );",
            (
                '    path("data\\characters", MF_DEFAULT, MF_SKINNING, '
                "MF_SKINNING | MF_SKINNING_ONE_BONE, "
                "MF_SKINNING | MF_MORPH_TARGETS);"
            ),
            '    exclude("*.msh.dds");',
            '    exclude("*.eds.dds");',
            "    resources(_MESH_, _TEXTURE_, _MATERIAL_);",
            "}",
            "",
        ]
    )


def mesh_rules_text() -> str:
    return "\n".join(
        [
            'ResourceRule("*.msh")',
            'ResourceRule("*.mat")',
            'ResourceRule("*.dds")',
            "",
        ]
    )


def common_override_text() -> str:
    return "sub main()\n{\n}\n"


def _candidate_devtools_data_dirs(
    compiler: str | Path,
    explicit_data_dir: str | Path | None = None,
) -> list[Path]:
    """Return likely ``Engine/Data`` folders in priority order."""

    candidates: list[Path] = []
    if explicit_data_dir:
        candidates.append(Path(explicit_data_dir))

    compiler_path = Path(compiler)
    root = compiler_path.parent
    candidates.extend(
        (
            root / "Engine" / "Data",
            root / "engine" / "data",
            root.parent / "Engine" / "Data",
            root.parent / "engine" / "data",
        )
    )

    unique: list[Path] = []
    seen: set[str] = set()
    for candidate in candidates:
        key = os.path.normcase(os.path.abspath(os.fspath(candidate)))
        if key not in seen:
            seen.add(key)
            unique.append(candidate)
    return unique


def _copy_devtools_bootstrap(
    devtools_data_dir: str | Path,
    project_dir: str | Path,
) -> list[Path]:
    """Copy compiler scripts from an official ``Engine/Data`` directory."""

    source_root = Path(devtools_data_dir)
    project = Path(project_dir)
    if not source_root.is_dir():
        return []

    written: list[Path] = []
    for target_relative, source_candidates in _DEVTOOLS_BOOTSTRAP_CANDIDATES.items():
        source_path = next(
            (
                source_root / relative
                for relative in source_candidates
                if (source_root / relative).is_file()
            ),
            None,
        )
        if source_path is None:
            continue
        target = project / target_relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source_path, target)
        written.append(target)
    return written


def _extract_data0_bootstrap(
    data0_pak: str | Path,
    project_dir: str | Path,
) -> list[Path]:
    """Extract legacy fallback bootstrap scripts from a ZIP-style Data0.pak."""

    source = Path(data0_pak)
    project = Path(project_dir)
    if not source.is_file() or not zipfile.is_zipfile(source):
        return []

    written: list[Path] = []
    with zipfile.ZipFile(source) as pak:
        for info in pak.infolist():
            normalized = info.filename.lower().replace("\\", "/")
            if normalized in _BOOTSTRAP_EXACT or any(
                normalized.startswith(prefix) for prefix in _BOOTSTRAP_PREFIXES
            ):
                target = project / Path(normalized)
                # Developer Tools files are authoritative. Do not overwrite them
                # with older or absent retail-package copies.
                if target.exists():
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(pak.read(info))
                written.append(target)
    return written


def _stage_varlist_aliases(project: Path, written: list[Path]) -> None:
    for source_name, targets in (
        (
            project / "data/scripts/varlist.scr",
            ("data/varlist.scr", "varlist.scr"),
        ),
        (
            project / "data/scripts/varlist_main.scr",
            ("data/varlist_main.scr", "varlist_main.scr"),
        ),
    ):
        if source_name.exists():
            for relative in targets:
                target = project / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source_name, target)
                written.append(target)


def stage_compiler_bootstrap(
    *,
    compiler: str | Path,
    data0_pak: str | Path,
    project_dir: str | Path,
    devtools_data_dir: str | Path | None = None,
) -> dict[str, Any]:
    """Stage bootstrap files, preferring the official DevTools installation.

    Dying Light Developer Tools stores ``ResourcePackCfg.scr`` in
    ``Engine/Data`` and ``EngineDefs.mth`` in
    ``Engine/Data/Shaders/Common``. Older versions of this tool incorrectly
    expected both files in retail ``Data0.pak``.
    """

    project = Path(project_dir)
    written: list[Path] = []
    searched: list[str] = []
    selected_devtools_data: Path | None = None

    for candidate in _candidate_devtools_data_dirs(compiler, devtools_data_dir):
        searched.append(str(candidate))
        copied = _copy_devtools_bootstrap(candidate, project)
        if copied:
            written.extend(copied)
            selected_devtools_data = candidate
            break

    # Retain Data0.pak as a compatibility fallback for uncommon installs and
    # for optional varlist scripts, but it is no longer the primary source.
    written.extend(_extract_data0_bootstrap(data0_pak, project))
    _stage_varlist_aliases(project, written)

    required = (
        project / "data/enginedefs.mth",
        project / "data/resourcepackcfg.scr",
    )
    missing = [path for path in required if not path.is_file()]
    if missing:
        searched_text = ", ".join(searched) if searched else "no candidates"
        raise MeshStageError(
            "Required ResPack compiler scripts were not found. Searched "
            f"DevTools Engine/Data locations: {searched_text}. Missing staged "
            "files: " + ", ".join(str(path) for path in missing)
        )

    # Stable de-duplication for readable reports.
    unique_written: list[Path] = []
    seen_written: set[str] = set()
    for path in written:
        key = os.path.normcase(os.path.abspath(os.fspath(path)))
        if key not in seen_written:
            seen_written.add(key)
            unique_written.append(path)

    return {
        "source": (
            "devtools_engine_data"
            if selected_devtools_data is not None
            else "data0_pak_fallback"
        ),
        "devtools_data_dir": (
            str(selected_devtools_data) if selected_devtools_data is not None else None
        ),
        "searched_devtools_data_dirs": searched,
        # This structure is embedded directly in compile/install JSON reports.
        # Keep the public diagnostic payload JSON-native instead of leaking
        # pathlib objects from the internal staging implementation.
        "files": [str(path) for path in unique_written],
    }


def extract_compiler_bootstrap(data0_pak: str | Path, project_dir: str | Path) -> list[Path]:
    """Backward-compatible Data0-only helper retained for API consumers."""

    written = _extract_data0_bootstrap(data0_pak, project_dir)
    project = Path(project_dir)
    _stage_varlist_aliases(project, written)
    required = (
        project / "data/enginedefs.mth",
        project / "data/resourcepackcfg.scr",
    )
    missing = [path for path in required if not path.is_file()]
    if missing:
        raise MeshStageError(
            "Data0.pak fallback did not contain required compiler scripts: "
            + ", ".join(str(path) for path in missing)
        )
    return written


def compiled_mesh_object_path(
    project_dir: str | Path,
    virtual_path: str,
    platform: str = "PC",
) -> Path:
    normalized = virtual_path.replace("\\", "/").lstrip("/")
    if normalized.casefold().startswith("data/"):
        normalized = normalized[5:]
    return Path(project_dir) / f"assets_{platform.casefold()}" / Path(normalized + "_obj")


def compiler_project_mapping(
    project_dir: str | Path,
    requested_workshop_dir: str | Path,
) -> dict[str, Any]:
    """Resolve the directory model expected by ResPackCompilerConsole.

    ``/WorkshopDir`` is not a generic dependency folder.  It is the parent
    directory containing the project named by ``dn=``.  The compiler resolves
    source ``data/...`` paths through ``<WorkshopDir>/<dn>/data``.  Earlier GUI
    builds passed the user's real DevTools workshop together with an unrelated
    ``dn`` while running from a temporary external project.  The compiler then
    ignored the staged sources and updated only its built-in Engine/Data assets.
    """

    project = Path(project_dir).resolve()
    workshop_root = Path(requested_workshop_dir).resolve()
    project_name = project.name
    expected = (workshop_root / project_name).resolve()
    return {
        "requested_editor_workshop_dir": str(Path(requested_workshop_dir)),
        "effective_compiler_workshop_dir": str(workshop_root),
        "effective_compiler_project_name": project_name,
        "expected_compiler_project_dir": str(expected),
        "actual_compiler_project_dir": str(project),
        "mapping_valid": expected == project,
    }


def compiler_pack_command(
    *,
    compiler: str | Path,
    project_name: str,
    platform: str,
    out_dir: str | Path,
    compiler_workshop_dir: str | Path,
    script_rules: str | Path,
    rsrc_path: str | Path,
    output_name: str = DEFAULT_MESH_PACK_NAME,
) -> list[str]:
    """Build an RP6L pack from resources declared by ``/Script=<rsrc>``.

    Runtime evidence from GUI 0.2.5 proved that a positional RSRC selects the
    compiler's asset-validation mode (``0 assets found``), not resource-pack
    generation.  ``/Script=`` is the actual resource-script switch.  Loose
    resource switches are enabled as well so the compiler retains intermediate
    runtime resources when its normal pack destination differs by installation.
    """

    command = compiler_base_args(
        compiler=compiler,
        project_name=project_name,
        platform=platform,
        out_dir=out_dir,
        compiler_workshop_dir=compiler_workshop_dir,
        script_rules=script_rules,
        output_name=output_name,
        multiprocess=False,
        save_dependencies=True,
        loose_resources=True,
    )
    command.append(f"/Script={rsrc_path}")
    command.append(f"/ScriptOut={Path(out_dir) / (Path(output_name).stem + '_resolved.rsrc')}")
    return command

def compiler_base_args(
    *,
    compiler: str | Path,
    project_name: str,
    platform: str,
    out_dir: str | Path,
    compiler_workshop_dir: str | Path,
    script_rules: str | Path,
    output_name: str = DEFAULT_MESH_PACK_NAME,
    multiprocess: bool = True,
    save_dependencies: bool = True,
    loose_resources: bool = False,
) -> list[str]:
    """Build common compiler arguments for a staged workshop project.

    ``dn`` is not merely an output label.  The compiler resolves the source
    data directory as ``<WorkshopDir>/<dn>/Data``.  Therefore the effective
    project name must equal the staged project directory name and
    ``WorkshopDir`` must be that directory's parent.
    """

    workshop = str(compiler_workshop_dir).replace("\\", "/").rstrip("/") + "/"
    args = [
        str(compiler),
        f"dn={project_name}",
        f"platform={platform}",
        f"output={output_name}",
        f"out={out_dir}",
        "/Verbose",
        "/ShowFiles",
        "/ShowMissingFiles",
    ]
    if save_dependencies:
        args.append("/SaveDependencies")
    args.extend(
        [
            "/FC-",
            "/FS-",
            f"/WorkshopDir={workshop}",
            f"/ScriptRules={script_rules}",
        ]
    )
    if multiprocess:
        args.append("/MP")
    if loose_resources:
        args.extend(("/LooseResources", "/LooseGpuResources"))
    return args


def compiler_update_filter(specs: Sequence[MeshResourceSpec]) -> str:
    """Return the narrowest shared virtual source-directory wildcard.

    ``*.*`` made the 0.2.4 diagnostic update every file selected by Techland's
    broad default folder script.  All first-milestone candidates live in one
    directory, so updating only that directory is both deterministic and much
    easier to diagnose.
    """

    if not specs:
        raise MeshStageError("at least one mesh resource is required")
    directories = [
        posixpath.dirname(spec.virtual_path.replace("\\", "/").lstrip("/"))
        for spec in specs
    ]
    common = posixpath.commonpath(directories) if directories else ""
    if not common or common in (".", "/"):
        return "*.*"
    return common.rstrip("/") + "/*.*"


def compiler_commands(
    *,
    compiler: str | Path,
    project_name: str,
    platform: str,
    out_dir: str | Path,
    compiler_workshop_dir: str | Path,
    script_rules: str | Path,
    rsrc_path: str | Path,
    specs: Sequence[MeshResourceSpec],
    output_name: str = DEFAULT_MESH_PACK_NAME,
) -> list[list[str]]:
    """Return the source-update and direct-RSRC pack commands.

    Resource discovery uses ``<WorkshopDir>/<dn>/data``.  Once that mapping is
    correct, ``-updatefromrscr ... -update <shared-source-dir>/*.*`` updates only
    the declared source directory in the staged project.  The final pack step
    uses the compiler's explicit ``/Script=`` option.  GUI 0.2.5 proved that a
    positional RSRC only runs validation and emits no pack.
    """

    if not specs:
        raise MeshStageError("at least one mesh resource is required")
    update_filter = compiler_update_filter(specs)
    update_base = compiler_base_args(
        compiler=compiler,
        project_name=project_name,
        platform=platform,
        out_dir=out_dir,
        compiler_workshop_dir=compiler_workshop_dir,
        script_rules=script_rules,
        output_name=output_name,
        multiprocess=True,
        save_dependencies=True,
        loose_resources=True,
    )
    return [
        [
            *update_base,
            f"-updatefromrscr={rsrc_path}",
            "-update",
            update_filter,
        ],
        compiler_pack_command(
            compiler=compiler,
            project_name=project_name,
            platform=platform,
            out_dir=out_dir,
            compiler_workshop_dir=compiler_workshop_dir,
            script_rules=script_rules,
            rsrc_path=rsrc_path,
            output_name=output_name,
        ),
    ]


