"""Facial/mimic workspace integrated with the project GUI.

This module keeps facial controls separate from the host window while using the
same project object and build pipeline.  It intentionally contains no bundled
sample scans, generated data, or reverse-engineering fixtures.
"""

from __future__ import annotations

from pathlib import Path
import types
from typing import Any

from PySide6.QtCore import Qt
from PySide6.QtWidgets import (
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QDoubleSpinBox,
    QFormLayout,
    QGroupBox,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QMessageBox,
    QPushButton,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from .fbx_blendshapes import FbxFacialScan, scan_fbx_blendshapes
from .mimic_profiles import (
    BUILTIN_COMMON46_REF,
    MimicMappingRow,
    auto_map_shapes,
    resolve_mimic_profile,
)

_CONTENT_ITEMS = (
    ("Auto", "auto"),
    ("Body only", "body_only"),
    ("Mimic only", "mimic_only"),
    ("Body + mimic", "both"),
)


def _set_combo_data(combo: QComboBox, value: Any) -> None:
    index = combo.findData(value)
    if index >= 0:
        combo.setCurrentIndex(index)
    elif combo.isEditable():
        combo.setEditText(str(value))


def _settings(animation: Any) -> dict[str, Any]:
    row = animation.extensions.get("mimic")
    if not isinstance(row, dict):
        row = {}
        animation.extensions["mimic"] = row
    row.setdefault("mode", "auto")
    row.setdefault("mapping", [])
    row.setdefault("resource_name", "")
    row.setdefault("clamp_mode", "none")
    return row


def _scan(controller: Any, animation: Any) -> FbxFacialScan:
    path = Path(animation.source_fbx)
    key = (
        str(path.resolve()),
        animation.source_animation_stack,
        animation.resolved_sample_fps(),
        path.stat().st_mtime_ns,
        path.stat().st_size,
    )
    cached = controller._mimic_scan_cache.get(key)
    if cached is None:
        cached = scan_fbx_blendshapes(
            path,
            fps=animation.resolved_sample_fps(),
            animation_stack=animation.source_animation_stack or None,
        )
        controller._mimic_scan_cache[key] = cached
    return cached


class _MappingDialog(QDialog):
    def __init__(self, parent: QWidget, scan: FbxFacialScan, profile: Any, configured: list[dict[str, Any]]):
        super().__init__(parent)
        self.scan = scan
        self.profile = profile
        self.setWindowTitle("Facial retargeting")
        self.resize(950, 620)
        layout = QVBoxLayout(self)
        intro = QLabel(
            f"{len(scan.curves)} facial channel(s) found; {len(scan.animated_curves)} animated. "
            "Map each source curve to a Dying Light mimic target. Multiple source curves may feed one target."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)
        actions = QHBoxLayout()
        auto_button = QPushButton("Auto-map recognizable names")
        auto_button.clicked.connect(self.auto_map)
        clear_button = QPushButton("Clear")
        clear_button.clicked.connect(lambda: self.load_rows([]))
        actions.addWidget(auto_button)
        actions.addWidget(clear_button)
        actions.addStretch(1)
        layout.addLayout(actions)
        self.table = QTableWidget(0, 5)
        self.table.setHorizontalHeaderLabels(["Use", "Source blendshape", "Target", "Weight", "Confidence"])
        header = self.table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeToContents)
        header.setSectionResizeMode(1, QHeaderView.Stretch)
        header.setSectionResizeMode(2, QHeaderView.Stretch)
        header.setSectionResizeMode(3, QHeaderView.ResizeToContents)
        header.setSectionResizeMode(4, QHeaderView.ResizeToContents)
        layout.addWidget(self.table, 1)
        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
        self.load_rows(configured or [row.to_dict() for row in auto_map_shapes(scan.animated_shape_names, profile)])

    def _append(self, row: MimicMappingRow) -> None:
        index = self.table.rowCount()
        self.table.insertRow(index)
        use = QComboBox()
        use.addItem("Yes", True)
        use.addItem("No", False)
        _set_combo_data(use, bool(row.enabled))
        self.table.setCellWidget(index, 0, use)
        source = QComboBox()
        source.setEditable(True)
        source.addItems([curve.name for curve in self.scan.animated_curves])
        source.setCurrentText(row.source)
        self.table.setCellWidget(index, 1, source)
        target = QComboBox()
        target.addItem("(unmapped)", None)
        for item in self.profile.targets:
            target.addItem(item.display_name, item.descriptor)
        _set_combo_data(target, row.target_descriptor)
        self.table.setCellWidget(index, 2, target)
        weight = QDoubleSpinBox()
        weight.setRange(-8.0, 8.0)
        weight.setDecimals(4)
        weight.setValue(float(row.weight))
        self.table.setCellWidget(index, 3, weight)
        confidence = QTableWidgetItem(f"{row.confidence:.0%} — {row.method}")
        confidence.setFlags(confidence.flags() & ~Qt.ItemIsEditable)
        confidence.setData(Qt.UserRole, (float(row.confidence), str(row.method)))
        self.table.setItem(index, 4, confidence)

    def load_rows(self, payload: list[dict[str, Any]]) -> None:
        self.table.setRowCount(0)
        rows: list[MimicMappingRow] = []
        for value in payload:
            try:
                rows.append(MimicMappingRow.from_dict(value))
            except (TypeError, ValueError, KeyError):
                continue
        represented = {row.source for row in rows}
        for row in rows:
            self._append(row)
        for curve in self.scan.animated_curves:
            if curve.name not in represented:
                self._append(MimicMappingRow(curve.name, self.profile.targets[0].descriptor, enabled=False, confidence=0.0, method="unmapped"))

    def auto_map(self) -> None:
        self.load_rows([row.to_dict() for row in auto_map_shapes(self.scan.animated_shape_names, self.profile)])

    def mappings(self) -> list[dict[str, Any]]:
        result: list[dict[str, Any]] = []
        for index in range(self.table.rowCount()):
            enabled = bool(self.table.cellWidget(index, 0).currentData())
            source = self.table.cellWidget(index, 1).currentText().strip()
            descriptor = self.table.cellWidget(index, 2).currentData()
            if not source or descriptor is None:
                continue
            confidence_item = self.table.item(index, 4)
            confidence, method = confidence_item.data(Qt.UserRole) or (1.0, "manual")
            if enabled and method == "unmapped":
                confidence, method = 1.0, "manual"
            result.append(
                MimicMappingRow(
                    source=source,
                    target_descriptor=int(descriptor),
                    weight=float(self.table.cellWidget(index, 3).value()),
                    enabled=enabled,
                    confidence=float(confidence),
                    method=str(method),
                ).to_dict()
            )
        return result


