from __future__ import annotations

"""Embedded model-import workspace for the unified DL ReAnimated project GUI."""

from dataclasses import dataclass, field
from copy import deepcopy
from pathlib import Path
from typing import Any, Callable
import hashlib
import json
import re
import string

from ..model_importer.compiler_bridge import CompilerSettings, compile_and_install_model
from ..background_tasks import BackgroundTaskRunner, TaskFailure
from ..chrome_rig import ChromeRig
from ..model_importer.crig import (
    build_crig_from_rig_contract_bytes,
    write_prebuilt_crig_payload,
)
from ..model_importer.fbx_model import (
    FbxImportTolerance,
    FbxScene,
    ORIENTATION_POLICIES,
)
from ..model_importer.msh_builder import (
    ModelBuildOptions,
    build_source_from_fbx,
    humanoid_bone_mapping,
    sanitize_name,
    source_skin_weight_usage,
)
from ..model_importer.vendor.chrome_mesh_tools.smd import SmdFile
from ..fbx_preflight import preflight_fbx

MODEL_WORKSPACE_EXTENSION_KEY = "model_workspace_v2"


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


@dataclass(slots=True)
class ModelEntry:
    path: str
    resource_name: str
    mode: str = "auto"
    enabled: bool = True
    orientation_policy: str = "auto"
    humanoid_bone_map: dict[str, str] = field(default_factory=dict)
    scene: FbxScene | None = None
    inventory: dict[str, Any] | None = None
    build_report: dict[str, Any] | None = None
    source_msh: Path | None = None
    crig_path: Path | None = None
    installed_crig_ref: str = ""
    installed_crig_path: Path | None = None
    status: str = "Not analyzed"
    extensions: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        result = deepcopy(self.extensions)
        result.update({
            "path": self.path,
            "resource_name": self.resource_name,
            "mode": self.mode,
            "enabled": self.enabled,
            "orientation_policy": self.orientation_policy,
            "humanoid_bone_map": dict(self.humanoid_bone_map),
            "generated_crig_ref": self.installed_crig_ref,
            "generated_crig_path": (
                str(self.installed_crig_path) if self.installed_crig_path else ""
            ),
        })
        return result

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "ModelEntry":
        known = {
            "path",
            "resource_name",
            "mode",
            "enabled",
            "orientation_policy",
            "humanoid_bone_map",
            "generated_crig_ref",
            "generated_crig_path",
        }
        return cls(
            path=str(value.get("path", "")),
            resource_name=str(value.get("resource_name", "model")),
            mode=str(value.get("mode", "auto")),
            enabled=bool(value.get("enabled", True)),
            orientation_policy=str(value.get("orientation_policy", "auto")),
            humanoid_bone_map={
                str(key): str(row)
                for key, row in dict(value.get("humanoid_bone_map", {})).items()
            },
            installed_crig_ref=str(value.get("generated_crig_ref", "")),
            installed_crig_path=(
                Path(str(value["generated_crig_path"]))
                if value.get("generated_crig_path")
                else None
            ),
            extensions={
                str(key): deepcopy(row)
                for key, row in value.items()
                if key not in known
            },
        )


