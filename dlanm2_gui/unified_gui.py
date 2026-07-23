from __future__ import annotations

"""Unified three-workspace shell for DL ReAnimated."""

from copy import deepcopy
from pathlib import Path
import sys
from typing import Any
import uuid

from . import __version__
from .animation_targets import (
    RetargetUiKind,
    resolve_animation_target,
    retarget_ui_kind,
)
from .bone_maps import GenericBoneMap
from .chrome_rig import ChromeRig
from .retarget_mapping import canonical_humanoid_role
from .workspaces.animation_mapping import CrigMappingWorkspace
from .workspaces.models import MODEL_WORKSPACE_EXTENSION_KEY, ModelWorkspace


def main() -> int:
    from . import gui as legacy_gui

    try:
        qt = legacy_gui._load_qt()
    except RuntimeError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    app = qt["QApplication"].instance() or qt["QApplication"](sys.argv)
    app.setApplicationName("DL ReAnimated")
    app.setApplicationVersion(__version__)
    app.setStyleSheet(
        """
        QWidget { font-size: 10pt; }
        QMenuBar { padding: 2px; }
        QMenuBar::item { padding: 5px 10px; }
        QTabWidget::pane { border: 1px solid palette(mid); top: -1px; }
        QTabBar::tab { padding: 8px 14px; }
        QGroupBox { margin-top: 10px; padding-top: 10px; }
        QGroupBox::title { font-weight: 600; subcontrol-origin: margin; left: 10px; padding: 0 4px; }
        QPushButton { min-height: 26px; padding: 4px 10px; }
        QLineEdit, QComboBox, QSpinBox { min-height: 28px; }
        QTableWidget { gridline-color: palette(mid); alternate-background-color: palette(alternate-base); }
        QToolTip { padding: 5px; }
        """
    )
    shell = UnifiedMainWindow(qt, legacy_gui)
    shell.show()
    return int(app.exec())