def install_mimic_ui(controller: Any) -> None:
    if getattr(controller, "_mimic_ui_installed", False):
        return
    controller._mimic_scan_cache = {}
    controller.facial_page = QWidget()
    layout = QVBoxLayout(controller.facial_page)
    intro = QLabel(
        "Scan animated FBX blendshapes, map them to a target mimic profile, and export body and facial ANM2 resources from the same project."
    )
    intro.setWordWrap(True)
    layout.addWidget(intro)
    target_group = QGroupBox("Target facial support")
    target_form = QFormLayout(target_group)
    controller.facial_policy_combo = controller._combo_box()
    controller.facial_policy_combo.addItem("Auto-detect from target and FBX", "auto")
    controller.facial_policy_combo.addItem("Model supports facial animations", "yes")
    controller.facial_policy_combo.addItem("Model has no facial animations", "no")
    controller.facial_policy_combo.currentIndexChanged.connect(controller._mark_dirty)
    target_form.addRow("Facial animations", controller.facial_policy_combo)
    controller.mimic_profile_combo = controller._combo_box()
    controller.mimic_profile_combo.addItem("Automatic target profile", "auto")
    controller.mimic_profile_combo.addItem("Human / infected common 46", BUILTIN_COMMON46_REF)
    controller.mimic_profile_combo.currentIndexChanged.connect(controller._mark_dirty)
    target_form.addRow("Facial target", controller.mimic_profile_combo)
    layout.addWidget(target_group)
    clip_group = QGroupBox("Selected animation")
    clip_layout = QVBoxLayout(clip_group)
    row = QHBoxLayout()
    controller.mimic_clip_combo = controller._combo_box()
    controller.mimic_scan_button = QPushButton("Scan facial curves")
    controller.mimic_map_button = QPushButton("Open facial retargeting…")
    row.addWidget(controller.mimic_clip_combo, 1)
    row.addWidget(controller.mimic_scan_button)
    row.addWidget(controller.mimic_map_button)
    clip_layout.addLayout(row)
    controller.mimic_status = QLabel("Add an FBX animation to begin facial detection.")
    controller.mimic_status.setWordWrap(True)
    clip_layout.addWidget(controller.mimic_status)
    layout.addWidget(clip_group)
    layout.addStretch(1)
    help_index = next((i for i in range(controller.tabs.count()) if controller.tabs.tabText(i) == "Help"), controller.tabs.count())
    controller.tabs.insertTab(help_index, controller.facial_page, "Facial")

    def refresh_facial(preserve: bool = True) -> None:
        current = str(controller.mimic_clip_combo.currentData() or "") if preserve else ""
        controller.mimic_clip_combo.blockSignals(True)
        controller.mimic_clip_combo.clear()
        for animation in controller.project.animations:
            controller.mimic_clip_combo.addItem(animation.display_name, animation.animation_id)
        if current:
            _set_combo_data(controller.mimic_clip_combo, current)
        controller.mimic_clip_combo.blockSignals(False)
        update_status()

    def selected() -> Any | None:
        return controller.project.animation_by_id(str(controller.mimic_clip_combo.currentData() or ""))

    def update_status() -> None:
        animation = selected()
        controller.mimic_scan_button.setEnabled(animation is not None)
        controller.mimic_map_button.setEnabled(animation is not None)
        if animation is None:
            controller.mimic_status.setText("Add an FBX animation to begin facial detection.")
            return
        settings = _settings(animation)
        detection = settings.get("last_detection")
        if isinstance(detection, dict):
            controller.mimic_status.setText(
                f"{animation.display_name}: {int(detection.get('shape_count', 0))} facial channel(s), "
                f"{int(detection.get('animated_shape_count', 0))} animated; mode {settings.get('mode', 'auto')}."
            )
        else:
            controller.mimic_status.setText(f"{animation.display_name}: not scanned yet; mode {settings.get('mode', 'auto')}.")

    def scan_selected() -> None:
        animation = selected()
        if animation is None:
            return
        try:
            found = _scan(controller, animation)
            _settings(animation)["last_detection"] = found.summary()
            controller._mark_dirty()
            update_status()
            controller._refresh_animation_table()
        except Exception as exc:
            controller._show_error("Facial scan failed", exc)

    def map_selected() -> None:
        animation = selected()
        if animation is None:
            return
        try:
            found = _scan(controller, animation)
            profile = resolve_mimic_profile(controller.project)
            if profile is None:
                raise ValueError("The selected target rig has no facial profile.")
            if not found.curves:
                QMessageBox.information(controller.window, "No facial channels detected", "This FBX contains no BlendShapeChannel objects. Body export remains available.")
                return
            settings = _settings(animation)
            dialog = _MappingDialog(controller.window, found, profile, list(settings.get("mapping", [])))
            if dialog.exec() == QDialog.Accepted:
                settings["mapping"] = dialog.mappings()
                settings["profile_id"] = profile.profile_id
                settings["last_detection"] = found.summary()
                controller._mark_dirty()
                update_status()
                controller._refresh_animation_table()
        except Exception as exc:
            controller._show_error("Could not open facial mapping", exc)

    controller.mimic_clip_combo.currentIndexChanged.connect(lambda _index: update_status())
    controller.mimic_scan_button.clicked.connect(scan_selected)
    controller.mimic_map_button.clicked.connect(map_selected)

    original_refresh_table = controller._refresh_animation_table
    def refresh_animation_table(self) -> None:
        table = self.animation_table
        # Preserve the complete animation target/readiness/action surface, then
        # insert facial mode immediately before the contextual Retarget action.
        table.setColumnCount(11)
        original_refresh_table()
        table.insertColumn(10)
        table.setHorizontalHeaderLabels([
            "Use", "Display name", "FBX source", "FBX animation", "Resource name",
            "Animation SCR", "Target rig", "Compatibility / mapping", "Root motion",
            "IK", "Body / face", "Retarget",
        ])
        for row_index, animation in enumerate(self.project.animations):
            combo = self._combo_box()
            for label, value in _CONTENT_ITEMS:
                combo.addItem(label, value)
            _set_combo_data(combo, _settings(animation).get("mode", "auto"))
            combo.currentIndexChanged.connect(
                lambda _index, item=animation, widget=combo: (
                    _settings(item).__setitem__("mode", str(widget.currentData() or "auto")),
                    self._mark_dirty(),
                )
            )
            holder = QWidget()
            holder_layout = QHBoxLayout(holder)
            holder_layout.setContentsMargins(1, 1, 1, 1)
            button = QPushButton("Face…")
            button.clicked.connect(lambda _checked=False, aid=animation.animation_id: (_set_combo_data(self.mimic_clip_combo, aid), map_selected()))
            holder_layout.addWidget(combo, 1)
            holder_layout.addWidget(button)
            table.setCellWidget(row_index, 10, holder)
        table.setColumnWidth(10, 220)
        refresh_facial()
    controller._refresh_animation_table = types.MethodType(refresh_animation_table, controller)

    original_refresh_all = controller._refresh_all
    def refresh_all(self) -> None:
        original_refresh_all()
        extensions = self.project.rig.extensions or {}
        _set_combo_data(self.facial_policy_combo, extensions.get("facial_animation_policy", "auto"))
        _set_combo_data(self.mimic_profile_combo, extensions.get("mimic_profile_ref", "auto"))
        refresh_facial(False)
    controller._refresh_all = types.MethodType(refresh_all, controller)

    original_sync = controller._sync_project_from_ui
    def sync_project_from_ui(self) -> None:
        original_sync()
        self.project.rig.extensions["facial_animation_policy"] = str(self.facial_policy_combo.currentData() or "auto")
        self.project.rig.extensions["mimic_profile_ref"] = str(self.mimic_profile_combo.currentData() or "auto")
    controller._sync_project_from_ui = types.MethodType(sync_project_from_ui, controller)
    controller._mimic_ui_installed = True

__all__ = ["install_mimic_ui"]
