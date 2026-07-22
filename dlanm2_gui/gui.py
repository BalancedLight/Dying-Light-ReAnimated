"""Release-oriented PySide6 project GUI.

The interface is intentionally backed by the versioned project, retarget-profile,
and project-builder modules.  Widgets do not own build logic, which keeps the
GUI replaceable and makes later versions able to migrate old projects.
"""

from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass, field
import json
import os
from pathlib import Path
import shutil
import struct
import sys
import threading
from typing import Any, Callable

from . import __version__
from .chrome_rig import ChromeRig
from .chrome_rig_builder import build_chrome_rig_from_fbx
from .chrome_rig_registry import BUILTIN_MALE_RIG_REF, ChromeRigRegistry
from .anm2 import Anm2Header, HEADER_LENGTH
from .dl2_anm2 import detect_anm2_format, parse_dl2_header42
from .anm2_fbx import chrome_rig_from_fbx_skeleton
from .anm2_provenance import load_anm2_provenance
from .blender_fbx import discover_blender, export_anm2_to_fbx
from .background_tasks import BackgroundTaskRunner, TaskFailure
from .animation_targets import RetargetUiKind, resolve_animation_target, retarget_ui_kind
from .automatic_retarget import (
    DL2_ADVANCED_RIG_ID,
    build_automatic_retarget_plan,
    build_dl2_advanced_body_map_with_local_recipe,
    revalidate_verified_dl2_advanced_body_map,
)
from .bone_maps import (
    COMPONENT_POLICIES,
    TRANSFER_POLICIES,
    GenericBoneMap,
    BoneMapPair,
    auto_map_skeletons,
    mapping_profile_origin,
)
from .helper_profiles import recognized_helper_names, suggested_helper_source
from .helper_retarget import (
    HelperRetargetRule,
    helper_rules_from_dicts,
    helper_rules_to_dicts,
)
from .fbx_core import FbxDocument, resolve_fbx_declared_timebase
from .project_builder import build_project, export_project_anm2_files
from .fbx_preflight import classify_target_compatibility, preflight_fbx
from .model_importer.fbx_model import FbxImportTolerance
from .retarget_profiles import (
    HUMANOID_ROLES,
    ROLE_BY_ID,
    SourceBoneMappingProfile,
    auto_map_source_bones,
)
from .semantic_retarget import (
    BundledSemanticState,
    compile_bundled_semantic_profile,
    migrate_generic_map_to_semantic_profile,
    prepare_bundled_semantic_state,
    readiness_for_state,
)
from .retarget_recipes import (
    materialize_reviewed_retarget_recipe,
    revalidate_materialized_retarget_recipe,
    resolve_local_retarget_recipe,
)
from .retarget_mapping import auto_map_crig_to_fbx, source_mapping_evidence
from .root_mapping import read_smd_hierarchy, resolve_source_root
from .root_motion import RootHeadingMode, RootMotionMode, RootMotionSelection
from .locomotion import get_builtin_locomotion_profile
from .target_mapping_inventory import visible_extra_target_names
from .target_retarget_policy import build_target_retarget_policy
from .script_targets import (
    AnimationScriptTarget,
    BUILTIN_SCRIPT_TARGETS,
    DEFAULT_SCRIPT_TARGET_ID,
    ScriptTargetRegistry,
)
from .runtime_paths import resource_root, writable_application_root
from .game_profiles import (
    DL1_GAME_ID, DL2_GAME_ID, DL2_RIG_REF, GAME_PROFILES,
    apply_game_profile_defaults, apply_target_package_selection, get_game_profile,
)
from .workspace_project import (
    DlReanimatedProject,
    Anm2ToFbxItem,
    PROJECT_EXTENSION,
    ProjectAnimation,
)


_HELPER_COMPONENT_LABELS = {
    "rotation": "Rotation",
    "translation": "Translation",
    "rotation_translation": "Rotation + translation",
    "scale": "Scale",
    "full_transform": "Full transform",
}

_TRANSFER_LABELS = {
    "default": "Target default",
    "rest_relative": "Rest-relative",
    "rotation_delta": "Rotation delta",
    "global_bind_basis": "Global bind basis",
    "copy_local": "Copy local",
    "bind": "Bind pose",
}

_RECENT_PROJECTS_SETTING = "recent_projects"
_MAX_RECENT_PROJECTS = 10


@dataclass(slots=True)
class _AnimationImportRequest:
    """Immutable inputs captured before an FBX import leaves the UI thread."""

    paths: tuple[str, ...]
    existing: set[tuple[Path, str]]
    game_id: str
    retarget_mode: str
    target_rig_path: str
    resource_root: Path
    resource_prefix: str
    tolerance: FbxImportTolerance


@dataclass(slots=True)
class _AnimationImportResult:
    """Pure-Python import result applied by the Qt thread on completion."""

    rows: list[ProjectAnimation] = field(default_factory=list)
    mapping_profiles: dict[str, dict[str, Any]] = field(default_factory=dict)
    documents: dict[str, FbxDocument] = field(default_factory=dict)
    semantic_states: dict[str, BundledSemanticState] = field(default_factory=dict)
    repair_messages: list[str] = field(default_factory=list)
    blocked_messages: list[str] = field(default_factory=list)


def _apply_declared_animation_timing(
    row: ProjectAnimation,
    document: FbxDocument,
) -> None:
    # Adjacent-version integrations and lightweight test doubles may not yet
    # expose the v10 timing contract. Use the same explicit low-confidence
    # fallback as a parsed FBX with no usable timing declaration.
    timebase = getattr(document, "declared_timebase", None)
    if timebase is None:
        timebase = resolve_fbx_declared_timebase(None)
    rate = float(timebase.declared_fps)
    row.source_fps = rate
    row.sample_fps = rate
    row.playback_fps = rate
    row.fps = rate
    extensions = dict(row.extensions)
    extensions["timing_origin_v10"] = timebase.to_dict()
    row.extensions = extensions


def _exact_import_mapping_profile_for_request(
    document: FbxDocument,
    target_rig: ChromeRig,
    *,
    game_id: str,
    retarget_mode: str,
) -> tuple[GenericBoneMap, str]:
    """Build an import-time mapping without reaching back into a Qt controller."""

    compatibility = classify_target_compatibility(document, target_rig)
    verified_error = ""
    if (
        target_rig.rig_id == DL2_ADVANCED_RIG_ID
        and compatibility.get("classification") != "exact_identity"
    ):
        try:
            policy = build_target_retarget_policy(
                target_rig,
                game_id=game_id,
                clip_domain="body",
            )
            if not policy.automatic_routing_authorized:
                raise ValueError(
                    "the selected DL2 advanced target package did not pass coherence checks"
                )
            profile = build_dl2_advanced_body_map_with_local_recipe(
                document,
                target_rig,
                policy,
            )
            if mapping_profile_origin(profile) == "manually_reviewed":
                return profile, "Applied a matching live-validated local retarget recipe."
            certificate = dict(
                profile.extensions.get("automatic_retarget_certificate", {})
                or profile.extensions.get("verified_mapping_certificate", {})
                or {}
            )
            exact_rows = int(
                certificate.get(
                    "exact_target_subset_rows",
                    compatibility.get("exact_target_subset_rows", 0),
                )
                or 0
            )
            semantic_rows = int(certificate.get("semantic_rows", 0) or 0)
            bind_rows = int(
                certificate.get(
                    "target_bind_rows",
                    compatibility.get("target_bind_rows", 0),
                )
                or 0
            )
            return (
                profile,
                "Created a complete verified DL2 map: "
                f"{exact_rows} exact target-subset row(s), "
                f"{semantic_rows} semantic row(s), and "
                f"{bind_rows} target row(s) held at bind.",
            )
        except Exception as exc:
            # A failed certificate never authorizes routing. Preserve the
            # established editable automatic_repair/manual-review fallback.
            verified_error = str(exc)

    try:
        policy = build_target_retarget_policy(
            target_rig,
            game_id=game_id,
            clip_domain="body",
        )
        fresh = build_automatic_retarget_plan(
            document,
            target_rig,
            policy,
            clip_domain="body",
        )
        local = resolve_local_retarget_recipe(
            fresh,
            document,
            target_rig,
            policy,
        )
        if local.applied:
            assert local.recipe is not None
            profile = materialize_reviewed_retarget_recipe(
                local.recipe,
                document,
                target_rig,
                policy,
                clip_domain="body",
                profile_name="Reviewed local retarget recipe",
            )
            return profile, "Applied a matching live-validated local retarget recipe."
    except (TypeError, ValueError):
        # Generic/custom targets retain the established editable mapping
        # fallback when no safe reviewed recipe can be constructed.
        pass

    profile = auto_map_crig_to_fbx(
        target_rig,
        document.limb_models.keys(),
        document.parent_by_name,
        **source_mapping_evidence(document),
    )
    note = (
        f"Created an editable .crig map with {len(profile.pairs)} suggestion(s) "
        f"for {len(target_rig.bones)} target bones."
    )
    if verified_error:
        failure_action = (
            "Open Root & .crig Mapping"
            if retarget_mode == "exact"
            else "Open Retargeting and review the diagnostic"
        )
        profile.extensions["automatic_retarget_generation_failure"] = {
            "status": "needs_attention",
            "reason": "The safe DL2 body-map verification did not pass.",
            "action": failure_action,
            "diagnostic": verified_error,
        }
        note = (
            "The safe DL2 body map could not be verified. "
            + note
            + f" {failure_action}."
        )
    return profile, note


def _prepare_animation_import(
    request: _AnimationImportRequest,
    progress: Callable[[str], None],
    partial: Callable[[_AnimationImportResult], None] | None = None,
) -> _AnimationImportResult:
    """Parse and map FBX files off the Qt event thread."""

    result = _AnimationImportResult()
    existing = set(request.existing)
    target_rig: ChromeRig | None = None
    target_rig_error = ""
    automatic_dl2 = (
        request.retarget_mode == "auto" and request.game_id == DL2_GAME_ID
    )
    try:
        if request.retarget_mode == "exact" or automatic_dl2:
            if not request.target_rig_path:
                raise FileNotFoundError(
                    "No target .crig is selected. Choose or import one on the Project tab."
                )
            target_rig = ChromeRig.load(request.target_rig_path)
        elif request.game_id == DL1_GAME_ID:
            target_rig = ChromeRig.load(
                request.resource_root / "reference" / "male_npc_infected.crig"
            )
    except (OSError, ValueError) as exc:
        target_rig_error = str(exc)

    total = len(request.paths)
    for index, raw in enumerate(request.paths, start=1):
        row_start = len(result.rows)
        repair_start = len(result.repair_messages)
        blocked_start = len(result.blocked_messages)
        known_profiles = set(result.mapping_profiles)
        known_documents = set(result.documents)
        known_states = set(result.semantic_states)

        def publish_file_result() -> None:
            if partial is None:
                return
            partial(
                _AnimationImportResult(
                    rows=list(result.rows[row_start:]),
                    mapping_profiles={
                        key: value
                        for key, value in result.mapping_profiles.items()
                        if key not in known_profiles
                    },
                    documents={
                        key: value
                        for key, value in result.documents.items()
                        if key not in known_documents
                    },
                    semantic_states={
                        key: value
                        for key, value in result.semantic_states.items()
                        if key not in known_states
                    },
                    repair_messages=list(result.repair_messages[repair_start:]),
                    blocked_messages=list(result.blocked_messages[blocked_start:]),
                )
            )

        path = Path(raw).resolve()
        progress(f"Importing animation {index}/{total}: {path.name}")
        try:
            document = FbxDocument(
                path,
                purpose="animation",
                tolerance=request.tolerance,
            )
        except Exception:
            preflight = preflight_fbx(
                path,
                purpose="animation",
                game_id=request.game_id,
                tolerance=request.tolerance,
            )
            result.blocked_messages.append(
                f"{path.name}\n{preflight.actionable_message()}"
            )
            publish_file_result()
            continue

        result.documents[str(path)] = document
        stacks = list(document.animation_stacks)
        preferred_stack = (
            document.preferred_animation_stack()
            if hasattr(document, "preferred_animation_stack")
            else None
        )
        if preferred_stack is not None:
            selections = [preferred_stack.name]
        elif len(stacks) == 1:
            selections = [stacks[0].name]
        else:
            # Preserve every manual stack choice on one editable row when
            # curve activity has no unique winner.
            selections = [""]

        for stack_name in selections:
            key = (path, stack_name)
            if key in existing:
                continue
            multi = len(selections) > 1
            resource_seed = f"{path.stem}_{stack_name}" if multi else path.stem
            row = ProjectAnimation.create(
                str(path),
                resource_name=resource_seed,
                animation_stack=stack_name,
            )
            _apply_declared_animation_timing(row, document)
            if multi:
                row.display_name = f"{path.stem}: {stack_name}"
            if request.resource_prefix:
                row.resource_name = f"{request.resource_prefix}_{row.resource_name}"
            preflight = preflight_fbx(
                path,
                purpose="animation",
                animation_stack=stack_name or None,
                target_rig=target_rig,
                game_id=request.game_id,
                document=document,
                tolerance=request.tolerance,
            )
            row.extensions["fbx_preflight"] = preflight.to_dict()
            if preflight.import_blocking:
                result.blocked_messages.append(
                    f"{row.display_name}\n"
                    + preflight.actionable_message(
                        finding
                        for finding in preflight.findings
                        if finding.severity == "error" and not finding.can_continue
                    )
                )
                continue

            attention_findings = [
                finding
                for finding in preflight.findings
                if finding.severity == "warning"
                and finding.code != "multiple_animation_stacks"
            ]
            mapping_note = ""
            verified_mapping_ready = False
            try:
                if (
                    request.retarget_mode == "exact" or automatic_dl2
                ) and target_rig is not None:
                    compatibility = dict(
                        preflight.inventory.get("target_compatibility", {}) or {}
                    )
                    needs_target_map = (
                        compatibility.get("classification") != "exact_identity"
                    )
                    if preflight.repairable_findings or needs_target_map:
                        if stack_name and hasattr(document, "select_animation_stack"):
                            document.select_animation_stack(stack_name)
                        profile, mapping_note = _exact_import_mapping_profile_for_request(
                            document,
                            target_rig,
                            game_id=request.game_id,
                            retarget_mode=request.retarget_mode,
                        )
                        result.mapping_profiles[profile.profile_id] = profile.to_dict()
                        row.mapping_profile_id = profile.profile_id
                        if mapping_profile_origin(profile) == "automatic_verified":
                            row.extensions["retarget_domain"] = "body"
                            verified_mapping_ready = True
                        if automatic_dl2 and isinstance(profile, GenericBoneMap):
                            policy = build_target_retarget_policy(
                                target_rig,
                                game_id=request.game_id,
                                clip_domain="body",
                            )
                            semantic_profile = migrate_generic_map_to_semantic_profile(
                                profile,
                                document.limb_models,
                                document.parent_by_name,
                                policy,
                                name=f"Bundled humanoid mapping: {row.display_name}",
                            )
                            semantic_state = prepare_bundled_semantic_state(
                                document,
                                target_rig,
                                policy,
                                semantic_profile,
                                profile_name=f"Bundled humanoid mapping: {row.display_name}",
                            )
                            result.mapping_profiles[semantic_profile.profile_id] = (
                                semantic_profile.to_dict()
                            )
                            row.mapping_profile_id = semantic_profile.profile_id
                            row.extensions["legacy_target_map_profile_id"] = profile.profile_id
                            row.extensions["semantic_profile_migration"] = dict(
                                semantic_profile.extensions.get("migration_audit", {}) or {}
                            )
                            result.semantic_states[row.animation_id] = semantic_state
                elif request.retarget_mode in {"auto", "humanoid"}:
                    profile = auto_map_source_bones(
                        document.limb_models,
                        parents=document.parent_by_name,
                        profile_name=f"Humanoid mapping: {row.display_name}",
                    )
                    result.mapping_profiles[profile.profile_id] = profile.to_dict()
                    row.mapping_profile_id = profile.profile_id
            except Exception as exc:
                if (
                    target_rig is not None
                    and target_rig.rig_id == DL2_ADVANCED_RIG_ID
                ):
                    row.extensions["automatic_retarget_generation_failure"] = {
                        "status": "needs_attention",
                        "reason": "The safe DL2 body-map verification did not pass.",
                        "action": "Open Retargeting and review the diagnostic",
                        "diagnostic": str(exc),
                    }
                    mapping_note = (
                        "The safe DL2 body map could not be generated. "
                        "Open Retargeting and review the diagnostic."
                    )
                else:
                    mapping_note = (
                        f"The clip was added, but automatic mapping failed: {exc}. "
                        "Open its mapping editor and assign bones manually."
                    )

            repairable = preflight.repairable_findings
            grouped_findings: dict[str, list[dict[str, Any]]] = {}
            for finding in preflight.findings:
                grouped_findings.setdefault(finding.group, []).append(
                    {
                        "code": finding.code,
                        "outcome": finding.outcome,
                        "detected": finding.detected,
                        "action": finding.action,
                    }
                )
            if (
                preflight.findings
                or repairable
                or attention_findings
                or target_rig_error
                or mapping_note
            ):
                mapping_failure = bool(
                    row.extensions.get("automatic_retarget_generation_failure")
                )
                if mapping_failure or target_rig_error or (
                    repairable and not verified_mapping_ready
                ):
                    readiness_level = "needs_attention"
                    readiness_label = "Needs attention — review the selected target mapping"
                elif repairable:
                    readiness_level = "advisory"
                    readiness_label = "Advisory — mapping repair applied; export remains available"
                elif attention_findings:
                    readiness_level = "advisory"
                    readiness_label = preflight.readiness_label
                else:
                    readiness_level = preflight.readiness_level
                    readiness_label = preflight.readiness_label
                row.extensions["import_state"] = {
                    "status": readiness_level,
                    "level": readiness_level,
                    "label": readiness_label,
                    "requested_purpose": "animation",
                    "finding_groups": grouped_findings,
                    "repairable_codes": [finding.code for finding in repairable],
                    "warning_codes": [finding.code for finding in attention_findings],
                    "mapping_note": mapping_note,
                    "target_rig_error": target_rig_error,
                }
            row.extensions.setdefault(
                "import_state",
                {
                    "status": preflight.readiness_level,
                    "level": preflight.readiness_level,
                    "label": preflight.readiness_label,
                    "requested_purpose": "animation",
                    "finding_groups": grouped_findings,
                    "repairable_codes": [],
                    "warning_codes": [],
                    "mapping_note": "",
                    "target_rig_error": "",
                },
            )
            if repairable or attention_findings:
                explanation = preflight.actionable_message(
                    [*repairable, *attention_findings]
                )
                result.repair_messages.append(
                    f"{row.display_name}\n{mapping_note}\n\n{explanation}"
                )
            elif target_rig_error:
                result.repair_messages.append(
                    f"{row.display_name}\nThe clip was added, but the selected target .crig "
                    f"could not be loaded: {target_rig_error}\nChoose a valid target rig, then open mapping."
                )
            result.rows.append(row)
            existing.add(key)
        publish_file_result()
        progress(f"Finished animation {index}/{total}: {path.name}")
    return result


@dataclass(slots=True)
class _AutoRetargetRequest:
    source_fbx: str
    animation_stack: str
    display_name: str
    existing_profile_id: str
    game_id: str
    target_rig_path: str
    existing_profile: dict[str, Any]
    tolerance: FbxImportTolerance


@dataclass(slots=True)
class _AutoRetargetResult:
    document: FbxDocument
    profile: SourceBoneMappingProfile
    compiled_profile: GenericBoneMap | None = None
    semantic_state: BundledSemanticState | None = None
    migrated_from_profile_id: str = ""


def _prepare_auto_retarget(
    request: _AutoRetargetRequest,
    progress: Callable[[str], None],
) -> _AutoRetargetResult:
    """Rebuild an editable humanoid map (and DL2 certificate) in a worker."""

    path = Path(request.source_fbx).resolve()
    progress(f"Analyzing {path.name} for automatic retargeting…")
    document = FbxDocument(path, purpose="animation", tolerance=request.tolerance)
    if request.animation_stack and hasattr(document, "select_animation_stack"):
        document.select_animation_stack(request.animation_stack)

    if request.game_id != DL2_GAME_ID:
        profile = auto_map_source_bones(
            document.limb_models,
            parents=document.parent_by_name,
            profile_name=f"Humanoid mapping: {request.display_name}",
        )
        return _AutoRetargetResult(document, profile)

    if not request.target_rig_path:
        raise FileNotFoundError("The selected bundled target rig is unavailable")
    rig = ChromeRig.load(request.target_rig_path)
    policy = build_target_retarget_policy(rig, game_id=request.game_id, clip_domain="body")
    payload = dict(request.existing_profile)
    profile: SourceBoneMappingProfile | None = None
    migrated_from = ""
    if payload.get("format") == "dl-reanimated-retarget-profile":
        profile = SourceBoneMappingProfile.from_dict(payload)
    elif payload.get("format") == "dl-reanimated-bone-map":
        old_map = GenericBoneMap.from_dict(payload)
        profile = migrate_generic_map_to_semantic_profile(
            old_map,
            document.limb_models,
            document.parent_by_name,
            policy,
            name=f"Bundled humanoid mapping: {request.display_name}",
        )
        migrated_from = old_map.profile_id
    if profile is not None:
        profile.role_modes = {role_id: "auto" for role_id in profile.role_modes}
        profile.cleared_roles = []
        profile.clear_compiled_cache()

    progress("Building the automatic retarget plan…")
    state = prepare_bundled_semantic_state(
        document,
        rig,
        policy,
        profile,
        profile_name=f"Bundled humanoid mapping: {request.display_name}",
    )
    progress("Verifying the DL2 retarget map…")
    compiled, _verification, _plan = compile_bundled_semantic_profile(
        document, rig, policy, state.profile
    )
    return _AutoRetargetResult(
        document,
        state.profile,
        compiled_profile=compiled,
        semantic_state=state,
        migrated_from_profile_id=migrated_from,
    )


def _default_unknown_track_policy(game_id: str) -> str:
    return "sidecar" if game_id == DL2_GAME_ID else "helpers"


def _load_qt() -> dict[str, Any]:
    try:
        from PySide6.QtCore import QSettings, QTimer, QUrl, Qt
        from PySide6.QtGui import QAction, QDesktopServices, QKeySequence
        from PySide6.QtWidgets import (
            QAbstractItemView,
            QApplication,
            QButtonGroup,
            QCheckBox,
            QComboBox,
            QDialog,
            QDialogButtonBox,
            QDoubleSpinBox,
            QFileDialog,
            QFormLayout,
            QGroupBox,
            QHBoxLayout,
            QHeaderView,
            QLabel,
            QLineEdit,
            QListWidget,
            QMainWindow,
            QMessageBox,
            QPlainTextEdit,
            QProgressBar,
            QPushButton,
            QRadioButton,
            QSizePolicy,
            QSpinBox,
            QSplitter,
            QStatusBar,
            QTabWidget,
            QTableWidget,
            QTableWidgetItem,
            QToolBar,
            QVBoxLayout,
            QWidget,
        )
    except ModuleNotFoundError as exc:
        raise RuntimeError(
            "PySide6 is not installed. Run run_gui.bat or install the GUI extra with "
            "python -m pip install -e .[gui]."
        ) from exc
    return locals()


def main() -> int:
    try:
        qt = _load_qt()
    except RuntimeError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    app = qt["QApplication"].instance() or qt["QApplication"](sys.argv)
    app.setApplicationName("DL ReAnimated")
    app.setApplicationVersion(__version__)
    app.setStyleSheet(
        """
        QWidget { font-size: 10pt; }
        QTabBar::tab { padding: 8px 14px; }
        QGroupBox { margin-top: 10px; padding-top: 10px; }
        QGroupBox::title { font-weight: 600; subcontrol-origin: margin; left: 10px; padding: 0 4px; }
        QPushButton { min-height: 26px; padding: 4px 10px; }
        QLineEdit, QComboBox, QSpinBox, QDoubleSpinBox { min-height: 28px; }
        QTableWidget {
            gridline-color: palette(mid);
            alternate-background-color: palette(alternate-base);
        }
        QToolTip { padding: 5px; }
        """
    )
    controller = MainWindow(qt)
    controller.show()
    return int(app.exec())