class UnifiedMainWindow:
    def __init__(self, qt: dict[str, Any], legacy_gui: Any) -> None:
        self.qt = qt
        self.legacy_gui_module = legacy_gui
        self.controller = legacy_gui.MainWindow(qt)
        self.window = self.controller.window
        self.window.resize(1480, 920)
        self.window.setMinimumSize(1120, 720)
        self.window.setWindowTitle(f"DL ReAnimated {__version__}")

        pages: dict[str, Any] = {}
        while self.controller.tabs.count():
            text = self.controller.tabs.tabText(0)
            widget = self.controller.tabs.widget(0)
            self.controller.tabs.removeTab(0)
            pages[text] = widget

        # The legacy Help page is replaced below. Its buttons are owned by that
        # page and are deleted when it is discarded, so the legacy visibility
        # pass must not retain and dereference them later.
        self.controller.advanced_help_buttons = []

        # The legacy flat action strip is replaced by the application menu.
        for toolbar in self.window.findChildren(qt["QToolBar"]):
            self.window.removeToolBar(toolbar)
            toolbar.hide()

        self.main_tabs = qt["QTabWidget"]()
        self.animation_tabs = qt["QTabWidget"]()
        for title in ("Project", "Animations", "Retargeting", "Facial", "Export"):
            if title in pages:
                self.animation_tabs.addTab(pages[title], title)
        # Facial callbacks are installed by the standalone controller before
        # Unified takes ownership of its pages.  Repoint them while the old
        # QTabWidget is still alive; setCentralWidget below will delete it.
        self.controller.facial_tab_host = self.animation_tabs

        self.crig_mapping = CrigMappingWorkspace(qt, controller=self.controller, mark_dirty=self._mark_dirty)
        self.animation_tabs.addTab(
            self._help_page(
                "Animation help",
                "Guides for importing, mapping, packaging, and facial animation clips.",
                (
                    ("Humanoid retargeting", "RETARGETING.md"),
                    ("Animation script targets", "ANIMATION_SCRIPT_TARGETS.md"),
                    ("RPack export workflow", "RPACK_WORKFLOW.md"),
                    ("Root motion and IK", "ROOT_MOTION_AND_IK.md"),
                    ("Facial animations", "FACIAL_ANIMATIONS.md"),
                    ("Mimic profiles", "MIMIC_PROFILES.md"),
                ),
            ),
            "Help",
        )
        self.animation_tabs.currentChanged.connect(self._animation_subtab_changed)

        self.models = ModelWorkspace(
            qt,
            parent_window=self.window,
            root=Path(self.controller.root),
            mark_dirty=self._mark_dirty,
            status_callback=lambda message: self.controller.status.showMessage(message),
            rigs_installed_callback=self._model_rigs_installed,
            animations_for_rig_callback=self._show_animations_targeting_model,
        )
        self.controller.extra_task_runners = [self.models.background_tasks]
        self._remove_tab(self.models.tabs, "Help")
        self.models.tabs.addTab(
            self._help_page(
                "Model help",
                "Guides for importing model FBXs, choosing a rig workflow, and installing model assets.",
                (
                    ("Model import and installation", "MODEL_IMPORT.md"),
                    ("Chrome Rig custom targets", "CHROME_RIGS.md"),
                ),
            ),
            "Help",
        )

        reverse_holder = qt["QWidget"]()
        reverse_layout = qt["QVBoxLayout"](reverse_holder)
        reverse_layout.setContentsMargins(0, 0, 0, 0)
        self.reverse_tabs = qt["QTabWidget"]()
        if "ANM2 → FBX" in pages:
            self.reverse_tabs.addTab(pages["ANM2 → FBX"], "Convert")
        self.reverse_tabs.addTab(
            self._help_page(
                "ANM2 to FBX help",
                "Guides for extracting animation resources and converting them into editable FBX files.",
                (("ANM2 to FBX workflow", "ANM2_TO_FBX.md"), ("ANM2 file format", "ANM2_FORMAT.md")),
            ),
            "Help",
        )
        reverse_layout.addWidget(self.reverse_tabs)

        self.main_tabs.addTab(self.animation_tabs, "Animations")
        self.main_tabs.addTab(self.models.widget, "Models")
        self.main_tabs.addTab(reverse_holder, "ANM2 → FBX")
        self.window.setCentralWidget(self.main_tabs)
        self._build_menu_bar()
        self.controller.advanced_mode_toggle.toggled.connect(self._advanced_visibility_changed)
        self._set_crig_tab_visible(self.controller.advanced_mode_toggle.isChecked())
        self.controller.mapping_navigation_callback = self._open_animation_mapping
        self.controller.target_selection_changed_callback = (
            self._animation_target_changed
        )
        self.controller.target_rig_combo.currentIndexChanged.connect(
            lambda *_args: self._set_crig_tab_visible(
                self.controller.advanced_mode_toggle.isChecked()
            )
        )
        self._install_project_sync_hooks()
        if hasattr(self.controller, "game_combo"):
            self.controller.game_combo.currentIndexChanged.connect(self._game_profile_changed)
        self._restore_extension_state()
        self._refresh_facial_visibility()
        self.crig_mapping.reload_clips()
        self.main_tabs.currentChanged.connect(self._workspace_changed)

    def show(self) -> None:
        self.window.show()

    def _game_profile_changed(self, *_args) -> None:
        """Keep model defaults coherent while preserving deliberate custom SMD paths."""

        self._refresh_facial_visibility()
        if not hasattr(self.models, "target_smd"):
            return
        current = self.models.target_smd.text().strip().replace("\\", "/").casefold()
        default_like = (
            not current
            or current.endswith("reference/player_1_tpp.smd")
            or current.endswith("reference/dl2/player_skeleton.smd")
            or current.endswith("reference/dl2/player_shadow_caster.smd")
        )
        if default_like:
            self.models.target_smd.setText(self.controller.project.rig.canonical_smd)

    def _refresh_facial_visibility(self) -> None:
        refresh = getattr(
            self.controller, "_refresh_facial_availability", None
        )
        if callable(refresh):
            refresh()
        self.controller._refresh_animation_table()

    def _build_menu_bar(self) -> None:
        qt = self.qt
        menu_bar = self.window.menuBar()
        menu_bar.clear()

        def action(menu: Any, text: str, shortcut: str | None, callback) -> Any:
            row = qt["QAction"](text, self.window)
            if shortcut:
                row.setShortcut(qt["QKeySequence"](shortcut))
            row.triggered.connect(callback)
            menu.addAction(row)
            return row

        file_menu = menu_bar.addMenu("&File")
        action(file_menu, "New Project", "Ctrl+N", self.controller.new_project)
        action(file_menu, "Open Project…", "Ctrl+O", self.controller.open_project)
        self.recent_projects_menu = file_menu.addMenu("Open Recent")
        self.recent_projects_menu.aboutToShow.connect(self._refresh_recent_projects_menu)
        self.controller.recent_projects_changed_callback = self._refresh_recent_projects_menu
        self._refresh_recent_projects_menu()
        file_menu.addSeparator()
        action(file_menu, "Save Project", "Ctrl+S", self.controller.save_project)
        action(file_menu, "Save Project As…", "Ctrl+Shift+S", self.controller.save_project_as)
        file_menu.addSeparator()
        action(file_menu, "Exit", "Alt+F4", self.window.close)

        import_menu = menu_bar.addMenu("&Import")
        action(import_menu, "Animation FBX…", "Ctrl+I", self._add_animation)
        action(import_menu, "Model FBX…", "Ctrl+Shift+I", self._add_model)

        build_menu = menu_bar.addMenu("&Build")
        action(build_menu, "Animation RPack", "Ctrl+B", self._build_animations)
        action(build_menu, "Model Assets", "Ctrl+Shift+B", self._build_models)

        workspace_menu = menu_bar.addMenu("&Workspace")
        action(workspace_menu, "Animations", "Alt+1", lambda: self.main_tabs.setCurrentIndex(0))
        action(workspace_menu, "Models", "Alt+2", lambda: self.main_tabs.setCurrentIndex(1))
        action(workspace_menu, "ANM2 → FBX", "Alt+3", lambda: self.main_tabs.setCurrentIndex(2))

        view_menu = menu_bar.addMenu("&View")
        self.advanced_action = action(view_menu, "Advanced Settings", None, self._set_advanced_from_action)
        self.advanced_action.setCheckable(True)
        self.advanced_action.setChecked(self.controller.advanced_mode_toggle.isChecked())

        help_menu = menu_bar.addMenu("&Help")
        action(help_menu, "GUI Guide", "F1", lambda: self.controller.open_doc("GUI_GUIDE.md"))
        action(help_menu, "Troubleshooting", None, lambda: self.controller.open_doc("TROUBLESHOOTING.md"))
        action(help_menu, "Project Compatibility", None, lambda: self.controller.open_doc("PROJECT_FORMAT.md"))
        help_menu.addSeparator()
        action(help_menu, "About DL ReAnimated", None, self._show_about)

        advanced_toggle = self.controller.advanced_mode_toggle
        advanced_toggle.setText("Advanced settings")
        advanced_toggle.setToolTip("Show custom rig mapping, diagnostic controls, and developer options.")
        menu_bar.setCornerWidget(advanced_toggle)

    def _refresh_recent_projects_menu(self) -> None:
        menu = self.recent_projects_menu
        menu.clear()
        paths = self.controller.recent_project_paths()
        if not paths:
            empty = self.qt["QAction"]("No Recent Projects", menu)
            empty.setEnabled(False)
            menu.addAction(empty)
            return

        for index, path in enumerate(paths, start=1):
            name = path.name.replace("&", "&&")
            parent = str(path.parent).replace("&", "&&")
            label = f"{index}. {name}  —  {parent}"
            row = self.qt["QAction"](label, menu)
            row.setData(str(path))
            row.setStatusTip(str(path))
            row.triggered.connect(
                lambda _checked=False, project_path=path: self.controller.open_recent_project(
                    project_path
                )
            )
            menu.addAction(row)

        menu.addSeparator()
        clear = self.qt["QAction"]("Clear Recent Projects", menu)
        clear.triggered.connect(self.controller.clear_recent_projects)
        menu.addAction(clear)

    def _help_page(self, heading: str, description: str, documents: tuple[tuple[str, str], ...]) -> Any:
        qt = self.qt
        page = qt["QWidget"]()
        layout = qt["QVBoxLayout"](page)
        layout.setSpacing(10)
        title = qt["QLabel"](heading)
        title.setStyleSheet("font-size: 14pt; font-weight: 600;")
        layout.addWidget(title)
        intro = qt["QLabel"](description)
        intro.setWordWrap(True)
        layout.addWidget(intro)
        group = qt["QGroupBox"]("Documentation")
        rows = qt["QVBoxLayout"](group)
        for label, filename in documents:
            button = qt["QPushButton"](label)
            button.setToolTip(f"Open docs/{filename} in your default Markdown viewer.")
            button.clicked.connect(lambda _checked=False, name=filename: self.controller.open_doc(name))
            rows.addWidget(button)
        layout.addWidget(group)
        layout.addStretch(1)
        return page

    @staticmethod
    def _remove_tab(tabs: Any, title: str) -> None:
        for index in range(tabs.count() - 1, -1, -1):
            if tabs.tabText(index) == title:
                tabs.removeTab(index)

    def _set_advanced_from_action(self, checked: bool) -> None:
        self.controller.advanced_mode_toggle.setChecked(bool(checked))

    def _advanced_visibility_changed(self, checked: bool) -> None:
        self.advanced_action.setChecked(bool(checked))
        self._set_crig_tab_visible(bool(checked))

    def _set_crig_tab_visible(self, visible: bool) -> None:
        # The full per-CRIG editor is an advanced/manual surface. A focused
        # Fix mapping action force-opens it through _open_animation_mapping;
        # simply importing or selecting an exact target does not keep the
        # diagnostic table permanently visible.
        has_expert_target = (
            retarget_ui_kind(
                self.controller.project,
                None,
                rig_paths=getattr(self.controller, "_rig_paths_by_ref", {}),
            )
            == RetargetUiKind.CUSTOM_CRIG
        )
        if not has_expert_target:
            has_expert_target = any(
                retarget_ui_kind(
                    self.controller.project,
                    animation,
                    rig_paths=getattr(self.controller, "_rig_paths_by_ref", {}),
                )
                == RetargetUiKind.CUSTOM_CRIG
                for animation in self.controller.project.animations
            )
        visible = bool(visible and has_expert_target)
        index = self._animation_tab_index("Root & .crig Mapping")
        if visible and index < 0:
            insert_at = self._animation_tab_index("Export")
            if insert_at < 0:
                insert_at = max(0, self.animation_tabs.count() - 1)
            self.animation_tabs.insertTab(insert_at, self.crig_mapping.widget, "Root & .crig Mapping")
        elif not visible and index >= 0:
            self.animation_tabs.removeTab(index)

    def _show_about(self) -> None:
        self.qt["QMessageBox"].information(
            self.window,
            "About DL ReAnimated",
            f"DL ReAnimated {__version__}\n\nProject-based FBX, ANM2, RPack, and model authoring tools for Dying Light.",
        )

    def _install_project_sync_hooks(self) -> None:
        original_sync = self.controller._sync_project_from_ui
        original_refresh = self.controller._refresh_all

        def sync_with_models() -> None:
            original_sync()
            self.controller.project.extensions[MODEL_WORKSPACE_EXTENSION_KEY] = self.models.serialize()

        def refresh_with_models() -> None:
            original_refresh()
            self._restore_extension_state()
            self._set_crig_tab_visible(self.controller.advanced_mode_toggle.isChecked())
            self.crig_mapping.reload_clips()

        self.controller._sync_project_from_ui = sync_with_models
        self.controller._refresh_all = refresh_with_models

    def _restore_extension_state(self) -> None:
        payload = self.controller.project.extensions.get(MODEL_WORKSPACE_EXTENSION_KEY, {})
        self.models.restore(payload if isinstance(payload, dict) else {})

    def _mark_dirty(self) -> None:
        self.controller.dirty = True
        self.controller._update_title()

    def _add_animation(self) -> None:
        self.main_tabs.setCurrentIndex(0)
        self.animation_tabs.setCurrentIndex(max(0, self._animation_tab_index("Animations")))
        self.controller.add_animations()
        self.crig_mapping.reload_clips()

    def _open_animation_mapping(self, animation_id: str) -> None:
        self.main_tabs.setCurrentIndex(0)
        animation = self.controller.project.animation_by_id(animation_id)
        custom_crig_ui = bool(
            animation is not None
            and retarget_ui_kind(
                self.controller.project,
                animation,
                rig_paths=getattr(self.controller, "_rig_paths_by_ref", {}),
            )
            == RetargetUiKind.CUSTOM_CRIG
        )
        if custom_crig_ui:
            self._set_crig_tab_visible(True)
            self.crig_mapping.reload_clips()
            index = self.crig_mapping.clip_combo.findData(animation_id)
            if index >= 0:
                self.crig_mapping.clip_combo.setCurrentIndex(index)
            mapping_tab = self._animation_tab_index("Root & .crig Mapping")
            if mapping_tab >= 0:
                self.animation_tabs.setCurrentIndex(mapping_tab)
            if animation is not None and self.crig_mapping._current_profile(animation) is None:
                # "Create .crig map" must persist the suggestions immediately;
                # merely rendering an unsaved preview leaves strict build mode
                # active and recreates the original import/build dead end.
                self.crig_mapping.auto_map()
            else:
                self.crig_mapping.refresh()
            return

        self.controller._set_combo_data(self.controller.retarget_clip_combo, animation_id)
        retarget_tab = self._animation_tab_index("Retargeting")
        if retarget_tab >= 0:
            self.animation_tabs.setCurrentIndex(retarget_tab)
        self.controller._retarget_clip_changed()
        self.controller.focus_first_unresolved_mapping_role()

    def _animation_target_changed(self, _animation_id: str = "") -> None:
        self._set_crig_tab_visible(
            self.controller.advanced_mode_toggle.isChecked()
        )
        self.crig_mapping.reload_clips()

    def _add_model(self) -> None:
        self.main_tabs.setCurrentIndex(1)
        self.models.tabs.setCurrentIndex(0)
        self.models.add_models()

    def _build_animations(self) -> None:
        self.main_tabs.setCurrentIndex(0)
        self.controller._sync_project_from_ui()
        self.controller.build_rpack()

    def _build_models(self) -> None:
        self.main_tabs.setCurrentIndex(1)
        self.models.tabs.setCurrentIndex(2)
        self.models.compile_and_install()

    def _show_animations_targeting_model(self, entry: Any) -> None:
        """Jump from one model to clips resolved against its generated CRIG."""

        rig_ref = str(getattr(entry, "installed_crig_ref", "") or "")
        if not rig_ref:
            self.controller.status.showMessage(
                "This model has no installed generated CRIG to filter by."
            )
            return
        self.main_tabs.setCurrentIndex(0)
        animation_index = self._animation_tab_index("Animations")
        if animation_index >= 0:
            self.animation_tabs.setCurrentIndex(animation_index)
        visible = self.controller.set_animation_target_filter(rig_ref)
        resource = str(getattr(entry, "resource_name", "model") or "model")
        self.controller.status.showMessage(
            f"Showing {visible} animation clip(s) targeting {resource!r} ({rig_ref})."
            if visible
            else (
                f"No animation clips currently target {resource!r} ({rig_ref}). "
                "Select a clip and use the model's generated-rig handoff to assign it."
            )
        )

    def _animation_tab_index(self, title: str) -> int:
        for index in range(self.animation_tabs.count()):
            if self.animation_tabs.tabText(index) == title:
                return index
        return -1

    def _animation_subtab_changed(self, index: int) -> None:
        if index >= 0 and self.animation_tabs.tabText(index) == "Root & .crig Mapping":
            self.controller._sync_project_from_ui()
            self.crig_mapping.reload_clips()

    def _workspace_changed(self, index: int) -> None:
        if index == 0:
            self.crig_mapping.reload_clips()
        elif index == 1:
            self.models._refresh_mapping_model_combo()

    def _model_rigs_installed(self, results: list[Any]) -> None:
        """Assign one generated CRIG without rewriting unrelated animations."""

        self.controller._sync_project_from_ui()
        self.controller._reload_target_rig_combo()
        if len(results) != 1:
            self.controller.status.showMessage(
                "Installed model CRIG targets. Use each animation's Target rig column "
                "to assign the intended model."
            )
            return

        result = results[0]
        new_path = Path(result.installed_crig_path or result.crig_path or "")
        if not new_path.is_file():
            return
        new_rig = ChromeRig.load(new_path)
        selected_animation = self.controller._selected_animation()
        if selected_animation is None and len(self.controller.project.animations) == 1:
            selected_animation = self.controller.project.animations[0]

        if selected_animation is None and self.controller.project.animations:
            self.controller.status.showMessage(
                f"Installed {new_rig.name!r}. Select an animation row, then click "
                "Use generated rig in Animations; no existing clip was changed."
            )
            self.controller._refresh_animation_table()
            return

        if selected_animation is None:
            self.controller.project.rig.target_rig_ref = new_rig.rig_id
            self.controller.project.rig.target_rig_path = str(new_path.resolve())
            self.controller.project.rig.target_rig_name = new_rig.name
            self.controller.project.rig.retarget_mode = "exact"
            self.controller._reload_target_rig_combo()
            self.controller._set_combo_data(
                self.controller.target_rig_combo, new_rig.rig_id
            )
            self._mark_dirty()
            self.controller.status.showMessage(
                f"Set {new_rig.name!r} as the project default animation target."
            )
            return

        previous = resolve_animation_target(
            self.controller.project,
            selected_animation,
            rig_paths=getattr(self.controller, "_rig_paths_by_ref", {}),
        )
        old_ref = previous.rig_ref
        old_path = previous.rig_path
        old_hash = ""
        if old_path and Path(old_path).is_file():
            try:
                old_hash = ChromeRig.load(old_path).skeleton_hash
            except (OSError, ValueError):
                pass

        selected_animation.target_rig_ref = new_rig.rig_id
        selected_animation.target_rig_path = str(new_path.resolve())
        new_by_name = {bone.name: bone.descriptor for bone in new_rig.bones}
        migrated = 0
        profile_id = str(selected_animation.mapping_profile_id or "")
        payload = self.controller.project.mapping_profiles.get(profile_id)
        if (
            profile_id
            and isinstance(payload, dict)
            and payload.get("format") == "dl-reanimated-bone-map"
        ):
            try:
                profile = GenericBoneMap.from_dict(payload)
            except (TypeError, ValueError):
                profile = None
            if profile is not None:
                compatible_rows = bool(profile.pairs) and all(
                    row.target_rig_bone in new_by_name
                    and int(row.target_rig_descriptor)
                    == int(new_by_name[row.target_rig_bone])
                    for row in profile.pairs
                )
                expected_old_hash = profile.target_bind_hash or profile.source_skeleton_hash
                belongs_to_previous_target = (
                    profile.source_rig_ref == old_ref
                    or (bool(old_hash) and expected_old_hash == old_hash)
                )
                if compatible_rows and belongs_to_previous_target:
                    shared_elsewhere = any(
                        row.animation_id != selected_animation.animation_id
                        and row.mapping_profile_id == profile_id
                        for row in self.controller.project.animations
                    )
                    if shared_elsewhere:
                        profile.profile_id = str(uuid.uuid4())
                        selected_animation.mapping_profile_id = profile.profile_id
                    profile.source_rig_ref = new_rig.rig_id
                    profile.source_skeleton_hash = new_rig.skeleton_hash
                    profile.target_bind_hash = new_rig.skeleton_hash
                    for row in profile.pairs:
                        row.target_rig_descriptor = new_by_name[row.target_rig_bone]
                    profile.extensions = deepcopy(profile.extensions)
                    profile.extensions["migrated_after_model_rig_rebuild"] = {
                        "previous_rig_ref": old_ref,
                        "previous_skeleton_hash": old_hash,
                        "new_rig_ref": new_rig.rig_id,
                        "new_skeleton_hash": new_rig.skeleton_hash,
                    }
                    target_root = new_rig.bones[new_rig.root_index]
                    legacy_rows = [
                        row
                        for row in profile.pairs
                        if row.target_rig_bone == target_root.name
                        and row.method == "hierarchy_root"
                    ]
                    pelvis_candidates = [
                        bone
                        for bone in new_rig.bones
                        if canonical_humanoid_role(bone.name) == "pelvis"
                    ]
                    pelvis_candidates.sort(
                        key=lambda bone: (
                            0 if bone.deform and not bone.helper else 1,
                            bone.index,
                        )
                    )
                    for legacy in legacy_rows:
                        already_used = any(
                            row is not legacy
                            and row.source_fbx_bone == legacy.source_fbx_bone
                            for row in profile.pairs
                        )
                        if pelvis_candidates and not already_used:
                            pelvis = pelvis_candidates[0]
                            legacy.target_rig_bone = pelvis.name
                            legacy.target_rig_descriptor = pelvis.descriptor
                            legacy.confidence = 0.98
                            legacy.method = "model_rig_rebuild:pelvis_pose"
                        else:
                            profile.pairs.remove(legacy)
                    self.controller.project.mapping_profiles[profile.profile_id] = (
                        profile.to_dict()
                    )
                    migrated = 1

        self.controller._reload_target_rig_combo()
        self.controller._refresh_animation_table()
        self._set_crig_tab_visible(True)
        self.crig_mapping.reload_clips()
        self._mark_dirty()
        self.controller.status.showMessage(
            f"Assigned generated rig {new_rig.name!r} to animation "
            f"{selected_animation.display_name!r}; {migrated} compatible map migrated. "
            "Other animation targets were left unchanged."
        )


if __name__ == "__main__":
    raise SystemExit(main())