class ModelWorkspace:
    def __init__(
        self,
        qt: dict[str, Any],
        *,
        parent_window: Any,
        root: Path,
        mark_dirty: Callable[[], None],
        status_callback: Callable[[str], None] | None = None,
        rigs_installed_callback: Callable[[list["ModelEntry"]], None] | None = None,
        animations_for_rig_callback: Callable[["ModelEntry"], None] | None = None,
    ) -> None:
        self.qt = qt
        self.parent_window = parent_window
        self.root = Path(root)
        self.mark_dirty = mark_dirty
        self.status_callback = status_callback or (lambda _message: None)
        self.rigs_installed_callback = rigs_installed_callback
        self.animations_for_rig_callback = animations_for_rig_callback
        self.settings = qt["QSettings"]("DL ReAnimated", "Unified Model Workspace")
        self.entries: list[ModelEntry] = []
        self._workspace_unknown_fields: dict[str, Any] = {}
        self._settings_unknown_fields: dict[str, Any] = {}
        self.busy = False
        # Path rows emit textChanged while the UI is still being assembled.
        # Defer persistence until every settings widget exists.
        self._initializing = True
        self.widget = qt["QWidget"]()
        self.background_tasks = BackgroundTaskRunner(self.widget)
        outer = qt["QVBoxLayout"](self.widget)
        outer.setContentsMargins(0, 0, 0, 0)
        self.tabs = qt["QTabWidget"]()
        outer.addWidget(self.tabs)
        self._build_models_tab()
        self._build_mapping_tab()
        self._build_build_tab()
        self._build_devtools_tab()
        self._build_help_tab()
        self._load_settings()
        self._initializing = False
        self._refresh_table()

    # ------------------------------------------------------------------ project state
    def serialize(self) -> dict[str, Any]:
        self._capture_all_table_state()
        result = deepcopy(self._workspace_unknown_fields)
        settings = deepcopy(self._settings_unknown_fields)
        settings.update(self._settings_payload())
        result.update({
            "format": "dl-reanimated-model-workspace",
            "schema_version": 2,
            "models": [entry.to_dict() for entry in self.entries],
            "settings": settings,
        })
        return result

    def restore(self, value: dict[str, Any] | None) -> None:
        payload = dict(value or {})
        self._workspace_unknown_fields = {
            str(key): deepcopy(row)
            for key, row in payload.items()
            if key not in {"format", "schema_version", "models", "settings"}
        }
        self.entries = [ModelEntry.from_dict(dict(row)) for row in payload.get("models", [])]
        settings = dict(payload.get("settings", {}))
        self._settings_unknown_fields = {
            str(key): deepcopy(row)
            for key, row in settings.items()
            if key not in self._settings_payload()
        }
        self._apply_settings_payload(settings)
        self._refresh_table()
        self._refresh_mapping_model_combo()

    def reset(self) -> None:
        self.entries.clear()
        self._refresh_table()
        self._refresh_mapping_model_combo()

    # ------------------------------------------------------------------ basic UI
    def _build_models_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        intro = qt["QLabel"](
            "Import static props or skinned FBX models. Auto orientation respects the FBX scene "
            "axis metadata and evaluated Model/BindPose transforms, so ordinary Y-up objects "
            "should be upright at identity rotation in ChromeEd."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)
        buttons = qt["QHBoxLayout"]()
        for label, callback in (
            ("Add model FBX…", self.add_models),
            ("Remove selected", self.remove_selected),
            ("Analyze models", self.analyze_models),
        ):
            button = qt["QPushButton"](label)
            button.clicked.connect(callback)
            buttons.addWidget(button)
        buttons.addStretch(1)
        layout.addLayout(buttons)

        self.model_table = qt["QTableWidget"](0, 10)
        self.model_table.setHorizontalHeaderLabels(
            (
                "Use", "Model FBX", "Resource", "Mode", "Orientation",
                "Meshes", "Bones", "Materials", "Mapping", "Status",
            )
        )
        self.model_table.setAlternatingRowColors(True)
        self.model_table.setSelectionBehavior(qt["QAbstractItemView"].SelectRows)
        self.model_table.setSelectionMode(qt["QAbstractItemView"].ExtendedSelection)
        self.model_table.itemSelectionChanged.connect(self._selection_changed)
        header = self.model_table.horizontalHeader()
        header.setSectionResizeMode(0, qt["QHeaderView"].ResizeToContents)
        header.setSectionResizeMode(1, qt["QHeaderView"].Stretch)
        header.setSectionResizeMode(2, qt["QHeaderView"].Interactive)
        header.setSectionResizeMode(3, qt["QHeaderView"].ResizeToContents)
        header.setSectionResizeMode(4, qt["QHeaderView"].ResizeToContents)
        for index in range(5, 10):
            header.setSectionResizeMode(index, qt["QHeaderView"].ResizeToContents)
        layout.addWidget(self.model_table, 3)
        details_group = qt["QGroupBox"]("Selected model analysis")
        details_layout = qt["QVBoxLayout"](details_group)
        self.details = qt["QPlainTextEdit"]()
        self.details.setReadOnly(True)
        details_layout.addWidget(self.details)
        detail_actions = qt["QHBoxLayout"]()
        self.open_model_report_button = qt["QPushButton"]("Open model build report")
        self.open_model_report_button.clicked.connect(self._open_model_build_report)
        self.open_generated_crig_button = qt["QPushButton"]("Open generated CRIG location")
        self.open_generated_crig_button.clicked.connect(
            self._open_generated_crig_location
        )
        self.show_targeting_animations_button = qt["QPushButton"](
            "Show animations targeting this CRIG"
        )
        self.show_targeting_animations_button.clicked.connect(
            self._show_targeting_animations
        )
        for button in (
            self.open_model_report_button,
            self.open_generated_crig_button,
            self.show_targeting_animations_button,
        ):
            button.setEnabled(False)
            detail_actions.addWidget(button)
        detail_actions.addStretch(1)
        details_layout.addLayout(detail_actions)
        layout.addWidget(details_group, 2)
        self.tabs.addTab(page, "Models")

    def _build_mapping_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        intro = qt["QLabel"](
            "Manual skin mapping for fitted DL1-name model imports. Auto-map is a starting "
            "point; twist, helper, face, costume, or accessory bones may remain unmapped. "
            "This mode preserves model-specific proportions: retarget every animation to the "
            "generated CRIG and never attach raw stock tracks directly."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)
        row = qt["QHBoxLayout"]()
        self.mapping_model_combo = qt["QComboBox"]()
        self.mapping_model_combo.currentIndexChanged.connect(self._refresh_model_mapping_table)
        auto_button = qt["QPushButton"]("Auto-map selected model")
        auto_button.clicked.connect(self.auto_map_model)
        self.review_ambiguous_button = qt["QPushButton"]("Review ambiguous only")
        self.review_ambiguous_button.setCheckable(True)
        self.review_ambiguous_button.toggled.connect(
            lambda _checked: self._apply_model_mapping_filter()
        )
        self.reset_safe_suggestions_button = qt["QPushButton"](
            "Reset safe suggestions"
        )
        self.reset_safe_suggestions_button.setToolTip(
            "Return high-confidence rows to their current automatic suggestion while "
            "leaving ambiguous and intentionally unmapped rows unchanged."
        )
        self.reset_safe_suggestions_button.clicked.connect(
            self.reset_safe_model_suggestions
        )
        clear_button = qt["QPushButton"]("Clear manual overrides")
        clear_button.clicked.connect(self.clear_model_mapping)
        row.addWidget(qt["QLabel"]("Model"))
        row.addWidget(self.mapping_model_combo, 1)
        row.addWidget(auto_button)
        row.addWidget(self.review_ambiguous_button)
        row.addWidget(self.reset_safe_suggestions_button)
        row.addWidget(clear_button)
        layout.addLayout(row)
        self.model_mapping_table = qt["QTableWidget"](0, 11)
        self.model_mapping_table.setHorizontalHeaderLabels(
            (
                "Source FBX bone",
                "Source weight %",
                "Semantic role",
                "Automatic target",
                "Final target",
                "Transfer",
                "Components",
                "Confidence",
                "Review",
                "Method",
                "Status",
            )
        )
        mapping_header = self.model_mapping_table.horizontalHeader()
        mapping_header.setSectionResizeMode(0, qt["QHeaderView"].Stretch)
        mapping_header.setSectionResizeMode(3, qt["QHeaderView"].Stretch)
        mapping_header.setSectionResizeMode(4, qt["QHeaderView"].Stretch)
        for index in (1, 2, 5, 6, 7, 8, 9, 10):
            mapping_header.setSectionResizeMode(
                index, qt["QHeaderView"].ResizeToContents
            )
        self.model_mapping_table.verticalHeader().setVisible(False)
        layout.addWidget(self.model_mapping_table, 1)
        self.mapping_note = qt["QLabel"]()
        self.mapping_note.setWordWrap(True)
        layout.addWidget(self.mapping_note)
        self.tabs.addTab(page, "Bone Mapping")

    def _build_build_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        output = qt["QGroupBox"]("Output and model authoring")
        form = qt["QFormLayout"](output)
        self.output_path = self._path_row(form, "Output folder", directory=True)
        self.material_mode = qt["QComboBox"]()
        self.material_mode.addItem("Known-good test material", "test")
        self.material_mode.addItem("Preserve slots as placeholder .mat names", "preserve_slots")
        self.import_tolerance = qt["QComboBox"]()
        self.import_tolerance.addItem(
            "Recommended / forgiving", FbxImportTolerance.RECOMMENDED.value
        )
        self.import_tolerance.addItem(
            "Strict diagnostics", FbxImportTolerance.STRICT_DIAGNOSTICS.value
        )
        self.import_tolerance.setToolTip(
            "Recommended triangulates safe non-planar quads/n-gons and reports repairs. "
            "Strict diagnostics blocks selected recovery warnings before output."
        )
        self.import_tolerance.currentIndexChanged.connect(self._changed)
        self.test_material = qt["QLineEdit"]("bottle_trash_a.mat")
        self.surface_name = qt["QLineEdit"]("Flesh")
        self.flip_v = qt["QCheckBox"]("Flip UV V")
        self.retain_skeleton = qt["QCheckBox"]("Retain every rig bone")
        self.retain_skeleton.setChecked(True)
        self.create_crig = qt["QCheckBox"]("Create/install .crig for every skinned model")
        self.create_crig.setChecked(True)
        self.animation_script = qt["QLineEdit"]()
        self.animation_script.setPlaceholderText(
            "Optional: script containing animations retargeted to this model"
        )
        self.target_smd = self._path_row(
            form, "Dying Light target SMD", directory=False, file_filter="SMD (*.smd)"
        )
        default_smd = self.root / "reference" / "player_1_tpp.smd"
        if default_smd.is_file():
            self.target_smd.setText(str(default_smd))
        form.addRow("Materials", self.material_mode)
        form.addRow("Import tolerance", self.import_tolerance)
        form.addRow("Test material", self.test_material)
        form.addRow("Physical surface", self.surface_name)
        form.addRow(self.flip_v)
        form.addRow(self.retain_skeleton)
        form.addRow(self.create_crig)
        form.addRow("Animation script", self.animation_script)
        layout.addWidget(output)
        actions = qt["QHBoxLayout"]()
        self.build_source_button = qt["QPushButton"]("Build source MSH")
        self.build_source_button.clicked.connect(self.build_sources)
        self.install_button = qt["QPushButton"]("Build, compile & install")
        self.install_button.clicked.connect(self.compile_and_install)
        self.use_generated_rig_button = qt["QPushButton"](
            "Use generated rig in Animations"
        )
        self.use_generated_rig_button.setToolTip(
            "Assign the selected model's generated CRIG to the selected animation clip (or "
            "the project default when no clips exist) without changing unrelated clips."
        )
        self.use_generated_rig_button.clicked.connect(
            self._use_selected_generated_rig
        )
        self.use_generated_rig_button.setEnabled(False)
        actions.addWidget(self.build_source_button)
        actions.addWidget(self.install_button)
        actions.addWidget(self.use_generated_rig_button)
        actions.addStretch(1)
        layout.addLayout(actions)
        self.progress = qt["QProgressBar"]()
        layout.addWidget(self.progress)
        self.log = qt["QPlainTextEdit"]()
        self.log.setReadOnly(True)
        layout.addWidget(self.log, 1)
        self.tabs.addTab(page, "Build & Install")

    def _build_devtools_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        group = qt["QGroupBox"]("Dying Light Developer Tools")
        form = qt["QFormLayout"](group)
        self.compiler_path = self._path_row(
            form, "ResPack compiler", directory=False,
            file_filter="ResPackCompilerConsole_x64_rwdi.exe (*.exe)",
        )
        self.data0_path = self._path_row(form, "Data0.pak", directory=False, file_filter="Data0.pak")
        self.workshop_path = self._path_row(form, "Workshop root", directory=True)
        self.active_project_path = self._path_row(form, "Project to install into", directory=True)
        self.devtools_data_path = self._path_row(form, "Developer Tools Engine\\Data", directory=True)
        layout.addWidget(group)
        row = qt["QHBoxLayout"]()
        detect = qt["QPushButton"]("Auto-detect")
        detect.clicked.connect(self.auto_detect_paths)
        validate = qt["QPushButton"]("Validate")
        validate.clicked.connect(self.validate_paths)
        row.addWidget(detect)
        row.addWidget(validate)
        row.addStretch(1)
        layout.addLayout(row)
        note = qt["QLabel"](
            "Meshes are installed as loose source files under data and compiled .msh_obj files "
            "under assets_pc. Model resources do not need to be merged into the animation RPack."
        )
        note.setWordWrap(True)
        layout.addWidget(note)
        layout.addStretch(1)
        self.tabs.addTab(page, "DevTools")

    def _build_help_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        text = qt["QPlainTextEdit"]()
        text.setReadOnly(True)
        text.setPlainText(
            "AUTO ORIENTATION\n"
            "  Respects the FBX axis metadata and evaluated bind transforms. Test the model at "
            "identity rotation in ChromeEd. Manual ±90° policies are diagnostics for unusual FBXs.\n\n"
            "EXACT RIG\n"
            "  Preserves the FBX skeleton and creates a matching .crig. Animations may use exact "
            "matching skeletons or the mapped-.crig workspace under Animations.\n\n"
            "DYING LIGHT HUMANOID\n"
            "  Preserves the FBX bind-pose surface and remaps only skin weights onto the Dying "
            "Light bones. Per-bone bind reshaping is intentionally not applied because different "
            "body proportions can shred an otherwise valid mesh. Review Bone Mapping before compiling.\n\n"
            "DELIVERY\n"
            "  Use the normal loose data/assets_pc project layout. Keep animation ANM2 resources "
            "in common_anims_sp_pc.rpack through the Animations workspace."
        )
        layout.addWidget(text)
        self.tabs.addTab(page, "Help")

    def _path_row(self, form, label: str, *, directory: bool, file_filter: str = "All files (*)"):
        qt = self.qt
        holder = qt["QWidget"]()
        row = qt["QHBoxLayout"](holder)
        row.setContentsMargins(0, 0, 0, 0)
        edit = qt["QLineEdit"]()
        edit.textChanged.connect(self._changed)
        button = qt["QPushButton"]("Browse…")
        def choose() -> None:
            if directory:
                value = qt["QFileDialog"].getExistingDirectory(self.parent_window, label, edit.text())
            else:
                value, _ = qt["QFileDialog"].getOpenFileName(
                    self.parent_window, label, edit.text(), file_filter
                )
            if value:
                edit.setText(value)
        button.clicked.connect(choose)
        row.addWidget(edit, 1)
        row.addWidget(button)
        form.addRow(label, holder)
        return edit

    # ------------------------------------------------------------------ entries
    def add_models(self) -> None:
        paths, _ = self.qt["QFileDialog"].getOpenFileNames(
            self.parent_window, "Add model FBX files", "", "FBX models (*.fbx)"
        )
        existing = {str(Path(entry.path).resolve()).casefold() for entry in self.entries}
        for value in paths:
            resolved = str(Path(value).resolve())
            if resolved.casefold() in existing:
                continue
            self.entries.append(
                ModelEntry(
                    path=resolved,
                    resource_name=sanitize_name(Path(value).stem, max_bytes=56).casefold(),
                )
            )
            existing.add(resolved.casefold())
        if paths:
            self._changed()
            self._refresh_table()
            self._refresh_mapping_model_combo()
            self.analyze_models()

    def remove_selected(self) -> None:
        rows = sorted({index.row() for index in self.model_table.selectedIndexes()}, reverse=True)
        for row in rows:
            if 0 <= row < len(self.entries):
                del self.entries[row]
        if rows:
            self._changed()
            self._refresh_table()
            self._refresh_mapping_model_combo()

    def analyze_models(self) -> None:
        rows = [entry for entry in self.entries if entry.enabled]
        if not rows:
            self._message("No models", "Add and enable at least one model FBX.")
            return
        self._set_busy(True)
        self.progress.setRange(0, len(rows))
        try:
            for index, entry in enumerate(rows, 1):
                try:
                    entry.scene = FbxScene.from_path(
                        entry.path,
                        purpose="model",
                        tolerance=self._import_tolerance_value(),
                    )
                    entry.inventory = entry.scene.inventory()
                    preflight = preflight_fbx(
                        entry.path,
                        purpose="model",
                        scene=entry.scene,
                        tolerance=self._import_tolerance_value(),
                    )
                    entry.inventory["preflight"] = preflight.to_dict()
                    preflight.require_buildable()
                    entry.status = f"Ready ({entry.inventory['detected_mode']})"
                except Exception as exc:
                    entry.status = f"ERROR: {exc}"
                    self._append_log(f"Model analysis failed: {exc}")
                self.progress.setValue(index)
                self.qt["QApplication"].processEvents()
        finally:
            self._set_busy(False)
            self._refresh_table()
            self._refresh_mapping_model_combo()
            self._show_selected_details()

    def _capture_all_table_state(self) -> None:
        for row, entry in enumerate(self.entries):
            resource = self.model_table.cellWidget(row, 2)
            mode = self.model_table.cellWidget(row, 3)
            orientation = self.model_table.cellWidget(row, 4)
            if resource is not None:
                entry.resource_name = sanitize_name(resource.text(), max_bytes=56).casefold()
            if mode is not None:
                entry.mode = str(mode.currentData())
            if orientation is not None:
                entry.orientation_policy = str(orientation.currentData())

    def _refresh_table(self) -> None:
        qt = self.qt
        self.model_table.setRowCount(len(self.entries))
        for row, entry in enumerate(self.entries):
            use = qt["QCheckBox"]()
            use.setChecked(entry.enabled)
            use.toggled.connect(lambda value, index=row: self._set_enabled(index, value))
            self.model_table.setCellWidget(row, 0, use)
            source = qt["QTableWidgetItem"](entry.path)
            source.setToolTip(entry.path)
            self.model_table.setItem(row, 1, source)
            resource = qt["QLineEdit"](entry.resource_name)
            resource.textChanged.connect(self._changed)
            self.model_table.setCellWidget(row, 2, resource)
            mode = qt["QComboBox"]()
            for label, value in (
                ("Auto", "auto"),
                ("Static prop", "static"),
                ("Exact original FBX rig", "exact_rig"),
                (
                    "DL1 humanoid names - preserve proportions, retarget animations",
                    "dying_light_humanoid",
                ),
            ):
                mode.addItem(label, value)
            mode.setCurrentIndex(max(0, mode.findData(entry.mode)))
            mode.currentIndexChanged.connect(lambda _value, i=row, widget=mode: self._set_mode(i, widget.currentData()))
            self.model_table.setCellWidget(row, 3, mode)
            orientation = qt["QComboBox"]()
            labels = (
                ("Auto: respect FBX axis metadata", "auto"),
                ("Legacy FBX Y-up → Dying Light", "fbx_y_up_to_dying_light"),
                ("No conversion", "none"),
                ("Rotate X +90°", "rotate_x_90"),
                ("Rotate X -90°", "rotate_x_minus_90"),
                ("Rotate Y +90°", "rotate_y_90"),
                ("Rotate Y -90°", "rotate_y_minus_90"),
                ("Rotate Z +90°", "rotate_z_90"),
                ("Rotate Z -90°", "rotate_z_minus_90"),
            )
            for label, value in labels:
                orientation.addItem(label, value)
            orientation.setCurrentIndex(max(0, orientation.findData(entry.orientation_policy)))
            orientation.currentIndexChanged.connect(
                lambda _value, i=row, widget=orientation: self._set_orientation(i, widget.currentData())
            )
            self.model_table.setCellWidget(row, 4, orientation)
            inventory = entry.inventory or {}
            mapping_count = len(entry.humanoid_bone_map)
            values = (
                inventory.get("mesh_geometry_count", "—"),
                inventory.get("limb_node_count", "—"),
                inventory.get("material_count", "—"),
                mapping_count if mapping_count else "Auto",
                entry.status,
            )
            for column, value in enumerate(values, 5):
                self.model_table.setItem(row, column, qt["QTableWidgetItem"](str(value)))
        self._selection_changed()

    def _set_enabled(self, index: int, value: bool) -> None:
        if 0 <= index < len(self.entries):
            self.entries[index].enabled = bool(value)
            self._changed()

    def _set_mode(self, index: int, value: Any) -> None:
        if 0 <= index < len(self.entries):
            self.entries[index].mode = str(value)
            self._changed()
            self._refresh_mapping_model_combo()

    def _set_orientation(self, index: int, value: Any) -> None:
        if 0 <= index < len(self.entries):
            self.entries[index].orientation_policy = str(value)
            self._changed()

    def _selection_changed(self) -> None:
        self._show_selected_details()
        selected = self._selected_entry()
        if selected:
            index = self.mapping_model_combo.findData(selected.path)
            if index >= 0:
                self.mapping_model_combo.setCurrentIndex(index)
        if hasattr(self, "use_generated_rig_button"):
            self.use_generated_rig_button.setEnabled(
                bool(selected and selected.installed_crig_ref and not self.busy)
            )
        if hasattr(self, "open_model_report_button"):
            self.open_model_report_button.setEnabled(
                bool(selected and isinstance(selected.build_report, dict))
            )
            crig_path = (
                selected.installed_crig_path or selected.crig_path
                if selected is not None
                else None
            )
            self.open_generated_crig_button.setEnabled(bool(crig_path))
            self.show_targeting_animations_button.setEnabled(
                bool(
                    selected
                    and selected.installed_crig_ref
                    and self.animations_for_rig_callback is not None
                )
            )

    @staticmethod
    def _model_report_path(entry: ModelEntry) -> Path | None:
        report = entry.build_report if isinstance(entry.build_report, dict) else {}
        msh_value = str(report.get("msh_path", "") or "")
        msh_path = Path(msh_value) if msh_value else entry.source_msh
        if msh_path is None:
            return None
        return Path(msh_path).with_suffix(".model_import.json")

    def _open_local_path(self, path: Path) -> None:
        self.qt["QDesktopServices"].openUrl(
            self.qt["QUrl"].fromLocalFile(str(path.resolve()))
        )

    def _open_model_build_report(self) -> None:
        entry = self._selected_entry()
        report_path = self._model_report_path(entry) if entry is not None else None
        if report_path is None or not report_path.is_file():
            self._message(
                "Model build report is unavailable",
                "Build the selected model first. The report is created beside its source MSH "
                "as <resource>.model_import.json.",
                critical=True,
            )
            return
        self._open_local_path(report_path)

    def _open_generated_crig_location(self) -> None:
        entry = self._selected_entry()
        crig_path = (
            entry.installed_crig_path or entry.crig_path
            if entry is not None
            else None
        )
        if crig_path is None or not Path(crig_path).is_file():
            self._message(
                "Generated CRIG is unavailable",
                "Build the selected skinned model with Create/install .crig enabled, then "
                "retry. No animation target was changed.",
                critical=True,
            )
            return
        self._open_local_path(Path(crig_path).parent)

    def _show_targeting_animations(self) -> None:
        entry = self._selected_entry()
        if (
            entry is None
            or not entry.installed_crig_ref
            or self.animations_for_rig_callback is None
        ):
            self._message(
                "No generated animation target",
                "Build/install a generated CRIG for this model before filtering animations.",
            )
            return
        self.animations_for_rig_callback(entry)

    @staticmethod
    def _generated_rig_handoff_error(entry: ModelEntry) -> str:
        report = entry.build_report
        if not isinstance(report, dict):
            return (
                "The selected model has no current build report. Analyze and rebuild the model "
                "and generated CRIG together before using it as an animation target."
            )
        contract = report.get("authored_rig_contract")
        if not isinstance(contract, dict):
            return (
                "The model build report has no authored rig contract, so MSH/CRIG bind identity "
                "cannot be verified. Rebuild in Exact original FBX rig mode."
            )
        source = Path(entry.path)
        if not source.is_file():
            return f"The model source FBX is missing: {source}. Restore it and rebuild the model."
        expected_source_hash = str(contract.get("source_fbx_sha256", "") or "")
        actual_source_hash = _sha256_file(source)
        if not expected_source_hash or actual_source_hash != expected_source_hash:
            return (
                f"The model source FBX {source.name!r} changed after its authored MSH/CRIG was "
                "built. Analyze and rebuild the model and CRIG together before handoff."
            )
        source_msh = entry.source_msh
        if source_msh is None or not Path(source_msh).is_file():
            return (
                "The source MSH associated with this generated rig is missing. Rebuild the model "
                "before assigning its CRIG to animations."
            )
        expected_msh_hash = str(report.get("msh_sha256", "") or "")
        if not expected_msh_hash or _sha256_file(Path(source_msh)) != expected_msh_hash:
            return (
                f"The source MSH {Path(source_msh).name!r} does not match its build report. "
                "Rebuild the model and CRIG as one bind unit."
            )
        crig_path = entry.installed_crig_path or entry.crig_path
        if crig_path is None or not Path(crig_path).is_file():
            return "The generated CRIG is missing. Build and install it again before handoff."
        try:
            rig = ChromeRig.load(crig_path)
        except (OSError, ValueError) as exc:
            return f"The generated CRIG cannot be validated: {exc}. Rebuild and install it again."
        expected = {
            "authored_bind_hash": str(contract.get("bind_hash", "") or ""),
            "authored_rig_contract_id": str(contract.get("contract_id", "") or ""),
            "authored_skeleton_hash": str(contract.get("skeleton_hash", "") or ""),
            "authored_descriptor_hash": str(contract.get("descriptor_hash", "") or ""),
        }
        mismatches = [
            key
            for key, value in expected.items()
            if value and str(rig.extensions.get(key, "") or "") != value
        ]
        if mismatches:
            return (
                f"The installed CRIG {Path(crig_path).name!r} does not match the model's current "
                "authored MSH contract (mismatched " + ", ".join(mismatches) + "). Rebuild/install "
                "the generated CRIG and retry; no animation target was changed."
            )
        return ""

    def _use_selected_generated_rig(self) -> None:
        entry = self._selected_entry()
        if (
            entry is None
            or not entry.installed_crig_ref
            or entry.installed_crig_path is None
        ):
            self._message(
                "No generated rig",
                "Build and install a CRIG for the selected model first.",
            )
            return
        handoff_error = self._generated_rig_handoff_error(entry)
        if handoff_error:
            self._message(
                "Generated rig is stale or unverifiable",
                handoff_error,
                critical=True,
            )
            return
        if self.rigs_installed_callback is not None:
            self.rigs_installed_callback([entry])

    def _selected_entry(self) -> ModelEntry | None:
        rows = sorted({index.row() for index in self.model_table.selectedIndexes()})
        if not rows and self.entries:
            rows = [0]
        return self.entries[rows[0]] if rows and 0 <= rows[0] < len(self.entries) else None

    def _show_selected_details(self) -> None:
        entry = self._selected_entry()
        if entry is None:
            self.details.clear()
            return
        if entry.inventory is None:
            self.details.setPlainText(f"{entry.path}\n\n{entry.status}")
            return
        inv = entry.inventory
        report = entry.build_report if isinstance(entry.build_report, dict) else {}
        geometries = [
            row for row in inv.get("geometries", ()) if isinstance(row, dict)
        ]
        source_triangles = sum(int(row.get("triangle_count", 0)) for row in geometries)
        contract = report.get("authored_rig_contract")
        contract = contract if isinstance(contract, dict) else {}
        contract_nodes = [
            row for row in contract.get("nodes", ()) if isinstance(row, dict)
        ]
        animation_prefix = int(
            contract.get("animation_entity_prefix_length", len(contract_nodes)) or 0
        )
        root_names = [
            str(contract_nodes[index].get("name", index))
            for index in contract.get("roots", ())
            if (
                isinstance(index, int)
                and 0 <= index < len(contract_nodes)
                and index < animation_prefix
            )
        ] or [str(row) for row in inv.get("armature_roots", ())]
        partition = report.get("skin_partitions")
        partition = partition if isinstance(partition, dict) else {}
        compatibility = report.get("humanoid_bind_compatibility")
        if isinstance(compatibility, dict):
            compatibility_text = str(compatibility.get("status", "review"))
        elif contract:
            validation = report.get("authored_rig_validation")
            validation_status = (
                str(validation.get("status", "recorded"))
                if isinstance(validation, dict)
                else "recorded"
            )
            compatibility_text = f"authored MSH/CRIG bind contract {validation_status}"
        elif str(report.get("effective_mode", inv.get("detected_mode", ""))) == "static":
            compatibility_text = "not applicable (static model)"
        else:
            compatibility_text = "not built"
        axis_text = json.dumps(inv.get("axis_settings", {}), sort_keys=True)
        generated_path = entry.installed_crig_path or entry.crig_path
        coordinate = report.get("coordinate_contract")
        coordinate = coordinate if isinstance(coordinate, dict) else {}
        resolved_orientation = str(
            coordinate.get("resolved_orientation_policy", entry.orientation_policy)
        )
        lines = [
            entry.path, "",
            f"Detected mode: {inv.get('detected_mode')}",
            f"FBX version: {inv.get('fbx_version')}",
            f"Scene units (meters/unit): {inv.get('meters_per_unit')}",
            f"Scene orientation metadata: {axis_text}",
            f"Resolved output orientation: {resolved_orientation}",
            f"Mesh geometries: {inv.get('mesh_geometry_count')}",
            f"Source triangles: {source_triangles}",
            f"Emitted vertices: {report.get('total_vertices', '—')}",
            f"Emitted triangles: {report.get('total_triangles', '—')}",
            f"Hierarchy nodes: {report.get('total_hierarchy_node_count', inv.get('limb_node_count'))}",
            f"Deform bones: {report.get('bone_count', inv.get('weighted_bone_count'))}",
            f"Helper bones: {report.get('helper_count', '—')}",
            f"Hierarchy roots: {', '.join(root_names) if root_names else '—'}",
            f"Materials: {inv.get('material_count')}",
            f"Skin partitions: {partition.get('partition_count', '—')}",
            f"Maximum subset palette: {partition.get('maximum_local_palette_size', '—')} / 256 local entries",
            f"Generated CRIG ref: {entry.installed_crig_ref or '—'}",
            f"Generated CRIG path: {generated_path or '—'}",
            f"Animation compatibility: {compatibility_text}",
            "",
        ]
        for warning in inv.get("warnings", []):
            lines.append(f"WARNING: {warning}")
        if entry.mode == "dying_light_humanoid":
            lines.extend(
                (
                    "WARNING: This model uses DL1-style names but a model-specific bind pose.",
                    "Use the generated CRIG to retarget stock or custom animations.",
                    "Do not attach raw stock animation tracks directly.",
                )
            )
        if isinstance(entry.build_report, dict):
            for warning in entry.build_report.get("warnings", ()) or ():
                rendered = f"WARNING: {warning}"
                if rendered not in lines:
                    lines.append(rendered)
        self.details.setPlainText("\n".join(lines))

    # ------------------------------------------------------------------ model mapping
    def _refresh_mapping_model_combo(self) -> None:
        current = self.mapping_model_combo.currentData()
        self.mapping_model_combo.blockSignals(True)
        self.mapping_model_combo.clear()
        for entry in self.entries:
            if entry.mode == "dying_light_humanoid":
                self.mapping_model_combo.addItem(entry.resource_name, entry.path)
        index = self.mapping_model_combo.findData(current)
        self.mapping_model_combo.setCurrentIndex(index if index >= 0 else 0)
        self.mapping_model_combo.blockSignals(False)
        self._refresh_model_mapping_table()

    def _mapping_entry(self) -> ModelEntry | None:
        path = self.mapping_model_combo.currentData()
        return next((entry for entry in self.entries if entry.path == path), None)

    def _target_smd_nodes(self):
        path = Path(self.target_smd.text())
        return list(SmdFile.from_path(path).nodes) if path.is_file() else []

    def auto_map_model(self) -> None:
        entry = self._mapping_entry()
        if entry is None:
            self._message("No humanoid model", "Choose Dying Light humanoid mode for a model first.")
            return
        if (
            entry.scene is None
            or getattr(
                entry.scene,
                "import_tolerance",
                self._import_tolerance_value(),
            )
            != self._import_tolerance_value()
        ):
            entry.scene = FbxScene.from_path(
                entry.path,
                purpose="model",
                tolerance=self._import_tolerance_value(),
            )
            entry.inventory = entry.scene.inventory()
        preflight = preflight_fbx(
            entry.path,
            purpose="model",
            scene=entry.scene,
            tolerance=self._import_tolerance_value(),
        )
        preflight.require_buildable()
        entry.inventory = dict(entry.inventory or {})
        entry.inventory["preflight"] = preflight.to_dict()
        nodes = self._target_smd_nodes()
        if not nodes:
            self._message("Target SMD missing", "Choose a valid player_1_tpp target SMD.", critical=True)
            return
        weighted = {
            cluster.bone_id
            for geometry in entry.scene.geometries
            for cluster in geometry.clusters
            if cluster.bone_id is not None
        }
        source_ids = entry.scene.depth_first_bones_for_weighted_ids(weighted)
        usage = source_skin_weight_usage(entry.scene, source_ids)
        mapping, report = humanoid_bone_mapping(
            entry.scene,
            source_ids,
            nodes,
            source_weight_totals=usage["bone_weight_totals"],
        )
        entry.humanoid_bone_map = {
            entry.scene.model_names[bone_id]: nodes[target].name
            for bone_id, target in mapping.items()
            if target is not None
        }
        entry.extensions["humanoid_mapping_review_v1"] = {
            entry.scene.model_names[bone_id]: "automatic_unreviewed"
            for bone_id in source_ids
        }
        entry.status = f"Mapped {report['directly_mapped_count']}/{report['source_bone_count']} source bones"
        self._changed()
        self._refresh_model_mapping_table()
        self._refresh_table()

    def clear_model_mapping(self) -> None:
        entry = self._mapping_entry()
        if entry:
            entry.humanoid_bone_map.clear()
            entry.extensions.pop("humanoid_mapping_review_v1", None)
            self._changed()
            self._refresh_model_mapping_table()
            self._refresh_table()

    def reset_safe_model_suggestions(self) -> None:
        """Reset only high-confidence rows; retain ambiguous user decisions."""

        entry = self._mapping_entry()
        if entry is None:
            return
        review_states = entry.extensions.get("humanoid_mapping_review_v1")
        states = dict(review_states) if isinstance(review_states, dict) else {}
        changed = False
        for row in range(self.model_mapping_table.rowCount()):
            source_item = self.model_mapping_table.item(row, 0)
            auto_item = self.model_mapping_table.item(row, 3)
            confidence_item = self.model_mapping_table.item(row, 7)
            review_item = self.model_mapping_table.item(row, 8)
            if not all((source_item, auto_item, confidence_item, review_item)):
                continue
            source_name = source_item.text()
            confidence = float(confidence_item.data(self.qt["Qt"].UserRole) or 0.0)
            review_required = bool(review_item.data(self.qt["Qt"].UserRole))
            safe = (
                auto_item.text() != "Unmapped"
                and confidence >= 0.95
                and not review_required
                and review_item.text() != "Intentionally unmapped"
            )
            if safe and source_name in entry.humanoid_bone_map:
                entry.humanoid_bone_map.pop(source_name, None)
                states.pop(source_name, None)
                changed = True
        if changed:
            if states:
                entry.extensions["humanoid_mapping_review_v1"] = states
            else:
                entry.extensions.pop("humanoid_mapping_review_v1", None)
            self._changed()
            self._refresh_model_mapping_table()
            self._refresh_table()

    def _refresh_model_mapping_table(self) -> None:
        qt = self.qt
        entry = self._mapping_entry()
        if entry is None:
            self.model_mapping_table.setRowCount(0)
            self.mapping_note.setText("No model currently uses Dying Light Humanoid mode.")
            return
        try:
            if (
                entry.scene is None
                or getattr(
                    entry.scene,
                    "import_tolerance",
                    self._import_tolerance_value(),
                )
                != self._import_tolerance_value()
            ):
                entry.scene = FbxScene.from_path(
                    entry.path,
                    purpose="model",
                    tolerance=self._import_tolerance_value(),
                )
                entry.inventory = entry.scene.inventory()
            nodes = self._target_smd_nodes()
            if not nodes:
                raise FileNotFoundError("Target SMD is missing")
            weighted = {
                cluster.bone_id
                for geometry in entry.scene.geometries
                for cluster in geometry.clusters
                if cluster.bone_id is not None
            }
            source_ids = entry.scene.depth_first_bones_for_weighted_ids(weighted)
            usage = source_skin_weight_usage(entry.scene, source_ids)
            auto, report = humanoid_bone_mapping(
                entry.scene,
                source_ids,
                nodes,
                source_weight_totals=usage["bone_weight_totals"],
            )
            final, final_report = humanoid_bone_mapping(
                entry.scene,
                source_ids,
                nodes,
                manual_mapping=entry.humanoid_bone_map,
                source_weight_totals=usage["bone_weight_totals"],
            )
        except Exception as exc:
            self.model_mapping_table.setRowCount(0)
            self.mapping_note.setText(str(exc))
            return
        target_names = [node.name for node in nodes]
        auto_rows = {
            str(row.get("source_bone")): row for row in report.get("rows", ())
        }
        final_rows = {
            str(row.get("source_bone")): row
            for row in final_report.get("rows", ())
        }
        total_weight = float(usage.get("total_normalized_weight", 0.0) or 0.0)
        review_payload = entry.extensions.get("humanoid_mapping_review_v1")
        review_states = (
            dict(review_payload) if isinstance(review_payload, dict) else {}
        )
        self.model_mapping_table.setRowCount(len(source_ids))
        for row, bone_id in enumerate(source_ids):
            source_name = entry.scene.model_names[bone_id]
            auto_index = auto.get(bone_id)
            auto_name = nodes[auto_index].name if auto_index is not None else ""
            final_index = final.get(bone_id)
            final_name = nodes[final_index].name if final_index is not None else ""
            auto_row = auto_rows.get(source_name, {})
            final_row = final_rows.get(source_name, {})
            source_weight = float(
                usage.get("bone_weight_totals", {}).get(bone_id, 0.0) or 0.0
            )
            weight_fraction = source_weight / total_weight if total_weight > 0.0 else 0.0
            confidence = float(auto_row.get("confidence", 0.0) or 0.0)
            manual = source_name in entry.humanoid_bone_map
            state = str(
                review_states.get(
                    source_name,
                    "manually_reviewed" if manual else "automatic_unreviewed",
                )
            )
            manual_mismatch = bool(final_row.get("manual_role_mismatch", False))
            review_required = bool(
                manual_mismatch
                or (
                    state == "automatic_unreviewed"
                    and (not auto_name or confidence < 0.95)
                )
            )
            source_item = qt["QTableWidgetItem"](source_name)
            source_item.setData(qt["Qt"].UserRole, source_name)
            self.model_mapping_table.setItem(row, 0, source_item)
            self.model_mapping_table.setItem(
                row, 1, qt["QTableWidgetItem"](f"{weight_fraction:.2%}")
            )
            self.model_mapping_table.setItem(
                row,
                2,
                qt["QTableWidgetItem"](str(auto_row.get("role") or "Unclassified")),
            )
            self.model_mapping_table.setItem(
                row, 3, qt["QTableWidgetItem"](auto_name or "Unmapped")
            )
            combo = qt["QComboBox"]()
            combo.addItem("Unmapped", "")
            for target in target_names:
                combo.addItem(target, target)
            combo.setCurrentIndex(max(0, combo.findData(final_name)))
            combo.currentIndexChanged.connect(
                lambda _value, source=source_name, widget=combo: self._set_model_map_row(source, str(widget.currentData()))
            )
            self.model_mapping_table.setCellWidget(row, 4, combo)
            self.model_mapping_table.setItem(
                row, 5, qt["QTableWidgetItem"]("Skin-weight transfer")
            )
            self.model_mapping_table.setItem(
                row, 6, qt["QTableWidgetItem"]("Authored bind T/R/S")
            )
            confidence_item = qt["QTableWidgetItem"](f"{confidence:.0%}")
            confidence_item.setData(qt["Qt"].UserRole, confidence)
            self.model_mapping_table.setItem(row, 7, confidence_item)
            if state == "intentionally_unmapped":
                review_text = "Intentionally unmapped"
            elif state == "manually_reviewed":
                review_text = "Manual reviewed"
            elif review_required:
                review_text = "Automatic - review"
            else:
                review_text = "Automatic safe"
            review_item = qt["QTableWidgetItem"](review_text)
            review_item.setData(qt["Qt"].UserRole, review_required)
            self.model_mapping_table.setItem(row, 8, review_item)
            self.model_mapping_table.setItem(
                row,
                9,
                qt["QTableWidgetItem"](str(final_row.get("method") or "unmapped")),
            )
            status = (
                "Review role mismatch"
                if manual_mismatch
                else "Intentionally unmapped"
                if state == "intentionally_unmapped"
                else "Unmapped; ancestor/root fallback at build"
                if not final_name
                else "Review required"
                if review_required
                else "Ready"
            )
            self.model_mapping_table.setItem(
                row, 10, qt["QTableWidgetItem"](status)
            )
        self._apply_model_mapping_filter()
        self.mapping_note.setText(
            f"Auto mapped {report['directly_mapped_count']} of {report['source_bone_count']} source bones. "
            "Source weight is normalized over emitted skin corners. Automatic rows below 95% "
            "confidence remain visible for review; final target choices are saved with the project."
        )

    def _apply_model_mapping_filter(self) -> None:
        ambiguous_only = bool(
            hasattr(self, "review_ambiguous_button")
            and self.review_ambiguous_button.isChecked()
        )
        for row in range(self.model_mapping_table.rowCount()):
            review = self.model_mapping_table.item(row, 8)
            required = bool(
                review is not None
                and review.data(self.qt["Qt"].UserRole)
            )
            self.model_mapping_table.setRowHidden(
                row, ambiguous_only and not required
            )

    def _set_model_map_row(self, source_name: str, target_name: str) -> None:
        entry = self._mapping_entry()
        if entry is None:
            return
        # An explicit empty string is meaningful: it intentionally suppresses
        # an otherwise automatic mapping. Clear manual overrides returns rows
        # to automatic behavior.
        entry.humanoid_bone_map[source_name] = target_name
        payload = entry.extensions.get("humanoid_mapping_review_v1")
        states = dict(payload) if isinstance(payload, dict) else {}
        states[source_name] = (
            "manually_reviewed" if target_name else "intentionally_unmapped"
        )
        entry.extensions["humanoid_mapping_review_v1"] = states
        self._changed()
        self._refresh_model_mapping_table()
        self._refresh_table()

    # ------------------------------------------------------------------ build
    def build_sources(self) -> None:
        self._capture_all_table_state()
        rows = [entry for entry in self.entries if entry.enabled]
        if not rows:
            self._message("No models", "Add and enable at least one model.")
            return
        jobs = [self._job_copy(entry) for entry in rows]
        config = self._build_config()
        self.tabs.setCurrentIndex(2)
        self._set_busy(True)
        self.progress.setRange(0, 0)

        def work(progress):
            for entry in jobs:
                self._build_entry_for_job(entry, config, progress)
            return jobs

        if not self.background_tasks.start(
            work,
            progress=self._append_log,
            succeeded=self._model_jobs_succeeded,
            failed=lambda failure: self._model_job_failed("Model build failed", failure),
            finished=self._model_jobs_finished,
        ):
            self._model_jobs_finished()
            self.status_callback("Another model task is already running.")

    def _build_entry(self, entry: ModelEntry) -> None:
        self._build_entry_for_job(entry, self._build_config(), self._append_log)

    def _build_config(self) -> dict[str, Any]:
        return {
            "output_path": self.output_path.text(),
            "material_mode": str(self.material_mode.currentData()),
            "import_tolerance": self._import_tolerance_value(),
            "test_material": self.test_material.text().strip(),
            "surface_name": self.surface_name.text().strip(),
            "flip_v": self.flip_v.isChecked(),
            "retain_skeleton": self.retain_skeleton.isChecked(),
            "create_crig": self.create_crig.isChecked(),
            "animation_script": self.animation_script.text().strip(),
            "target_smd": self.target_smd.text().strip(),
        }

    @staticmethod
    def _job_copy(entry: ModelEntry) -> ModelEntry:
        result = ModelEntry.from_dict(entry.to_dict())
        result.inventory = deepcopy(entry.inventory)
        result.build_report = deepcopy(entry.build_report)
        result.source_msh = entry.source_msh
        result.crig_path = entry.crig_path
        result.installed_crig_ref = entry.installed_crig_ref
        result.installed_crig_path = entry.installed_crig_path
        result.status = entry.status
        return result

    def _build_entry_for_job(self, entry: ModelEntry, config: dict[str, Any], progress) -> None:
        tolerance = str(config.get("import_tolerance", "recommended"))
        if (
            entry.scene is None
            or getattr(entry.scene, "import_tolerance", tolerance) != tolerance
        ):
            entry.scene = FbxScene.from_path(
                entry.path,
                purpose="model",
                tolerance=tolerance,
            )
            entry.inventory = entry.scene.inventory()
        model_preflight = preflight_fbx(
            entry.path,
            purpose="model",
            scene=entry.scene,
            tolerance=tolerance,
        )
        model_preflight.require_buildable()
        if entry.inventory is None:
            entry.inventory = entry.scene.inventory()
        entry.inventory["fbx_preflight"] = model_preflight.to_dict()
        script = str(config["animation_script"])
        options = ModelBuildOptions(
            resource_name=entry.resource_name,
            mode=entry.mode,
            material_mode=str(config["material_mode"]),
            test_material=str(config["test_material"]),
            surface_name=str(config["surface_name"]),
            flip_v=bool(config["flip_v"]),
            retain_full_skeleton=bool(config["retain_skeleton"]),
            animation_script=script,
            target_smd=str(config["target_smd"]),
            preserve_helpers=True,
            orientation_policy=entry.orientation_policy,
            humanoid_bone_map=dict(entry.humanoid_bone_map),
        )
        output = Path(str(config["output_path"])).expanduser() / "sources" / entry.resource_name
        progress(f"Building {entry.resource_name} ({entry.mode}, {entry.orientation_policy})…")
        result = build_source_from_fbx(entry.scene, options)
        result.report["fbx_preflight"] = model_preflight.to_dict()
        crig_payload: bytes | None = None
        crig_report: dict[str, Any] | None = None
        if bool(config["create_crig"]) and result.report["effective_mode"] != "static":
            if result.authored_rig_contract is None:
                raise ValueError(
                    f"Model {entry.resource_name!r} did not produce an authored rig contract; "
                    "no MSH or CRIG was written because their bind identity could not be proven. "
                    "Re-analyze the FBX in Exact original FBX rig mode; Exact Rig is viable only "
                    "when the report contains a complete authored animation hierarchy."
                )
            try:
                crig_payload, crig_report = build_crig_from_rig_contract_bytes(
                    result.authored_rig_contract,
                    name=entry.resource_name,
                )
            except Exception as exc:
                raise ValueError(
                    f"Model {entry.resource_name!r} cannot generate a CRIG from the exact authored "
                    f"MSH hierarchy: {exc} No MSH or CRIG was written. Apply/freeze the named "
                    "unsupported bone transform and re-export; Exact Rig remains viable after its "
                    "bind is representable as finite Chrome TRS."
                ) from exc
        paths = result.write(output)
        entry.source_msh = paths["msh"]
        entry.build_report = json.loads(paths["report"].read_text(encoding="utf-8"))
        if crig_payload is not None and crig_report is not None:
            assert result.authored_rig_contract is not None
            entry.crig_path, crig_report = write_prebuilt_crig_payload(
                crig_payload,
                crig_report,
                output / f"{entry.resource_name}.crig",
            )
            (output / f"{entry.resource_name}.crig_build.json").write_text(
                json.dumps(crig_report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
            )
            try:
                from ..chrome_rig_registry import ChromeRigRegistry
                from ..runtime_paths import writable_application_root
                record = ChromeRigRegistry(
                    writable_application_root() / "rigs"
                ).import_rig(entry.crig_path)
                entry.installed_crig_ref = record.rig_ref
                entry.installed_crig_path = Path(record.path)
                progress(
                    f"Installed .crig target {record.rig_ref}: {record.path}"
                )
            except Exception as exc:
                progress(f".crig built but not installed automatically: {exc}")
            resolved_ref = entry.installed_crig_ref or str(crig_report.get("rig_id", ""))
            resolved_path = entry.installed_crig_path or entry.crig_path
            result.authored_rig_contract = result.authored_rig_contract.with_generated_crig(
                rig_ref=resolved_ref,
                path=str(resolved_path or ""),
            )
            entry.build_report["authored_rig_contract"] = (
                result.authored_rig_contract.to_dict()
            )
            entry.build_report["generated_crig"] = {
                "rig_ref": resolved_ref,
                "path": str(resolved_path or ""),
                "bind_hash": result.authored_rig_contract.bind_hash,
                "contract_id": result.authored_rig_contract.contract_id,
                "skeleton_hash": result.authored_rig_contract.skeleton_hash,
                "descriptor_hash": result.authored_rig_contract.descriptor_hash,
                "skeleton_sha256": str(crig_report.get("skeleton_sha256", "")),
            }
            paths["report"].write_text(
                json.dumps(entry.build_report, indent=2, ensure_ascii=False) + "\n",
                encoding="utf-8",
            )
        entry.status = "Source built; expected editor rotation = identity"
        fitted = result.report.get("humanoid_fitted_bind")
        if isinstance(fitted, dict):
            progress(
                "WARNING: This model uses DL1-style names but a model-specific bind pose. "
                "Use the generated CRIG to retarget animations; do not attach raw stock "
                "animation tracks directly."
            )
            progress(
                "Humanoid geometry pivot fit: "
                f"{int(fitted.get('anchor_count', 0))} FBX pivot anchors, "
                f"{int(fitted.get('interpolated_hierarchy_node_count', 0))} "
                "interpolated hierarchy nodes, "
                f"mean weighted pivot error "
                f"{float(fitted.get('weighted_mean_pivot_distance_m', 0.0)) * 100.0:.2f} cm"
            )
        bone_bounds = result.report.get("bone_bounds")
        if isinstance(bone_bounds, dict):
            progress(
                "Bone bounds: "
                f"{int(bone_bounds.get('nonzero_bound_count', 0))}/"
                f"{int(bone_bounds.get('bone_count', 0))} usable; "
                f"aggregate diagonal "
                f"{float(bone_bounds.get('aggregate_model_diagonal_m', 0.0)):.3f} m"
            )
        progress(
            f"Built {entry.source_msh}: {result.report['total_vertices']} vertices, "
            f"{result.report['total_triangles']} triangles, {result.report.get('bone_count', 0)} bones"
        )

    def compile_and_install(self) -> None:
        self._capture_all_table_state()
        rows = [entry for entry in self.entries if entry.enabled]
        if not rows:
            self._message("No models", "Add and enable at least one model.")
            return
        try:
            settings = self._compiler_settings()
            settings.validate()
        except Exception as exc:
            self.tabs.setCurrentIndex(3)
            self._message("DevTools paths are not ready", str(exc), critical=True)
            return
        jobs = [self._job_copy(entry) for entry in rows]
        config = self._build_config()
        self.tabs.setCurrentIndex(2)
        self._set_busy(True)
        self.progress.setRange(0, 0)

        def work(progress):
            for entry in jobs:
                if entry.source_msh is None or not entry.source_msh.is_file():
                    self._build_entry_for_job(entry, config, progress)
                assert entry.source_msh is not None and entry.build_report is not None
                compile_and_install_model(
                    source_msh=entry.source_msh,
                    source_report=entry.build_report,
                    settings=settings,
                    log_callback=progress,
                )
                entry.status = "Compiled and installed; test at rotation 0,0,0"
            return jobs

        if not self.background_tasks.start(
            work,
            progress=self._append_log,
            succeeded=self._model_jobs_succeeded,
            failed=lambda failure: self._model_job_failed("Compile/install failed", failure),
            finished=self._model_jobs_finished,
        ):
            self._model_jobs_finished()
            self.status_callback("Another model task is already running.")

    # ------------------------------------------------------------------ DevTools/settings
    def _compiler_settings(self) -> CompilerSettings:
        return CompilerSettings(
            compiler=self.compiler_path.text().strip(),
            data0_pak=self.data0_path.text().strip(),
            workshop_root=self.workshop_path.text().strip(),
            active_project=self.active_project_path.text().strip(),
            output_directory=str(Path(self.output_path.text()).expanduser() / "compiled"),
            devtools_data_directory=self.devtools_data_path.text().strip(),
        )

    def validate_paths(self) -> None:
        try:
            self._compiler_settings().validate()
        except Exception as exc:
            self._message("Path validation failed", str(exc), critical=True)
        else:
            self._message("Paths valid", "Compiler, game data, workshop and active project are ready.")

    def auto_detect_paths(self) -> None:
        compiler_candidates: list[Path] = []
        data_candidates: list[Path] = []
        for letter in string.ascii_uppercase:
            root = Path(f"{letter}:/")
            compiler_candidates.extend((
                root / "SteamLibrary/steamapps/common/Dying Light Developer Tools/ResPackCompilerConsole_x64_rwdi.exe",
                root / "Program Files (x86)/Steam/steamapps/common/Dying Light Developer Tools/ResPackCompilerConsole_x64_rwdi.exe",
            ))
            data_candidates.extend((
                root / "SteamLibrary/steamapps/common/Dying Light/DW/Data0.pak",
                root / "Program Files (x86)/Steam/steamapps/common/Dying Light/DW/Data0.pak",
            ))
        if not self.compiler_path.text():
            found = next((row for row in compiler_candidates if row.is_file()), None)
            if found:
                self.compiler_path.setText(str(found))
        if not self.data0_path.text():
            found = next((row for row in data_candidates if row.is_file()), None)
            if found:
                self.data0_path.setText(str(found))
        if self.data0_path.text() and not self.workshop_path.text():
            data0 = Path(self.data0_path.text())
            candidate = data0.parent.parent / "DevTools" / "workshop"
            if candidate.is_dir():
                self.workshop_path.setText(str(candidate))
        if self.compiler_path.text() and not self.devtools_data_path.text():
            candidate = Path(self.compiler_path.text()).parent / "Engine" / "Data"
            if candidate.is_dir():
                self.devtools_data_path.setText(str(candidate))
        self._save_settings()

    def _settings_payload(self) -> dict[str, Any]:
        return {
            "output": self.output_path.text(),
            "compiler": self.compiler_path.text(),
            "data0": self.data0_path.text(),
            "workshop": self.workshop_path.text(),
            "active_project": self.active_project_path.text(),
            "devtools_data": self.devtools_data_path.text(),
            "target_smd": self.target_smd.text(),
            "test_material": self.test_material.text(),
            "surface": self.surface_name.text(),
            "material_mode": str(self.material_mode.currentData()),
            "import_tolerance": self._import_tolerance_value(),
            "retain_skeleton": self.retain_skeleton.isChecked(),
            "create_crig": self.create_crig.isChecked(),
            "flip_v": self.flip_v.isChecked(),
            "animation_script": self.animation_script.text(),
        }

    def _apply_settings_payload(self, config: dict[str, Any]) -> None:
        for widget, key in (
            (self.output_path, "output"),
            (self.compiler_path, "compiler"),
            (self.data0_path, "data0"),
            (self.workshop_path, "workshop"),
            (self.active_project_path, "active_project"),
            (self.devtools_data_path, "devtools_data"),
            (self.target_smd, "target_smd"),
            (self.test_material, "test_material"),
            (self.surface_name, "surface"),
            (self.animation_script, "animation_script"),
        ):
            if key in config:
                widget.setText(str(config[key]))
        if "material_mode" in config:
            index = self.material_mode.findData(str(config["material_mode"]))
            if index >= 0:
                self.material_mode.setCurrentIndex(index)
        if "import_tolerance" in config:
            index = self.import_tolerance.findData(str(config["import_tolerance"]))
            if index >= 0:
                self.import_tolerance.setCurrentIndex(index)
        for widget, key in (
            (self.retain_skeleton, "retain_skeleton"),
            (self.create_crig, "create_crig"),
            (self.flip_v, "flip_v"),
        ):
            if key in config:
                widget.setChecked(bool(config[key]))

    def _load_settings(self) -> None:
        defaults = {
            "output": str(Path.home() / "Documents" / "DL ReAnimated" / "Models"),
            "compiler": "",
            "data0": "",
            "workshop": "",
            "active_project": "",
            "devtools_data": "",
            "target_smd": self.target_smd.text(),
            "test_material": "bottle_trash_a.mat",
            "surface": "Flesh",
            "material_mode": "test",
            "import_tolerance": FbxImportTolerance.RECOMMENDED.value,
            "retain_skeleton": True,
            "create_crig": True,
            "flip_v": False,
            "animation_script": "",
        }
        payload = {
            key: self.settings.value(key, value, type=bool) if isinstance(value, bool)
            else self.settings.value(key, value)
            for key, value in defaults.items()
        }
        self._apply_settings_payload(payload)
        self.auto_detect_paths()

    def _save_settings(self) -> None:
        for key, value in self._settings_payload().items():
            self.settings.setValue(key, value)

    def _import_tolerance_value(self) -> str:
        return FbxImportTolerance.coerce(
            str(self.import_tolerance.currentData() or "recommended")
        ).value

    # ------------------------------------------------------------------ helpers
    def _changed(self, *_args) -> None:
        if self._initializing:
            return
        self._save_settings()
        self.mark_dirty()

    def _set_busy(self, value: bool) -> None:
        self.busy = bool(value)
        self.build_source_button.setEnabled(not value)
        self.install_button.setEnabled(not value)
        if hasattr(self, "use_generated_rig_button"):
            selected = self._selected_entry()
            self.use_generated_rig_button.setEnabled(
                bool(not value and selected and selected.installed_crig_ref)
            )
        self.status_callback("Working…" if value else "Ready")

    def _append_log(self, message: str) -> None:
        self.log.appendPlainText(str(message))
        self.log.verticalScrollBar().setValue(self.log.verticalScrollBar().maximum())

    def _model_jobs_succeeded(self, results: list[ModelEntry]) -> None:
        current = {entry.path: entry for entry in self.entries}
        for result in results:
            entry = current.get(result.path)
            if entry is None:
                continue
            entry.scene = result.scene
            entry.inventory = result.inventory
            entry.build_report = result.build_report
            entry.source_msh = result.source_msh
            entry.crig_path = result.crig_path
            entry.installed_crig_ref = result.installed_crig_ref
            entry.installed_crig_path = result.installed_crig_path
            entry.status = result.status
        self.progress.setRange(0, max(1, len(results)))
        self.progress.setValue(len(results))
        self._refresh_table()
        self._refresh_mapping_model_combo()
        installed = [row for row in results if row.installed_crig_ref]
        if installed and self.rigs_installed_callback is not None:
            self.rigs_installed_callback(installed)

    def _model_job_failed(self, title: str, failure: TaskFailure) -> None:
        message = failure.display_message(False)
        self._append_log(message)
        self._message(title, message, critical=True)

    def _model_jobs_finished(self) -> None:
        self._set_busy(False)
        if self.progress.maximum() == 0:
            self.progress.setRange(0, 1)
            self.progress.setValue(0)

    def _message(self, title: str, text: str, *, critical: bool = False) -> None:
        box = self.qt["QMessageBox"]
        if critical:
            box.critical(self.parent_window, title, text)
        else:
            box.information(self.parent_window, title, text)


__all__ = ["MODEL_WORKSPACE_EXTENSION_KEY", "ModelEntry", "ModelWorkspace"]