class MainWindow:
    def __init__(self, qt: dict[str, Any]) -> None:
        self.qt = qt
        self.root = writable_application_root()
        self.resource_root = resource_root()
        self.settings = qt["QSettings"]("DL ReAnimated", "DL ReAnimated")
        controller = self

        combo_base = qt["QComboBox"]

        class _NoWheelComboBox(combo_base):
            """Ignore wheel changes unless the popup list is actually open."""

            def wheelEvent(self, event) -> None:  # type: ignore[override]
                if self.view().isVisible():
                    super().wheelEvent(event)
                else:
                    event.ignore()

        self._NoWheelComboBox = _NoWheelComboBox

        class _ProjectWindow(qt["QMainWindow"]):
            def closeEvent(self, event) -> None:  # type: ignore[override]
                if controller._background_work_active():
                    if (
                        controller.background_tasks.busy
                        and controller._animation_operation_kind
                    ):
                        controller._close_when_background_idle = True
                        controller.cancel_animation_operation()
                        controller._poll_close_when_background_idle()
                        controller.qt["QMessageBox"].information(
                            self,
                            "Cancelling animation work",
                            "The active animation import or retarget is being cancelled. "
                            "DL ReAnimated will close automatically at the next safe checkpoint.",
                        )
                        event.ignore()
                        return
                    controller.qt["QMessageBox"].information(
                        self,
                        "Work still running",
                        "Wait for the active build, export, or model operation to finish "
                        "before closing DL ReAnimated.",
                    )
                    event.ignore()
                    return
                if controller._confirm_discard_changes():
                    event.accept()
                else:
                    event.ignore()

        self.window = _ProjectWindow()
        self.background_tasks = BackgroundTaskRunner(self.window)
        self.window.setWindowTitle(f"DL ReAnimated {__version__}")
        self.window.resize(1380, 900)
        self.window.setMinimumSize(1050, 700)
        self.status = qt["QStatusBar"]()
        self.window.setStatusBar(self.status)
        self.tabs = qt["QTabWidget"]()
        self.window.setCentralWidget(self.tabs)

        self.project = self._new_default_project()
        self.project_path: Path | None = None
        self.dirty = False
        self._refreshing = False
        self._animation_operation_kind = ""
        self._close_when_background_idle = False
        self._source_cache: dict[str, FbxDocument] = {}
        self.mapping_navigation_callback = None
        self.target_selection_changed_callback = None
        self.recent_projects_changed_callback = None
        self._target_rig_cache: dict[str, ChromeRig] = {}
        self.script_registry = ScriptTargetRegistry()
        self.rig_registry = ChromeRigRegistry(self.root / "rigs")

        self._build_toolbar()
        self._build_project_tab()
        self._build_animations_tab()
        self._build_retarget_tab()
        self._build_export_tab()
        self._build_anm2_to_fbx_tab()
        self._build_help_tab()
        # DLR_MIMIC_PROTOTYPE_BEGIN - facial workspace integration
        from .mimic_gui import install_mimic_ui
        install_mimic_ui(self)
        # DLR_MIMIC_PROTOTYPE_END - facial workspace integration
        self._refresh_all()

    def show(self) -> None:
        self.window.show()

    # ------------------------------------------------------------------ setup
    def _new_default_project(self) -> DlReanimatedProject:
        project = DlReanimatedProject.new()
        project.rig.canonical_smd = str(self.resource_root / "reference" / "player_1_tpp.smd")
        project.rig.target_template_anm2 = str(
            self.resource_root / "reference" / "infected_turn_90r.template.anm2"
        )
        project.rig.stock_writer_control_anm2 = str(
            self.resource_root / "reference" / "stock_writer_control.anm2"
        )
        trusted_rest = self.resource_root / "reference" / "same_model_tpose_20260619.json"
        project.rig.trusted_source_rest_json = str(trusted_rest) if trusted_rest.is_file() else ""
        project.export.output_directory = str(self.root / "build")
        project.anm2_to_fbx.output_directory = str(self.root / "build" / "fbx")
        project.export.default_script_target = DEFAULT_SCRIPT_TARGET_ID
        apply_game_profile_defaults(project, self.resource_root, force=True)
        return project

    def _build_toolbar(self) -> None:
        qt = self.qt
        toolbar = qt["QToolBar"]("Project")
        toolbar.setMovable(False)
        self.window.addToolBar(toolbar)

        def action(text: str, shortcut: str | None, callback) -> Any:
            row = qt["QAction"](text, self.window)
            if shortcut:
                row.setShortcut(qt["QKeySequence"](shortcut))
            row.triggered.connect(callback)
            toolbar.addAction(row)
            return row

        action("New", "Ctrl+N", self.new_project)
        action("Open", "Ctrl+O", self.open_project)
        action("Save", "Ctrl+S", self.save_project)
        action("Save As", "Ctrl+Shift+S", self.save_project_as)
        toolbar.addSeparator()
        action("Add FBX", "Ctrl+I", self.add_animations)
        action("Build RPack", "Ctrl+B", self.build_rpack)
        toolbar.addSeparator()
        action("Documentation", "F1", lambda: self.open_doc("GUI_GUIDE.md"))

    def _build_project_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        layout.setSpacing(10)

        mode_row = qt["QHBoxLayout"]()
        welcome = qt["QLabel"](
            "Create a project, add one or more FBX animations, confirm the humanoid mapping, "
            "then build an editor-ready RPack."
        )
        welcome.setWordWrap(True)
        self.advanced_mode_toggle = qt["QCheckBox"]("Show advanced settings")
        self.advanced_mode_toggle.setToolTip(
            "Shows custom target-rig files, diagnostic controls, intermediate outputs, and "
            "developer-oriented options. Normal Mixamo imports do not need these fields."
        )
        self.advanced_mode_toggle.toggled.connect(self._advanced_mode_changed)
        mode_row.addWidget(welcome, 1)
        mode_row.addWidget(self.advanced_mode_toggle)
        layout.addLayout(mode_row)

        basics = qt["QGroupBox"]("Project")
        form = qt["QFormLayout"](basics)
        self.project_name = qt["QLineEdit"]()
        self.project_name.setToolTip("A friendly name stored inside the .dlraproj project file.")
        self.project_name.textChanged.connect(self._mark_dirty)
        self.project_notes = qt["QPlainTextEdit"]()
        self.project_notes.setMaximumHeight(84)
        self.project_notes.setPlaceholderText("Optional notes about this animation set…")
        self.project_notes.textChanged.connect(self._mark_dirty)
        self.game_combo = self._combo_box()
        self.game_combo.addItem("Dying Light 1", DL1_GAME_ID)
        self.game_combo.addItem("Dying Light 2", DL2_GAME_ID)
        self.game_combo.setToolTip("Selects one coherent target rig, root policy, reference ANM2 format, and workspace profile.")
        self.game_combo.currentIndexChanged.connect(self._game_changed)
        self.game_status = qt["QLabel"]()
        self.game_status.setWordWrap(True)
        self.import_tolerance_combo = self._combo_box()
        self.import_tolerance_combo.addItem(
            "Recommended / forgiving (FBX parsing)",
            FbxImportTolerance.RECOMMENDED.value,
        )
        self.import_tolerance_combo.addItem(
            "Strict diagnostics", FbxImportTolerance.STRICT_DIAGNOSTICS.value
        )
        self.import_tolerance_combo.setToolTip(
            "Controls recoverable FBX parsing and geometry diagnostics. It does not approve "
            "cross-rig bone mappings or bypass skeleton safety checks."
        )
        self.import_tolerance_combo.currentIndexChanged.connect(self._mark_dirty)
        form.addRow("Game", self.game_combo)
        form.addRow("Target status", self.game_status)
        form.addRow("Import tolerance", self.import_tolerance_combo)
        form.addRow("Project name", self.project_name)
        form.addRow("Notes", self.project_notes)
        layout.addWidget(basics)

        rig = qt["QGroupBox"]("Source avatar and target rig")
        rig_form = qt["QFormLayout"](rig)
        self.target_rig_combo = self._combo_box()
        self._reload_target_rig_combo()
        self.target_rig_combo.setToolTip(
            "The bundled target uses humanoid retargeting. Installed .crig targets use "
            "exact same-skeleton mapping for objects, machinery, animals, or custom models."
        )
        self.target_rig_combo.currentIndexChanged.connect(self._target_rig_changed)
        rig_form.addRow("Default target rig", self.target_rig_combo)

        self.custom_rig_actions = qt["QWidget"]()
        custom_rig_row = qt["QHBoxLayout"](self.custom_rig_actions)
        custom_rig_row.setContentsMargins(0, 0, 0, 0)
        import_rig_button = qt["QPushButton"]("Import .crig…")
        import_rig_button.clicked.connect(self.import_chrome_rig)
        create_rig_button = qt["QPushButton"]("Create .crig from model FBX…")
        create_rig_button.clicked.connect(self.create_chrome_rig)
        manage_rigs_button = qt["QPushButton"]("Manage rigs…")
        manage_rigs_button.clicked.connect(self.manage_chrome_rigs)
        custom_rig_row.addWidget(import_rig_button)
        custom_rig_row.addWidget(create_rig_button)
        custom_rig_row.addWidget(manage_rigs_button)
        custom_rig_row.addStretch(1)
        rig_form.addRow("Custom targets", self.custom_rig_actions)
        self.custom_rig_actions_label = rig_form.labelForField(self.custom_rig_actions)

        self.use_imported_bind_pose = qt["QCheckBox"](
            "Use imported animation FBX bind pose (recommended)"
        )
        self.use_imported_bind_pose.setToolTip(
            "Recommended. Reads the unanimated/bind transforms stored in each imported FBX, "
            "so a separate T-pose file is not required. Disable this only when the animation "
            "FBX has missing or unreliable bind data."
        )
        self.use_imported_bind_pose.toggled.connect(self._bind_pose_mode_changed)
        rig_form.addRow(self.use_imported_bind_pose)

        self.source_rest_path = self._path_row(
            rig_form,
            "Separate source rest / T-pose FBX",
            "FBX (*.fbx)",
            directory=False,
            tooltip=(
                "Fallback rest-pose file for the same source skeleton. This is only used when "
                "embedded bind-pose mode is disabled."
            ),
        )
        layout.addWidget(rig)

        self.advanced_rig_group = qt["QGroupBox"]("Advanced target-rig files")
        advanced_form = qt["QFormLayout"](self.advanced_rig_group)
        self.trusted_rest_path = self._path_row(
            advanced_form,
            "Trusted source-rest JSON",
            "JSON (*.json)",
            directory=False,
            tooltip="Optional matrix oracle used to validate a known source rest pose.",
        )
        self.canonical_smd_path = self._path_row(
            advanced_form,
            "Target bind skeleton (SMD)",
            "SMD (*.smd)",
            directory=False,
            tooltip="Defines the Dying Light target hierarchy, bone lengths, bind pose, and roll.",
        )
        self.template_anm2_path = self._path_row(
            advanced_form,
            "Target ANM2 template",
            "ANM2 (*.anm2)",
            directory=False,
            tooltip="Provides the target descriptor table and ANM2 layout used by the writer.",
        )
        self.stock_control_path = self._path_row(
            advanced_form,
            "Writer regression control",
            "ANM2 (*.anm2)",
            directory=False,
            tooltip="Known-good ANM2 used only for optional writer validation controls.",
        )
        layout.addWidget(self.advanced_rig_group)

        defaults = qt["QGroupBox"]("Project defaults")
        default_form = qt["QFormLayout"](defaults)
        self.default_script_combo = self._script_combo(include_project_default=False)
        self.default_script_combo.setToolTip(
            "Selects the _ANIMATION_SCR_ resource that receives this project's sequences."
        )
        self.default_script_combo.currentTextChanged.connect(self._script_default_changed)
        self.script_description = qt["QLabel"]()
        self.script_description.setWordWrap(True)
        self.custom_script_resource = qt["QLineEdit"]()
        self.custom_script_resource.setPlaceholderText("Example: anims_my_character_all")
        self.custom_script_resource.setToolTip(
            "Advanced: exact custom _ANIMATION_SCR_ resource name to create or append."
        )
        self.custom_script_resource.textChanged.connect(self._script_default_changed)
        self.resource_prefix = qt["QLineEdit"]()
        self.resource_prefix.setToolTip(
            "Prefix added to generated _ANIMATION_ resource names to avoid collisions."
        )
        self.resource_prefix.textChanged.connect(self._mark_dirty)
        default_form.addRow("Animation script target", self.default_script_combo)
        default_form.addRow("Custom script resource", self.custom_script_resource)
        self.custom_script_resource_label = default_form.labelForField(self.custom_script_resource)
        default_form.addRow("Target note", self.script_description)
        self.script_description_label = default_form.labelForField(self.script_description)
        default_form.addRow("Resource prefix", self.resource_prefix)
        layout.addWidget(defaults)
        layout.addStretch(1)
        self.tabs.addTab(page, "Project")


    def _build_animations_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        intro = qt["QLabel"](
            "Each row becomes one animation resource. Double-check its name, script target, "
            "root-motion policy, and retarget mapping before export."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        row = qt["QHBoxLayout"]()
        add_button = qt["QPushButton"]("Add FBX animations…")
        self.add_animations_button = add_button
        self.add_animations_button.setToolTip("Import one or more FBX animation files into this project.")
        self.add_animations_button.clicked.connect(self.add_animations)
        self.cancel_animation_operation_button = qt["QPushButton"]("Cancel import")
        self.cancel_animation_operation_button.setToolTip(
            "Stop after the FBX currently being parsed reaches a safe checkpoint."
        )
        self.cancel_animation_operation_button.clicked.connect(
            self.cancel_animation_operation
        )
        self.cancel_animation_operation_button.hide()
        self.remove_animation_button = qt["QPushButton"]("Remove selected")
        self.remove_animation_button.setToolTip("Remove the selected animation from the project only.")
        self.remove_animation_button.clicked.connect(self.remove_selected_animation)
        self.duplicate_animation_button = qt["QPushButton"]("Duplicate selected")
        self.duplicate_animation_button.setToolTip(
            "Create another project entry for the same FBX, useful for alternate root-motion "
            "or script-target versions."
        )
        self.duplicate_animation_button.clicked.connect(self.duplicate_selected_animation)
        row.addWidget(self.add_animations_button)
        row.addWidget(self.cancel_animation_operation_button)
        row.addWidget(self.remove_animation_button)
        row.addWidget(self.duplicate_animation_button)
        row.addStretch(1)
        layout.addLayout(row)

        target_tools = qt["QHBoxLayout"]()
        target_tools.addWidget(qt["QLabel"]("Target rig filter"))
        self.animation_target_filter = qt["QComboBox"]()
        self.animation_target_filter.setToolTip(
            "Show clips resolved to one target CRIG. Inherited clips are grouped with the "
            "project target they actually use."
        )
        self.animation_target_filter.currentIndexChanged.connect(
            lambda _index: self._apply_animation_target_filter()
        )
        self.animation_target_group = qt["QCheckBox"]("Group rows by target rig")
        self.animation_target_group.setToolTip(
            "Keep clips for the same resolved CRIG together without changing project order."
        )
        self.animation_target_group.toggled.connect(
            lambda _checked: self._refresh_animation_table()
        )
        target_tools.addWidget(self.animation_target_filter, 1)
        target_tools.addWidget(self.animation_target_group)
        layout.addLayout(target_tools)

        self.animation_table = qt["QTableWidget"](0, 11)
        self.animation_table.setHorizontalHeaderLabels(
            [
                "Use",
                "Display name",
                "FBX source",
                "FBX animation",
                "Resource name",
                "Animation SCR",
                "Target rig",
                "Compatibility / mapping",
                "Root motion",
                "IK",
                "Retarget",
            ]
        )
        self.animation_table.setSelectionBehavior(qt["QAbstractItemView"].SelectRows)
        self.animation_table.setSelectionMode(qt["QAbstractItemView"].SingleSelection)
        self.animation_table.setAlternatingRowColors(True)
        self.animation_table.setShowGrid(False)
        self.animation_table.setMinimumHeight(360)
        self.animation_table.verticalHeader().setVisible(False)
        self.animation_table.verticalHeader().setDefaultSectionSize(46)
        header = self.animation_table.horizontalHeader()
        header.setMinimumSectionSize(70)
        header.setSectionResizeMode(0, qt["QHeaderView"].ResizeToContents)
        header.setSectionResizeMode(1, qt["QHeaderView"].Interactive)
        header.setSectionResizeMode(2, qt["QHeaderView"].Stretch)
        header.setSectionResizeMode(3, qt["QHeaderView"].Interactive)
        header.setSectionResizeMode(4, qt["QHeaderView"].Interactive)
        header.setSectionResizeMode(5, qt["QHeaderView"].Interactive)
        header.setSectionResizeMode(6, qt["QHeaderView"].Interactive)
        header.setSectionResizeMode(7, qt["QHeaderView"].Interactive)
        header.setSectionResizeMode(8, qt["QHeaderView"].Interactive)
        header.setSectionResizeMode(9, qt["QHeaderView"].Interactive)
        header.setSectionResizeMode(10, qt["QHeaderView"].ResizeToContents)
        self.animation_table.setColumnWidth(1, 210)
        self.animation_table.setColumnWidth(3, 190)
        self.animation_table.setColumnWidth(4, 190)
        self.animation_table.setColumnWidth(5, 210)
        self.animation_table.setColumnWidth(6, 220)
        self.animation_table.setColumnWidth(7, 220)
        self.animation_table.setColumnWidth(8, 155)
        self.animation_table.setColumnWidth(9, 155)
        self.animation_table.itemSelectionChanged.connect(self._animation_selection_changed)
        layout.addWidget(self.animation_table, 1)

        detail = qt["QGroupBox"]("Selected clip playback range")
        detail.setToolTip(
            "Changes the sequence range advertised by the animation script. It does not rewrite "
            "the FBX sampling rate."
        )
        detail_form = qt["QFormLayout"](detail)
        range_row = qt["QHBoxLayout"]()
        self.start_frame_spin = qt["QSpinBox"]()
        self.start_frame_spin.setRange(-1, 1_000_000)
        self.start_frame_spin.setSpecialValueText("First")
        self.start_frame_spin.setToolTip("First source frame to expose, or First for the full clip.")
        self.start_frame_spin.valueChanged.connect(self._selected_range_changed)
        self.end_frame_spin = qt["QSpinBox"]()
        self.end_frame_spin.setRange(-1, 1_000_000)
        self.end_frame_spin.setSpecialValueText("Last")
        self.end_frame_spin.setToolTip("Last source frame to expose, or Last for the full clip.")
        self.end_frame_spin.valueChanged.connect(self._selected_range_changed)
        self.fps_spin = qt["QDoubleSpinBox"]()
        self.fps_spin.setRange(0.001, 1000.0)
        self.fps_spin.setDecimals(9)
        self.fps_spin.setToolTip("Playback speed written to the animation-script sequence.")
        self.fps_spin.valueChanged.connect(self._selected_range_changed)
        self.sample_fps_spin = qt["QDoubleSpinBox"]()
        self.sample_fps_spin.setRange(0.001, 1000.0)
        self.sample_fps_spin.setDecimals(9)
        self.sample_fps_spin.setToolTip(
            "Cadence used to sample FBX transforms into ANM2. Defaults to the FBX-declared rate."
        )
        self.sample_fps_spin.valueChanged.connect(self._selected_range_changed)
        self.source_fps_label = qt["QLabel"]("—")
        self.source_fps_label.setToolTip("Timebase declared by the imported FBX.")
        range_row.addWidget(qt["QLabel"]("Start"))
        range_row.addWidget(self.start_frame_spin)
        range_row.addSpacing(14)
        range_row.addWidget(qt["QLabel"]("End"))
        range_row.addWidget(self.end_frame_spin)
        range_row.addSpacing(14)
        range_row.addWidget(qt["QLabel"]("Playback FPS"))
        range_row.addWidget(self.fps_spin)
        range_row.addSpacing(14)
        range_row.addWidget(qt["QLabel"]("Sampling FPS"))
        range_row.addWidget(self.sample_fps_spin)
        range_row.addSpacing(14)
        range_row.addWidget(qt["QLabel"]("Source FPS"))
        range_row.addWidget(self.source_fps_label)
        range_row.addStretch(1)
        detail_form.addRow(range_row)
        self.range_note = qt["QLabel"](
            "First/Last uses the complete sampled range. Sampling FPS controls FBX-to-ANM2 "
            "keys; Playback FPS controls the animation-script cadence."
        )
        self.range_note.setWordWrap(True)
        detail_form.addRow(self.range_note)
        layout.addWidget(detail)

        diagnostics = qt["QGroupBox"]("Selected clip import diagnostics")
        diagnostics.setToolTip(
            "Findings are grouped by requested purpose and disposition. Repaired and ignored "
            "model details do not prevent skeletal animation import."
        )
        diagnostics_layout = qt["QVBoxLayout"](diagnostics)
        self.animation_import_diagnostics = qt["QPlainTextEdit"]()
        self.animation_import_diagnostics.setReadOnly(True)
        self.animation_import_diagnostics.setMaximumHeight(170)
        self.animation_import_diagnostics.setPlaceholderText(
            "Select an imported clip to review its FBX diagnostics."
        )
        diagnostics_layout.addWidget(self.animation_import_diagnostics)
        layout.addWidget(diagnostics)
        self.tabs.addTab(page, "Animations")


    def _build_retarget_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        layout.setSpacing(8)
        intro = qt["QLabel"](
            "Auto-map handles standard Mixamo and common humanoid names. Unmapped source tracks "
            "are ignored and unmapped target bones keep their bind pose. Change a source-bone "
            "dropdown only when you want an explicit override. Enable Show helper bones to map "
            "refcamera, eyecamera, holders, twists, and other target helpers."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        top = qt["QHBoxLayout"]()
        self.retarget_clip_combo = self._combo_box()
        self.retarget_clip_combo.setToolTip("Choose which imported FBX mapping to edit.")
        self.retarget_clip_combo.currentIndexChanged.connect(self._retarget_clip_changed)
        self.retarget_filter = qt["QLineEdit"]()
        self.retarget_filter.setPlaceholderText("Filter roles or source bones")
        self.retarget_filter.setToolTip("Filter the mapping table by group, role, or source-bone name.")
        self.retarget_filter.textChanged.connect(self._filter_mapping_rows)
        top.addWidget(qt["QLabel"]("Animation"))
        top.addWidget(self.retarget_clip_combo, 2)
        top.addWidget(self.retarget_filter, 1)
        layout.addLayout(top)

        actions = qt["QHBoxLayout"]()
        self.retarget_auto_map_button = qt["QPushButton"]("Auto-map humanoid")
        self.retarget_auto_map_button.setToolTip(
            "Rebuild the mapping using exact Mixamo names, common aliases, and conservative "
            "humanoid-name heuristics."
        )
        self.retarget_auto_map_button.clicked.connect(self.auto_map_selected)
        self.retarget_apply_button = qt["QPushButton"]("Apply to compatible clips")
        self.retarget_apply_button.setToolTip(
            "Reuse this mapping on other project clips that have the exact same source skeleton "
            "hash. Clips with different hierarchies are skipped."
        )
        self.retarget_apply_button.clicked.connect(self.apply_mapping_to_compatible_clips)
        actions.addWidget(self.retarget_auto_map_button)
        actions.addWidget(self.retarget_apply_button)

        self.show_helper_bones = qt["QCheckBox"]("Show helper bones")
        self.show_helper_bones.setToolTip(
            "Adds helper targets from the selected Dying Light target SMD to this table. "
            "Helpers remain unmapped until you select a source FBX bone."
        )
        self.show_helper_bones.toggled.connect(self._retarget_clip_changed)
        actions.addWidget(self.show_helper_bones)
        self.show_all_target_bones = qt["QCheckBox"]("Show all target bones")
        self.show_all_target_bones.setToolTip(
            "Shows the complete selected target hierarchy exactly once. Advanced DL2 "
            "contains 271 target bones; unmapped rows remain at target bind."
        )
        self.show_all_target_bones.toggled.connect(self._retarget_clip_changed)
        actions.addWidget(self.show_all_target_bones)

        self.retarget_advanced_actions = qt["QWidget"]()
        advanced_actions = qt["QHBoxLayout"](self.retarget_advanced_actions)
        advanced_actions.setContentsMargins(0, 0, 0, 0)
        clear_button = qt["QPushButton"]("Clear mapping")
        clear_button.setToolTip("Remove every role assignment for the selected clip.")
        clear_button.clicked.connect(self.clear_mapping)
        load_button = qt["QPushButton"]("Load .dlrmap")
        load_button.setToolTip("Load a reusable humanoid mapping profile from disk.")
        load_button.clicked.connect(self.load_mapping_profile)
        save_button = qt["QPushButton"]("Save .dlrmap")
        save_button.setToolTip("Save the current mapping as a reusable profile.")
        save_button.clicked.connect(self.save_mapping_profile)
        advanced_actions.addWidget(clear_button)
        advanced_actions.addWidget(load_button)
        advanced_actions.addWidget(save_button)
        actions.addWidget(self.retarget_advanced_actions)
        actions.addStretch(1)
        layout.addLayout(actions)

        self.root_locomotion_panel = qt["QGroupBox"]("Root & locomotion")
        root_layout = qt["QVBoxLayout"](self.root_locomotion_panel)
        root_row = qt["QHBoxLayout"]()
        self.root_source_combo = self._combo_box()
        self.root_target_combo = self._combo_box()
        self.root_motion_mode_combo = self._combo_box()
        self.root_motion_mode_combo.addItem("In place", RootMotionMode.IN_PLACE.value)
        self.root_motion_mode_combo.addItem("Selected skeletal root", RootMotionMode.SKELETAL_ROOT.value)
        self.root_motion_mode_combo.addItem("Motion accumulator", RootMotionMode.MOTION_ACCUMULATOR.value)
        self.root_heading_mode_combo = self._combo_box()
        self.root_heading_mode_combo.addItem("Lock initial heading", RootHeadingMode.LOCK_INITIAL.value)
        self.root_heading_mode_combo.addItem("Preserve on skeletal root", RootHeadingMode.PRESERVE.value)
        self.root_heading_mode_combo.addItem("Move to accumulator", RootHeadingMode.TO_MOTION_ACCUMULATOR.value)
        for label, widget, stretch in (
            ("Source root", self.root_source_combo, 2),
            ("Target root", self.root_target_combo, 2),
            ("Root motion", self.root_motion_mode_combo, 2),
            ("Heading", self.root_heading_mode_combo, 2),
        ):
            root_row.addWidget(qt["QLabel"](label))
            root_row.addWidget(widget, stretch)
        root_layout.addLayout(root_row)

        feet_row = qt["QHBoxLayout"]()
        self.left_source_foot_combo = self._combo_box()
        self.right_source_foot_combo = self._combo_box()
        self.left_target_foot_combo = self._combo_box()
        self.right_target_foot_combo = self._combo_box()
        self.ik_recommendation_combo = self._combo_box()
        self.ik_recommendation_combo.addItem("Runtime / consumer IK", "runtime")
        self.ik_recommendation_combo.addItem("IK off authoring preset", "off")
        for label, widget in (
            ("Left source foot", self.left_source_foot_combo),
            ("Left target foot", self.left_target_foot_combo),
            ("Right source foot", self.right_source_foot_combo),
            ("Right target foot", self.right_target_foot_combo),
            ("IK", self.ik_recommendation_combo),
        ):
            feet_row.addWidget(qt["QLabel"](label))
            feet_row.addWidget(widget, 1)
        root_layout.addLayout(feet_row)
        self.locomotion_policy_note = qt["QLabel"]()
        self.locomotion_policy_note.setWordWrap(True)
        root_layout.addWidget(self.locomotion_policy_note)
        for widget in (
            self.root_source_combo,
            self.root_target_combo,
            self.root_motion_mode_combo,
            self.root_heading_mode_combo,
            self.left_source_foot_combo,
            self.right_source_foot_combo,
            self.left_target_foot_combo,
            self.right_target_foot_combo,
            self.ik_recommendation_combo,
        ):
            widget.currentIndexChanged.connect(
                self._root_locomotion_widgets_changed
            )
        layout.addWidget(self.root_locomotion_panel)

        self.mapping_status = qt["QLabel"]()
        self.mapping_status.setWordWrap(True)
        self.mapping_status.setMaximumHeight(84)
        self.mapping_status.setSizePolicy(
            qt["QSizePolicy"].Preferred, qt["QSizePolicy"].Maximum
        )
        layout.addWidget(self.mapping_status)

        splitter = qt["QSplitter"]()
        splitter.setChildrenCollapsible(False)
        self.mapping_table = qt["QTableWidget"](0, 7)
        self.mapping_table.setHorizontalHeaderLabels(
            [
                "Group",
                "Target role / helper",
                "Source FBX bone",
                "Required",
                "Confidence",
                "Method",
                "Components",
            ]
        )
        self.mapping_table.setAlternatingRowColors(True)
        self.mapping_table.setShowGrid(False)
        self.mapping_table.setMinimumHeight(390)
        self.mapping_table.setVerticalScrollMode(qt["QAbstractItemView"].ScrollPerPixel)
        self.mapping_table.verticalHeader().setVisible(False)
        self.mapping_table.verticalHeader().setDefaultSectionSize(36)
        mheader = self.mapping_table.horizontalHeader()
        mheader.setSectionResizeMode(0, qt["QHeaderView"].ResizeToContents)
        mheader.setSectionResizeMode(1, qt["QHeaderView"].ResizeToContents)
        mheader.setSectionResizeMode(2, qt["QHeaderView"].Stretch)
        mheader.setSectionResizeMode(3, qt["QHeaderView"].ResizeToContents)
        mheader.setSectionResizeMode(4, qt["QHeaderView"].ResizeToContents)
        mheader.setSectionResizeMode(5, qt["QHeaderView"].ResizeToContents)
        mheader.setSectionResizeMode(6, qt["QHeaderView"].ResizeToContents)
        splitter.addWidget(self.mapping_table)

        self.ignored_bones_panel = qt["QGroupBox"]("Unmapped / ignored source bones")
        self.ignored_bones_panel.setToolTip(
            "Source nodes that are not currently assigned to a humanoid role. End markers and "
            "non-deforming helpers are often safe to leave here."
        )
        side_layout = qt["QVBoxLayout"](self.ignored_bones_panel)
        self.ignored_bones = qt["QPlainTextEdit"]()
        self.ignored_bones.setReadOnly(True)
        self.ignored_bones.setMinimumWidth(260)
        side_layout.addWidget(self.ignored_bones)
        splitter.addWidget(self.ignored_bones_panel)
        splitter.setStretchFactor(0, 1)
        splitter.setStretchFactor(1, 0)
        splitter.setSizes([1100, 280])
        layout.addWidget(splitter, 1)
        self.tabs.addTab(page, "Retargeting")


    def _build_export_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        intro = qt["QLabel"](
            "Build a new animation RPack, or append animations to a pack previously created by "
            "DL ReAnimated. Retail RPacks are not modified directly."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        mode_group = qt["QGroupBox"]("RPack export mode")
        mode_layout = qt["QVBoxLayout"](mode_group)
        self.new_pack_radio = qt["QRadioButton"]("Create a new tool-owned RPack")
        self.new_pack_radio.setToolTip(
            "Starts a new animation library containing the enabled clips in this project."
        )
        self.append_pack_radio = qt["QRadioButton"](
            "Append/replace animations in an existing RPack created by DL ReAnimated"
        )
        self.append_pack_radio.setToolTip(
            "Preserves resources already present in a DL ReAnimated pack and adds this project's "
            "enabled clips. The matching .dlrmanifest.json sidecar is checked when available."
        )
        self.mode_buttons = qt["QButtonGroup"](mode_group)
        self.mode_buttons.addButton(self.new_pack_radio)
        self.mode_buttons.addButton(self.append_pack_radio)
        self.new_pack_radio.toggled.connect(self._export_mode_changed)
        mode_layout.addWidget(self.new_pack_radio)
        mode_layout.addWidget(self.append_pack_radio)
        layout.addWidget(mode_group)

        output_group = qt["QGroupBox"]("Output")
        output_form = qt["QFormLayout"](output_group)
        self.output_directory = self._path_row(
            output_form,
            "Output folder",
            "",
            directory=True,
            tooltip="Folder that receives the RPack, manifest, and optional build reports.",
        )
        self.pack_filename = qt["QLineEdit"]()
        self.pack_filename.setToolTip(
            "Editor-loadable packs normally use common_anims_sp_pc.rpack. Do not overwrite "
            "common_anims_PC.rpack."
        )
        self.pack_filename.textChanged.connect(self._mark_dirty)
        output_form.addRow("Pack filename", self.pack_filename)
        self.existing_rpack = self._path_row(
            output_form,
            "Existing tool RPack",
            "RPack (*.rpack)",
            directory=False,
            tooltip="Required only in append mode. Choose a pack previously built by this tool.",
        )
        layout.addWidget(output_group)

        self.advanced_export_group = qt["QGroupBox"]("Advanced export options")
        advanced_form = qt["QFormLayout"](self.advanced_export_group)
        self.collision_combo = self._combo_box()
        self.collision_combo.addItem("Stop on duplicate resource names", "error")
        self.collision_combo.addItem("Replace duplicate resources/sequences", "replace")
        self.collision_combo.setToolTip(
            "Controls what happens when an animation or sequence name already exists in append mode."
        )
        self.collision_combo.currentIndexChanged.connect(self._mark_dirty)
        advanced_form.addRow("When a name already exists", self.collision_combo)
        self.include_controls = qt["QCheckBox"]("Include stock writer and bind-pose controls")
        self.include_controls.setToolTip(
            "Adds two diagnostic animations: a known-good writer regression control and a static "
            "target bind-pose control. Useful for development/testing, but unnecessary in normal packs."
        )
        self.include_controls.toggled.connect(self._mark_dirty)
        self.keep_anm2 = qt["QCheckBox"]("Keep exported ANM2 files and retarget reports")
        self.keep_anm2.setToolTip(
            "Writes intermediate ANM2 files and per-clip JSON reports beside the RPack. Leave off "
            "for a clean user-facing build folder."
        )
        self.keep_anm2.toggled.connect(self._mark_dirty)
        self.developer_diagnostics = qt["QCheckBox"](
            "Include Python tracebacks in developer logs"
        )
        self.developer_diagnostics.setToolTip(
            "Developer-only diagnostics. Normal dialogs and logs show actionable messages "
            "without Python tracebacks."
        )
        self.developer_diagnostics.toggled.connect(
            lambda checked: self.settings.setValue(
                "developer_diagnostics", bool(checked)
            )
        )
        advanced_form.addRow(self.include_controls)
        advanced_form.addRow(self.keep_anm2)
        advanced_form.addRow(self.developer_diagnostics)
        layout.addWidget(self.advanced_export_group)

        warning = qt["QLabel"](
            "Base script names such as anims_man_all and anims_woman_all are override "
            "targets. Use an additive/custom script resource for a standalone pack when possible."
        )
        warning.setWordWrap(True)
        layout.addWidget(warning)

        build_row = qt["QHBoxLayout"]()
        self.build_button = qt["QPushButton"]("Build RPack")
        self.build_button.setToolTip("Validate the project, retarget enabled clips, and write the RPack.")
        self.build_button.clicked.connect(self.build_rpack)
        self.export_anm2_button = qt["QPushButton"]("Export ANM2 only…")
        self.export_anm2_button.setToolTip(
            "Retarget enabled clips and write only their generated ANM2 files to a folder."
        )
        self.export_anm2_button.clicked.connect(self.export_anm2_only)
        self.progress_bar = qt["QProgressBar"]()
        self.progress_bar.setRange(0, 1)
        self.progress_bar.setValue(0)
        build_row.addWidget(self.build_button)
        build_row.addWidget(self.export_anm2_button)
        build_row.addWidget(self.progress_bar, 1)
        layout.addLayout(build_row)
        self.build_log = qt["QPlainTextEdit"]()
        self.build_log.setReadOnly(True)
        self.build_log.setPlaceholderText("Build progress and errors appear here.")
        layout.addWidget(self.build_log, 1)
        self.tabs.addTab(page, "Export")


    def _build_anm2_to_fbx_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        intro = qt["QLabel"](
            "Convert extracted ANM2 files into editable skeleton-and-animation FBXs. "
            "Choose the matching Chrome Rig because ANM2 stores hashes, not a hierarchy or bind pose."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        toolbar = qt["QHBoxLayout"]()
        add_button = qt["QPushButton"]("Add ANM2 files…")
        add_button.setToolTip("Add one or more extracted .anm2 animation files to this project.")
        add_button.clicked.connect(self._reverse_add_files)
        remove_button = qt["QPushButton"]("Remove selected")
        remove_button.clicked.connect(self._reverse_remove_selected)
        create_rig = qt["QPushButton"]("Create .crig from model FBX…")
        create_rig.setToolTip(
            "For doors, props, animals, or custom models, create the source rig definition from "
            "the matching binary model FBX."
        )
        create_rig.clicked.connect(self.create_chrome_rig)
        toolbar.addWidget(add_button)
        toolbar.addWidget(remove_button)
        toolbar.addWidget(create_rig)
        toolbar.addStretch(1)
        layout.addLayout(toolbar)

        self.reverse_table = qt["QTableWidget"](0, 9)
        self.reverse_table.setHorizontalHeaderLabels(
            [
                "Use", "ANM2", "Output action", "Frames", "Tracks",
                "ANM2 FPS", "FBX FPS", "Start", "End",
            ]
        )
        self.reverse_table.setSelectionBehavior(qt["QAbstractItemView"].SelectRows)
        self.reverse_table.setAlternatingRowColors(True)
        header = self.reverse_table.horizontalHeader()
        header.setSectionResizeMode(1, qt["QHeaderView"].Stretch)
        header.setSectionResizeMode(2, qt["QHeaderView"].Stretch)
        for column in (0, 3, 4, 5, 6, 7, 8):
            header.setSectionResizeMode(column, qt["QHeaderView"].ResizeToContents)
        layout.addWidget(self.reverse_table, 2)

        settings = qt["QGroupBox"]("Rig and output settings")
        form = qt["QFormLayout"](settings)
        self.reverse_source_rig = self._combo_box()
        self.reverse_source_rig.setToolTip(
            "Rig that resolves ANM2 descriptor hashes to bone names, hierarchy, and bind transforms."
        )
        self.reverse_source_rig.currentIndexChanged.connect(self._reverse_source_rig_changed)
        form.addRow("Source Chrome Rig", self.reverse_source_rig)
        self.reverse_unknown_track_policy = self._combo_box()
        self.reverse_unknown_track_policy.addItem(
            "JSON sidecar (Dying Light 2 default)", "sidecar"
        )
        self.reverse_unknown_track_policy.addItem(
            "Non-deforming helper roots in FBX (advanced)", "helpers"
        )
        self.reverse_unknown_track_policy.addItem(
            "Drop unresolved tracks (explicit; warning)", "drop"
        )
        self.reverse_unknown_track_policy.setToolTip(
            "DL2 defaults to a deterministic .dlr_unknown_tracks.json sidecar so unresolved "
            "descriptors are preserved without pretending they are skeleton bones."
        )
        self.reverse_unknown_track_policy.currentIndexChanged.connect(self._mark_dirty)
        form.addRow("Unresolved ANM2 tracks", self.reverse_unknown_track_policy)
        self.reverse_bake_motion_accumulator = qt["QCheckBox"](
            "Bake detected motion accumulator into root"
        )
        self.reverse_bake_motion_accumulator.setChecked(True)
        self.reverse_bake_motion_accumulator.setToolTip(
            "When 0xCCC3CDDF contains animated offset-helper motion, compose its full "
            "transform into the exported primary root while retaining the original helper "
            "as a non-deforming FBX Empty. Disable this for raw helper-only inspection."
        )
        self.reverse_bake_motion_accumulator.toggled.connect(self._mark_dirty)
        form.addRow("Motion accumulator", self.reverse_bake_motion_accumulator)
        self.reverse_mode = self._combo_box()
        self.reverse_mode.addItem("Native rig (recommended)", "native")
        self.reverse_mode.addItem("Retarget onto another skeleton", "retarget")
        self.reverse_mode.setToolTip(
            "Native export preserves the original skeleton. Retarget mode transfers bind-relative "
            "motion onto the target FBX after you review the bone map."
        )
        self.reverse_mode.currentIndexChanged.connect(self._reverse_mode_changed)
        form.addRow("Conversion mode", self.reverse_mode)
        self.reverse_target_fbx = self._path_row(
            form, "Target skeleton FBX", "FBX (*.fbx)", directory=False,
            tooltip="Binary FBX whose armature becomes the output skeleton in retarget mode.",
        )
        self.reverse_translation_scale = self._combo_box()
        self.reverse_translation_scale.setEditable(True)
        self.reverse_translation_scale.addItem("Auto from mapped bone lengths", "auto")
        self.reverse_translation_scale.addItem("No scaling (1.0)", "1.0")
        self.reverse_translation_scale.setToolTip(
            "Scales only animated translation deltas. Target bind offsets and proportions remain unchanged."
        )
        self.reverse_translation_scale.currentIndexChanged.connect(self._mark_dirty)
        form.addRow("Translation motion scale", self.reverse_translation_scale)
        self.reverse_blender_path = self._path_row(
            form, "Blender executable", "Blender (blender.exe);;Executable (*.exe);;All files (*)",
            directory=False,
            tooltip="Blender is used only as the standards-compatible FBX writer and is not bundled in the EXE.",
        )
        self.reverse_blender_path.textChanged.connect(self._reverse_blender_path_changed)
        self.reverse_blender_status = qt["QLabel"]()
        form.addRow("Blender status", self.reverse_blender_status)
        self.reverse_output_directory = self._path_row(
            form, "FBX output folder", "", directory=True,
            tooltip="Each enabled ANM2 is exported as one skeleton-and-animation FBX.",
        )
        layout.addWidget(settings)

        mapping_group = qt["QGroupBox"]("Cross-rig bone mapping")
        mapping_layout = qt["QVBoxLayout"](mapping_group)
        mapping_actions = qt["QHBoxLayout"]()
        auto_button = qt["QPushButton"]("Automatic map")
        auto_button.setToolTip(
            "Suggest unique matches using descriptors, names, aliases, hierarchy, and structure. "
            "Review every low-confidence or unmapped row before export."
        )
        auto_button.clicked.connect(self._reverse_auto_map)
        load_button = qt["QPushButton"]("Load .dlrbmap.json…")
        load_button.clicked.connect(self._reverse_load_map)
        save_button = qt["QPushButton"]("Save mapping…")
        save_button.clicked.connect(self._reverse_save_map)
        mapping_actions.addWidget(auto_button)
        mapping_actions.addWidget(load_button)
        mapping_actions.addWidget(save_button)
        mapping_actions.addStretch(1)
        mapping_layout.addLayout(mapping_actions)
        self.reverse_mapping_table = qt["QTableWidget"](0, 5)
        self.reverse_mapping_table.setHorizontalHeaderLabels(
            ["Source bone", "Descriptor", "Target bone", "Confidence", "Method"]
        )
        map_header = self.reverse_mapping_table.horizontalHeader()
        map_header.setSectionResizeMode(0, qt["QHeaderView"].Stretch)
        map_header.setSectionResizeMode(2, qt["QHeaderView"].Stretch)
        for column in (1, 3, 4):
            map_header.setSectionResizeMode(column, qt["QHeaderView"].ResizeToContents)
        mapping_layout.addWidget(self.reverse_mapping_table, 1)
        self.reverse_mapping_status = qt["QLabel"]("Native mode does not require a bone map.")
        self.reverse_mapping_status.setWordWrap(True)
        mapping_layout.addWidget(self.reverse_mapping_status)
        layout.addWidget(mapping_group, 2)
        self.reverse_mapping_group = mapping_group

        export_row = qt["QHBoxLayout"]()
        self.reverse_export_button = qt["QPushButton"]("Export FBX batch")
        self.reverse_export_button.clicked.connect(self._reverse_export)
        self.reverse_cancel_button = qt["QPushButton"]("Cancel")
        self.reverse_cancel_button.setEnabled(False)
        self.reverse_cancel_button.clicked.connect(self._reverse_cancel)
        self.reverse_log = qt["QPlainTextEdit"]()
        self.reverse_log.setReadOnly(True)
        self.reverse_log.setMaximumHeight(110)
        export_row.addWidget(self.reverse_export_button)
        export_row.addWidget(self.reverse_cancel_button)
        layout.addLayout(export_row)
        layout.addWidget(self.reverse_log)
        self._reverse_cancel_requested = False
        self._reverse_cancel_event = threading.Event()
        self.tabs.addTab(page, "ANM2 → FBX")

    def _build_help_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        intro = qt["QLabel"](
            "Open a focused guide below. Most users only need the GUI guide, retargeting guide, "
            "and root-motion guide."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)
        self.advanced_help_buttons: list[Any] = []
        rows = (
            ("GUI quick start", "GUI_GUIDE.md", False),
            ("Humanoid retargeting", "RETARGETING.md", False),
            ("Animation script targets", "ANIMATION_SCRIPT_TARGETS.md", False),
            ("New versus append RPack export", "RPACK_WORKFLOW.md", False),
            ("Root motion and IK", "ROOT_MOTION_AND_IK.md", False),
            ("Troubleshooting", "TROUBLESHOOTING.md", False),
            ("ANM2 format", "ANM2_FORMAT.md", True),
            ("ANM2 to FBX", "ANM2_TO_FBX.md", False),
            ("Project file format and compatibility", "PROJECT_FORMAT.md", True),
            ("Building the Windows EXE", "BUILDING_WINDOWS_EXE.md", True),
            ("Developer architecture", "DEVELOPER_ARCHITECTURE.md", True),
        )
        for title, filename, advanced in rows:
            button = qt["QPushButton"](title)
            button.setToolTip(f"Open docs/{filename} in your default Markdown viewer.")
            button.clicked.connect(lambda _checked=False, name=filename: self.open_doc(name))
            layout.addWidget(button)
            if advanced:
                self.advanced_help_buttons.append(button)
        layout.addStretch(1)
        self.tabs.addTab(page, "Help")


    def _path_row(
        self,
        form: Any,
        label: str,
        file_filter: str,
        *,
        directory: bool,
        tooltip: str = "",
    ) -> Any:
        qt = self.qt
        holder = qt["QWidget"]()
        row = qt["QHBoxLayout"](holder)
        row.setContentsMargins(0, 0, 0, 0)
        edit = qt["QLineEdit"]()
        edit.textChanged.connect(self._mark_dirty)
        button = qt["QPushButton"]("Browse…")
        if tooltip:
            edit.setToolTip(tooltip)
            button.setToolTip(tooltip)
            holder.setToolTip(tooltip)
        if directory:
            button.clicked.connect(lambda: self._browse_directory(edit))
        else:
            button.clicked.connect(lambda: self._browse_file(edit, file_filter))
        row.addWidget(edit, 1)
        row.addWidget(button)
        form.addRow(label, holder)
        edit._dlr_row_holder = holder
        edit._dlr_row_label = form.labelForField(holder)
        return edit


    # ------------------------------------------------------------- project I/O
    def recent_project_paths(self) -> list[Path]:
        """Return persisted project paths in most-recently-used order."""

        stored = self.settings.value(_RECENT_PROJECTS_SETTING, [])
        if isinstance(stored, str):
            values = [stored]
        elif isinstance(stored, (list, tuple)):
            values = stored
        else:
            values = []

        paths: list[Path] = []
        seen: set[str] = set()
        for value in values:
            text = str(value or "").strip()
            if not text:
                continue
            path = Path(text).expanduser().resolve()
            key = os.path.normcase(str(path))
            if key in seen:
                continue
            seen.add(key)
            paths.append(path)
        return paths[:_MAX_RECENT_PROJECTS]

    def _set_recent_project_paths(self, paths: list[Path]) -> None:
        self.settings.setValue(
            _RECENT_PROJECTS_SETTING,
            [str(path) for path in paths[:_MAX_RECENT_PROJECTS]],
        )
        callback = self.recent_projects_changed_callback
        if callback is not None:
            callback()

    def _remember_recent_project(self, path: str | Path) -> None:
        resolved = Path(path).expanduser().resolve()
        key = os.path.normcase(str(resolved))
        remaining = [
            candidate
            for candidate in self.recent_project_paths()
            if os.path.normcase(str(candidate)) != key
        ]
        self._set_recent_project_paths([resolved, *remaining])

    def remove_recent_project(self, path: str | Path) -> None:
        key = os.path.normcase(str(Path(path).expanduser().resolve()))
        self._set_recent_project_paths(
            [
                candidate
                for candidate in self.recent_project_paths()
                if os.path.normcase(str(candidate)) != key
            ]
        )

    def clear_recent_projects(self) -> None:
        self._set_recent_project_paths([])

    def new_project(self) -> None:
        if not self._confirm_discard_changes():
            return
        self.project = self._new_default_project()
        self.project_path = None
        self._source_cache.clear()
        getattr(self, "_semantic_state_cache", {}).clear()
        self.dirty = False
        self._refresh_all()

    def open_project(self) -> None:
        if not self._confirm_discard_changes():
            return
        path, _ = self.qt["QFileDialog"].getOpenFileName(
            self.window,
            "Open DL ReAnimated project",
            str(self.project_path.parent if self.project_path else self.root),
            f"DL ReAnimated Project (*{PROJECT_EXTENSION});;JSON (*.json)",
        )
        if not path:
            return
        self._load_project(path)

    def open_recent_project(self, path: str | Path) -> None:
        if not self._confirm_discard_changes():
            return
        self._load_project(path)

    def _load_project(self, path: str | Path) -> bool:
        try:
            self.project = DlReanimatedProject.load(path)
            self.project_path = Path(path).expanduser().resolve()
            self._source_cache.clear()
            getattr(self, "_semantic_state_cache", {}).clear()
            self.dirty = False
            self._refresh_all()
            self._remember_recent_project(self.project_path)
            self.status.showMessage(f"Opened {self.project_path}", 5000)
            return True
        except Exception as exc:
            if not Path(path).expanduser().is_file():
                self.remove_recent_project(path)
            self._show_error("Could not open project", exc)
            return False

    def save_project(self) -> None:
        if self.project_path is None:
            self.save_project_as()
            return
        self._sync_project_from_ui()
        try:
            self.project_path = self.project.save(self.project_path)
            self.dirty = False
            self._update_title()
            self._remember_recent_project(self.project_path)
            self.status.showMessage(f"Saved {self.project_path}", 5000)
        except Exception as exc:
            self._show_error("Could not save project", exc)

    def save_project_as(self) -> None:
        self._sync_project_from_ui()
        suggested = self.project_path or (self.root / f"{self.project.name}{PROJECT_EXTENSION}")
        path, _ = self.qt["QFileDialog"].getSaveFileName(
            self.window,
            "Save DL ReAnimated project",
            str(suggested),
            f"DL ReAnimated Project (*{PROJECT_EXTENSION})",
        )
        if not path:
            return
        try:
            self.project_path = self.project.save(path)
            self.dirty = False
            self._update_title()
            self._remember_recent_project(self.project_path)
            self.status.showMessage(f"Saved {self.project_path}", 5000)
        except Exception as exc:
            self._show_error("Could not save project", exc)

    def _confirm_discard_changes(self) -> bool:
        if not self.dirty:
            return True
        result = self.qt["QMessageBox"].question(
            self.window,
            "Unsaved project",
            "Save changes before continuing?",
            self.qt["QMessageBox"].Save
            | self.qt["QMessageBox"].Discard
            | self.qt["QMessageBox"].Cancel,
            self.qt["QMessageBox"].Save,
        )
        if result == self.qt["QMessageBox"].Cancel:
            return False
        if result == self.qt["QMessageBox"].Save:
            self.save_project()
            return not self.dirty
        return True

    def _exact_import_mapping_profile(
        self,
        document: FbxDocument,
        target_rig: ChromeRig,
    ) -> tuple[GenericBoneMap, str]:
        """Create a verified advanced bridge or preserve the legacy review path."""

        compatibility = classify_target_compatibility(document, target_rig)
        verified_error = ""
        if (
            target_rig.rig_id == DL2_ADVANCED_RIG_ID
            and compatibility.get("classification") != "exact_identity"
        ):
            try:
                policy = build_target_retarget_policy(
                    target_rig,
                    game_id=self.project.game_id,
                    clip_domain="body",
                )
                if not policy.automatic_routing_authorized:
                    raise ValueError(
                        "the selected DL2 advanced target package did not pass coherence checks"
                    )
                profile = build_dl2_advanced_body_map_with_local_recipe(
                    document,
                    target_rig,
                    policy,
                )
                if mapping_profile_origin(profile) == "manually_reviewed":
                    return (
                        profile,
                        "Applied a matching live-validated local retarget recipe.",
                    )
                certificate = dict(
                    profile.extensions.get("automatic_retarget_certificate", {})
                    or profile.extensions.get("verified_mapping_certificate", {})
                    or {}
                )
                exact_rows = int(
                    certificate.get(
                        "exact_target_subset_rows",
                        compatibility.get("exact_target_subset_rows", 0),
                    )
                    or 0
                )
                semantic_rows = int(certificate.get("semantic_rows", 0) or 0)
                bind_rows = int(
                    certificate.get(
                        "target_bind_rows",
                        compatibility.get("target_bind_rows", 0),
                    )
                    or 0
                )
                return (
                    profile,
                    "Created a complete verified DL2 map: "
                    f"{exact_rows} exact target-subset row(s), "
                    f"{semantic_rows} semantic row(s), and "
                    f"{bind_rows} target row(s) held at bind.",
                )
            except Exception as exc:
                # A failed certificate never authorizes routing. Preserve the
                # established editable automatic_repair/manual-review fallback.
                verified_error = str(exc)

        try:
            policy = build_target_retarget_policy(
                target_rig,
                game_id=self.project.game_id,
                clip_domain="body",
            )
            fresh = build_automatic_retarget_plan(
                document,
                target_rig,
                policy,
                clip_domain="body",
            )
            local = resolve_local_retarget_recipe(
                fresh,
                document,
                target_rig,
                policy,
            )
            if local.applied:
                assert local.recipe is not None
                profile = materialize_reviewed_retarget_recipe(
                    local.recipe,
                    document,
                    target_rig,
                    policy,
                    clip_domain="body",
                    profile_name="Reviewed local retarget recipe",
                )
                return (
                    profile,
                    "Applied a matching live-validated local retarget recipe.",
                )
        except (TypeError, ValueError):
            # Generic/custom targets retain the established editable mapping
            # fallback when no safe reviewed recipe can be constructed.
            pass

        profile = auto_map_crig_to_fbx(
            target_rig,
            document.limb_models.keys(),
            document.parent_by_name,
            **source_mapping_evidence(document),
        )
        note = (
            f"Created an editable .crig map with {len(profile.pairs)} suggestion(s) "
            f"for {len(target_rig.bones)} target bones."
        )
        if verified_error:
            expert_exact = (
                getattr(getattr(self.project, "rig", None), "retarget_mode", "auto")
                == "exact"
            )
            failure_action = (
                "Open Root & .crig Mapping"
                if expert_exact
                else "Open Retargeting and review the diagnostic"
            )
            profile.extensions["automatic_retarget_generation_failure"] = {
                "status": "needs_attention",
                "reason": "The safe DL2 body-map verification did not pass.",
                "action": failure_action,
                "diagnostic": verified_error,
            }
            note = (
                "The safe DL2 body map could not be verified. "
                + note
                + f" {failure_action}."
            )
        return profile, note

    # --------------------------------------------------------------- animation
    def add_animations(self) -> None:
        """Choose FBXs, then parse and map them without blocking Qt's event loop."""

        # A hidden controller has no usable event loop for queued worker
        # callbacks (for example, automation and headless integration hosts).
        # Preserve the established synchronous behavior there; every visible
        # application window uses the responsive worker path below.
        if not self.window.isVisible():
            self._add_animations_sync_legacy()
            return
        if self.background_tasks.busy:
            self.status.showMessage(
                "Wait for the current animation operation to finish before importing more FBX files.",
                5000,
            )
            return
        paths, _ = self.qt["QFileDialog"].getOpenFileNames(
            self.window,
            "Add binary FBX animations",
            str(
                Path(self.project.rig.source_rest_fbx).parent
                if self.project.rig.source_rest_fbx
                else self.root
            ),
            "FBX animations (*.fbx)",
        )
        if not paths:
            return

        request = _AnimationImportRequest(
            paths=tuple(paths),
            existing={
                (Path(row.source_fbx).resolve(), row.source_animation_stack)
                for row in self.project.animations
            },
            game_id=self.project.game_id,
            retarget_mode=self.project.rig.retarget_mode,
            target_rig_path=self.project.rig.target_rig_path,
            resource_root=self.resource_root,
            resource_prefix=self.project.export.resource_prefix.strip(),
            tolerance=self._current_import_tolerance(),
        )
        self._animation_operation_kind = "import"
        self._set_animation_operation_busy(
            True,
            f"Importing {len(request.paths)} animation FBX file(s) in the background…",
        )

        imported = {"rows": 0, "blocked": 0}

        def partial_result(result: _AnimationImportResult) -> None:
            imported["rows"] += len(result.rows)
            imported["blocked"] += len(result.blocked_messages)
            self._apply_animation_import_result(result)

        def succeeded(_result: _AnimationImportResult) -> None:
            if imported["rows"]:
                self.status.showMessage(
                    f"Imported {imported['rows']} animation clip(s).", 6000
                )
            elif imported["blocked"]:
                self.status.showMessage(
                    "No animation clips were imported; review the import diagnostics.",
                    12000,
                )

        if not self.background_tasks.start(
            lambda progress, partial: _prepare_animation_import(
                request, progress, partial
            ),
            progress=lambda message: self.status.showMessage(message),
            partial=partial_result,
            succeeded=succeeded,
            failed=lambda failure: self._background_animation_error(
                "Animation import failed", failure
            ),
            cancelled=lambda: self.status.showMessage(
                f"Import cancelled; kept {imported['rows']} completed clip(s).",
                8000,
            ),
            finished=lambda: self._set_animation_operation_busy(False),
        ):
            self._set_animation_operation_busy(False)
            self.status.showMessage(
                "Another animation operation is already running.", 5000
            )

    def _add_animations_sync_legacy(self) -> None:
        paths, _ = self.qt["QFileDialog"].getOpenFileNames(
            self.window,
            "Add binary FBX animations",
            str(Path(self.project.rig.source_rest_fbx).parent if self.project.rig.source_rest_fbx else self.root),
            "FBX animations (*.fbx)",
        )
        if not paths:
            return
        existing = {
            (Path(row.source_fbx).resolve(), row.source_animation_stack)
            for row in self.project.animations
        }
        added_rows: list[ProjectAnimation] = []
        repair_messages: list[str] = []
        blocked_messages: list[str] = []
        for raw in paths:
            path = Path(raw).resolve()
            try:
                document = self._source_document(str(path))
            except Exception:
                preflight = preflight_fbx(
                    path,
                    purpose="animation",
                    game_id=self.project.game_id,
                    tolerance=self._current_import_tolerance(),
                )
                blocked_messages.append(
                    f"{path.name}\n{preflight.actionable_message()}"
                )
                continue
            stacks = list(document.animation_stacks)
            preferred_stack = (
                document.preferred_animation_stack()
                if hasattr(document, "preferred_animation_stack")
                else None
            )
            if preferred_stack is not None:
                selections = [preferred_stack.name]
            elif len(stacks) == 1:
                selections = [stacks[0].name]
            else:
                # Preserve every manual stack choice on one editable row when
                # curve activity has no unique winner.
                selections = [""]
            target_rig = None
            target_rig_error = ""
            try:
                automatic_dl2 = (
                    self.project.rig.retarget_mode == "auto"
                    and self.project.game_id == DL2_GAME_ID
                )
                if self.project.rig.retarget_mode == "exact" or automatic_dl2:
                    if not self.project.rig.target_rig_path:
                        raise FileNotFoundError(
                            "No target .crig is selected. Choose or import one on the Project tab."
                        )
                    target_rig = ChromeRig.load(self.project.rig.target_rig_path)
                elif self.project.game_id == DL1_GAME_ID:
                    target_rig = ChromeRig.load(
                        self.resource_root / "reference" / "male_npc_infected.crig"
                    )
            except (OSError, ValueError) as exc:
                target_rig_error = str(exc)
            for stack_name in selections:
                key = (path, stack_name)
                if key in existing:
                    continue
                multi = len(selections) > 1
                resource_seed = f"{path.stem}_{stack_name}" if multi else path.stem
                row = ProjectAnimation.create(
                    str(path),
                    resource_name=resource_seed,
                    animation_stack=stack_name,
                )
                _apply_declared_animation_timing(row, document)
                if multi:
                    row.display_name = f"{path.stem}: {stack_name}"
                prefix = self.project.export.resource_prefix.strip()
                if prefix:
                    row.resource_name = f"{prefix}_{row.resource_name}"
                preflight = preflight_fbx(
                    path, purpose="animation", animation_stack=stack_name or None,
                    target_rig=target_rig, game_id=self.project.game_id,
                    document=document,
                    tolerance=self._current_import_tolerance(),
                )
                row.extensions["fbx_preflight"] = preflight.to_dict()
                if preflight.import_blocking:
                    blocked_messages.append(
                        f"{row.display_name}\n"
                        + preflight.actionable_message(
                            finding
                            for finding in preflight.findings
                            if finding.severity == "error" and not finding.can_continue
                        )
                    )
                    continue

                attention_findings = [
                    finding
                    for finding in preflight.findings
                    if finding.severity == "warning"
                    and finding.code != "multiple_animation_stacks"
                ]

                mapping_note = ""
                verified_mapping_ready = False
                try:
                    if (
                        self.project.rig.retarget_mode == "exact" or automatic_dl2
                    ) and target_rig is not None:
                        compatibility = dict(
                            preflight.inventory.get("target_compatibility", {}) or {}
                        )
                        needs_target_map = (
                            compatibility.get("classification") != "exact_identity"
                        )
                        if preflight.repairable_findings or needs_target_map:
                            if stack_name and hasattr(
                                document, "select_animation_stack"
                            ):
                                document.select_animation_stack(stack_name)
                            profile, mapping_note = self._exact_import_mapping_profile(
                                document,
                                target_rig,
                            )
                            self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
                            row.mapping_profile_id = profile.profile_id
                            if mapping_profile_origin(profile) == "automatic_verified":
                                row.extensions["retarget_domain"] = "body"
                                verified_mapping_ready = True
                    elif self.project.rig.retarget_mode in {"auto", "humanoid"}:
                        profile = auto_map_source_bones(
                            document.limb_models,
                            parents=document.parent_by_name,
                            profile_name=f"Humanoid mapping: {row.display_name}",
                        )
                        self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
                        row.mapping_profile_id = profile.profile_id
                except Exception as exc:
                    if (
                        target_rig is not None
                        and target_rig.rig_id == DL2_ADVANCED_RIG_ID
                    ):
                        row.extensions["automatic_retarget_generation_failure"] = {
                            "status": "needs_attention",
                            "reason": "The safe DL2 body-map verification did not pass.",
                            "action": "Open Retargeting and review the diagnostic",
                            "diagnostic": str(exc),
                        }
                        mapping_note = (
                            "The safe DL2 body map could not be generated. "
                            "Open Retargeting and review the diagnostic."
                        )
                    else:
                        mapping_note = (
                            f"The clip was added, but automatic mapping failed: {exc}. "
                            "Open its mapping editor and assign bones manually."
                        )

                repairable = preflight.repairable_findings
                grouped_findings: dict[str, list[dict[str, Any]]] = {}
                for finding in preflight.findings:
                    grouped_findings.setdefault(finding.group, []).append(
                        {
                            "code": finding.code,
                            "outcome": finding.outcome,
                            "detected": finding.detected,
                            "action": finding.action,
                        }
                    )
                if (
                    preflight.findings
                    or repairable
                    or attention_findings
                    or target_rig_error
                    or mapping_note
                ):
                    mapping_failure = bool(
                        row.extensions.get("automatic_retarget_generation_failure")
                    )
                    if mapping_failure or target_rig_error or (
                        repairable and not verified_mapping_ready
                    ):
                        readiness_level = "needs_attention"
                        readiness_label = (
                            "Needs attention — review the selected target mapping"
                        )
                    elif repairable:
                        readiness_level = "advisory"
                        readiness_label = (
                            "Advisory — mapping repair applied; export remains available"
                        )
                    elif attention_findings:
                        readiness_level = "advisory"
                        readiness_label = preflight.readiness_label
                    else:
                        readiness_level = preflight.readiness_level
                        readiness_label = preflight.readiness_label
                    row.extensions["import_state"] = {
                        "status": readiness_level,
                        "level": readiness_level,
                        "label": readiness_label,
                        "requested_purpose": "animation",
                        "finding_groups": grouped_findings,
                        "repairable_codes": [finding.code for finding in repairable],
                        "warning_codes": [finding.code for finding in attention_findings],
                        "mapping_note": mapping_note,
                        "target_rig_error": target_rig_error,
                    }
                row.extensions.setdefault(
                    "import_state",
                    {
                        "status": preflight.readiness_level,
                        "level": preflight.readiness_level,
                        "label": preflight.readiness_label,
                        "requested_purpose": "animation",
                        "finding_groups": grouped_findings,
                        "repairable_codes": [],
                        "warning_codes": [],
                        "mapping_note": "",
                        "target_rig_error": "",
                    },
                )
                if repairable or attention_findings:
                    explanation = preflight.actionable_message(
                        [*repairable, *attention_findings]
                    )
                    repair_messages.append(
                        f"{row.display_name}\n{mapping_note}\n\n{explanation}"
                    )
                elif target_rig_error:
                    repair_messages.append(
                        f"{row.display_name}\nThe clip was added, but the selected target .crig "
                        f"could not be loaded: {target_rig_error}\nChoose a valid target rig, then open mapping."
                    )
                self.project.animations.append(row)
                added_rows.append(row)
                existing.add(key)
        if added_rows:
            self._mark_dirty()
        self._refresh_animation_table()
        self._refresh_retarget_clip_combo(analyze=False)
        if self.project.animations:
            self.animation_table.selectRow(len(self.project.animations) - 1)
        if blocked_messages:
            self._last_import_errors = tuple(blocked_messages)
            self.status.showMessage(
                "Cannot read — one or more FBX files were invalid or had no usable "
                "animation domain. No project row was added for those files.",
                12000,
            )
        # Repairable mapping/adaptation findings stay on the row and in its
        # details panel. Import uses a modal only for unreadable/no-skeleton/
        # no-requested-animation blockers, so recognized partial rigs do not
        # produce one popup per normal accommodation.

    def _apply_animation_import_result(self, result: _AnimationImportResult) -> None:
        """Apply a completed import result on the Qt thread."""

        self._source_cache.update(result.documents)
        self.project.mapping_profiles.update(result.mapping_profiles)
        self.project.animations.extend(result.rows)
        if result.semantic_states:
            cache = getattr(self, "_semantic_state_cache", None)
            if cache is None:
                cache = {}
                self._semantic_state_cache = cache
            for animation_id, state in result.semantic_states.items():
                animation = self.project.animation_by_id(animation_id)
                if animation is None:
                    continue
                payload = self.project.mapping_profiles.get(
                    state.profile.profile_id, state.profile.to_dict()
                )
                fingerprint = json.dumps(
                    payload,
                    sort_keys=True,
                    ensure_ascii=False,
                    separators=(",", ":"),
                )
                cache[animation_id] = (
                    (
                        animation_id,
                        animation.source_animation_stack,
                        state.profile.target_skeleton_hash,
                        fingerprint,
                    ),
                    state,
                )
        if result.rows:
            self._mark_dirty()
        self._refresh_animation_table()
        self._refresh_retarget_clip_combo(analyze=False)
        if self.project.animations:
            self.animation_table.selectRow(len(self.project.animations) - 1)
        if result.blocked_messages:
            self._last_import_errors = tuple(result.blocked_messages)
            self.status.showMessage(
                "Cannot read — one or more FBX files were invalid or had no usable "
                "animation domain. No project row was added for those files.",
                12000,
            )
        elif result.rows:
            self.status.showMessage(
                f"Imported {len(result.rows)} animation clip(s).", 6000
            )

    def _set_animation_operation_busy(
        self, busy: bool, message: str = ""
    ) -> None:
        """Prevent overlapping import/retarget operations while keeping Qt responsive."""

        for widget in (
            getattr(self, "add_animations_button", None),
            getattr(self, "remove_animation_button", None),
            getattr(self, "duplicate_animation_button", None),
            getattr(self, "retarget_auto_map_button", None),
        ):
            if widget is not None:
                widget.setEnabled(not busy)
        cancel_button = getattr(self, "cancel_animation_operation_button", None)
        if cancel_button is not None:
            cancel_button.setVisible(busy)
            cancel_button.setEnabled(busy)
            cancel_button.setText(
                "Cancel import"
                if self._animation_operation_kind == "import"
                else "Cancel retarget"
            )
        if message:
            self.status.showMessage(message)
        if not busy:
            self._animation_operation_kind = ""
            self._poll_close_when_background_idle()

    def cancel_animation_operation(self) -> None:
        if not self.background_tasks.busy or not self._animation_operation_kind:
            return
        if self.background_tasks.cancel():
            button = getattr(self, "cancel_animation_operation_button", None)
            if button is not None:
                button.setEnabled(False)
            operation = (
                "import"
                if self._animation_operation_kind == "import"
                else "automatic retarget"
            )
            self.status.showMessage(
                f"Cancelling {operation} at the next safe checkpoint…"
            )

    def _poll_close_when_background_idle(self) -> None:
        if not self._close_when_background_idle:
            return
        if self._background_work_active():
            self.qt["QTimer"].singleShot(
                100, self._poll_close_when_background_idle
            )
            return
        self._close_when_background_idle = False
        self.window.close()

    def _background_animation_error(self, title: str, failure: TaskFailure) -> None:
        self._show_error(title, RuntimeError(failure.display_message(False)))

    def remove_selected_animation(self) -> None:
        animation = self._selected_animation()
        if animation is None:
            return
        self.project.animations = [
            row for row in self.project.animations if row.animation_id != animation.animation_id
        ]
        self._mark_dirty()
        self._refresh_animation_table()
        self._refresh_retarget_clip_combo(analyze=False)

    def duplicate_selected_animation(self) -> None:
        source = self._selected_animation()
        if source is None:
            return
        row = ProjectAnimation.create(source.source_fbx, resource_name=source.resource_name + "_copy")
        row.display_name = source.display_name + " (copy)"
        row.source_animation_stack = source.source_animation_stack
        row.script_target = source.script_target
        row.root_policy = source.root_policy
        row.ik_preset = source.ik_preset
        row.mapping_profile_id = source.mapping_profile_id
        row.target_rig_ref = source.target_rig_ref
        row.target_rig_path = source.target_rig_path
        row.source_root_bone = source.source_root_bone
        row.target_root_bone = source.target_root_bone
        row.fps = source.fps
        row.source_fps = source.source_fps
        row.sample_fps = source.sample_fps
        row.playback_fps = source.playback_fps
        row.start_frame = source.start_frame
        row.end_frame = source.end_frame
        self.project.animations.append(row)
        self._mark_dirty()
        self._refresh_animation_table()
        self._refresh_retarget_clip_combo(analyze=False)
        self.animation_table.selectRow(len(self.project.animations) - 1)

    def _animation_target_mode(self, animation: ProjectAnimation) -> str:
        return resolve_animation_target(
            self.project,
            animation,
            rig_paths=getattr(self, "_rig_paths_by_ref", {}),
        ).retarget_mode

    def _retarget_ui_kind(self, animation: ProjectAnimation) -> RetargetUiKind:
        selection = resolve_animation_target(
            self.project,
            animation,
            rig_paths=getattr(self, "_rig_paths_by_ref", {}),
        )
        return retarget_ui_kind(
            self.project,
            animation,
            rig_paths=getattr(self, "_rig_paths_by_ref", {}),
            selection=selection,
        )

    def _resolved_animation_target_ref(self, animation: ProjectAnimation) -> str:
        return resolve_animation_target(
            self.project,
            animation,
            rig_paths=getattr(self, "_rig_paths_by_ref", {}),
        ).rig_ref

    def _refresh_animation_target_filter_options(self) -> None:
        combo = self.animation_target_filter
        current = str(combo.currentData() or "__all__")
        labels = dict(getattr(self, "_rig_labels_by_ref", {}))
        counts: dict[str, int] = {}
        for animation in self.project.animations:
            rig_ref = self._resolved_animation_target_ref(animation)
            counts[rig_ref] = counts.get(rig_ref, 0) + 1
        combo.blockSignals(True)
        combo.clear()
        combo.addItem(f"All target rigs ({len(self.project.animations)} clips)", "__all__")
        for rig_ref in sorted(
            counts,
            key=lambda value: (labels.get(value, value).casefold(), value.casefold()),
        ):
            label = labels.get(rig_ref, rig_ref or "No resolved target")
            combo.addItem(f"{label} ({counts[rig_ref]} clips)", rig_ref)
        index = combo.findData(current)
        combo.setCurrentIndex(index if index >= 0 else 0)
        combo.blockSignals(False)

    def set_animation_target_filter(self, rig_ref: str) -> int:
        """Filter the table to one resolved CRIG and return its visible clip count."""

        self._refresh_animation_target_filter_options()
        value = str(rig_ref or "__all__")
        index = self.animation_target_filter.findData(value)
        if index < 0 and value != "__all__":
            label = getattr(self, "_rig_labels_by_ref", {}).get(value, value)
            self.animation_target_filter.addItem(f"{label} (0 clips)", value)
            index = self.animation_target_filter.findData(value)
        self.animation_target_filter.setCurrentIndex(max(0, index))
        self._apply_animation_target_filter()
        return sum(
            not self.animation_table.isRowHidden(row)
            for row in range(self.animation_table.rowCount())
        )

    def _apply_animation_target_filter(self) -> None:
        if not hasattr(self, "animation_table"):
            return
        selected = str(self.animation_target_filter.currentData() or "__all__")
        for row in range(self.animation_table.rowCount()):
            item = self.animation_table.item(row, 2)
            animation = (
                self.project.animation_by_id(str(item.data(self.qt["Qt"].UserRole)))
                if item is not None
                else None
            )
            matches = bool(
                animation is not None
                and (
                    selected == "__all__"
                    or self._resolved_animation_target_ref(animation) == selected
                )
            )
            self.animation_table.setRowHidden(row, not matches)

    def _animation_target_combo(self, animation: ProjectAnimation) -> Any:
        combo = self._combo_box()
        default_ref = str(self.project.rig.target_rig_ref or "")
        labels = dict(getattr(self, "_rig_labels_by_ref", {}))
        default_label = labels.get(
            default_ref,
            self.project.rig.target_rig_name or default_ref or "No default target",
        )
        combo.addItem(f"Inherit project target — {default_label}", "")
        for rig_ref, label in labels.items():
            combo.addItem(label, rig_ref)
        if animation.target_rig_ref and combo.findData(animation.target_rig_ref) < 0:
            label = animation.target_rig_ref
            if animation.target_rig_path:
                try:
                    label = ChromeRig.load(animation.target_rig_path).name
                except (OSError, ValueError):
                    label += " [missing]"
            combo.addItem(label, animation.target_rig_ref)
        self._set_combo_data(combo, animation.target_rig_ref or "")
        return combo

    def _set_animation_target(self, animation_id: str, rig_ref: str) -> None:
        if self._refreshing:
            return
        animation = self.project.animation_by_id(animation_id)
        if animation is None:
            return
        rig_ref = str(rig_ref or "")
        animation.target_rig_ref = rig_ref
        if not rig_ref or rig_ref.startswith("builtin:"):
            animation.target_rig_path = ""
        else:
            animation.target_rig_path = str(
                getattr(self, "_rig_paths_by_ref", {}).get(rig_ref, "")
            )
        self._mark_dirty()
        self._refresh_animation_table()
        self._refresh_animation_target_filter_options()
        self._apply_animation_target_filter()
        if self.target_selection_changed_callback is not None:
            self.target_selection_changed_callback(animation_id)

    def _target_rig_for_status(
        self, animation: ProjectAnimation
    ) -> ChromeRig | None:
        selection = resolve_animation_target(
            self.project,
            animation,
            rig_paths=getattr(self, "_rig_paths_by_ref", {}),
        )
        path = Path(selection.rig_path) if selection.rig_path else None
        if path is None or not path.is_file():
            return None
        resolved = str(path.resolve())
        rig = self._target_rig_cache.get(resolved)
        if rig is None:
            rig = ChromeRig.load(path)
            self._target_rig_cache[resolved] = rig
        return rig

    def _animation_target_status_core(
        self,
        animation: ProjectAnimation,
        *,
        analyze: bool = True,
    ) -> tuple[str, str]:
        selection = resolve_animation_target(
            self.project,
            animation,
            rig_paths=getattr(self, "_rig_paths_by_ref", {}),
        )
        prefix = "Inherited" if selection.inherited else "Override"
        target_label = getattr(self, "_rig_labels_by_ref", {}).get(
            selection.rig_ref, selection.rig_ref or "No target"
        )
        details = [
            f"{prefix} target: {target_label}",
            f"Rig reference: {selection.rig_ref or '(empty)'}",
            f"Resolved mode: {selection.retarget_mode}",
        ]

        # Table painting and project loading must never open an FBX or build a
        # retarget plan.  A cached import result is enough for a useful status;
        # full analysis is performed only when the user opens Retargeting (or
        # explicitly rebuilds the map).
        if not analyze:
            if self._retarget_ui_kind(animation) == RetargetUiKind.CUSTOM_CRIG:
                details.append(
                    "Exact skeleton validation is deferred until build; project loading "
                    "does not open the source FBX."
                )
                return "Ready â€” exact skeleton match", "\n".join(details)

            cached = getattr(self, "_semantic_state_cache", {}).get(
                animation.animation_id
            )
            if cached is not None:
                state = cached[1]
                readiness = readiness_for_state(state)
                details.extend((readiness.reason, *readiness.details))
                return readiness.label, "\n".join(details)

            import_state = dict(animation.extensions.get("import_state", {}) or {})
            saved_label = str(import_state.get("label", "") or "")
            if saved_label:
                details.append(
                    "Detailed retarget analysis is deferred until Retargeting is opened."
                )
                return saved_label, "\n".join(details)

            payload = dict(
                self.project.mapping_profiles.get(animation.mapping_profile_id, {}) or {}
            )
            if payload:
                details.append("Using the mapping saved with the project.")
                return "Ready â€” saved mapping", "\n".join(details)

            details.append(
                "Detailed retarget analysis is deferred until Retargeting is opened."
            )
            return "Ready â€” analysis deferred", "\n".join(details)
        if selection.rig_path:
            details.append(f"CRIG path: {selection.rig_path}")

        ui_kind = self._retarget_ui_kind(animation)
        if (
            ui_kind == RetargetUiKind.BUILTIN_HUMANOID
            and self.project.game_id == DL2_GAME_ID
        ):
            try:
                state = self._bundled_semantic_state(animation)
                readiness = readiness_for_state(state)
                details.extend(
                    (
                        f"Semantic profile: {state.profile.profile_id}",
                        f"Target policy: {state.profile.target_policy_id}",
                        f"Visible semantic roles: {len(state.rows)}",
                        f"Manual overrides: {state.profile.manual_override_count}",
                        readiness.reason,
                    )
                )
                details.extend(readiness.details)
                return readiness.label, "\n".join(details)
            except Exception as exc:
                details.append(f"Bundled humanoid analysis failed: {exc}")
                return "Needs attention — semantic roles could not be analyzed", "\n".join(details)

        automatic_dl2 = (
            selection.retarget_mode == "auto" and self.project.game_id == DL2_GAME_ID
        )
        solver_routed = selection.retarget_mode == "exact" or automatic_dl2
        if solver_routed:
            rig = None
            try:
                rig = self._target_rig_for_status(animation)
            except (OSError, ValueError) as exc:
                details.append(f"Target error: {exc}")
            if rig is None:
                return "Needs attention — target rig missing", "\n".join(details)

        payload = self.project.mapping_profiles.get(
            animation.mapping_profile_id, {}
        )
        mapping_format = str(payload.get("format", "") or "")
        row_generation_failure = dict(
            animation.extensions.get(
                "automatic_retarget_generation_failure", {}
            )
            or {}
        )
        if not mapping_format:
            if row_generation_failure:
                details.append(
                    str(
                        row_generation_failure.get(
                            "reason",
                            "The safe DL2 body map could not be generated.",
                        )
                    )
                )
                details.append(
                    (
                        "Open Root & .crig Mapping"
                        if selection.retarget_mode == "exact"
                        else "Open Retargeting and review the diagnostic"
                    )
                )
                return (
                    "Needs attention — safe DL2 body map unavailable",
                    "\n".join(details),
                )
            label = (
                "Ready — exact skeleton match"
                if solver_routed
                else "Needs attention — mapping not created"
            )
            return label, "\n".join(details)

        if solver_routed:
            if mapping_format != "dl-reanimated-bone-map":
                details.append("The selected profile is not a .crig bone map.")
                return "Needs attention — wrong mapping type", "\n".join(details)
            try:
                profile = GenericBoneMap.from_dict(payload)
                errors = profile.validate()
                rig = self._target_rig_for_status(animation)
                expected_hash = profile.target_bind_hash or profile.source_skeleton_hash
                if rig is not None and expected_hash and expected_hash != rig.skeleton_hash:
                    details.append(
                        "Mapping full-bind hash does not match the selected CRIG."
                    )
                    return "Needs attention — stale target map", "\n".join(details)
                origin = mapping_profile_origin(profile)
                details.append(f"Mapping origin: {origin}")
                if errors:
                    details.extend(errors)
                    return "Needs attention — mapping invalid", "\n".join(details)
                generation_failure = dict(
                    profile.extensions.get(
                        "automatic_retarget_generation_failure", {}
                    )
                    or row_generation_failure
                )
                if generation_failure:
                    details.append(
                        str(
                            generation_failure.get(
                                "reason",
                                "The safe DL2 body map could not be verified.",
                            )
                        )
                    )
                    details.append(
                        (
                            "Open Root & .crig Mapping"
                            if selection.retarget_mode == "exact"
                            else "Open Retargeting and review the diagnostic"
                        )
                    )
                    return (
                        "Needs attention — safe DL2 body map unavailable",
                        "\n".join(details),
                    )
                if isinstance(
                    profile.extensions.get("local_retarget_recipe"), dict
                ):
                    try:
                        if rig is None:
                            raise ValueError(
                                "the selected target CRIG is unavailable"
                            )
                        document = self._source_document(animation.source_fbx)
                        if animation.source_animation_stack and hasattr(
                            document, "select_animation_stack"
                        ):
                            document.select_animation_stack(
                                animation.source_animation_stack
                            )
                        recipe_policy = build_target_retarget_policy(
                            rig,
                            game_id=self.project.game_id,
                            clip_domain="body",
                        )
                        recipe_validation = (
                            revalidate_materialized_retarget_recipe(
                                profile,
                                document,
                                rig,
                                recipe_policy,
                                clip_domain="body",
                            )
                        )
                    except Exception as exc:
                        details.append(
                            f"Live reviewed-recipe revalidation failed: {exc}"
                        )
                        return (
                            "Needs attention — reviewed recipe changed",
                            "\n".join(details),
                        )
                    if not recipe_validation.ok:
                        details.extend(
                            recipe_validation.errors
                            or ("The reviewed recipe no longer matches.",)
                        )
                        return (
                            "Needs attention — reviewed recipe changed",
                            "\n".join(details),
                        )
                    details.append("Reviewed recipe passed live revalidation.")
                if any(
                    row.review_state == "automatic_unreviewed"
                    for row in profile.base_pairs
                ):
                    return "Needs attention — mapping review required", "\n".join(details)
                if origin == "automatic_verified":
                    try:
                        if rig is None:
                            raise ValueError("the selected target CRIG is unavailable")
                        document = self._source_document(animation.source_fbx)
                        if animation.source_animation_stack and hasattr(
                            document, "select_animation_stack"
                        ):
                            document.select_animation_stack(
                                animation.source_animation_stack
                            )
                        policy = build_target_retarget_policy(
                            rig,
                            game_id=self.project.game_id,
                            clip_domain="body",
                        )
                        verification = revalidate_verified_dl2_advanced_body_map(
                            profile,
                            document,
                            rig,
                            policy,
                        )
                    except Exception as exc:
                        details.append(f"Live automatic-map revalidation failed: {exc}")
                        return (
                            "Needs attention — automatic mapping changed",
                            "\n".join(details),
                        )
                    if not verification.ok or not verification.live_revalidated:
                        details.extend(
                            verification.errors
                            or ("The verified mapping certificate is stale.",)
                        )
                        return (
                            "Needs attention — automatic mapping changed",
                            "\n".join(details),
                        )
                    certificate = dict(verification.certificate)
                    direct = int(
                        certificate.get(
                            "direct_mapping_count",
                            certificate.get("mapped_body_row_count", 0),
                        )
                        or 0
                    )
                    bind = int(
                        certificate.get(
                            "bind_row_count",
                            certificate.get("held_at_bind_row_count", 0),
                        )
                        or 0
                    )
                    details.append(
                        f"Live-verified automatic body bridge: {direct} mapped, "
                        f"{bind} held at bind."
                    )
                    return "Ready — automatically retargeted", "\n".join(details)
                if origin in {"manually_reviewed", "imported_profile"}:
                    return "Ready — reviewed mapping", "\n".join(details)
                return "Ready — exact skeleton match", "\n".join(details)
            except (TypeError, ValueError) as exc:
                details.append(str(exc))
                return "Needs attention — mapping invalid", "\n".join(details)

        if mapping_format != "dl-reanimated-retarget-profile":
            details.append("The selected profile is not a humanoid mapping.")
            return "Needs attention — wrong mapping type", "\n".join(details)
        return "Ready — automatically retargeted", "\n".join(details)

    def _animation_target_status(
        self,
        animation: ProjectAnimation,
        *,
        analyze: bool = True,
    ) -> tuple[str, str]:
        status, tooltip = self._animation_target_status_core(
            animation, analyze=analyze
        )
        import_state = dict(animation.extensions.get("import_state", {}) or {})
        import_level = str(
            import_state.get("level", import_state.get("status", "")) or ""
        )
        import_label = str(import_state.get("label", "") or "")
        if import_label and (
            import_level != "ready"
            or (status.startswith("Ready") and import_label != "Ready")
        ):
            status = import_label
        groups = dict(import_state.get("finding_groups", {}) or {})
        nonfatal_count = sum(
            len(rows)
            for group, rows in groups.items()
            if group != "fatal" and isinstance(rows, list)
        )
        if nonfatal_count:
            tooltip = (
                f"{nonfatal_count} non-blocking FBX diagnostic(s) are available in Details.\n\n"
                + tooltip
            )
        return status, tooltip

    def _refresh_animation_table(self) -> None:
        qt = self.qt
        self._refreshing = True
        try:
            table = self.animation_table
            self._refresh_animation_target_filter_options()
            animations = list(self.project.animations)
            if self.animation_target_group.isChecked():
                project_order = {
                    animation.animation_id: index
                    for index, animation in enumerate(self.project.animations)
                }
                labels = dict(getattr(self, "_rig_labels_by_ref", {}))
                animations.sort(
                    key=lambda animation: (
                        labels.get(
                            self._resolved_animation_target_ref(animation),
                            self._resolved_animation_target_ref(animation),
                        ).casefold(),
                        project_order[animation.animation_id],
                    )
                )
            table.setRowCount(len(animations))
            for row_index, animation in enumerate(animations):
                table.setRowHeight(row_index, 46)
                enabled = qt["QCheckBox"]()
                enabled.setChecked(animation.enabled)
                enabled.toggled.connect(
                    lambda value, aid=animation.animation_id: self._set_animation_field(aid, "enabled", value)
                )
                table.setCellWidget(row_index, 0, enabled)

                name = qt["QLineEdit"](animation.display_name)
                name.setMinimumHeight(32)
                name.setToolTip("Friendly project label; this does not have to match the resource name.")
                name.textChanged.connect(
                    lambda value, aid=animation.animation_id: self._set_animation_field(aid, "display_name", value)
                )
                table.setCellWidget(row_index, 1, name)

                source_item = qt["QTableWidgetItem"](animation.source_fbx)
                source_item.setData(qt["Qt"].UserRole, animation.animation_id)
                source_item.setToolTip(animation.source_fbx)
                source_item.setFlags(source_item.flags() & ~qt["Qt"].ItemIsEditable)
                table.setItem(row_index, 2, source_item)

                stack = self._combo_box()
                stack.setMinimumHeight(32)
                stack.setToolTip("Animation stack/action inside the selected FBX file.")
                document = self._cached_source_document(animation.source_fbx)
                stack_names = document.animation_stack_names if document is not None else ()
                if not stack_names and animation.source_animation_stack:
                    # The saved stack remains editable without reparsing the
                    # FBX merely to paint a project row.
                    stack_names = (animation.source_animation_stack,)
                if len(stack_names) > 1:
                    stack.addItem("Choose animation…", "")
                elif not stack_names:
                    stack.addItem("Static/default pose", "")
                for stack_name in stack_names:
                    stack.addItem(stack_name, stack_name)
                self._set_combo_data(stack, animation.source_animation_stack)
                stack.currentIndexChanged.connect(
                    lambda _index, combo=stack, aid=animation.animation_id: self._set_animation_field(
                        aid, "source_animation_stack", combo.currentData() or ""
                    )
                )
                table.setCellWidget(row_index, 3, stack)

                resource = qt["QLineEdit"](animation.resource_name)
                resource.setMinimumHeight(32)
                resource.setToolTip("Final _ANIMATION_ resource and sequence name written to the RPack.")
                resource.textChanged.connect(
                    lambda value, aid=animation.animation_id: self._set_animation_field(aid, "resource_name", value)
                )
                table.setCellWidget(row_index, 4, resource)

                script = self._script_combo(include_project_default=True)
                script.setMinimumHeight(32)
                script.setToolTip("Override the project's default _ANIMATION_SCR_ target for this clip.")
                self._set_script_combo_value(script, animation.script_target)
                script.currentTextChanged.connect(
                    lambda _value, combo=script, aid=animation.animation_id: self._set_animation_field(
                        aid, "script_target", self._script_combo_value(combo, allow_default=True)
                    )
                )
                table.setCellWidget(row_index, 5, script)

                target = self._animation_target_combo(animation)
                target.setMinimumHeight(32)
                target.setToolTip(
                    "Choose a CRIG for this clip, or inherit the project's default target. "
                    "Changing one row does not change any other animation."
                )
                target.currentIndexChanged.connect(
                    lambda _index, combo=target, aid=animation.animation_id: self._set_animation_target(
                        aid, str(combo.currentData() or "")
                    )
                )
                table.setCellWidget(row_index, 6, target)

                status_text, status_tooltip = self._animation_target_status(
                    animation, analyze=False
                )
                target_status = qt["QTableWidgetItem"](status_text)
                target_status.setToolTip(status_tooltip)
                target_status.setFlags(
                    target_status.flags() & ~qt["Qt"].ItemIsEditable
                )
                table.setItem(row_index, 7, target_status)

                root = self._combo_box()
                root.setMinimumHeight(32)
                root.setToolTip(
                    "In place locks motion; Skeletal root writes movement to the selected target root; Motion accumulator "
                    "splits pose/root motion for consumers that accumulate OffsetHelper motion."
                )
                root.addItem("In place", "inplace")
                target_root_label = animation.target_root_bone
                if not target_root_label:
                    try:
                        selection = resolve_animation_target(
                            self.project,
                            animation,
                            rig_paths=getattr(self, "_rig_paths_by_ref", {}),
                        )
                        package = get_game_profile(self.project.game_id).package_for_rig_ref(
                            selection.rig_ref
                        )
                        target_root_label = package.primary_root if package else "selected target"
                    except Exception:
                        target_root_label = "selected target"
                root.addItem(f"Skeletal root ({target_root_label})", "bip01")
                root.addItem("Motion accumulator", "motion")
                self._set_combo_data(root, animation.root_policy)
                root.currentIndexChanged.connect(
                    lambda _index, combo=root, aid=animation.animation_id: self._set_animation_field(
                        aid, "root_policy", combo.currentData()
                    )
                )
                table.setCellWidget(row_index, 8, root)

                ik = self._combo_box()
                ik.setMinimumHeight(32)
                ik.setToolTip(
                    "Authoring recommendation for the movie/animation-graph consumer. IK is not a "
                    "universal flag stored inside ANM2."
                )
                ik.addItem("Runtime / consumer IK", "runtime")
                ik.addItem("IK off authoring preset", "off")
                self._set_combo_data(ik, animation.ik_preset)
                ik.currentIndexChanged.connect(
                    lambda _index, combo=ik, aid=animation.animation_id: self._set_animation_field(
                        aid, "ik_preset", combo.currentData()
                    )
                )
                table.setCellWidget(row_index, 9, ik)

                custom_crig_mapping = (
                    self._retarget_ui_kind(animation) == RetargetUiKind.CUSTOM_CRIG
                )
                needs_mapping_attention = status_text.startswith("Needs attention")
                mapping = qt["QPushButton"](
                    "Fix mapping…" if needs_mapping_attention else "Details…"
                )
                mapping.setMinimumHeight(32)
                mapping.setToolTip(
                    (
                        "Open Root & .crig Mapping"
                        if needs_mapping_attention and custom_crig_mapping
                        else "Open Retargeting and review the diagnostic"
                        if needs_mapping_attention
                        else "Select this clip and show its retarget and FBX diagnostics below."
                    )
                )
                if needs_mapping_attention:
                    mapping.clicked.connect(
                        lambda _checked=False, aid=animation.animation_id: self._open_mapping_for_animation(aid)
                    )
                else:
                    mapping.clicked.connect(
                        lambda _checked=False, index=row_index: (
                            self.animation_table.selectRow(index),
                            self._animation_selection_changed(),
                        )
                    )
                table.setCellWidget(row_index, 10, mapping)
        finally:
            self._refreshing = False
        self._apply_animation_target_filter()
        self._animation_selection_changed()

    def _set_animation_field(self, animation_id: str, field_name: str, value: Any) -> None:
        if self._refreshing:
            return
        animation = self.project.animation_by_id(animation_id)
        if animation is None:
            return
        setattr(animation, field_name, value)
        self._mark_dirty()
        if field_name in {"display_name", "source_fbx"}:
            self._refresh_retarget_clip_combo(analyze=False)

    def _selected_animation(self) -> ProjectAnimation | None:
        row = self.animation_table.currentRow()
        if row < 0:
            return None
        item = self.animation_table.item(row, 2)
        if item is None:
            return None
        return self.project.animation_by_id(str(item.data(self.qt["Qt"].UserRole)))

    def _animation_selection_changed(self) -> None:
        animation = self._selected_animation()
        self._refreshing = True
        try:
            enabled = animation is not None
            for widget in (
                self.start_frame_spin,
                self.end_frame_spin,
                self.fps_spin,
                self.sample_fps_spin,
            ):
                widget.setEnabled(enabled)
            if animation is None:
                self.start_frame_spin.setValue(-1)
                self.end_frame_spin.setValue(-1)
                self.fps_spin.setValue(30)
                self.sample_fps_spin.setValue(30)
                self.source_fps_label.setText("—")
            else:
                self.start_frame_spin.setValue(
                    -1 if animation.start_frame is None else animation.start_frame
                )
                self.end_frame_spin.setValue(
                    -1 if animation.end_frame is None else animation.end_frame
                )
                self.fps_spin.setValue(animation.resolved_playback_fps())
                self.sample_fps_spin.setValue(animation.resolved_sample_fps())
                self.source_fps_label.setText(
                    f"{animation.source_fps:g}"
                    if animation.source_fps is not None
                    else "Unknown"
                )
            if hasattr(self, "animation_import_diagnostics"):
                self.animation_import_diagnostics.setPlainText(
                    self._format_animation_import_diagnostics(animation)
                )
        finally:
            self._refreshing = False

    @staticmethod
    def _format_animation_import_diagnostics(
        animation: ProjectAnimation | None,
    ) -> str:
        if animation is None:
            return "Select an imported clip to review its FBX diagnostics."
        payload = dict(animation.extensions.get("fbx_preflight", {}) or {})
        if not payload:
            return (
                f"File: {animation.source_fbx}\n"
                "No saved FBX preflight report is available for this project row."
            )
        lines = [
            f"File: {payload.get('path') or animation.source_fbx}",
            f"Requested purpose: {payload.get('purpose', 'animation')}",
        ]
        grouped: dict[str, list[dict[str, Any]]] = {
            "repaired": [],
            "ignored": [],
            "needs_review": [],
            "fatal": [],
        }
        for finding in payload.get("findings", ()) or ():
            if not isinstance(finding, dict):
                continue
            group = str(finding.get("group", "needs_review") or "needs_review")
            grouped.setdefault(group, []).append(finding)
        for group in ("repaired", "ignored", "needs_review", "fatal"):
            rows = grouped.get(group, [])
            lines.extend(("", f"{group.replace('_', ' ').title()} ({len(rows)})"))
            if not rows:
                lines.append("  None")
                continue
            for finding in rows:
                lines.append(
                    f"  [{finding.get('code', 'fbx_finding')}] "
                    f"{finding.get('detected', '')}"
                )
                action = str(finding.get("action", "") or "")
                if action:
                    lines.append(f"    Action: {action}")
        return "\n".join(lines)

    def _selected_range_changed(self) -> None:
        if self._refreshing:
            return
        animation = self._selected_animation()
        if animation is None:
            return
        animation.start_frame = None if self.start_frame_spin.value() < 0 else self.start_frame_spin.value()
        animation.end_frame = None if self.end_frame_spin.value() < 0 else self.end_frame_spin.value()
        animation.playback_fps = float(self.fps_spin.value())
        animation.sample_fps = float(self.sample_fps_spin.value())
        animation.fps = animation.playback_fps
        self._mark_dirty()

    # --------------------------------------------------------------- retarget
    def _target_inventory_for_animation(
        self, animation: ProjectAnimation
    ) -> tuple[str, tuple[str, ...]]:
        selection = resolve_animation_target(
            self.project,
            animation,
            rig_paths=getattr(self, "_rig_paths_by_ref", {}),
        )
        rig = self._target_rig_for_status(animation)
        if rig is not None:
            return selection.rig_ref, tuple(bone.name for bone in rig.bones)
        configured = Path(self.project.rig.canonical_smd)
        candidates = [configured]
        if not configured.is_absolute():
            candidates.append(resource_root() / configured)
        smd_path = next((path for path in candidates if path.is_file()), None)
        if smd_path is None:
            raise FileNotFoundError(
                f"Target SMD was not found: {self.project.rig.canonical_smd}"
            )
        return selection.rig_ref, tuple(
            row.name for row in read_smd_hierarchy(smd_path)
        )

    def _refresh_root_locomotion_panel(
        self,
        animation: ProjectAnimation,
        profile: SourceBoneMappingProfile,
    ) -> None:
        document = self._source_document(animation.source_fbx)
        target_ref, target_names = self._target_inventory_for_animation(animation)
        locomotion = get_builtin_locomotion_profile(self.project.game_id, target_ref)
        source_names = tuple(sorted(document.limb_models, key=str.casefold))
        requested = RootMotionSelection.from_dict(
            profile.root_motion,
            legacy_policy=animation.root_policy,
            source_root_bone=animation.source_root_bone,
            target_root_bone=animation.target_root_bone,
        )
        try:
            automatic_source_root, _method = resolve_source_root(
                document.limb_models,
                document.parent_by_name,
                requested_bone=requested.source_root_bone,
            )
        except ValueError:
            automatic_source_root = requested.source_root_bone
        source_root = requested.source_root_bone or automatic_source_root
        target_root = requested.target_root_bone or locomotion.primary_root
        left_source = str(
            profile.locomotion.get("left_source_foot")
            or profile.role_to_bone.get("left_foot", "")
        )
        right_source = str(
            profile.locomotion.get("right_source_foot")
            or profile.role_to_bone.get("right_foot", "")
        )

        self._refreshing = True
        try:
            self.root_locomotion_panel.setVisible(True)
            for combo, names, selected in (
                (self.root_source_combo, source_names, source_root),
                (self.left_source_foot_combo, source_names, left_source),
                (self.right_source_foot_combo, source_names, right_source),
                (self.root_target_combo, target_names, target_root),
                (
                    self.left_target_foot_combo,
                    target_names,
                    str(profile.locomotion.get("left_target_foot") or locomotion.left_foot),
                ),
                (
                    self.right_target_foot_combo,
                    target_names,
                    str(profile.locomotion.get("right_target_foot") or locomotion.right_foot),
                ),
            ):
                combo.clear()
                combo.addItem("(Automatic)", "")
                for name in names:
                    combo.addItem(name, name)
                selected_index = combo.findData(selected)
                combo.setCurrentIndex(selected_index if selected_index >= 0 else 0)
            self._set_combo_data(self.root_motion_mode_combo, requested.motion_mode)
            self._set_combo_data(self.root_heading_mode_combo, requested.heading_mode)
            self._set_combo_data(
                self.ik_recommendation_combo,
                str(profile.locomotion.get("ik_preset") or animation.ik_preset),
            )
            soles = ", ".join(
                value
                for value in (
                    locomotion.left_sole_helper,
                    locomotion.right_sole_helper,
                )
                if value
            ) or "none"
            legacy_ik = ", ".join(
                value
                for value in (
                    locomotion.legacy_left_ik_root,
                    locomotion.legacy_right_ik_root,
                )
                if value
            ) or "none (Advanced uses no hidden IK-root dependency)"
            self.locomotion_policy_note.setText(
                f"Target-owned profile: {locomotion.profile_id}. Sole helpers: {soles}. "
                f"Legacy IK targets: {legacy_ik}. IK ownership: {locomotion.ik_owner}; "
                "ANM2 stores transforms, not a universal IK enable flag."
            )
        finally:
            self._refreshing = False

    def _root_locomotion_widgets_changed(self, *_args: Any) -> None:
        if self._refreshing:
            return
        animation = self._retarget_animation()
        if animation is None:
            return
        payload = self.project.mapping_profiles.get(animation.mapping_profile_id, {})
        if not payload:
            return
        try:
            profile = SourceBoneMappingProfile.from_dict(payload)
            motion_mode = str(self.root_motion_mode_combo.currentData())
            heading_mode = str(self.root_heading_mode_combo.currentData())
            if motion_mode == RootMotionMode.IN_PLACE.value:
                heading_mode = RootHeadingMode.LOCK_INITIAL.value
            elif motion_mode == RootMotionMode.MOTION_ACCUMULATOR.value:
                heading_mode = RootHeadingMode.TO_MOTION_ACCUMULATOR.value
            elif heading_mode == RootHeadingMode.TO_MOTION_ACCUMULATOR.value:
                heading_mode = RootHeadingMode.PRESERVE.value
            selection = RootMotionSelection(
                str(self.root_source_combo.currentData() or ""),
                str(self.root_target_combo.currentData() or ""),
                motion_mode,
                heading_mode,
            )
            selection.store(animation)
            profile.root_motion = selection.to_dict()
            target_ref, _target_names = self._target_inventory_for_animation(animation)
            target_locomotion = get_builtin_locomotion_profile(
                self.project.game_id, target_ref
            )
            profile.locomotion = {
                "profile_id": target_locomotion.profile_id,
                "left_source_foot": str(self.left_source_foot_combo.currentData() or ""),
                "right_source_foot": str(self.right_source_foot_combo.currentData() or ""),
                "left_target_foot": str(
                    self.left_target_foot_combo.currentData() or target_locomotion.left_foot
                ),
                "right_target_foot": str(
                    self.right_target_foot_combo.currentData() or target_locomotion.right_foot
                ),
                "left_sole_helper": target_locomotion.left_sole_helper,
                "right_sole_helper": target_locomotion.right_sole_helper,
                "legacy_left_ik_root": target_locomotion.legacy_left_ik_root,
                "legacy_right_ik_root": target_locomotion.legacy_right_ik_root,
                "ik_preset": str(self.ik_recommendation_combo.currentData() or "runtime"),
                "ik_owner": target_locomotion.ik_owner,
            }
            animation.ik_preset = str(profile.locomotion["ik_preset"])
            profile.clear_compiled_cache()
            self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
            animation.extensions.pop("compiled_target_map_profile_id", None)
            animation.extensions.pop("compiled_target_map_hash", None)
            animation.extensions.pop("compiled_target_map_live_validation", None)
            getattr(self, "_semantic_state_cache", {}).pop(animation.animation_id, None)
            self._set_combo_data(self.root_heading_mode_combo, heading_mode)
            self._mark_dirty()
            self._refresh_animation_table()
        except Exception as exc:
            self._show_error("Could not update root and locomotion", exc)

    def _refresh_retarget_clip_combo(self, *, analyze: bool = True) -> None:
        current = self.retarget_clip_combo.currentData()
        self._refreshing = True
        try:
            self.retarget_clip_combo.clear()
            for row in self.project.animations:
                self.retarget_clip_combo.addItem(row.display_name, row.animation_id)
            if current:
                self._set_combo_data(self.retarget_clip_combo, current)
        finally:
            self._refreshing = False
        if analyze:
            self._retarget_clip_changed()
        else:
            self.root_locomotion_panel.setVisible(False)
            self.mapping_table.setRowCount(0)
            if self.project.animations:
                self.mapping_status.setText(
                    "Retargeting analysis is deferred until this tab is opened."
                )
                self.ignored_bones.setPlainText(
                    "Import and project loading remain responsive; open Retargeting "
                    "to inspect the selected clip."
                )
            else:
                self.mapping_status.setText(
                    "Add an FBX animation to create a humanoid mapping."
                )
                self.ignored_bones.clear()

    def _bundled_semantic_state(
        self,
        animation: ProjectAnimation,
        *,
        force: bool = False,
    ) -> BundledSemanticState:
        if self.project.game_id != DL2_GAME_ID:
            raise ValueError("The DL2 semantic planner is only used by bundled DL2 targets")
        rig = self._target_rig_for_status(animation)
        if rig is None:
            raise FileNotFoundError("The selected bundled target rig is unavailable")
        policy = build_target_retarget_policy(
            rig, game_id=self.project.game_id, clip_domain="body"
        )
        document = self._source_document(animation.source_fbx)
        if animation.source_animation_stack and hasattr(document, "select_animation_stack"):
            document.select_animation_stack(animation.source_animation_stack)
        payload = dict(
            self.project.mapping_profiles.get(animation.mapping_profile_id, {}) or {}
        )
        fingerprint = json.dumps(
            payload, sort_keys=True, ensure_ascii=False, separators=(",", ":")
        )
        cache_key = (
            animation.animation_id,
            animation.source_animation_stack,
            rig.skeleton_hash,
            fingerprint,
        )
        cached = getattr(self, "_semantic_state_cache", {}).get(animation.animation_id)
        if not force and cached is not None and cached[0] == cache_key:
            return cached[1]

        profile: SourceBoneMappingProfile | None = None
        migrated_from = ""
        if payload.get("format") == "dl-reanimated-retarget-profile":
            profile = SourceBoneMappingProfile.from_dict(payload)
        elif payload.get("format") == "dl-reanimated-bone-map":
            old_map = GenericBoneMap.from_dict(payload)
            profile = migrate_generic_map_to_semantic_profile(
                old_map,
                document.limb_models,
                document.parent_by_name,
                policy,
                name=f"Bundled humanoid mapping: {animation.display_name}",
            )
            migrated_from = old_map.profile_id

        before = profile.to_dict() if profile is not None else None
        state = prepare_bundled_semantic_state(
            document,
            rig,
            policy,
            profile,
            profile_name=f"Bundled humanoid mapping: {animation.display_name}",
        )
        self.project.mapping_profiles[state.profile.profile_id] = state.profile.to_dict()
        animation.mapping_profile_id = state.profile.profile_id
        if migrated_from:
            animation.extensions["legacy_target_map_profile_id"] = migrated_from
            animation.extensions.pop("compiled_target_map_profile_id", None)
            animation.extensions.pop("compiled_target_map_hash", None)
            animation.extensions.pop("compiled_target_map_live_validation", None)
            animation.extensions["semantic_profile_migration"] = dict(
                state.profile.extensions.get("migration_audit", {}) or {}
            )
        if before != state.profile.to_dict() or migrated_from:
            self._mark_dirty()
        refreshed_payload = self.project.mapping_profiles[state.profile.profile_id]
        refreshed_fingerprint = json.dumps(
            refreshed_payload,
            sort_keys=True,
            ensure_ascii=False,
            separators=(",", ":"),
        )
        refreshed_key = (
            animation.animation_id,
            animation.source_animation_stack,
            rig.skeleton_hash,
            refreshed_fingerprint,
        )
        cache = getattr(self, "_semantic_state_cache", None)
        if cache is None:
            cache = {}
            self._semantic_state_cache = cache
        cache[animation.animation_id] = (refreshed_key, state)
        return state

    def _refresh_bundled_semantic_table(
        self,
        animation: ProjectAnimation,
        state: BundledSemanticState,
    ) -> None:
        qt = self.qt
        source_bones = sorted(
            self._source_document(animation.source_fbx).limb_models,
            key=str.casefold,
        )
        rig = self._target_rig_for_status(animation)
        if rig is None:
            raise FileNotFoundError("The selected bundled target rig is unavailable")
        semantic_targets = tuple(row.target_bone for row in state.rows)
        extra_targets = visible_extra_target_names(
            tuple(bone.name for bone in rig.bones),
            semantic_targets,
            target_rig_ref=rig.rig_id,
            show_helper_bones=self.show_helper_bones.isChecked(),
            show_all_target_bones=self.show_all_target_bones.isChecked(),
        )
        if self.show_all_target_bones.isChecked() and (
            len(state.rows) + len(extra_targets) != len(rig.bones)
        ):
            raise ValueError(
                "Complete target inventory is not one-to-one with the selected CRIG"
            )
        self.mapping_table.setHorizontalHeaderLabels(
            ["Group", "Target bone / role", "Source FBX bone", "Mode", "Transfer", "Component", "Status"]
        )
        self._first_unresolved_mapping_row = -1
        self._refreshing = True
        try:
            self.show_helper_bones.setVisible(True)
            self.show_all_target_bones.setVisible(True)
            self.retarget_auto_map_button.setText("Auto-map humanoid")
            self.mapping_table.setRowCount(len(state.rows) + len(extra_targets))
            for row_index, row in enumerate(state.rows):
                role_payload = {
                    "profile_role": row.profile_role,
                    "semantic_role": row.semantic_role,
                    "target_bone": row.target_bone,
                    "plan_mode": row.plan_mode,
                }
                for column, value in (
                    (0, row.group),
                    (1, f"{row.label}  [{row.target_bone}]"),
                    (6, f"{row.requirement} - {row.result}"),
                ):
                    item = qt["QTableWidgetItem"](value)
                    item.setData(qt["Qt"].UserRole, role_payload)
                    item.setFlags(item.flags() & ~qt["Qt"].ItemIsEditable)
                    item.setToolTip(
                        f"Target: {row.target_bone}\nSemantic role: {row.semantic_role}\n{row.result}"
                    )
                    self.mapping_table.setItem(row_index, column, item)

                combo = self._combo_box()
                combo.setMinimumHeight(30)
                automatic_source = " + ".join(row.source_bones)
                combo.addItem(
                    f"(Auto) - {automatic_source}" if automatic_source else "(Auto)",
                    "__auto__",
                )
                combo.addItem("(Inherit parent / target bind)", "__inherit_bind__")
                combo.addItem("(Hold at bind)", "__static_bind__")
                for source_name in source_bones:
                    combo.addItem(source_name, source_name)
                if row.selected_mode == "inherit_bind":
                    selected_value = "__inherit_bind__"
                elif row.selected_mode == "static_bind":
                    selected_value = "__static_bind__"
                elif row.selected_mode == "direct":
                    selected_value = str(
                        state.profile.role_to_bone.get(row.profile_role, "") or ""
                    )
                else:
                    selected_value = "__auto__"
                selected_index = combo.findData(selected_value)
                if selected_index >= 0:
                    combo.setCurrentIndex(selected_index)
                combo.setToolTip(
                    f"Choose the source FBX bone for {row.label}, leave it automatic, "
                    "or explicitly retain target bind motion."
                )
                combo.activated.connect(
                    lambda _index,
                    aid=animation.animation_id,
                    role_id=row.profile_role,
                    widget=combo: self._semantic_mapping_changed(
                        aid, role_id, str(widget.currentData() or "__auto__")
                    )
                )
                self.mapping_table.setCellWidget(row_index, 2, combo)

                mode_combo = self._combo_box()
                mode_combo.addItem("Auto", "auto")
                mode_combo.addItem("Direct", "direct")
                mode_combo.addItem("Inherit parent / bind", "inherit_bind")
                mode_combo.addItem("Static bind", "static_bind")
                self._set_combo_data(mode_combo, row.selected_mode)
                mode_combo.currentIndexChanged.connect(
                    lambda _index,
                    aid=animation.animation_id,
                    role_id=row.profile_role,
                    mode=mode_combo,
                    source=combo: self._semantic_mode_widget_changed(
                        aid, role_id, str(mode.currentData() or "auto"), source
                    )
                )
                self.mapping_table.setCellWidget(row_index, 3, mode_combo)

                transfer_combo = self._combo_box()
                transfer_value = (
                    "bind"
                    if row.plan_mode in {"inherit_bind", "static_bind"}
                    else "rotation_delta"
                    if row.plan_mode in {"composed", "distributed"}
                    else "global_bind_basis"
                )
                transfer_combo.addItem(_TRANSFER_LABELS[transfer_value], transfer_value)
                transfer_combo.setEnabled(False)
                self.mapping_table.setCellWidget(row_index, 4, transfer_combo)
                component_combo = self._combo_box()
                component_combo.addItem("Rotation", "rotation")
                component_combo.setEnabled(False)
                self.mapping_table.setCellWidget(row_index, 5, component_combo)
                if row.plan_mode == "manual_required" and self._first_unresolved_mapping_row < 0:
                    self._first_unresolved_mapping_row = row_index

            target_by_name = {bone.name: bone for bone in rig.bones}
            for offset, target_name in enumerate(extra_targets):
                row_index = len(state.rows) + offset
                target_bone = target_by_name[target_name]
                override = dict(
                    state.profile.target_bone_overrides.get(target_name, {}) or {}
                )
                mode_value = str(override.get("mode", "auto") or "auto")
                source_value = str(override.get("source_bone", "") or "")
                transfer_value = str(
                    override.get("transfer_policy", "default") or "default"
                )
                component_value = str(
                    override.get("component_policy", "rotation") or "rotation"
                )
                group = (
                    "Helper"
                    if target_bone.helper
                    else str(target_bone.tags[0]).replace("_", " ").title()
                    if target_bone.tags
                    else "Target"
                )
                status = (
                    f"Direct from {source_value}"
                    if mode_value == "direct"
                    else "Held at bind"
                    if mode_value in {"inherit_bind", "static_bind"}
                    else "Target default (bind unless semantically mapped)"
                )
                for column, value in ((0, group), (1, target_name), (6, status)):
                    item = qt["QTableWidgetItem"](value)
                    item.setData(
                        qt["Qt"].UserRole,
                        {"target_bone": target_name, "target_override": True},
                    )
                    item.setFlags(item.flags() & ~qt["Qt"].ItemIsEditable)
                    self.mapping_table.setItem(row_index, column, item)

                source_combo = self._combo_box()
                source_combo.addItem("(No direct source)", "")
                for source_name in source_bones:
                    source_combo.addItem(source_name, source_name)
                self._set_combo_data(source_combo, source_value)
                mode_combo = self._combo_box()
                for label, value in (
                    ("Auto", "auto"),
                    ("Direct", "direct"),
                    ("Inherit parent / bind", "inherit_bind"),
                    ("Static bind", "static_bind"),
                ):
                    mode_combo.addItem(label, value)
                self._set_combo_data(mode_combo, mode_value)
                transfer_combo = self._combo_box()
                for value in TRANSFER_POLICIES:
                    transfer_combo.addItem(_TRANSFER_LABELS[value], value)
                self._set_combo_data(transfer_combo, transfer_value)
                component_combo = self._combo_box()
                for value in COMPONENT_POLICIES:
                    component_combo.addItem(_HELPER_COMPONENT_LABELS[value], value)
                self._set_combo_data(component_combo, component_value)
                callback = (
                    lambda _index,
                    aid=animation.animation_id,
                    target=target_name,
                    source=source_combo,
                    mode=mode_combo,
                    transfer=transfer_combo,
                    component=component_combo: self._target_bone_override_widgets_changed(
                        aid, target, source, mode, transfer, component
                    )
                )
                source_combo.currentIndexChanged.connect(callback)
                mode_combo.currentIndexChanged.connect(callback)
                transfer_combo.currentIndexChanged.connect(callback)
                component_combo.currentIndexChanged.connect(callback)
                self.mapping_table.setCellWidget(row_index, 2, source_combo)
                self.mapping_table.setCellWidget(row_index, 3, mode_combo)
                self.mapping_table.setCellWidget(row_index, 4, transfer_combo)
                self.mapping_table.setCellWidget(row_index, 5, component_combo)
        finally:
            self._refreshing = False

        readiness = readiness_for_state(state)
        mapped_count = sum(
            row.mode in {"direct", "composed", "distributed"}
            for row in state.plan.decisions
        )
        bind_count = sum(
            row.mode in {"inherit_bind", "static_bind"}
            for row in state.plan.decisions
        )
        ignored_animated = state.plan.ignored_animated_source_bones
        color = (
            "#2e7d32"
            if readiness.state == "ready"
            else "#b26a00"
            if readiness.state == "advisory"
            else "#b71c1c"
        )
        self.mapping_status.setText(
            f"<b style='color:{color}'>{readiness.label}</b> — "
            f"{len(state.rows)} editable semantic roles; "
            f"{mapped_count} target row(s) mapped; "
            f"{bind_count} target row(s) use bind defaults; "
            f"{len(ignored_animated)} animated source track(s) ignored; "
            f"{state.profile.manual_override_count} manual override(s).<br>"
            f"{readiness.reason}"
        )
        self.mapping_status.setToolTip("\n".join(readiness.details))
        self.ignored_bones.setPlainText("\n".join(state.profile.ignored_bones))
        self._filter_mapping_rows()

    def _semantic_mode_widget_changed(
        self,
        animation_id: str,
        profile_role: str,
        mode: str,
        source_combo: Any,
    ) -> None:
        if self._refreshing:
            return
        if mode == "auto":
            selected = "__auto__"
        elif mode == "inherit_bind":
            selected = "__inherit_bind__"
        elif mode == "static_bind":
            selected = "__static_bind__"
        else:
            selected = str(source_combo.currentData() or "")
            if selected.startswith("__") or not selected:
                animation = self.project.animation_by_id(animation_id)
                payload = (
                    self.project.mapping_profiles.get(animation.mapping_profile_id, {})
                    if animation is not None
                    else {}
                )
                profile = SourceBoneMappingProfile.from_dict(payload)
                selected = str(profile.role_to_bone.get(profile_role, "") or "")
            if not selected:
                return
        self._semantic_mapping_changed(animation_id, profile_role, selected)

    def _target_bone_override_widgets_changed(
        self,
        animation_id: str,
        target_name: str,
        source_combo: Any,
        mode_combo: Any,
        transfer_combo: Any,
        component_combo: Any,
    ) -> None:
        if self._refreshing:
            return
        animation = self.project.animation_by_id(animation_id)
        if animation is None:
            return
        payload = self.project.mapping_profiles.get(animation.mapping_profile_id, {})
        profile = SourceBoneMappingProfile.from_dict(payload)
        source_name = str(source_combo.currentData() or "")
        mode = str(mode_combo.currentData() or "auto")
        if source_name and mode == "auto":
            mode = "direct"
        elif mode == "direct" and not source_name:
            mode = "auto"
        profile.set_target_bone_override(
            target_name,
            mode=mode,
            source_bone=source_name,
            transfer_policy=str(transfer_combo.currentData() or "default"),
            component_policy=str(component_combo.currentData() or "rotation"),
        )
        animation.extensions.pop("compiled_target_map_profile_id", None)
        animation.extensions.pop("compiled_target_map_hash", None)
        animation.extensions.pop("compiled_target_map_live_validation", None)
        self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
        getattr(self, "_semantic_state_cache", {}).pop(animation_id, None)
        self._mark_dirty()
        refreshed = self._bundled_semantic_state(animation, force=True)
        self._refresh_bundled_semantic_table(animation, refreshed)
        self._refresh_animation_table()

    def _semantic_mapping_changed(
        self,
        animation_id: str,
        profile_role: str,
        selected_value: str,
    ) -> None:
        if self._refreshing:
            return
        animation = self.project.animation_by_id(animation_id)
        if animation is None:
            return
        payload = self.project.mapping_profiles.get(animation.mapping_profile_id, {})
        profile = SourceBoneMappingProfile.from_dict(payload)
        if selected_value == "__auto__":
            profile.set_role_mode(profile_role, "auto")
        elif selected_value == "__inherit_bind__":
            profile.set_role_mode(profile_role, "inherit_bind")
        elif selected_value == "__static_bind__":
            profile.set_role_mode(profile_role, "static_bind")
        else:
            profile.set_mapping(
                profile_role,
                selected_value,
                confidence=1.0,
                method="manual_override",
                mode="direct",
                evidence=(
                    {
                        "kind": "manual_override",
                        "score": 1.0,
                        "detail": "selected in Retargeting",
                        "source": "user",
                    },
                ),
            )
        profile.clear_compiled_cache()
        animation.extensions.pop("compiled_target_map_profile_id", None)
        animation.extensions.pop("compiled_target_map_hash", None)
        self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
        getattr(self, "_semantic_state_cache", {}).pop(animation_id, None)
        self._mark_dirty()
        state = self._bundled_semantic_state(animation, force=True)
        self._refresh_bundled_semantic_table(animation, state)
        self._refresh_animation_table()

    def focus_first_unresolved_mapping_role(self) -> None:
        row = int(getattr(self, "_first_unresolved_mapping_row", -1))
        if row < 0 or row >= self.mapping_table.rowCount():
            return
        self.retarget_filter.clear()
        self.mapping_table.selectRow(row)
        item = self.mapping_table.item(row, 1)
        if item is not None:
            self.mapping_table.scrollToItem(item)

    def _retarget_clip_changed(self) -> None:
        if self._refreshing:
            return
        animation = self.project.animation_by_id(str(self.retarget_clip_combo.currentData() or ""))
        mode = self._animation_target_mode(animation) if animation is not None else ""
        ui_kind = self._retarget_ui_kind(animation) if animation is not None else None
        if (
            animation is not None
            and ui_kind == RetargetUiKind.BUILTIN_HUMANOID
            and self.project.game_id == DL2_GAME_ID
        ):
            try:
                state = self._bundled_semantic_state(animation)
                self._refresh_bundled_semantic_table(animation, state)
                # Keep the semantic table usable for integrations that expose
                # a lightweight state object. Production states always carry
                # the visible profile required by this companion panel.
                profile = getattr(state, "profile", None)
                if profile is not None:
                    self._refresh_root_locomotion_panel(animation, profile)
            except Exception as exc:
                self.mapping_table.setRowCount(0)
                self.mapping_status.setText(
                    "<b style='color:#b71c1c'>Cannot analyze bundled humanoid</b> — "
                    + str(exc)
                )
                self.ignored_bones.setPlainText(str(exc))
            return
        self.show_helper_bones.setVisible(True)
        self.show_all_target_bones.setVisible(True)
        if animation is not None and ui_kind == RetargetUiKind.CUSTOM_CRIG:
            self.root_locomotion_panel.setVisible(False)
            self.mapping_table.setRowCount(0)
            self.mapping_status.setText(
                "<b style='color:#2e7d32'>Exact skeleton mode</b> — bone names and parents "
                "are checked against the selected .crig during build; no humanoid mapping is required."
            )
            self.ignored_bones.setPlainText(
                "Exact mode preserves every target bone track, including small-object and "
                "non-humanoid skeletons."
            )
            return
        if animation is None:
            self.root_locomotion_panel.setVisible(False)
            self.mapping_table.setRowCount(0)
            self.mapping_status.setText("Add an FBX animation to create a humanoid mapping.")
            self.ignored_bones.clear()
            return
        try:
            document = self._source_document(animation.source_fbx)
            profile = self._profile_for_animation(animation, document, create=True)
            assert profile is not None
            self._refresh_root_locomotion_panel(animation, profile)
            self._refresh_mapping_table(animation, document, profile)
        except Exception as exc:
            self.mapping_table.setRowCount(0)
            self.mapping_status.setText(str(exc))

    def _source_document(self, path: str) -> FbxDocument:
        resolved = str(Path(path).resolve())
        document = self._source_cache.get(resolved)
        if document is None:
            document = FbxDocument(
                Path(resolved),
                purpose="animation",
                tolerance=self._current_import_tolerance(),
            )
            self._source_cache[resolved] = document
        return document

    def _cached_source_document(self, path: str) -> FbxDocument | None:
        """Return a parsed source only when another operation already loaded it."""

        return self._source_cache.get(str(Path(path).resolve()))

    def _profile_for_animation(
        self,
        animation: ProjectAnimation,
        document: FbxDocument,
        *,
        create: bool,
    ) -> SourceBoneMappingProfile | None:
        if animation.mapping_profile_id:
            payload = self.project.mapping_profiles.get(animation.mapping_profile_id)
            if payload is not None:
                return SourceBoneMappingProfile.from_dict(payload)
        if not create:
            return None
        profile = auto_map_source_bones(
            document.limb_models,
            parents=document.parent_by_name,
            profile_name=f"Humanoid mapping: {animation.display_name}",
        )
        self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
        animation.mapping_profile_id = profile.profile_id
        self._mark_dirty()
        return profile

    def _target_helper_names(self) -> tuple[str, ...]:
        configured = Path(self.project.rig.canonical_smd)
        candidates = [configured]
        if not configured.is_absolute():
            candidates.append(resource_root() / configured)
        smd_path = next((path for path in candidates if path.is_file()), None)
        if smd_path is None:
            raise FileNotFoundError(
                f"Target SMD was not found: {self.project.rig.canonical_smd}"
            )
        hierarchy = read_smd_hierarchy(smd_path)
        return recognized_helper_names(row.name for row in hierarchy)

    def _refresh_mapping_table(
        self,
        animation: ProjectAnimation,
        document: FbxDocument,
        profile: SourceBoneMappingProfile,
    ) -> None:
        qt = self.qt
        source_bones = sorted(document.limb_models)
        target_ref, target_names = self._target_inventory_for_animation(animation)
        helper_names = visible_extra_target_names(
            target_names,
            (role.target_name for role in HUMANOID_ROLES),
            target_rig_ref=target_ref,
            show_helper_bones=self.show_helper_bones.isChecked(),
            show_all_target_bones=self.show_all_target_bones.isChecked(),
        )
        helper_rules = {
            rule.target_bone: rule
            for rule in helper_rules_from_dicts(
                animation.extensions.get("helper_retarget_rules", ()) or ()
            )
        }
        self._refreshing = True
        try:
            self.mapping_table.setHorizontalHeaderLabels(
                [
                    "Group",
                    "Target role / bone",
                    "Source FBX bone",
                    "Required",
                    "Confidence",
                    "Method",
                    "Components",
                ]
            )
            self.mapping_table.setRowCount(len(HUMANOID_ROLES) + len(helper_names))
            for row_index, role in enumerate(HUMANOID_ROLES):
                for column, text in (
                    (0, role.group),
                    (1, role.label),
                    (3, "Yes" if role.required else "Optional"),
                    (4, f"{profile.confidence_by_role.get(role.role_id, 0.0):.2f}"),
                    (5, profile.method_by_role.get(role.role_id, "")),
                ):
                    item = qt["QTableWidgetItem"](text)
                    item.setData(qt["Qt"].UserRole, role.role_id)
                    item.setFlags(item.flags() & ~qt["Qt"].ItemIsEditable)
                    self.mapping_table.setItem(row_index, column, item)
                combo = self._combo_box()
                combo.setEditable(True)
                combo.setMinimumHeight(30)
                combo.setToolTip(
                    f"Source FBX bone assigned to the {role.label} humanoid role. Mouse-wheel changes "
                    "are disabled unless the dropdown is open. Type a custom bone name and press Enter to apply it."
                )
                combo.addItem("")
                combo.addItems(source_bones)
                mapped = profile.mapped_bone(role.role_id) or ""
                combo.setCurrentText(mapped)
                combo.activated.connect(
                    lambda _index, widget=combo, role_id=role.role_id, aid=animation.animation_id: self._mapping_changed(
                        aid, role_id, widget.currentText()
                    )
                )
                if combo.lineEdit() is not None:
                    # editingFinished also fires when focus moves from the line
                    # editor into its own popup. Rebuilding the table there
                    # destroys the open combo after roughly a second. Typed
                    # values commit explicitly with Enter; list choices use
                    # the activated signal above.
                    combo.lineEdit().returnPressed.connect(
                        lambda widget=combo, role_id=role.role_id, aid=animation.animation_id: self._mapping_changed(
                            aid, role_id, widget.currentText()
                        )
                    )
                self.mapping_table.setCellWidget(row_index, 2, combo)

                component_item = qt["QTableWidgetItem"]("Body solver")
                component_item.setFlags(
                    component_item.flags() & ~qt["Qt"].ItemIsEditable
                )
                self.mapping_table.setItem(row_index, 6, component_item)

            for helper_offset, target_name in enumerate(helper_names):
                row_index = len(HUMANOID_ROLES) + helper_offset
                rule = helper_rules.get(target_name)
                suggestion = suggested_helper_source(target_name, source_bones)
                component_policy = (
                    rule.component_policy
                    if rule is not None
                    else (suggestion[1] if suggestion else "full_transform")
                )
                method = (
                    "manual helper override"
                    if rule is not None
                    else (
                        f"Suggested: {suggestion[0]} (not enabled)"
                        if suggestion
                        else "unmapped helper"
                    )
                )
                for column, text in (
                    (0, "Helper"),
                    (1, target_name),
                    (3, "Optional"),
                    (4, "1.00" if rule is not None else "0.00"),
                    (5, method),
                ):
                    item = qt["QTableWidgetItem"](text)
                    item.setData(qt["Qt"].UserRole, f"helper:{target_name}")
                    item.setFlags(item.flags() & ~qt["Qt"].ItemIsEditable)
                    self.mapping_table.setItem(row_index, column, item)

                source_combo = self._combo_box()
                source_combo.setEditable(True)
                source_combo.setMinimumHeight(30)
                source_combo.setToolTip(
                    f"Source FBX bone that drives target helper {target_name}. "
                    "The same source bone may drive body roles and multiple helpers."
                )
                source_combo.addItem("(unmapped — keep bind / inherit parent)", "")
                for source_name in source_bones:
                    source_combo.addItem(source_name, source_name)
                mapped_source = rule.source_bone if rule is not None else ""
                source_index = source_combo.findData(mapped_source)
                if source_index >= 0:
                    source_combo.setCurrentIndex(source_index)
                else:
                    source_combo.setEditText(mapped_source)

                component_combo = self._combo_box()
                component_combo.setMinimumHeight(30)
                component_combo.setToolTip(
                    "Choose which parts of the source transform replace this helper track."
                )
                for value in COMPONENT_POLICIES:
                    component_combo.addItem(_HELPER_COMPONENT_LABELS[value], value)
                component_combo.setCurrentIndex(
                    max(0, component_combo.findData(component_policy))
                )

                callback = (
                    lambda _index,
                    aid=animation.animation_id,
                    target=target_name,
                    source=source_combo,
                    components=component_combo: self._helper_mapping_widgets_changed(
                        aid, target, source, components
                    )
                )
                source_combo.activated.connect(callback)
                component_combo.currentIndexChanged.connect(callback)
                if source_combo.lineEdit() is not None:
                    source_combo.lineEdit().returnPressed.connect(
                        lambda aid=animation.animation_id,
                        target=target_name,
                        source=source_combo,
                        components=component_combo: self._helper_mapping_widgets_changed(
                            aid, target, source, components
                        )
                    )
                self.mapping_table.setCellWidget(row_index, 2, source_combo)
                self.mapping_table.setCellWidget(row_index, 6, component_combo)
        finally:
            self._refreshing = False
        self._update_mapping_summary(profile, source_bones, animation)
        self._filter_mapping_rows()

    def _helper_mapping_widgets_changed(
        self,
        animation_id: str,
        target_name: str,
        source_combo: Any,
        component_combo: Any,
    ) -> None:
        source_data = source_combo.currentData()
        source_name = (
            str(source_data)
            if source_data is not None
            else source_combo.currentText().strip()
        )
        if source_name.startswith("(unmapped"):
            source_name = ""
        self._helper_mapping_changed(
            animation_id,
            target_name,
            source_name,
            str(component_combo.currentData() or "full_transform"),
        )

    def _helper_mapping_changed(
        self,
        animation_id: str,
        target_name: str,
        source_name: str,
        component_policy: str,
    ) -> None:
        if self._refreshing:
            return
        animation = self.project.animation_by_id(animation_id)
        if animation is None:
            return
        rules = helper_rules_from_dicts(
            animation.extensions.get("helper_retarget_rules", ()) or ()
        )
        rules = [rule for rule in rules if rule.target_bone != target_name]
        source_name = source_name.strip()
        if source_name:
            rules.append(
                HelperRetargetRule(
                    target_name,
                    source_name,
                    "rest_relative",
                    component_policy,
                )
            )
        animation.extensions["helper_retarget_rules"] = helper_rules_to_dicts(rules)
        # Old development builds persisted a hidden profile choice. It no
        # longer controls visibility or output and is removed on first edit.
        animation.extensions.pop("helper_target_profile", None)
        self._mark_dirty()

        for row in range(self.mapping_table.rowCount()):
            target_item = self.mapping_table.item(row, 1)
            if (
                target_item is None
                or target_item.data(self.qt["Qt"].UserRole)
                != f"helper:{target_name}"
            ):
                continue
            self.mapping_table.item(row, 4).setText("1.00" if source_name else "0.00")
            self.mapping_table.item(row, 5).setText(
                "manual helper override" if source_name else "unmapped helper"
            )
            break

        payload = self.project.mapping_profiles.get(animation.mapping_profile_id)
        if payload is not None:
            profile = SourceBoneMappingProfile.from_dict(payload)
            document = self._source_document(animation.source_fbx)
            self._update_mapping_summary(
                profile, sorted(document.limb_models), animation
            )
        self._filter_mapping_rows()

    def _update_mapping_summary(
        self,
        profile: SourceBoneMappingProfile,
        source_bones: list[str],
        animation: ProjectAnimation | None = None,
    ) -> None:
        errors = profile.validate(source_bones)
        mapped = len(profile.role_to_bone)
        color = "#2e7d32" if not errors else "#b71c1c"
        helper_rules = (
            helper_rules_from_dicts(
                animation.extensions.get("helper_retarget_rules", ()) or ()
            )
            if animation is not None
            else []
        )
        helper_sources = {rule.source_bone for rule in helper_rules}
        helper_note = (
            f"<br>{len(helper_rules)} helper override(s) mapped from the selected target rig."
            if helper_rules
            else ""
        )
        self.mapping_status.setText(
            f"<b style='color:{color}'>{'Ready' if not errors else 'Needs attention'}</b> — "
            f"{mapped} roles mapped; unmapped roles retain target defaults. "
            f"Skeleton hash: {profile.source_skeleton_hash[:16]}…"
            + ("<br>" + "<br>".join(errors[:8]) if errors else "")
            + helper_note
        )
        self.ignored_bones.setPlainText(
            "\n".join(
                bone for bone in profile.ignored_bones if bone not in helper_sources
            )
        )

    def _mapping_changed(self, animation_id: str, role_id: str, value: str) -> None:
        if self._refreshing:
            return
        animation = self.project.animation_by_id(animation_id)
        if animation is None:
            return
        payload = self.project.mapping_profiles.get(animation.mapping_profile_id)
        if payload is None:
            return
        profile = SourceBoneMappingProfile.from_dict(payload)
        profile.set_mapping(role_id, value.strip() or None, confidence=1.0, method="manual")
        document = self._source_document(animation.source_fbx)
        used = set(profile.role_to_bone.values())
        profile.ignored_bones = [bone for bone in sorted(document.limb_models) if bone not in used]
        self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
        self._mark_dirty()
        # Keep every embedded combo alive. Rebuilding the entire table here
        # replaces all 64 dropdowns immediately after each selection and makes
        # subsequent popups appear to close themselves.
        for row in range(self.mapping_table.rowCount()):
            role_item = self.mapping_table.item(row, 1)
            if role_item is None or role_item.data(self.qt["Qt"].UserRole) != role_id:
                continue
            self.mapping_table.item(row, 4).setText(
                f"{profile.confidence_by_role.get(role_id, 0.0):.2f}"
            )
            self.mapping_table.item(row, 5).setText(profile.method_by_role.get(role_id, ""))
            break
        self._update_mapping_summary(
            profile, sorted(document.limb_models), animation
        )
        self._filter_mapping_rows()

    def auto_map_selected(self) -> None:
        animation = self._retarget_animation()
        if animation is None:
            return
        ui_kind = self._retarget_ui_kind(animation)
        if ui_kind == RetargetUiKind.CUSTOM_CRIG:
            return
        if self.background_tasks.busy:
            self.status.showMessage(
                "Wait for the current animation operation to finish before running auto-retarget.",
                5000,
            )
            return
        selection = resolve_animation_target(
            self.project,
            animation,
            rig_paths=getattr(self, "_rig_paths_by_ref", {}),
        )
        request = _AutoRetargetRequest(
            source_fbx=animation.source_fbx,
            animation_stack=animation.source_animation_stack,
            display_name=animation.display_name,
            existing_profile_id=animation.mapping_profile_id,
            game_id=self.project.game_id,
            target_rig_path=selection.rig_path,
            existing_profile=deepcopy(
                self.project.mapping_profiles.get(animation.mapping_profile_id, {}) or {}
            ),
            tolerance=self._current_import_tolerance(),
        )
        animation_id = animation.animation_id
        self._animation_operation_kind = "retarget"
        self._set_animation_operation_busy(
            True, "Rebuilding the automatic retarget map in the background…"
        )

        def succeeded(result: _AutoRetargetResult) -> None:
            current = self.project.animation_by_id(animation_id)
            if current is None:
                return
            self._source_cache[str(Path(current.source_fbx).resolve())] = result.document
            if request.game_id == DL2_GAME_ID:
                assert result.compiled_profile is not None
                assert result.semantic_state is not None
                current.mapping_profile_id = result.profile.profile_id
                self.project.mapping_profiles[result.profile.profile_id] = (
                    result.profile.to_dict()
                )
                self.project.mapping_profiles[result.compiled_profile.profile_id] = (
                    result.compiled_profile.to_dict()
                )
                if result.migrated_from_profile_id:
                    current.extensions["legacy_target_map_profile_id"] = (
                        result.migrated_from_profile_id
                    )
                    current.extensions["semantic_profile_migration"] = dict(
                        result.profile.extensions.get("migration_audit", {}) or {}
                    )
                current.extensions["compiled_target_map_profile_id"] = (
                    result.compiled_profile.profile_id
                )
                current.extensions["compiled_target_map_hash"] = str(
                    result.profile.extensions.get("compiled_map_hash", "")
                )
                current.extensions["compiled_target_map_live_validation"] = dict(
                    result.profile.extensions.get("compiled_validation", {}) or {}
                )
                fingerprint = json.dumps(
                    result.profile.to_dict(),
                    sort_keys=True,
                    ensure_ascii=False,
                    separators=(",", ":"),
                )
                cache_key = (
                    current.animation_id,
                    current.source_animation_stack,
                    result.profile.target_skeleton_hash,
                    fingerprint,
                )
                cache = getattr(self, "_semantic_state_cache", None)
                if cache is None:
                    cache = {}
                    self._semantic_state_cache = cache
                cache[current.animation_id] = (cache_key, result.semantic_state)
                self._mark_dirty()
                self._refresh_bundled_semantic_table(current, result.semantic_state)
                self._refresh_animation_table()
                return

            profile = result.profile
            if request.existing_profile_id:
                profile.profile_id = request.existing_profile_id
            current.mapping_profile_id = profile.profile_id
            self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
            self._mark_dirty()
            self._refresh_mapping_table(current, result.document, profile)
            self._refresh_animation_table()

        if not self.background_tasks.start(
            lambda progress: _prepare_auto_retarget(request, progress),
            progress=lambda message: self.status.showMessage(message),
            succeeded=succeeded,
            failed=lambda failure: self._background_animation_error(
                "Auto-retarget failed", failure
            ),
            finished=lambda: self._set_animation_operation_busy(False),
        ):
            self._set_animation_operation_busy(False)
            self.status.showMessage("Another animation operation is already running.", 5000)

    def clear_mapping(self) -> None:
        animation = self._retarget_animation()
        if animation is None:
            return
        if (
            self.project.game_id == DL2_GAME_ID
            and self._retarget_ui_kind(animation) == RetargetUiKind.BUILTIN_HUMANOID
        ):
            try:
                state = self._bundled_semantic_state(animation)
                profile = state.profile
                for row in state.rows:
                    profile.set_role_mode(row.profile_role, "auto")
                profile.cleared_roles = []
                profile.clear_compiled_cache()
                self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
                animation.extensions.pop("compiled_target_map_profile_id", None)
                animation.extensions.pop("compiled_target_map_hash", None)
                animation.extensions.pop("compiled_target_map_live_validation", None)
                getattr(self, "_semantic_state_cache", {}).pop(
                    animation.animation_id, None
                )
                self._mark_dirty()
                refreshed = self._bundled_semantic_state(animation, force=True)
                self._refresh_bundled_semantic_table(animation, refreshed)
                self._refresh_animation_table()
            except Exception as exc:
                self._show_error("Could not clear mapping", exc)
            return
        if self._animation_target_mode(animation) == "exact":
            return
        document = self._source_document(animation.source_fbx)
        profile = SourceBoneMappingProfile.empty(
            document.limb_models,
            name=f"Manual mapping: {animation.display_name}",
            parents=document.parent_by_name,
        )
        if animation.mapping_profile_id:
            profile.profile_id = animation.mapping_profile_id
        animation.mapping_profile_id = profile.profile_id
        self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
        animation.extensions["helper_retarget_rules"] = []
        animation.extensions.pop("helper_target_profile", None)
        self._mark_dirty()
        self._refresh_mapping_table(animation, document, profile)

    def apply_mapping_to_compatible_clips(self) -> None:
        animation = self._retarget_animation()
        if animation is None or not animation.mapping_profile_id:
            return
        payload = self.project.mapping_profiles.get(animation.mapping_profile_id)
        if payload is None:
            return
        profile = SourceBoneMappingProfile.from_dict(payload)
        semantic = bool(profile.target_policy_id)
        applied = 0
        skipped = 0
        for row in self.project.animations:
            if row.animation_id == animation.animation_id:
                continue
            try:
                document = self._source_document(row.source_fbx)
                if semantic:
                    if (
                        self.project.game_id != DL2_GAME_ID
                        or self._retarget_ui_kind(row)
                        != RetargetUiKind.BUILTIN_HUMANOID
                    ):
                        skipped += 1
                        continue
                    rig = self._target_rig_for_status(row)
                    if rig is None:
                        skipped += 1
                        continue
                    policy = build_target_retarget_policy(
                        rig, game_id=self.project.game_id, clip_domain="body"
                    )
                    candidate = prepare_bundled_semantic_state(
                        document, rig, policy
                    ).profile
                    compatible = bool(
                        candidate.source_name_parent_hash
                        == profile.source_name_parent_hash
                        and policy.policy_id == profile.target_policy_id
                        and rig.rig_id == profile.target_rig_id
                        and rig.skeleton_hash == profile.target_skeleton_hash
                    )
                else:
                    candidate = auto_map_source_bones(
                        document.limb_models, parents=document.parent_by_name
                    )
                    compatible = (
                        candidate.source_skeleton_hash
                        == profile.source_skeleton_hash
                    )
            except Exception:
                skipped += 1
                continue
            if compatible:
                row.mapping_profile_id = profile.profile_id
                row.extensions.pop("compiled_target_map_profile_id", None)
                row.extensions.pop("compiled_target_map_hash", None)
                row.extensions.pop("compiled_target_map_live_validation", None)
                row.extensions["helper_retarget_rules"] = deepcopy(
                    animation.extensions.get("helper_retarget_rules", [])
                )
                row.extensions.pop("helper_target_profile", None)
                applied += 1
            else:
                skipped += 1
        self._mark_dirty()
        self._refresh_animation_table()
        self.status.showMessage(
            f"Applied mapping to {applied} compatible clip(s); skipped {skipped}.",
            7000,
        )

    def save_mapping_profile(self) -> None:
        animation = self._retarget_animation()
        if animation is None or not animation.mapping_profile_id:
            return
        payload = self.project.mapping_profiles.get(animation.mapping_profile_id)
        if payload is None:
            return
        path, _ = self.qt["QFileDialog"].getSaveFileName(
            self.window,
            "Save humanoid mapping profile",
            str(self.root / f"{Path(animation.source_fbx).stem}.dlrmap.json"),
            "DL ReAnimated Mapping (*.dlrmap.json);;JSON (*.json)",
        )
        if path:
            SourceBoneMappingProfile.from_dict(payload).save(path)
            self.status.showMessage(f"Saved mapping {path}", 5000)

    def load_mapping_profile(self) -> None:
        animation = self._retarget_animation()
        if animation is None:
            return
        path, _ = self.qt["QFileDialog"].getOpenFileName(
            self.window,
            "Load humanoid mapping profile",
            str(self.root),
            "DL ReAnimated Mapping (*.dlrmap.json);;JSON (*.json)",
        )
        if not path:
            return
        try:
            document = self._source_document(animation.source_fbx)
            profile = SourceBoneMappingProfile.load(path)
            bundled_semantic = bool(
                self.project.game_id == DL2_GAME_ID
                and self._retarget_ui_kind(animation)
                == RetargetUiKind.BUILTIN_HUMANOID
            )
            if bundled_semantic:
                rig = self._target_rig_for_status(animation)
                if rig is None:
                    raise FileNotFoundError("The selected bundled target rig is unavailable")
                policy = build_target_retarget_policy(
                    rig, game_id=self.project.game_id, clip_domain="body"
                )
                if profile.target_policy_id and (
                    profile.target_policy_id != policy.policy_id
                    or profile.target_rig_id != rig.rig_id
                    or profile.target_skeleton_hash != rig.skeleton_hash
                ):
                    raise ValueError(
                        "This semantic mapping belongs to a different bundled target package."
                    )
                if profile.target_policy_id:
                    current_hash = prepare_bundled_semantic_state(
                        document, rig, policy
                    ).profile.source_name_parent_hash
                    profile_source_hash = profile.source_name_parent_hash
                else:
                    current_hash = auto_map_source_bones(
                        document.limb_models, parents=document.parent_by_name
                    ).source_skeleton_hash
                    profile_source_hash = profile.source_skeleton_hash
            else:
                current_hash = auto_map_source_bones(
                    document.limb_models, parents=document.parent_by_name
                ).source_skeleton_hash
                profile_source_hash = profile.source_skeleton_hash
            if profile_source_hash and profile_source_hash != current_hash:
                result = self.qt["QMessageBox"].question(
                    self.window,
                    "Different source skeleton",
                    "This mapping was created for a different bone hierarchy. Load it anyway?",
                )
                if result != self.qt["QMessageBox"].Yes:
                    return
            animation.mapping_profile_id = profile.profile_id
            profile.clear_compiled_cache()
            self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
            self._mark_dirty()
            if (
                bundled_semantic
            ):
                animation.extensions.pop("compiled_target_map_profile_id", None)
                animation.extensions.pop("compiled_target_map_hash", None)
                animation.extensions.pop("compiled_target_map_live_validation", None)
                getattr(self, "_semantic_state_cache", {}).pop(
                    animation.animation_id, None
                )
                state = self._bundled_semantic_state(animation, force=True)
                self._refresh_bundled_semantic_table(animation, state)
            else:
                self._refresh_mapping_table(animation, document, profile)
            self._refresh_animation_table()
        except Exception as exc:
            self._show_error("Could not load mapping", exc)

    def _retarget_animation(self) -> ProjectAnimation | None:
        return self.project.animation_by_id(str(self.retarget_clip_combo.currentData() or ""))

    def _open_mapping_for_animation(self, animation_id: str) -> None:
        if self.mapping_navigation_callback is not None:
            self.mapping_navigation_callback(animation_id)
            return
        self._set_combo_data(self.retarget_clip_combo, animation_id)
        self.tabs.setCurrentIndex(2)
        self._retarget_clip_changed()

    def _filter_mapping_rows(self) -> None:
        text = self.retarget_filter.text().strip().lower()
        for row in range(self.mapping_table.rowCount()):
            role = self.mapping_table.item(row, 1)
            combo = self.mapping_table.cellWidget(row, 2)
            haystack = " ".join(
                [
                    self.mapping_table.item(row, 0).text() if self.mapping_table.item(row, 0) else "",
                    role.text() if role else "",
                    combo.currentText() if combo else "",
                ]
            ).lower()
            self.mapping_table.setRowHidden(row, bool(text and text not in haystack))

    # ----------------------------------------------------------------- export
    def export_anm2_only(self) -> None:
        self._sync_project_from_ui()
        destination = self.qt["QFileDialog"].getExistingDirectory(
            self.window,
            "Export generated ANM2 files",
            self.project.export.output_directory or str(self.root),
        )
        if not destination:
            return

        self.build_log.clear()
        self._set_animation_build_busy(True, "Exporting ANM2 files in the background…")
        project = deepcopy(self.project)

        def work(progress):
            export_warnings: list[str] = []
            paths = export_project_anm2_files(
                project,
                destination,
                progress=progress,
                warning=export_warnings.append,
            )
            return paths, export_warnings

        def succeeded(payload) -> None:
            paths, export_warnings = payload
            self._append_build_log("")
            self._append_build_log("Exported ANM2 files:")
            for path in paths:
                self._append_build_log(str(path))
            noun = "file" if len(paths) == 1 else "files"
            self.status.showMessage(f"Exported {len(paths)} ANM2 {noun}", 10000)
            message = f"Exported {len(paths)} ANM2 {noun} to:\n{destination}"
            if export_warnings:
                shown = export_warnings[:8]
                message += "\n\nWarnings:\n- " + "\n- ".join(shown)
                if len(export_warnings) > len(shown):
                    message += f"\n- ...and {len(export_warnings) - len(shown)} more; see the build log."
                self.qt["QMessageBox"].warning(
                    self.window, "ANM2 export completed with warnings", message
                )
            else:
                self.qt["QMessageBox"].information(
                    self.window, "ANM2 export complete", message
                )

        if not self.background_tasks.start(
            work,
            progress=self._append_build_log,
            succeeded=succeeded,
            failed=lambda failure: self._background_build_error("ANM2 export failed", failure),
            finished=lambda: self._set_animation_build_busy(False),
        ):
            self._set_animation_build_busy(False)
            self.status.showMessage("Another animation build or export is already running.", 5000)

    def build_rpack(self) -> None:
        self._sync_project_from_ui()
        if self.project_path is None:
            result = self.qt["QMessageBox"].question(
                self.window,
                "Save project first",
                "Projects are versioned build inputs. Save this project before building?",
            )
            if result != self.qt["QMessageBox"].Yes:
                return
            self.save_project_as()
            if self.project_path is None:
                return
        # Persist the exact build input before starting. Edits made while the
        # worker runs remain dirty and are never overwritten on completion.
        self.project.save(self.project_path)
        self.dirty = False
        self._update_title()
        project = deepcopy(self.project)
        self.build_log.clear()
        self._set_animation_build_busy(True, "Building the animation RPack in the background…")

        def work(progress):
            return build_project(project, progress=progress)

        def succeeded(result) -> None:
            self._append_build_log("")
            self._append_build_log(json.dumps(result.to_dict(), indent=2))
            self.status.showMessage(f"Built {result.pack_path}", 10000)
            message = (
                f"Created {result.animation_count} animation resources in:\n{result.pack_path}\n\n"
                f"SHA-256:\n{result.pack_sha256}"
            )
            if result.warnings:
                shown = result.warnings[:8]
                message += "\n\nWarnings:\n- " + "\n- ".join(shown)
                if len(result.warnings) > len(shown):
                    message += f"\n- ...and {len(result.warnings) - len(shown)} more; see the build report."
                self.qt["QMessageBox"].warning(
                    self.window, "RPack built with warnings", message
                )
            else:
                self.qt["QMessageBox"].information(
                    self.window, "RPack built", message
                )

        if not self.background_tasks.start(
            work,
            progress=self._append_build_log,
            succeeded=succeeded,
            failed=lambda failure: self._background_build_error("Build failed", failure),
            finished=lambda: self._set_animation_build_busy(False),
        ):
            self._set_animation_build_busy(False)
            self.status.showMessage("Another animation build or export is already running.", 5000)

    def _append_build_log(self, message: str) -> None:
        self.build_log.appendPlainText(message)

    def _set_animation_build_busy(self, busy: bool, message: str = "") -> None:
        self.progress_bar.setRange(0, 0 if busy else 1)
        if not busy:
            self.progress_bar.setValue(0)
        self.build_button.setEnabled(not busy)
        self.export_anm2_button.setEnabled(not busy)
        if message:
            self.status.showMessage(message)
        elif not busy:
            self.status.showMessage("Ready", 3000)

    def _background_build_error(self, title: str, failure: TaskFailure) -> None:
        developer_diagnostics = self._developer_diagnostics_enabled()
        self._append_build_log(
            failure.traceback if developer_diagnostics else failure.display_message(False)
        )
        self._show_error(
            title,
            RuntimeError(failure.display_message(developer_diagnostics)),
        )

    def _developer_diagnostics_enabled(self) -> bool:
        advanced = bool(
            getattr(self, "advanced_mode_toggle", None)
            and self.advanced_mode_toggle.isChecked()
        )
        enabled = bool(
            getattr(self, "developer_diagnostics", None)
            and self.developer_diagnostics.isChecked()
        )
        return advanced and enabled

    def _background_work_active(self) -> bool:
        runners = [self.background_tasks, *getattr(self, "extra_task_runners", [])]
        return any(runner.busy for runner in runners)

    def _export_mode_changed(self) -> None:
        if self._refreshing:
            return
        self.existing_rpack.setEnabled(self.append_pack_radio.isChecked())
        self._mark_dirty()

    # --------------------------------------------------------- ANM2 -> FBX
    def _reverse_reload_rigs(self) -> None:
        current = self.reverse_source_rig.currentData() if hasattr(self, "reverse_source_rig") else None
        if not hasattr(self, "reverse_source_rig"):
            return
        self.reverse_source_rig.clear()
        records = self.rig_registry.records()
        self._rig_paths_by_ref = {row.rig_ref: row.path for row in records}
        profile = GAME_PROFILES[self.project.game_id]
        default_ref = getattr(profile, "default_target_rig_ref", profile.target_rig_ref)
        compatible_refs = set(
            getattr(profile, "compatible_builtin_rig_refs", (default_ref,))
        )
        advanced = bool(
            getattr(self, "advanced_mode_toggle", None)
            and self.advanced_mode_toggle.isChecked()
        )
        project_refs = {
            item.source_rig_ref for item in self.project.anm2_to_fbx.items
        }
        for row in records:
            if row.rig_ref.startswith("builtin:"):
                if row.rig_ref not in compatible_refs:
                    continue
                if row.rig_ref != default_ref and not advanced and row.rig_ref not in project_refs:
                    continue
            suffix = "" if row.builtin else f" [{row.category}]"
            self.reverse_source_rig.addItem(row.display_name + suffix, row.rig_ref)
        selected = current or (
            self.project.anm2_to_fbx.items[0].source_rig_ref
            if self.project.anm2_to_fbx.items else default_ref
        )
        self._set_combo_data(self.reverse_source_rig, selected)

    def _reverse_load_rig(self, rig_ref: str, rig_path: str = "") -> ChromeRig:
        if rig_ref == BUILTIN_MALE_RIG_REF:
            return ChromeRig.load(self.resource_root / "reference" / "male_npc_infected.crig")
        path = rig_path or getattr(self, "_rig_paths_by_ref", {}).get(rig_ref, "")
        if not path:
            raise FileNotFoundError(f"Installed Chrome Rig not found: {rig_ref}")
        return ChromeRig.load(path)

    def _reverse_detect_rig(self, path: str | Path) -> tuple[str, str]:
        data = Path(path).read_bytes()
        if detect_anm2_format(data) == 42:
            profile = GAME_PROFILES[DL2_GAME_ID]
            rig_ref = getattr(profile, "default_target_rig_ref", DL2_RIG_REF)
            resolved = self.rig_registry.resolve(rig_ref)
            return rig_ref, str(resolved or "")
        header = Anm2Header.parse(data)
        descriptors = set(struct.unpack_from(f"<{header.track_count}I", data, HEADER_LENGTH))
        matches: list[tuple[int, str, str]] = []
        for row in self.rig_registry.records():
            try:
                rig = self._reverse_load_rig(row.rig_ref, row.path)
            except (OSError, ValueError):
                continue
            overlap = len(descriptors & set(rig.descriptors))
            if overlap:
                matches.append((overlap, row.rig_ref, row.path))
        if not matches:
            return BUILTIN_MALE_RIG_REF, ""
        matches.sort(reverse=True)
        return matches[0][1], matches[0][2]

    def _reverse_add_files(self) -> None:
        paths, _ = self.qt["QFileDialog"].getOpenFileNames(
            self.window, "Add extracted ANM2 files", str(self.root), "ANM2 animation (*.anm2)"
        )
        if not paths:
            return
        for path in paths:
            try:
                data = Path(path).read_bytes()
                detected = detect_anm2_format(data)
                validation_error = ""
                if detected == 42:
                    inspected = parse_dl2_header42(data)
                    if inspected.validation_errors:
                        validation_error = (
                            "Invalid DL2 Header_Version2 layout: "
                            + "; ".join(inspected.validation_errors)
                        )
                    frame_count = inspected.frame_count
                else:
                    frame_count = Anm2Header.parse(data).frame_count
                rig_ref, rig_path = self._reverse_detect_rig(path)
                item = Anm2ToFbxItem.create(path)
                timing = load_anm2_provenance(path)
                if timing.valid and int(timing.payload["frame_count"]) == int(frame_count):
                    item.anm2_input_fps = float(timing.payload["sample_fps"])
                    item.fbx_output_fps = float(timing.payload["source_fbx_fps"])
                    item.fps = item.fbx_output_fps
                    item.extensions["timing_metadata_status"] = "valid"
                    item.extensions["timing_metadata_path"] = timing.path
                    item.extensions["timing_provenance"] = dict(timing.payload)
                else:
                    item.anm2_input_fps = 30.0
                    item.fbx_output_fps = 30.0
                    item.fps = 30.0
                    if timing.valid:
                        advisory = (
                            "ANM2 timing metadata was ignored because its frame count does not "
                            "match the selected ANM2."
                        )
                        item.extensions["timing_metadata_status"] = "frame_count_mismatch"
                        item.extensions["timing_metadata_warnings"] = [advisory]
                    else:
                        item.extensions["timing_metadata_status"] = timing.status
                        if timing.warnings:
                            item.extensions["timing_metadata_warnings"] = list(
                                dict.fromkeys(timing.warnings)
                            )[:1]
                item.source_rig_ref = rig_ref
                item.source_rig_path = rig_path
                item.end_frame = frame_count - 1
                item.extensions["detected_anm2_format"] = detected
                if detected == 42:
                    item.extensions["conversion_status"] = (
                        "invalid_layout" if validation_error else "native_curve_decode_ready"
                    )
                    if validation_error:
                        item.enabled = False
                        item.extensions["conversion_error"] = validation_error
                    item.extensions["track_count"] = inspected.track_count
                self.project.anm2_to_fbx.items.append(item)
            except Exception as exc:
                self._show_error(f"Could not add {Path(path).name}", exc)
        if self.project.anm2_to_fbx.items:
            self._set_combo_data(
                self.reverse_source_rig, self.project.anm2_to_fbx.items[0].source_rig_ref
            )
        self._reverse_refresh_table()
        self._mark_dirty()

    def _reverse_remove_selected(self) -> None:
        selected = sorted({index.row() for index in self.reverse_table.selectedIndexes()}, reverse=True)
        for row in selected:
            if 0 <= row < len(self.project.anm2_to_fbx.items):
                self.project.anm2_to_fbx.items.pop(row)
        self._reverse_refresh_table()
        self._mark_dirty()

    def _reverse_refresh_table(self) -> None:
        if not hasattr(self, "reverse_table"):
            return
        qt = self.qt
        self.reverse_table.setRowCount(len(self.project.anm2_to_fbx.items))
        for row_index, item in enumerate(self.project.anm2_to_fbx.items):
            enabled = qt["QCheckBox"]()
            enabled.setChecked(item.enabled)
            if item.extensions.get("conversion_status") == "invalid_layout":
                enabled.setEnabled(False)
                enabled.setToolTip(str(item.extensions.get("conversion_error", "Invalid ANM2 layout.")))
            elif item.extensions.get("detected_anm2_format") == 42:
                enabled.setToolTip(
                    "Validated DL2 Header_Version2: native curve decode and FBX export are available; "
                    "unresolved tracks follow the selected policy."
                )
            enabled.toggled.connect(self._mark_dirty)
            self.reverse_table.setCellWidget(row_index, 0, enabled)
            source_item = qt["QTableWidgetItem"](item.source_anm2)
            source_item.setFlags(source_item.flags() & ~qt["Qt"].ItemIsEditable)
            timing_warnings = list(
                item.extensions.get("timing_metadata_warnings", ()) or ()
            )
            if timing_warnings:
                source_item.setToolTip(str(timing_warnings[0]))
            self.reverse_table.setItem(row_index, 1, source_item)
            output_item = qt["QTableWidgetItem"](item.output_name)
            self.reverse_table.setItem(row_index, 2, output_item)
            frames = "?"
            tracks = "?"
            try:
                data = Path(item.source_anm2).read_bytes()
                if detect_anm2_format(data) == 42:
                    inspected = parse_dl2_header42(data)
                    frames = str(inspected.frame_count)
                    tracks = str(inspected.track_count)
                else:
                    header = Anm2Header.parse(data)
                    frames = str(header.frame_count)
                    tracks = str(header.track_count)
            except (OSError, ValueError):
                pass
            frame_item = qt["QTableWidgetItem"](frames)
            frame_item.setFlags(frame_item.flags() & ~qt["Qt"].ItemIsEditable)
            self.reverse_table.setItem(row_index, 3, frame_item)
            track_item = qt["QTableWidgetItem"](tracks)
            track_item.setFlags(track_item.flags() & ~qt["Qt"].ItemIsEditable)
            self.reverse_table.setItem(row_index, 4, track_item)
            input_fps = qt["QDoubleSpinBox"]()
            input_fps.setRange(0.001, 1000.0)
            input_fps.setDecimals(9)
            input_fps.setValue(item.resolved_input_fps())
            output_fps = qt["QDoubleSpinBox"]()
            output_fps.setRange(0.001, 1000.0)
            output_fps.setDecimals(9)
            output_fps.setValue(item.resolved_output_fps())
            start = qt["QSpinBox"](); start.setRange(-1, 65534); start.setSpecialValueText("First")
            end = qt["QSpinBox"](); end.setRange(-1, 65534); end.setSpecialValueText("Last")
            start.setValue(-1 if item.start_frame is None else item.start_frame)
            end.setValue(-1 if item.end_frame is None else item.end_frame)
            for widget in (input_fps, output_fps, start, end):
                widget.valueChanged.connect(self._mark_dirty)
            self.reverse_table.setCellWidget(row_index, 5, input_fps)
            self.reverse_table.setCellWidget(row_index, 6, output_fps)
            self.reverse_table.setCellWidget(row_index, 7, start)
            self.reverse_table.setCellWidget(row_index, 8, end)
            self.reverse_table.setRowHeight(row_index, 38)

    def _sync_reverse_from_ui(self) -> None:
        if not hasattr(self, "reverse_table"):
            return
        settings = self.project.anm2_to_fbx
        settings.mode = str(self.reverse_mode.currentData() or "native")
        settings.target_fbx = self.reverse_target_fbx.text().strip()
        settings.output_directory = self.reverse_output_directory.text().strip()
        value = self.reverse_translation_scale.currentData()
        settings.translation_scale = str(value if value is not None else self.reverse_translation_scale.currentText()).strip()
        settings.extensions["unknown_track_policy"] = str(
            self.reverse_unknown_track_policy.currentData()
            or _default_unknown_track_policy(self.project.game_id)
        )
        settings.extensions["bake_motion_accumulator"] = bool(
            self.reverse_bake_motion_accumulator.isChecked()
        )
        rig_ref = str(self.reverse_source_rig.currentData() or BUILTIN_MALE_RIG_REF)
        rig_path = getattr(self, "_rig_paths_by_ref", {}).get(rig_ref, "")
        for row_index, item in enumerate(settings.items):
            item.enabled = self.reverse_table.cellWidget(row_index, 0).isChecked()
            item.output_name = self.reverse_table.item(row_index, 2).text().strip() or Path(item.source_anm2).stem
            item.anm2_input_fps = float(
                self.reverse_table.cellWidget(row_index, 5).value()
            )
            item.fbx_output_fps = float(
                self.reverse_table.cellWidget(row_index, 6).value()
            )
            item.fps = item.fbx_output_fps
            start = self.reverse_table.cellWidget(row_index, 7).value()
            end = self.reverse_table.cellWidget(row_index, 8).value()
            item.start_frame = None if start < 0 else start
            item.end_frame = None if end < 0 else end
            item.source_rig_ref = rig_ref
            item.source_rig_path = rig_path
        self.settings.setValue("blender_executable", self.reverse_blender_path.text().strip())

    def _reverse_source_rig_changed(self, *_args) -> None:
        if self._refreshing:
            return
        self._mark_dirty()
        if self.reverse_mode.currentData() == "retarget" and self.reverse_target_fbx.text().strip():
            self._reverse_auto_map()

    def _reverse_mode_changed(self, *_args) -> None:
        retarget = str(self.reverse_mode.currentData()) == "retarget"
        self._set_path_row_visible(self.reverse_target_fbx, retarget)
        self.reverse_translation_scale.setVisible(retarget)
        self.reverse_mapping_group.setVisible(retarget)
        if not self._refreshing:
            self._mark_dirty()

    def _reverse_blender_path_changed(self, value: str) -> None:
        if not hasattr(self, "reverse_blender_status"):
            return
        found = discover_blender(value.strip())
        self.reverse_blender_status.setText(
            f"Ready: {found}" if found else "Not found — choose blender.exe"
        )

    def _reverse_profile_from_table(self) -> GenericBoneMap:
        source = self._reverse_load_rig(str(self.reverse_source_rig.currentData()))
        target = chrome_rig_from_fbx_skeleton(self.reverse_target_fbx.text().strip())
        profile = GenericBoneMap.create(
            f"{source.name} to {target.name}", source.skeleton_hash, target.skeleton_hash,
            source_rig_ref=source.rig_id,
        )
        by_name = {bone.name: bone for bone in source.bones}
        for row in range(self.reverse_mapping_table.rowCount()):
            source_name = self.reverse_mapping_table.item(row, 0).text()
            combo = self.reverse_mapping_table.cellWidget(row, 2)
            target_name = str(combo.currentData() or "")
            if not target_name:
                continue
            bone = by_name[source_name]
            confidence_item = self.reverse_mapping_table.item(row, 3)
            method_item = self.reverse_mapping_table.item(row, 4)
            profile.pairs.append(BoneMapPair(
                bone.descriptor, bone.name, target_name,
                float(confidence_item.data(self.qt["Qt"].UserRole) or 1.0),
                str(method_item.text() or "manual"),
            ))
        errors = profile.validate()
        if errors:
            raise ValueError("Invalid reviewed mapping:\n- " + "\n- ".join(errors))
        return profile

    def _reverse_show_profile(self, profile: GenericBoneMap) -> None:
        source = self._reverse_load_rig(str(self.reverse_source_rig.currentData()))
        target = chrome_rig_from_fbx_skeleton(self.reverse_target_fbx.text().strip())
        pair_by_descriptor = {row.source_descriptor: row for row in profile.pairs}
        self.reverse_mapping_table.setRowCount(len(source.bones))
        for row_index, bone in enumerate(source.bones):
            pair = pair_by_descriptor.get(bone.descriptor)
            self.reverse_mapping_table.setItem(row_index, 0, self.qt["QTableWidgetItem"](bone.name))
            self.reverse_mapping_table.setItem(row_index, 1, self.qt["QTableWidgetItem"](f"0x{bone.descriptor:08X}"))
            combo = self._combo_box(); combo.addItem("(unmapped)", "")
            for target_bone in target.bones:
                combo.addItem(target_bone.name, target_bone.name)
            if pair:
                self._set_combo_data(combo, pair.target_bone)
            combo.currentIndexChanged.connect(
                lambda _index, row=row_index: self._reverse_mapping_changed(row)
            )
            self.reverse_mapping_table.setCellWidget(row_index, 2, combo)
            confidence = self.qt["QTableWidgetItem"](f"{pair.confidence:.0%}" if pair else "—")
            confidence.setData(self.qt["Qt"].UserRole, pair.confidence if pair else 1.0)
            self.reverse_mapping_table.setItem(row_index, 3, confidence)
            self.reverse_mapping_table.setItem(row_index, 4, self.qt["QTableWidgetItem"](pair.method if pair else "manual"))
        self.reverse_mapping_status.setText(
            f"Mapped {len(profile.pairs)} of {len(source.bones)} source bones. Unmapped target bones remain at bind pose."
        )

    def _reverse_mapping_changed(self, row: int) -> None:
        if self._refreshing:
            return
        confidence = self.reverse_mapping_table.item(row, 3)
        method = self.reverse_mapping_table.item(row, 4)
        if confidence is not None:
            confidence.setText("100%")
            confidence.setData(self.qt["Qt"].UserRole, 1.0)
        if method is not None:
            method.setText("manual")
        self._mark_dirty()

    def _reverse_restore_profile(self) -> None:
        settings = self.project.anm2_to_fbx
        if settings.mode != "retarget" or not settings.target_fbx:
            return
        payload = settings.bone_mapping_profiles.get(settings.selected_mapping_profile_id)
        if not payload:
            return
        try:
            self._reverse_show_profile(GenericBoneMap.from_dict(payload))
        except (OSError, ValueError):
            self.reverse_mapping_status.setText(
                "Saved map does not match the current rig/target; run Automatic map again."
            )

    def _reverse_auto_map(self) -> None:
        try:
            source = self._reverse_load_rig(str(self.reverse_source_rig.currentData()))
            target = chrome_rig_from_fbx_skeleton(self.reverse_target_fbx.text().strip())
            parents = {
                bone.name: target.bones[bone.parent_index].name if bone.parent_index >= 0 else None
                for bone in target.bones
            }
            profile = auto_map_skeletons(
                source, [bone.name for bone in target.bones], parents,
                target_skeleton_hash=target.skeleton_hash,
            )
            self.project.anm2_to_fbx.bone_mapping_profiles[profile.profile_id] = profile.to_dict()
            self.project.anm2_to_fbx.selected_mapping_profile_id = profile.profile_id
            self._reverse_show_profile(profile)
            self._mark_dirty()
        except Exception as exc:
            self._show_error("Automatic mapping failed", exc)

    def _reverse_load_map(self) -> None:
        path, _ = self.qt["QFileDialog"].getOpenFileName(
            self.window, "Load generic bone map", str(self.root), "DL ReAnimated bone map (*.dlrbmap.json)"
        )
        if not path: return
        try:
            profile = GenericBoneMap.load(path)
            self.project.anm2_to_fbx.bone_mapping_profiles[profile.profile_id] = profile.to_dict()
            self.project.anm2_to_fbx.selected_mapping_profile_id = profile.profile_id
            self._reverse_show_profile(profile)
            self._mark_dirty()
        except Exception as exc:
            self._show_error("Could not load bone map", exc)

    def _reverse_save_map(self) -> None:
        try:
            profile = self._reverse_profile_from_table()
            path, _ = self.qt["QFileDialog"].getSaveFileName(
                self.window, "Save generic bone map", str(self.root / "mapping.dlrbmap.json"),
                "DL ReAnimated bone map (*.dlrbmap.json)"
            )
            if path:
                profile.save(path)
                self.project.anm2_to_fbx.bone_mapping_profiles[profile.profile_id] = profile.to_dict()
                self.project.anm2_to_fbx.selected_mapping_profile_id = profile.profile_id
                self._mark_dirty()
        except Exception as exc:
            self._show_error("Could not save bone map", exc)

    def _reverse_cancel(self) -> None:
        self._reverse_cancel_requested = True
        self._reverse_cancel_event.set()
        self.reverse_log.appendPlainText("Cancelling Blender export…")

    def _reverse_export(self) -> None:
        self._sync_reverse_from_ui()
        settings = self.project.anm2_to_fbx
        enabled = [row for row in settings.items if row.enabled]
        if not enabled:
            self._show_error("Nothing to export", ValueError("Add and enable at least one ANM2 file.")); return
        blender = discover_blender(self.reverse_blender_path.text().strip())
        if blender is None:
            self._show_error("Blender not found", FileNotFoundError("Choose an installed blender.exe.")); return
        mapping = None
        if settings.mode == "retarget":
            try: mapping = self._reverse_profile_from_table()
            except Exception as exc: self._show_error("Bone mapping is not ready", exc); return
        output = Path(settings.output_directory); output.mkdir(parents=True, exist_ok=True)
        items = deepcopy(enabled)
        mode = settings.mode
        target_fbx = settings.target_fbx
        translation_scale = settings.translation_scale
        unknown_track_policy = str(
            settings.extensions.get(
                "unknown_track_policy",
                _default_unknown_track_policy(self.project.game_id),
            )
        )
        bake_motion_accumulator = bool(
            settings.extensions.get("bake_motion_accumulator", True)
        )
        mapping = deepcopy(mapping)
        rig_paths = dict(getattr(self, "_rig_paths_by_ref", {}))
        resource_root_path = Path(self.resource_root)
        self.reverse_log.clear(); self._reverse_cancel_requested = False
        self._reverse_cancel_event.clear()
        self.reverse_export_button.setEnabled(False); self.reverse_cancel_button.setEnabled(True)
        self.status.showMessage("Exporting FBX files in the background…")

        def work(progress):
            warnings: list[str] = []

            def load_rig(rig_ref: str, rig_path: str = "") -> ChromeRig:
                if rig_ref == BUILTIN_MALE_RIG_REF:
                    return ChromeRig.load(resource_root_path / "reference" / "male_npc_infected.crig")
                path = rig_path or rig_paths.get(rig_ref, "")
                if not path:
                    raise FileNotFoundError(f"Installed Chrome Rig not found: {rig_ref}")
                return ChromeRig.load(path)

            exported = 0
            for item in items:
                if self._reverse_cancel_event.is_set():
                    break
                if (
                    not item.output_name.strip()
                    or Path(item.output_name).name != item.output_name
                    or "/" in item.output_name
                    or "\\" in item.output_name
                ):
                    raise ValueError(f"Invalid FBX output name: {item.output_name!r}")
                rig = load_rig(item.source_rig_ref, item.source_rig_path)
                scale: str | float = translation_scale
                if scale != "auto": scale = float(scale)
                result = export_anm2_to_fbx(
                    item.source_anm2, rig, output / f"{item.output_name}.fbx",
                    anm2_input_fps=item.resolved_input_fps(),
                    fbx_output_fps=item.resolved_output_fps(),
                    start_frame=item.start_frame, end_frame=item.end_frame,
                    target_fbx=target_fbx if mode == "retarget" else None,
                    bone_map=mapping, translation_scale=scale, blender_executable=blender,
                    unknown_track_policy=unknown_track_policy,
                    bake_motion_accumulator=bake_motion_accumulator,
                    progress=progress,
                    cancel_check=self._reverse_cancel_event.is_set,
                )
                warnings.extend(result.warnings)
                progress(
                    f"Root parity {item.output_name}: "
                    f"{result.root_parity_max_angular_degrees:.6f}° angular, "
                    f"{result.root_parity_max_heading_degrees:.6f}° heading, "
                    f"{result.root_parity_max_translation_m:.3g} m translation"
                )
                if result.motion_accumulator_detected:
                    state = "baked" if result.motion_accumulator_baked else "preserved only"
                    activity = "active" if result.motion_accumulator_active else "static"
                    root_name = (
                        f" into {result.motion_accumulator_root}"
                        if result.motion_accumulator_root
                        else ""
                    )
                    progress(
                        f"Motion accumulator {item.output_name}: {activity}, {state}{root_name}"
                    )
                exported += 1
            return exported, warnings, self._reverse_cancel_event.is_set()

        def succeeded(payload) -> None:
            exported, warnings, cancelled = payload
            for warning in warnings:
                self.reverse_log.appendPlainText("WARNING: " + warning)
            if cancelled:
                self.reverse_log.appendPlainText("Export cancelled.")
                self.status.showMessage("FBX export cancelled", 5000)
            else:
                self.status.showMessage(f"Exported {exported} FBX file(s)", 10000)
                self.qt["QMessageBox"].information(
                    self.window, "FBX export complete", f"Exported {exported} FBX file(s) to:\n{output}"
                )

        def failed(failure: TaskFailure) -> None:
            developer_diagnostics = self._developer_diagnostics_enabled()
            self.reverse_log.appendPlainText(
                failure.traceback
                if developer_diagnostics
                else failure.display_message(False)
            )
            if not self._reverse_cancel_event.is_set():
                self._show_error(
                    "ANM2 to FBX export failed",
                    RuntimeError(failure.display_message(developer_diagnostics)),
                )

        def finished() -> None:
            self.reverse_export_button.setEnabled(True); self.reverse_cancel_button.setEnabled(False)
            self._reverse_cancel_requested = self._reverse_cancel_event.is_set()

        if self.project_path:
            self.project.save(self.project_path)
            self.dirty = False
            self._update_title()
        if not self.background_tasks.start(
            work,
            progress=self.reverse_log.appendPlainText,
            succeeded=succeeded,
            failed=failed,
            finished=finished,
        ):
            finished()
            self.status.showMessage("Another animation build or export is already running.", 5000)

    # --------------------------------------------------------------- helpers
    def _refresh_all(self) -> None:
        self._refreshing = True
        try:
            self.project_name.setText(self.project.name)
            self._set_combo_data(self.game_combo, self.project.game_id)
            self._refresh_game_status()
            self.project_notes.setPlainText(self.project.notes)
            self._reload_target_rig_combo()
            self._set_combo_data(self.target_rig_combo, self.project.rig.target_rig_ref)
            self.use_imported_bind_pose.setChecked(
                self.project.rig.use_imported_animation_bind_pose
            )
            self.source_rest_path.setText(self.project.rig.source_rest_fbx)
            self.trusted_rest_path.setText(self.project.rig.trusted_source_rest_json)
            self.canonical_smd_path.setText(self.project.rig.canonical_smd)
            self.template_anm2_path.setText(self.project.rig.target_template_anm2)
            self.stock_control_path.setText(self.project.rig.stock_writer_control_anm2)
            self.custom_script_resource.setText(self.project.export.custom_script_resource)
            self.resource_prefix.setText(self.project.export.resource_prefix)
            self._set_script_combo_value(
                self.default_script_combo, self.project.export.default_script_target
            )
            self.output_directory.setText(self.project.export.output_directory)
            self.pack_filename.setText(self.project.export.pack_filename)
            self.existing_rpack.setText(self.project.export.existing_rpack)
            self.new_pack_radio.setChecked(self.project.export.mode == "new")
            self.append_pack_radio.setChecked(self.project.export.mode == "append")
            self._set_combo_data(self.collision_combo, self.project.export.collision_policy)
            self.include_controls.setChecked(self.project.export.include_validation_controls)
            self.keep_anm2.setChecked(self.project.export.write_intermediate_anm2)
            self._set_combo_data(
                self.import_tolerance_combo,
                str(
                    self.project.extensions.get(
                        "import_tolerance", FbxImportTolerance.RECOMMENDED.value
                    )
                ),
            )
            self.developer_diagnostics.setChecked(
                self.settings.value("developer_diagnostics", False, type=bool)
            )
            self._reverse_reload_rigs()
            if self.project.anm2_to_fbx.items:
                self._set_combo_data(
                    self.reverse_source_rig,
                    self.project.anm2_to_fbx.items[0].source_rig_ref,
                )
            self._set_combo_data(self.reverse_mode, self.project.anm2_to_fbx.mode)
            self.reverse_target_fbx.setText(self.project.anm2_to_fbx.target_fbx)
            self.reverse_output_directory.setText(self.project.anm2_to_fbx.output_directory)
            self._set_combo_data(
                self.reverse_translation_scale,
                self.project.anm2_to_fbx.translation_scale,
            )
            self._set_combo_data(
                self.reverse_unknown_track_policy,
                str(
                    self.project.anm2_to_fbx.extensions.get(
                        "unknown_track_policy",
                        _default_unknown_track_policy(self.project.game_id),
                    )
                ),
            )
            self.reverse_bake_motion_accumulator.setChecked(
                bool(
                    self.project.anm2_to_fbx.extensions.get(
                        "bake_motion_accumulator", True
                    )
                )
            )
            saved_blender = str(self.settings.value("blender_executable", "") or "")
            detected_blender = discover_blender(saved_blender)
            self.reverse_blender_path.setText(str(detected_blender or saved_blender))
            self.reverse_blender_status.setText(
                f"Ready: {detected_blender}" if detected_blender else "Not found — choose blender.exe"
            )
            advanced = self.settings.value("advanced_mode", False, type=bool)
            self.advanced_mode_toggle.setChecked(bool(advanced))
        finally:
            self._refreshing = False
        self.existing_rpack.setEnabled(self.project.export.mode == "append")
        self._refresh_animation_table()
        self._reverse_refresh_table()
        self._refresh_retarget_clip_combo(analyze=False)
        self._refreshing = True
        try:
            self._script_default_changed()
            self._apply_advanced_visibility()
            self._bind_pose_mode_changed()
            self._reverse_mode_changed()
            self._reverse_restore_profile()
        finally:
            self._refreshing = False
        self._update_title()

    def _sync_project_from_ui(self) -> None:
        self.project.game_id = str(self.game_combo.currentData() or DL1_GAME_ID)
        self.project.name = self.project_name.text().strip() or "Untitled Animation Project"
        self.project.notes = self.project_notes.toPlainText()
        selected_rig_ref = str(self.target_rig_combo.currentData() or BUILTIN_MALE_RIG_REF)
        selected_builtin = apply_target_package_selection(
            self.project, self.resource_root, selected_rig_ref
        )
        if not selected_builtin:
            self.project.rig.target_rig_ref = selected_rig_ref
            self.project.rig.retarget_mode = "exact"
            selected_path = getattr(self, "_rig_paths_by_ref", {}).get(selected_rig_ref, "")
            if selected_path:
                self.project.rig.target_rig_path = selected_path
                try:
                    self.project.rig.target_rig_name = ChromeRig.load(selected_path).name
                except (OSError, ValueError):
                    pass
        self.project.rig.use_imported_animation_bind_pose = (
            self.use_imported_bind_pose.isChecked()
        )
        self.project.rig.source_rest_fbx = self.source_rest_path.text().strip()
        trusted = self.trusted_rest_path.text().strip()
        self.project.rig.trusted_source_rest_json = trusted if Path(trusted).is_file() else ""
        if selected_builtin:
            self.canonical_smd_path.setText(self.project.rig.canonical_smd)
            self.template_anm2_path.setText(self.project.rig.target_template_anm2)
            self.stock_control_path.setText(self.project.rig.stock_writer_control_anm2)
        else:
            self.project.rig.canonical_smd = self.canonical_smd_path.text().strip()
            self.project.rig.target_template_anm2 = self.template_anm2_path.text().strip()
            self.project.rig.stock_writer_control_anm2 = self.stock_control_path.text().strip()
        self.project.export.default_script_target = self._script_combo_value(
            self.default_script_combo, allow_default=False
        )
        self.project.export.custom_script_resource = self.custom_script_resource.text().strip()
        self.project.export.resource_prefix = self.resource_prefix.text().strip()
        self.project.export.mode = "append" if self.append_pack_radio.isChecked() else "new"
        self.project.export.output_directory = self.output_directory.text().strip()
        self.project.export.pack_filename = self.pack_filename.text().strip()
        self.project.export.existing_rpack = self.existing_rpack.text().strip()
        self.project.export.collision_policy = str(self.collision_combo.currentData())
        self.project.export.include_validation_controls = self.include_controls.isChecked()
        self.project.export.write_intermediate_anm2 = self.keep_anm2.isChecked()
        self.project.extensions["import_tolerance"] = self._current_import_tolerance()
        self._sync_reverse_from_ui()

    def _current_import_tolerance(self) -> str:
        combo = getattr(self, "import_tolerance_combo", None)
        if combo is not None:
            value = combo.currentData()
            if value:
                return FbxImportTolerance.coerce(str(value)).value
        project = getattr(self, "project", None)
        extensions = getattr(project, "extensions", {}) if project is not None else {}
        return FbxImportTolerance.coerce(
            extensions.get("import_tolerance", FbxImportTolerance.RECOMMENDED.value)
        ).value

    def _combo_box(self) -> Any:
        return self._NoWheelComboBox()

    def _set_path_row_visible(self, edit: Any, visible: bool) -> None:
        holder = getattr(edit, "_dlr_row_holder", None)
        label = getattr(edit, "_dlr_row_label", None)
        if holder is not None:
            holder.setVisible(visible)
        if label is not None:
            label.setVisible(visible)

    def _apply_advanced_visibility(self) -> None:
        if not hasattr(self, "advanced_mode_toggle"):
            return
        advanced = self.advanced_mode_toggle.isChecked()
        self.advanced_rig_group.setVisible(advanced)
        self.custom_rig_actions.setVisible(advanced)
        self.custom_rig_actions_label.setVisible(advanced)
        self.advanced_export_group.setVisible(advanced)
        self.retarget_advanced_actions.setVisible(advanced)
        self.ignored_bones_panel.setVisible(advanced)
        for button in getattr(self, "advanced_help_buttons", []):
            button.setVisible(advanced)
        custom_selected = (
            self._script_combo_value(self.default_script_combo, allow_default=False)
            == "custom"
        )
        self.custom_script_resource.setVisible(advanced or custom_selected)
        if getattr(self, "custom_script_resource_label", None) is not None:
            self.custom_script_resource_label.setVisible(advanced or custom_selected)

    def _reload_target_rig_combo(self) -> None:
        if not hasattr(self, "target_rig_combo"):
            return
        self._target_rig_cache.clear()
        current = self.target_rig_combo.currentData()
        self.target_rig_combo.clear()
        records = self.rig_registry.records()
        self._rig_paths_by_ref = {row.rig_ref: row.path for row in records}
        self._rig_labels_by_ref: dict[str, str] = {}
        game_id = getattr(getattr(self, "project", None), "game_id", DL1_GAME_ID)
        profile = GAME_PROFILES[game_id]
        default_ref = getattr(profile, "default_target_rig_ref", profile.target_rig_ref)
        compatible_refs = set(
            getattr(profile, "compatible_builtin_rig_refs", (default_ref,))
        )
        advanced = bool(
            getattr(self, "advanced_mode_toggle", None)
            and self.advanced_mode_toggle.isChecked()
        )
        selected_project_ref = str(
            getattr(getattr(self, "project", None), "rig", None).target_rig_ref
            if getattr(getattr(self, "project", None), "rig", None) is not None
            else ""
        )
        for row in records:
            if row.rig_ref.startswith("builtin:"):
                if row.rig_ref not in compatible_refs:
                    continue
                if row.rig_ref != default_ref and not advanced and row.rig_ref != selected_project_ref:
                    continue
            suffix = "" if row.builtin else f" [{row.category}]"
            label = row.display_name + suffix
            self.target_rig_combo.addItem(label, row.rig_ref)
            self._rig_labels_by_ref[row.rig_ref] = label
        project = getattr(self, "project", None)
        if (
            project is not None
            and project.rig.target_rig_ref not in self._rig_paths_by_ref
            and project.rig.target_rig_path
            and Path(project.rig.target_rig_path).is_file()
        ):
            try:
                rig = ChromeRig.load(project.rig.target_rig_path)
                self.target_rig_combo.addItem(
                    f"{rig.name} [{rig.category}, project]", project.rig.target_rig_ref
                )
                self._rig_paths_by_ref[project.rig.target_rig_ref] = project.rig.target_rig_path
                self._rig_labels_by_ref[project.rig.target_rig_ref] = (
                    f"{rig.name} [{rig.category}, project]"
                )
            except (OSError, ValueError):
                pass
        if project is not None:
            for animation in project.animations:
                rig_ref = str(animation.target_rig_ref or "")
                rig_path = str(animation.target_rig_path or "")
                if not rig_ref or rig_ref in self._rig_labels_by_ref or not rig_path:
                    continue
                path = Path(rig_path)
                if not path.is_file():
                    continue
                try:
                    rig = ChromeRig.load(path)
                except (OSError, ValueError):
                    continue
                label = f"{rig.name} [{rig.category}, project clip]"
                self._rig_paths_by_ref[rig_ref] = rig_path
                self._rig_labels_by_ref[rig_ref] = label
        if current:
            self._set_combo_data(self.target_rig_combo, current)

    def _target_rig_changed(self, *_args) -> None:
        if self._refreshing:
            return
        selected = str(self.target_rig_combo.currentData() or BUILTIN_MALE_RIG_REF)
        selected_builtin = apply_target_package_selection(
            self.project, self.resource_root, selected
        )
        if selected_builtin:
            self.canonical_smd_path.setText(self.project.rig.canonical_smd)
            self.template_anm2_path.setText(self.project.rig.target_template_anm2)
            self.stock_control_path.setText(self.project.rig.stock_writer_control_anm2)
        else:
            self.project.rig.target_rig_ref = selected
            self.project.rig.retarget_mode = "exact"
            selected_path = getattr(self, "_rig_paths_by_ref", {}).get(selected, "")
            self.project.rig.target_rig_path = selected_path
            if selected_path:
                try:
                    self.project.rig.target_rig_name = ChromeRig.load(selected_path).name
                except (OSError, ValueError):
                    pass
        self._mark_dirty()
        self._bind_pose_mode_changed()
        self._retarget_clip_changed()
        self._refresh_animation_table()
        if self.target_selection_changed_callback is not None:
            self.target_selection_changed_callback("")

    def _refresh_game_status(self) -> None:
        if not hasattr(self, "game_status"):
            return
        profile = get_game_profile(self.project.game_id)
        self.game_status.setText(
            f"{profile.display_name} • {profile.target_rig_name} • {profile.anm2_format_label} • "
            f"primary root: {profile.primary_root} • {profile.output_status}"
        )

    def _game_changed(self, *_args) -> None:
        if self._refreshing:
            return
        previous = self.project.game_id
        selected = str(self.game_combo.currentData() or DL1_GAME_ID)
        if selected == previous:
            return
        self.project.game_id = selected
        self.project.anm2_to_fbx.extensions["unknown_track_policy"] = (
            _default_unknown_track_policy(selected)
        )
        result = apply_game_profile_defaults(
            self.project, self.resource_root, previous_game_id=previous, force=False
        )
        if result["retained"]:
            self.qt["QMessageBox"].warning(
                self.window,
                "Custom target retained",
                "The game changed, but deliberate custom settings were retained: "
                + ", ".join(result["retained"])
                + ". Review them before building to avoid a cross-game target mixture.",
            )
        self._reload_target_rig_combo()
        self._refresh_all()
        self._mark_dirty()

    def import_chrome_rig(self) -> None:
        path, _ = self.qt["QFileDialog"].getOpenFileName(
            self.window,
            "Import Chrome Rig",
            str(self.root),
            "Chrome Rig (*.crig)",
        )
        if not path:
            return
        try:
            record = self.rig_registry.import_rig(path)
            self._reload_target_rig_combo()
            self._reverse_reload_rigs()
            self._set_combo_data(self.target_rig_combo, record.rig_ref)
            self._target_rig_changed()
            self.status.showMessage(f"Installed Chrome Rig: {record.display_name}", 7000)
        except Exception as exc:
            self._show_error("Could not import Chrome Rig", exc)

    def create_chrome_rig(self) -> None:
        model_path, _ = self.qt["QFileDialog"].getOpenFileName(
            self.window,
            "Choose target model FBX",
            str(self.root),
            "FBX (*.fbx)",
        )
        if not model_path:
            return
        suggested = self.root / f"{Path(model_path).stem}.crig"
        output_path, _ = self.qt["QFileDialog"].getSaveFileName(
            self.window,
            "Save shareable Chrome Rig",
            str(suggested),
            "Chrome Rig (*.crig)",
        )
        if not output_path:
            return
        try:
            rig = build_chrome_rig_from_fbx(model_path)
            saved = rig.save(output_path)
            record = self.rig_registry.import_rig(saved)
            self._reload_target_rig_combo()
            self._reverse_reload_rigs()
            self._set_combo_data(self.reverse_source_rig, record.rig_ref)
            self._set_combo_data(self.target_rig_combo, record.rig_ref)
            self._target_rig_changed()
            warnings = rig.validate().warnings
            message = (
                f"Created and installed {record.display_name} ({len(rig.bones)} bones)."
                + (f"\n\nWarnings:\n- " + "\n- ".join(warnings) if warnings else "")
            )
            self.qt["QMessageBox"].information(self.window, "Chrome Rig created", message)
        except Exception as exc:
            self._show_error("Could not create Chrome Rig", exc)

    def manage_chrome_rigs(self) -> None:
        self.rig_registry.root.mkdir(parents=True, exist_ok=True)
        self.qt["QDesktopServices"].openUrl(
            self.qt["QUrl"].fromLocalFile(str(self.rig_registry.root.resolve()))
        )

    def _advanced_mode_changed(self, checked: bool) -> None:
        if not self._refreshing:
            self.settings.setValue("advanced_mode", bool(checked))
        self._apply_advanced_visibility()
        previous = self._refreshing
        self._refreshing = True
        try:
            self._bind_pose_mode_changed()
            self._reverse_reload_rigs()
            self._reload_target_rig_combo()
        finally:
            self._refreshing = previous

    def _bind_pose_mode_changed(self, *_args) -> None:
        if not hasattr(self, "use_imported_bind_pose"):
            return
        selected_ref = str(
            self.target_rig_combo.currentData() or BUILTIN_MALE_RIG_REF
        )
        profile = get_game_profile(self.project.game_id)
        built_in = selected_ref in profile.compatible_builtin_rig_refs
        legacy_humanoid_controls = built_in and self.project.game_id == DL1_GAME_ID
        embedded = self.use_imported_bind_pose.isChecked()
        self.use_imported_bind_pose.setVisible(legacy_humanoid_controls)
        self._set_path_row_visible(
            self.source_rest_path, legacy_humanoid_controls and not embedded
        )
        if hasattr(self, "trusted_rest_path"):
            self._set_path_row_visible(
                self.trusted_rest_path,
                self.advanced_mode_toggle.isChecked()
                and legacy_humanoid_controls
                and not embedded,
            )
        if hasattr(self, "advanced_rig_group"):
            self.advanced_rig_group.setEnabled(legacy_humanoid_controls)
        if not self._refreshing:
            self._mark_dirty()

    def _script_combo(self, *, include_project_default: bool) -> Any:
        combo = self._combo_box()
        combo.setEditable(True)
        if include_project_default:
            combo.addItem("(Project default)", "")
        for target in BUILTIN_SCRIPT_TARGETS:
            combo.addItem(target.display_name, target.target_id)
        combo.addItem("Custom resource name…", "custom")
        return combo

    def _script_combo_value(self, combo: Any, *, allow_default: bool) -> str:
        data = combo.currentData()
        text = combo.currentText().strip()
        if data is not None:
            if data == "" and allow_default:
                return ""
            if data == "custom":
                if text and text != "Custom resource name…":
                    return text
                return "custom"
            return str(data)
        if text == "(Project default)" and allow_default:
            return ""
        known = next(
            (target.target_id for target in BUILTIN_SCRIPT_TARGETS if target.display_name == text),
            None,
        )
        return known or text

    def _set_script_combo_value(self, combo: Any, value: str) -> None:
        if not value:
            self._set_combo_data(combo, "")
            return
        index = combo.findData(value)
        if index >= 0:
            combo.setCurrentIndex(index)
            return
        target = self.script_registry.by_resource_name(value)
        if target is not None:
            self._set_combo_data(combo, target.target_id)
            return
        combo.setEditText(value)

    def _script_default_changed(self) -> None:
        if not self._refreshing:
            self._mark_dirty()
        value = self._script_combo_value(self.default_script_combo, allow_default=False)
        target = self.script_registry.by_id(value) or self.script_registry.by_resource_name(value)
        custom_selected = value == "custom"
        self.custom_script_resource.setEnabled(custom_selected)
        if custom_selected:
            resource = self.custom_script_resource.text().strip() or "anims_my_character_all"
            self.script_description.setText(
                f"Custom _ANIMATION_SCR_ resource: {resource}. The project will create or append that resource."
            )
        elif target is None:
            self.script_description.setText(
                f"Custom _ANIMATION_SCR_ resource: {value}. The project will create or append that resource."
            )
        else:
            self.script_description.setText(target.description)
        self._apply_advanced_visibility()

    def _set_combo_data(self, combo: Any, value: Any) -> None:
        index = combo.findData(value)
        if index >= 0:
            combo.setCurrentIndex(index)
        elif combo.isEditable():
            combo.setEditText(str(value))
    def _browse_file(self, edit: Any, file_filter: str) -> None:
        start = edit.text().strip() or str(self.root)
        path, _ = self.qt["QFileDialog"].getOpenFileName(
            self.window, "Choose file", start, file_filter or "All files (*)"
        )
        if path:
            edit.setText(path)

    def _browse_directory(self, edit: Any) -> None:
        start = edit.text().strip() or str(self.root)
        path = self.qt["QFileDialog"].getExistingDirectory(
            self.window, "Choose folder", start
        )
        if path:
            edit.setText(path)

    def _mark_dirty(self, *_args) -> None:
        if self._refreshing:
            return
        self.dirty = True
        self._update_title()

    def _update_title(self) -> None:
        filename = self.project_path.name if self.project_path else "Unsaved project"
        marker = " *" if self.dirty else ""
        self.window.setWindowTitle(
            f"DL ReAnimated {__version__} — {self.project.name} [{filename}]{marker}"
        )

    def _show_error(self, title: str, exc: Exception) -> None:
        self.qt["QMessageBox"].critical(self.window, title, str(exc))

    def open_doc(self, filename: str) -> None:
        path = self.resource_root / "docs" / filename
        if not path.exists():
            self._show_error("Documentation missing", FileNotFoundError(path))
            return
        self.qt["QDesktopServices"].openUrl(
            self.qt["QUrl"].fromLocalFile(str(path.resolve()))
        )
