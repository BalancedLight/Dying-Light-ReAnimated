"""Release-oriented PySide6 project GUI.

The interface is intentionally backed by the versioned project, retarget-profile,
and project-builder modules.  Widgets do not own build logic, which keeps the
GUI replaceable and makes later versions able to migrate old projects.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import sys
from typing import Any

from . import __version__
from .chrome_rig import ChromeRig
from .chrome_rig_builder import build_chrome_rig_from_fbx
from .chrome_rig_registry import BUILTIN_MALE_RIG_REF, ChromeRigRegistry
from .oracle.binary_fbx_mixamo import _FbxDocument
from .project_builder import build_project, export_project_anm2_files
from .retarget_profiles import (
    HUMANOID_ROLES,
    ROLE_BY_ID,
    SourceBoneMappingProfile,
    auto_map_source_bones,
)
from .script_targets import (
    AnimationScriptTarget,
    BUILTIN_SCRIPT_TARGETS,
    DEFAULT_SCRIPT_TARGET_ID,
    ScriptTargetRegistry,
)
from .runtime_paths import resource_root, writable_application_root
from .workspace_project import (
    DlReanimatedProject,
    PROJECT_EXTENSION,
    ProjectAnimation,
)


def _load_qt() -> dict[str, Any]:
    try:
        from PySide6.QtCore import QSettings, QUrl, Qt
        from PySide6.QtGui import QAction, QDesktopServices, QKeySequence
        from PySide6.QtWidgets import (
            QAbstractItemView,
            QApplication,
            QButtonGroup,
            QCheckBox,
            QComboBox,
            QDialog,
            QDialogButtonBox,
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
        QLineEdit, QComboBox, QSpinBox { min-height: 28px; }
        QTableWidget { gridline-color: #d6d6d6; alternate-background-color: #f6f8fa; }
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
                if controller._confirm_discard_changes():
                    event.accept()
                else:
                    event.ignore()

        self.window = _ProjectWindow()
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
        self._source_cache: dict[str, _FbxDocument] = {}
        self.script_registry = ScriptTargetRegistry()
        self.rig_registry = ChromeRigRegistry(self.root / "rigs")

        self._build_toolbar()
        self._build_project_tab()
        self._build_animations_tab()
        self._build_retarget_tab()
        self._build_export_tab()
        self._build_help_tab()
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
        project.rig.trusted_source_rest_json = str(
            self.resource_root / "reference" / "same_model_tpose_20260619.json"
        )
        project.export.output_directory = str(self.root / "build")
        project.export.default_script_target = DEFAULT_SCRIPT_TARGET_ID
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
        rig_form.addRow("Target rig preset", self.target_rig_combo)

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
        add_button.setToolTip("Import one or more FBX animation files into this project.")
        add_button.clicked.connect(self.add_animations)
        remove_button = qt["QPushButton"]("Remove selected")
        remove_button.setToolTip("Remove the selected animation from the project only.")
        remove_button.clicked.connect(self.remove_selected_animation)
        duplicate_button = qt["QPushButton"]("Duplicate selected")
        duplicate_button.setToolTip(
            "Create another project entry for the same FBX, useful for alternate root-motion "
            "or script-target versions."
        )
        duplicate_button.clicked.connect(self.duplicate_selected_animation)
        row.addWidget(add_button)
        row.addWidget(remove_button)
        row.addWidget(duplicate_button)
        row.addStretch(1)
        layout.addLayout(row)

        self.animation_table = qt["QTableWidget"](0, 9)
        self.animation_table.setHorizontalHeaderLabels(
            [
                "Use",
                "Display name",
                "FBX source",
                "FBX animation",
                "Resource name",
                "Animation SCR",
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
        header.setSectionResizeMode(8, qt["QHeaderView"].ResizeToContents)
        self.animation_table.setColumnWidth(1, 210)
        self.animation_table.setColumnWidth(3, 190)
        self.animation_table.setColumnWidth(4, 190)
        self.animation_table.setColumnWidth(5, 210)
        self.animation_table.setColumnWidth(6, 155)
        self.animation_table.setColumnWidth(7, 155)
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
        self.fps_spin = qt["QSpinBox"]()
        self.fps_spin.setRange(1, 240)
        self.fps_spin.setToolTip("Playback speed written to the animation-script sequence.")
        self.fps_spin.valueChanged.connect(self._selected_range_changed)
        range_row.addWidget(qt["QLabel"]("Start"))
        range_row.addWidget(self.start_frame_spin)
        range_row.addSpacing(14)
        range_row.addWidget(qt["QLabel"]("End"))
        range_row.addWidget(self.end_frame_spin)
        range_row.addSpacing(14)
        range_row.addWidget(qt["QLabel"]("Playback FPS"))
        range_row.addWidget(self.fps_spin)
        range_row.addStretch(1)
        detail_form.addRow(range_row)
        self.range_note = qt["QLabel"](
            "First/Last uses the complete FBX range. Playback FPS changes sequence speed; "
            "the current FBX sampler remains 30 FPS."
        )
        self.range_note.setWordWrap(True)
        detail_form.addRow(self.range_note)
        layout.addWidget(detail)
        self.tabs.addTab(page, "Animations")


    def _build_retarget_tab(self) -> None:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        layout.setSpacing(8)
        intro = qt["QLabel"](
            "Auto-map handles standard Mixamo and common humanoid names. Review required roles, "
            "then change any incorrect source-bone dropdown manually."
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
        auto_button = qt["QPushButton"]("Auto-map humanoid")
        auto_button.setToolTip(
            "Rebuild the mapping using exact Mixamo names, common aliases, and conservative "
            "humanoid-name heuristics."
        )
        auto_button.clicked.connect(self.auto_map_selected)
        apply_button = qt["QPushButton"]("Apply to compatible clips")
        apply_button.setToolTip(
            "Reuse this mapping on other project clips that have the exact same source skeleton "
            "hash. Clips with different hierarchies are skipped."
        )
        apply_button.clicked.connect(self.apply_mapping_to_compatible_clips)
        actions.addWidget(auto_button)
        actions.addWidget(apply_button)

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

        self.mapping_status = qt["QLabel"]()
        self.mapping_status.setWordWrap(True)
        self.mapping_status.setMaximumHeight(84)
        self.mapping_status.setSizePolicy(
            qt["QSizePolicy"].Preferred, qt["QSizePolicy"].Maximum
        )
        layout.addWidget(self.mapping_status)

        splitter = qt["QSplitter"]()
        splitter.setChildrenCollapsible(False)
        self.mapping_table = qt["QTableWidget"](0, 6)
        self.mapping_table.setHorizontalHeaderLabels(
            ["Group", "Humanoid role", "Source FBX bone", "Required", "Confidence", "Method"]
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
        advanced_form.addRow(self.include_controls)
        advanced_form.addRow(self.keep_anm2)
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
    def new_project(self) -> None:
        if not self._confirm_discard_changes():
            return
        self.project = self._new_default_project()
        self.project_path = None
        self._source_cache.clear()
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
        try:
            self.project = DlReanimatedProject.load(path)
            self.project_path = Path(path)
            self._source_cache.clear()
            self.dirty = False
            self._refresh_all()
            self.status.showMessage(f"Opened {path}", 5000)
        except Exception as exc:
            self._show_error("Could not open project", exc)

    def save_project(self) -> None:
        if self.project_path is None:
            self.save_project_as()
            return
        self._sync_project_from_ui()
        try:
            self.project_path = self.project.save(self.project_path)
            self.dirty = False
            self._update_title()
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

    # --------------------------------------------------------------- animation
    def add_animations(self) -> None:
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
        for raw in paths:
            path = Path(raw).resolve()
            document = self._source_document(str(path))
            stacks = list(document.animation_stacks)
            selections = [stack.name for stack in stacks] or [""]
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
                if multi:
                    row.display_name = f"{path.stem}: {stack_name}"
                prefix = self.project.export.resource_prefix.strip()
                if prefix:
                    row.resource_name = f"{prefix}_{row.resource_name}"
                self.project.animations.append(row)
                existing.add(key)
        self._mark_dirty()
        self._refresh_animation_table()
        self._refresh_retarget_clip_combo()
        if self.project.animations:
            self.animation_table.selectRow(len(self.project.animations) - 1)

    def remove_selected_animation(self) -> None:
        animation = self._selected_animation()
        if animation is None:
            return
        self.project.animations = [
            row for row in self.project.animations if row.animation_id != animation.animation_id
        ]
        self._mark_dirty()
        self._refresh_animation_table()
        self._refresh_retarget_clip_combo()

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
        row.fps = source.fps
        row.start_frame = source.start_frame
        row.end_frame = source.end_frame
        self.project.animations.append(row)
        self._mark_dirty()
        self._refresh_animation_table()
        self._refresh_retarget_clip_combo()
        self.animation_table.selectRow(len(self.project.animations) - 1)

    def _refresh_animation_table(self) -> None:
        qt = self.qt
        self._refreshing = True
        try:
            table = self.animation_table
            table.setRowCount(len(self.project.animations))
            for row_index, animation in enumerate(self.project.animations):
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
                try:
                    stack_names = self._source_document(animation.source_fbx).animation_stack_names
                except Exception:
                    stack_names = ()
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

                root = self._combo_box()
                root.setMinimumHeight(32)
                root.setToolTip(
                    "In place locks motion; Skeletal root writes movement to bip01; Motion accumulator "
                    "splits pose/root motion for consumers that accumulate OffsetHelper motion."
                )
                root.addItem("In place", "inplace")
                root.addItem("Skeletal root (bip01)", "bip01")
                root.addItem("Motion accumulator", "motion")
                self._set_combo_data(root, animation.root_policy)
                root.currentIndexChanged.connect(
                    lambda _index, combo=root, aid=animation.animation_id: self._set_animation_field(
                        aid, "root_policy", combo.currentData()
                    )
                )
                table.setCellWidget(row_index, 6, root)

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
                table.setCellWidget(row_index, 7, ik)

                mapping = qt["QPushButton"](
                    "Edit mapping" if animation.mapping_profile_id else "Create mapping"
                )
                mapping.setMinimumHeight(32)
                mapping.setToolTip(
                    "Open the Retargeting tab for this clip and review source-bone assignments."
                )
                mapping.clicked.connect(
                    lambda _checked=False, aid=animation.animation_id: self._open_mapping_for_animation(aid)
                )
                table.setCellWidget(row_index, 8, mapping)
        finally:
            self._refreshing = False
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
            self._refresh_retarget_clip_combo()

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
            for widget in (self.start_frame_spin, self.end_frame_spin, self.fps_spin):
                widget.setEnabled(enabled)
            if animation is None:
                self.start_frame_spin.setValue(-1)
                self.end_frame_spin.setValue(-1)
                self.fps_spin.setValue(30)
            else:
                self.start_frame_spin.setValue(
                    -1 if animation.start_frame is None else animation.start_frame
                )
                self.end_frame_spin.setValue(
                    -1 if animation.end_frame is None else animation.end_frame
                )
                self.fps_spin.setValue(animation.fps)
        finally:
            self._refreshing = False

    def _selected_range_changed(self) -> None:
        if self._refreshing:
            return
        animation = self._selected_animation()
        if animation is None:
            return
        animation.start_frame = None if self.start_frame_spin.value() < 0 else self.start_frame_spin.value()
        animation.end_frame = None if self.end_frame_spin.value() < 0 else self.end_frame_spin.value()
        animation.fps = self.fps_spin.value()
        self._mark_dirty()

    # --------------------------------------------------------------- retarget
    def _refresh_retarget_clip_combo(self) -> None:
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
        self._retarget_clip_changed()

    def _retarget_clip_changed(self) -> None:
        if self._refreshing:
            return
        if self.project.rig.retarget_mode == "exact":
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
        animation = self.project.animation_by_id(str(self.retarget_clip_combo.currentData() or ""))
        if animation is None:
            self.mapping_table.setRowCount(0)
            self.mapping_status.setText("Add an FBX animation to create a humanoid mapping.")
            self.ignored_bones.clear()
            return
        try:
            document = self._source_document(animation.source_fbx)
            profile = self._profile_for_animation(animation, document, create=True)
            self._refresh_mapping_table(animation, document, profile)
        except Exception as exc:
            self.mapping_table.setRowCount(0)
            self.mapping_status.setText(str(exc))

    def _source_document(self, path: str) -> _FbxDocument:
        resolved = str(Path(path).resolve())
        document = self._source_cache.get(resolved)
        if document is None:
            document = _FbxDocument(Path(resolved))
            self._source_cache[resolved] = document
        return document

    def _profile_for_animation(
        self,
        animation: ProjectAnimation,
        document: _FbxDocument,
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

    def _refresh_mapping_table(
        self,
        animation: ProjectAnimation,
        document: _FbxDocument,
        profile: SourceBoneMappingProfile,
    ) -> None:
        qt = self.qt
        source_bones = sorted(document.limb_models)
        self._refreshing = True
        try:
            self.mapping_table.setRowCount(len(HUMANOID_ROLES))
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
                    "are disabled unless the dropdown is open."
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
                    combo.lineEdit().editingFinished.connect(
                        lambda widget=combo, role_id=role.role_id, aid=animation.animation_id: self._mapping_changed(
                            aid, role_id, widget.currentText()
                        )
                    )
                self.mapping_table.setCellWidget(row_index, 2, combo)
        finally:
            self._refreshing = False
        errors = profile.validate(source_bones)
        mapped = len(profile.role_to_bone)
        required_count = sum(1 for role in HUMANOID_ROLES if role.required)
        required_mapped = sum(
            1 for role in HUMANOID_ROLES if role.required and role.role_id in profile.role_to_bone
        )
        color = "#2e7d32" if not errors else "#b71c1c"
        self.mapping_status.setText(
            f"<b style='color:{color}'>{'Ready' if not errors else 'Needs attention'}</b> — "
            f"{mapped} roles mapped; {required_mapped}/{required_count} required roles. "
            f"Skeleton hash: {profile.source_skeleton_hash[:16]}…"
            + ("<br>" + "<br>".join(errors[:8]) if errors else "")
        )
        self.ignored_bones.setPlainText("\n".join(profile.ignored_bones))
        self._filter_mapping_rows()

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
        self._refresh_mapping_table(animation, document, profile)

    def auto_map_selected(self) -> None:
        if self.project.rig.retarget_mode == "exact":
            return
        animation = self._retarget_animation()
        if animation is None:
            return
        try:
            document = self._source_document(animation.source_fbx)
            profile = auto_map_source_bones(
                document.limb_models,
                parents=document.parent_by_name,
                profile_name=f"Humanoid mapping: {animation.display_name}",
            )
            if animation.mapping_profile_id:
                profile.profile_id = animation.mapping_profile_id
            animation.mapping_profile_id = profile.profile_id
            self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
            self._mark_dirty()
            self._refresh_mapping_table(animation, document, profile)
            self._refresh_animation_table()
        except Exception as exc:
            self._show_error("Auto-map failed", exc)

    def clear_mapping(self) -> None:
        if self.project.rig.retarget_mode == "exact":
            return
        animation = self._retarget_animation()
        if animation is None:
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
        applied = 0
        skipped = 0
        for row in self.project.animations:
            if row.animation_id == animation.animation_id:
                continue
            try:
                document = self._source_document(row.source_fbx)
                candidate = auto_map_source_bones(
                    document.limb_models, parents=document.parent_by_name
                )
            except Exception:
                skipped += 1
                continue
            if candidate.source_skeleton_hash == profile.source_skeleton_hash:
                row.mapping_profile_id = profile.profile_id
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
            current_hash = auto_map_source_bones(
                document.limb_models, parents=document.parent_by_name
            ).source_skeleton_hash
            if profile.source_skeleton_hash and profile.source_skeleton_hash != current_hash:
                result = self.qt["QMessageBox"].question(
                    self.window,
                    "Different source skeleton",
                    "This mapping was created for a different bone hierarchy. Load it anyway?",
                )
                if result != self.qt["QMessageBox"].Yes:
                    return
            animation.mapping_profile_id = profile.profile_id
            self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
            self._mark_dirty()
            self._refresh_mapping_table(animation, document, profile)
            self._refresh_animation_table()
        except Exception as exc:
            self._show_error("Could not load mapping", exc)

    def _retarget_animation(self) -> ProjectAnimation | None:
        return self.project.animation_by_id(str(self.retarget_clip_combo.currentData() or ""))

    def _open_mapping_for_animation(self, animation_id: str) -> None:
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
        self.progress_bar.setRange(0, 0)
        self.build_button.setEnabled(False)
        self.export_anm2_button.setEnabled(False)
        self.qt["QApplication"].setOverrideCursor(self.qt["Qt"].WaitCursor)
        try:
            export_warnings: list[str] = []
            paths = export_project_anm2_files(
                self.project,
                destination,
                progress=self._append_build_log,
                warning=export_warnings.append,
            )
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
        except Exception as exc:
            self._append_build_log(f"ERROR: {exc}")
            self._show_error("ANM2 export failed", exc)
        finally:
            self.qt["QApplication"].restoreOverrideCursor()
            self.progress_bar.setRange(0, 1)
            self.progress_bar.setValue(0)
            self.build_button.setEnabled(True)
            self.export_anm2_button.setEnabled(True)

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
        self.build_log.clear()
        self.progress_bar.setRange(0, 0)
        self.build_button.setEnabled(False)
        self.export_anm2_button.setEnabled(False)
        self.qt["QApplication"].setOverrideCursor(self.qt["Qt"].WaitCursor)
        try:
            result = build_project(self.project, progress=self._append_build_log)
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
            self.project.save(self.project_path)
            self.dirty = False
            self._update_title()
        except Exception as exc:
            self._append_build_log(f"ERROR: {exc}")
            self._show_error("Build failed", exc)
        finally:
            self.qt["QApplication"].restoreOverrideCursor()
            self.progress_bar.setRange(0, 1)
            self.progress_bar.setValue(0)
            self.build_button.setEnabled(True)
            self.export_anm2_button.setEnabled(True)

    def _append_build_log(self, message: str) -> None:
        self.build_log.appendPlainText(message)
        self.qt["QApplication"].processEvents()

    def _export_mode_changed(self) -> None:
        if self._refreshing:
            return
        self.existing_rpack.setEnabled(self.append_pack_radio.isChecked())
        self._mark_dirty()

    # --------------------------------------------------------------- helpers
    def _refresh_all(self) -> None:
        self._refreshing = True
        try:
            self.project_name.setText(self.project.name)
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
            advanced = self.settings.value("advanced_mode", False, type=bool)
            self.advanced_mode_toggle.setChecked(bool(advanced))
        finally:
            self._refreshing = False
        self.existing_rpack.setEnabled(self.project.export.mode == "append")
        self._refresh_animation_table()
        self._refresh_retarget_clip_combo()
        self._refreshing = True
        try:
            self._script_default_changed()
            self._apply_advanced_visibility()
            self._bind_pose_mode_changed()
        finally:
            self._refreshing = False
        self._update_title()

    def _sync_project_from_ui(self) -> None:
        self.project.name = self.project_name.text().strip() or "Untitled Animation Project"
        self.project.notes = self.project_notes.toPlainText()
        selected_rig_ref = str(self.target_rig_combo.currentData() or BUILTIN_MALE_RIG_REF)
        self.project.rig.target_rig_ref = selected_rig_ref
        if selected_rig_ref == BUILTIN_MALE_RIG_REF:
            self.project.rig.retarget_mode = "humanoid"
            self.project.rig.target_rig_path = ""
            self.project.rig.target_rig_name = "Dying Light player_1_tpp / male humanoid"
        else:
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
        current = self.target_rig_combo.currentData()
        self.target_rig_combo.clear()
        records = self.rig_registry.records()
        self._rig_paths_by_ref = {row.rig_ref: row.path for row in records}
        for row in records:
            suffix = "" if row.builtin else f" [{row.category}]"
            self.target_rig_combo.addItem(row.display_name + suffix, row.rig_ref)
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
            except (OSError, ValueError):
                pass
        if current:
            self._set_combo_data(self.target_rig_combo, current)

    def _target_rig_changed(self, *_args) -> None:
        if self._refreshing:
            return
        selected = str(self.target_rig_combo.currentData() or BUILTIN_MALE_RIG_REF)
        self.project.rig.target_rig_ref = selected
        self.project.rig.retarget_mode = (
            "humanoid" if selected == BUILTIN_MALE_RIG_REF else "exact"
        )
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
        finally:
            self._refreshing = previous

    def _bind_pose_mode_changed(self, *_args) -> None:
        if not hasattr(self, "use_imported_bind_pose"):
            return
        exact = (
            str(self.target_rig_combo.currentData() or BUILTIN_MALE_RIG_REF)
            != BUILTIN_MALE_RIG_REF
        )
        embedded = self.use_imported_bind_pose.isChecked()
        self.use_imported_bind_pose.setVisible(not exact)
        self._set_path_row_visible(self.source_rest_path, not exact and not embedded)
        if hasattr(self, "trusted_rest_path"):
            self._set_path_row_visible(
                self.trusted_rest_path,
                self.advanced_mode_toggle.isChecked() and not exact and not embedded,
            )
        if hasattr(self, "advanced_rig_group"):
            self.advanced_rig_group.setEnabled(not exact)
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
