"""Non-invasive GUI integration for the local facial/mimic prototype.

The patch installs itself on the existing ``MainWindow`` instance. Keeping the
feature layer separate makes it easy to review, test, remove, or upstream later
without replacing the current GUI architecture.
"""

from __future__ import annotations

from copy import deepcopy
from pathlib import Path
import types
from typing import Any

from PySide6.QtCore import QPointF, QRectF, Qt
from PySide6.QtGui import QColor, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import (
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
    QMessageBox,
    QPushButton,
    QSlider,
    QSplitter,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from .fbx_blendshapes import FbxFacialScan, scan_fbx_blendshapes
from .mimic_profiles import (
    BUILTIN_COMMON46_REF,
    MimicMappingRow,
    MimicProfile,
    auto_map_shapes,
    mapping_from_payload,
    resolve_mimic_profile,
)


_CONTENT_ITEMS = (
    ("Auto", "auto", "Export body only unless animated facial blendshapes are detected."),
    ("Body only", "body_only", "Exclude mimic/facial resources for this clip."),
    ("Mimic only", "mimic_only", "Export only the facial resource, named with the _mimic suffix."),
    ("Body + mimic", "both", "Export the body ANM2 and a synchronized separate mimic ANM2."),
)


def _find_group(widget: QWidget, title: str) -> QGroupBox | None:
    current: QWidget | None = widget
    while current is not None:
        if isinstance(current, QGroupBox) and current.title() == title:
            return current
        current = current.parentWidget()
    return None


def _set_combo_data(combo: QComboBox, value: Any) -> None:
    index = combo.findData(value)
    if index >= 0:
        combo.setCurrentIndex(index)
    elif combo.isEditable():
        combo.setEditText(str(value))


def _mimic_settings(animation: Any) -> dict[str, Any]:
    raw = animation.extensions.get("mimic")
    if not isinstance(raw, dict):
        raw = {}
        animation.extensions["mimic"] = raw
    raw.setdefault("mode", "auto")
    raw.setdefault("resource_name", "")
    raw.setdefault("mapping", [])
    raw.setdefault("clamp_mode", "none")
    return raw


def _sync_mimic_project_settings(controller: Any) -> None:
    if not hasattr(controller, "facial_policy_combo"):
        return
    extensions = controller.project.rig.extensions
    extensions["facial_animation_policy"] = str(
        controller.facial_policy_combo.currentData() or "auto"
    )
    extensions["mimic_profile_ref"] = str(
        controller.mimic_profile_combo.currentData() or "auto"
    )
    path = controller.mimic_profile_path.text().strip()
    extensions["mimic_profile_path"] = path
    if extensions["mimic_profile_ref"] == "custom" and path and Path(path).is_file():
        # Keep projects portable and future-proof by embedding a declarative copy.
        extensions["mimic_profile_embedded"] = MimicProfile.load(path).to_dict()


class _GenericFacePreview(QWidget):
    """Procedural, asset-free preview of broad facial semantics."""

    def __init__(self, profile: MimicProfile, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.profile = profile
        self.values: dict[int, float] = {}
        self.setMinimumSize(260, 300)
        self.setToolTip(
            "Generic procedural preview. It shows broad eyelid/jaw/smile/funnel semantics and "
            "does not reproduce any Dying Light mesh or proprietary asset."
        )

    def set_values(self, values: dict[int, float]) -> None:
        self.values = values
        self.update()

    def _semantic(self, name: str, default: float = 0.0) -> float:
        for target in self.profile.targets:
            if target.name == name:
                return float(self.values.get(target.descriptor, default))
        return default

    def paintEvent(self, _event) -> None:  # type: ignore[override]
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.fillRect(self.rect(), QColor("#20242a"))
        width, height = self.width(), self.height()
        face = QRectF(width * 0.20, height * 0.08, width * 0.60, height * 0.80)
        painter.setPen(QPen(QColor("#d5d8dc"), 3))
        painter.setBrush(QColor("#666d75"))
        painter.drawEllipse(face)

        blink_l = max(0.0, self._semantic("morph_l_u_lid"))
        blink_r = max(0.0, self._semantic("morph_r_u_lid"))
        eye_y = height * 0.39
        for left, blink in ((True, blink_l), (False, blink_r)):
            x = width * (0.39 if left else 0.61)
            eye_h = max(2.0, height * 0.035 * (1.0 - min(1.0, blink)))
            painter.setPen(QPen(QColor("#f0f3f4"), 2))
            painter.setBrush(QColor("#111418"))
            painter.drawEllipse(QRectF(x - width * 0.065, eye_y - eye_h, width * 0.13, eye_h * 2))
            if eye_h > 4:
                painter.setBrush(QColor("#c5d7df"))
                painter.drawEllipse(QRectF(x - 5, eye_y - 5, 10, 10))

        jaw = max(0.0, self._semantic("morph_jaw_open"))
        smile_l = self._semantic("morph_lips_L_smile")
        smile_r = self._semantic("morph_lips_R_smile")
        funnel = abs(self._semantic("morph_lips_funnel"))
        mouth_y = height * 0.66
        mouth_w = width * (0.18 - min(0.10, funnel * 0.07))
        open_h = height * (0.018 + min(0.16, jaw * 0.11))
        left_y = mouth_y - smile_l * height * 0.045
        right_y = mouth_y - smile_r * height * 0.045
        painter.setPen(QPen(QColor("#2a0d12"), 5))
        mouth_path = QPainterPath(QPointF(width * 0.50 - mouth_w, left_y))
        mouth_path.quadTo(
            QPointF(width * 0.50, mouth_y + open_h),
            QPointF(width * 0.50 + mouth_w, right_y),
        )
        painter.drawPath(mouth_path)
        if jaw > 0.05:
            painter.setPen(QPen(QColor("#c98b92"), 2))
            painter.drawEllipse(QRectF(width * 0.50 - mouth_w * 0.75, mouth_y, mouth_w * 1.5, open_h * 1.5))

        painter.setPen(QColor("#d5d8dc"))
        painter.drawText(
            QRectF(10, height - 42, width - 20, 32),
            Qt.AlignmentFlag.AlignCenter,
            "Generic semantic preview",
        )
        painter.end()


class _FaceMappingDialog(QDialog):
    def __init__(
        self,
        parent: QWidget,
        *,
        scan: FbxFacialScan,
        profile: MimicProfile,
        configured: list[dict[str, Any]],
    ) -> None:
        super().__init__(parent)
        self.scan = scan
        self.profile = profile
        self.setWindowTitle("Facial retargeting")
        self.resize(1120, 700)
        root = QVBoxLayout(self)
        summary = QLabel(
            f"{len(scan.curves)} blendshape channels found; "
            f"{len(scan.animated_curves)} are animated. Multiple source rows may map to the "
            "same Dying Light target to consolidate a richer source face."
        )
        summary.setWordWrap(True)
        root.addWidget(summary)

        actions = QHBoxLayout()
        auto = QPushButton("Auto-map recognizable names")
        auto.setToolTip(
            "Uses exact target names plus conservative ARKit/viseme aliases. Ambiguous source "
            "shapes remain unmapped for manual review."
        )
        auto.clicked.connect(self._auto_map)
        clear = QPushButton("Clear mapping")
        clear.clicked.connect(lambda: self._load_rows([]))
        duplicate = QPushButton("Duplicate selected mapping")
        duplicate.setToolTip("Create a second target contribution from the same source curve.")
        duplicate.clicked.connect(self._duplicate_selected)
        remove = QPushButton("Remove selected mapping")
        remove.clicked.connect(self._remove_selected)
        actions.addWidget(auto)
        actions.addWidget(clear)
        actions.addWidget(duplicate)
        actions.addWidget(remove)
        actions.addStretch(1)
        root.addLayout(actions)

        splitter = QSplitter()
        self.table = QTableWidget(0, 5)
        self.table.setHorizontalHeaderLabels(
            ["Use", "Source FBX blendshape", "Dying Light target", "Weight", "Confidence"]
        )
        header = self.table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        header.setSectionResizeMode(2, QHeaderView.ResizeMode.Stretch)
        header.setSectionResizeMode(3, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(4, QHeaderView.ResizeMode.ResizeToContents)
        self.table.itemSelectionChanged.connect(self._preview_frame)
        splitter.addWidget(self.table)

        preview_holder = QWidget()
        preview_layout = QVBoxLayout(preview_holder)
        self.preview = _GenericFacePreview(profile)
        self.frame_label = QLabel()
        self.frame_slider = QSlider(Qt.Orientation.Horizontal)
        self.frame_slider.setRange(0, max(0, scan.frame_count - 1))
        self.frame_slider.valueChanged.connect(self._preview_frame)
        preview_layout.addWidget(self.preview, 1)
        preview_layout.addWidget(self.frame_label)
        preview_layout.addWidget(self.frame_slider)
        splitter.addWidget(preview_holder)
        splitter.setSizes([820, 300])
        root.addWidget(splitter, 1)

        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        root.addWidget(buttons)

        initial = configured or [row.to_dict() for row in auto_map_shapes(scan.animated_shape_names, profile)]
        self._load_rows(initial)
        self._preview_frame()

    def _target_combo(self, descriptor: int | None) -> QComboBox:
        combo = QComboBox()
        combo.addItem("(unmapped)", None)
        for target in self.profile.targets:
            combo.addItem(target.display_name, target.descriptor)
        if descriptor is not None:
            _set_combo_data(combo, descriptor)
        combo.currentIndexChanged.connect(self._preview_frame)
        return combo

    def _append_row(self, row: MimicMappingRow) -> None:
        index = self.table.rowCount()
        self.table.insertRow(index)
        enabled = QCheckBox()
        enabled.setChecked(row.enabled)
        enabled.toggled.connect(self._preview_frame)
        self.table.setCellWidget(index, 0, enabled)
        source = QComboBox()
        source.setEditable(True)
        for curve in self.scan.animated_curves:
            source.addItem(curve.name)
        source.setCurrentText(row.source)
        source.currentTextChanged.connect(self._preview_frame)
        self.table.setCellWidget(index, 1, source)
        self.table.setCellWidget(index, 2, self._target_combo(row.target_descriptor))
        weight = QDoubleSpinBox()
        weight.setRange(-8.0, 8.0)
        weight.setDecimals(4)
        weight.setSingleStep(0.05)
        weight.setValue(row.weight)
        weight.valueChanged.connect(self._preview_frame)
        self.table.setCellWidget(index, 3, weight)
        confidence = QTableWidgetItem(f"{row.confidence:.0%} — {row.method}")
        confidence.setFlags(confidence.flags() & ~Qt.ItemFlag.ItemIsEditable)
        confidence.setData(Qt.ItemDataRole.UserRole, (row.confidence, row.method))
        self.table.setItem(index, 4, confidence)
        self.table.setRowHeight(index, 36)

    def _load_rows(self, rows: list[dict[str, Any]]) -> None:
        self.table.setRowCount(0)
        parsed = mapping_from_payload(rows) if rows else []
        represented = {row.source for row in parsed}
        for row in parsed:
            self._append_row(row)
        for curve in self.scan.animated_curves:
            if curve.name not in represented:
                self._append_row(MimicMappingRow(
                    source=curve.name,
                    target_descriptor=self.profile.targets[0].descriptor,
                    enabled=False,
                    confidence=0.0,
                    method="unmapped",
                ))
        self._preview_frame()

    def _auto_map(self) -> None:
        self._load_rows([row.to_dict() for row in auto_map_shapes(self.scan.animated_shape_names, self.profile)])

    def _duplicate_selected(self) -> None:
        row = self.table.currentRow()
        if row < 0:
            return
        source = self.table.cellWidget(row, 1).currentText()
        target = self.table.cellWidget(row, 2).currentData()
        weight = self.table.cellWidget(row, 3).value()
        self._append_row(MimicMappingRow(
            source=source,
            target_descriptor=int(target or self.profile.targets[0].descriptor),
            weight=float(weight),
            enabled=target is not None,
            confidence=1.0,
            method="manual_duplicate",
        ))

    def _remove_selected(self) -> None:
        row = self.table.currentRow()
        if row >= 0:
            self.table.removeRow(row)
            self._preview_frame()

    def mappings(self) -> list[MimicMappingRow]:
        rows: list[MimicMappingRow] = []
        for index in range(self.table.rowCount()):
            enabled = self.table.cellWidget(index, 0).isChecked()
            source = self.table.cellWidget(index, 1).currentText().strip()
            descriptor = self.table.cellWidget(index, 2).currentData()
            if not source or descriptor is None:
                continue
            confidence_item = self.table.item(index, 4)
            confidence, method = confidence_item.data(Qt.ItemDataRole.UserRole) or (1.0, "manual")
            if enabled and method == "unmapped":
                confidence, method = 1.0, "manual"
            rows.append(MimicMappingRow(
                source=source,
                target_descriptor=int(descriptor),
                weight=float(self.table.cellWidget(index, 3).value()),
                enabled=bool(enabled),
                confidence=float(confidence),
                method=str(method),
            ))
        return rows

    def _preview_frame(self, *_args) -> None:
        frame = self.frame_slider.value()
        values: dict[int, float] = {}
        curves = self.scan.curve_by_name()
        for mapping in self.mappings():
            if not mapping.enabled:
                continue
            curve = curves.get(mapping.source)
            if curve is None or frame >= len(curve.values):
                continue
            values[mapping.target_descriptor] = values.get(mapping.target_descriptor, 0.0) + (
                curve.values[frame] * mapping.weight + mapping.bias
            )
        self.preview.set_values(values)
        self.frame_label.setText(f"Generic preview — frame {frame} / {max(0, self.scan.frame_count - 1)}")


def install_mimic_ui(controller: Any) -> None:
    """Install facial controls and advanced root selection on an existing GUI."""

    if getattr(controller, "_mimic_ui_installed", False):
        return
    # Mark the feature installed only after every widget and wrapper is ready.
    # This prevents a partially installed/invisible UI after an integration error.
    controller._mimic_scan_cache = {}

    # Project-level target-face policy.
    rig_group = _find_group(controller.target_rig_combo, "Source avatar and target rig")
    if rig_group is not None and isinstance(rig_group.layout(), QFormLayout):
        form: QFormLayout = rig_group.layout()  # type: ignore[assignment]
        controller.facial_policy_combo = controller._combo_box()
        controller.facial_policy_combo.addItem("Auto-detect from target and FBX", "auto")
        controller.facial_policy_combo.addItem("Model supports facial animations", "yes")
        controller.facial_policy_combo.addItem("Model has no facial animations", "no")
        controller.facial_policy_combo.setToolTip(
            "Auto uses a target mimic profile and checks each FBX for animated BlendShapeChannel "
            "curves. No suppresses mimic export project-wide."
        )
        controller.facial_policy_combo.currentIndexChanged.connect(controller._mark_dirty)
        form.addRow("Facial animations", controller.facial_policy_combo)

        controller.mimic_profile_combo = controller._combo_box()
        controller.mimic_profile_combo.addItem("Automatic target profile", "auto")
        controller.mimic_profile_combo.addItem("Human / infected common 46", BUILTIN_COMMON46_REF)
        controller.mimic_profile_combo.addItem("Custom .dlrmimic profile", "custom")
        controller.mimic_profile_combo.setToolTip(
            "The profile maps target morph descriptors and semantics. Custom .crig targets may "
            "embed their own profile; custom files are available in Advanced settings."
        )
        controller.mimic_profile_combo.currentIndexChanged.connect(
            lambda _index: _mimic_profile_selection_changed(controller)
        )
        form.addRow("Facial target", controller.mimic_profile_combo)

        holder = QWidget()
        holder_layout = QHBoxLayout(holder)
        holder_layout.setContentsMargins(0, 0, 0, 0)
        controller.mimic_profile_path = QLineEdit()
        controller.mimic_profile_path.setToolTip("Custom declarative .dlrmimic.json profile path.")
        controller.mimic_profile_path.textChanged.connect(controller._mark_dirty)
        browse = QPushButton("Browse…")
        browse.clicked.connect(lambda: _browse_mimic_profile(controller))
        holder_layout.addWidget(controller.mimic_profile_path, 1)
        holder_layout.addWidget(browse)
        form.addRow("Custom facial profile", holder)
        controller._mimic_profile_holder = holder
        controller._mimic_profile_label = form.labelForField(holder)
    else:
        # Defensive fallback for a customized host GUI. The controls are still
        # fully configured and will be placed in the dedicated Facial tab.
        controller.facial_policy_combo = controller._combo_box()
        controller.facial_policy_combo.addItem("Auto-detect from target and FBX", "auto")
        controller.facial_policy_combo.addItem("Model supports facial animations", "yes")
        controller.facial_policy_combo.addItem("Model has no facial animations", "no")
        controller.facial_policy_combo.currentIndexChanged.connect(controller._mark_dirty)
        controller.mimic_profile_combo = controller._combo_box()
        controller.mimic_profile_combo.addItem("Automatic target profile", "auto")
        controller.mimic_profile_combo.addItem("Human / infected common 46", BUILTIN_COMMON46_REF)
        controller.mimic_profile_combo.addItem("Custom .dlrmimic profile", "custom")
        controller.mimic_profile_combo.currentIndexChanged.connect(
            lambda _index: _mimic_profile_selection_changed(controller)
        )
        controller.mimic_profile_path = QLineEdit()
        controller.mimic_profile_path.textChanged.connect(controller._mark_dirty)
        controller._mimic_profile_holder = controller.mimic_profile_path
        controller._mimic_profile_label = None

    # A dedicated, always-visible facial workspace makes the feature discoverable
    # even before an animation has been imported.
    controller.facial_page = QWidget()
    facial_layout = QVBoxLayout(controller.facial_page)
    facial_intro = QLabel(
        "Import an FBX with animated shape keys/blendshapes, then scan and map its facial "
        "curves to the selected Dying Light mimic profile. Body-only FBXs remain supported."
    )
    facial_intro.setWordWrap(True)
    facial_layout.addWidget(facial_intro)

    if rig_group is None:
        fallback_group = QGroupBox("Target facial support")
        fallback_form = QFormLayout(fallback_group)
        fallback_form.addRow("Facial animations", controller.facial_policy_combo)
        fallback_form.addRow("Facial target", controller.mimic_profile_combo)
        fallback_form.addRow("Custom facial profile", controller.mimic_profile_path)
        facial_layout.addWidget(fallback_group)

    clip_group = QGroupBox("Selected animation facial mapping")
    clip_layout = QVBoxLayout(clip_group)
    clip_row = QHBoxLayout()
    controller.mimic_clip_combo = controller._combo_box()
    controller.mimic_clip_combo.setToolTip("Choose the imported animation whose facial curves you want to inspect.")
    controller.mimic_clip_combo.currentIndexChanged.connect(
        lambda _index: _facial_clip_selection_changed(controller)
    )
    controller.mimic_scan_button = QPushButton("Scan facial curves")
    controller.mimic_scan_button.setToolTip(
        "Inspect the selected FBX for BlendShapeChannel/DeformPercent animation without changing the project."
    )
    controller.mimic_scan_button.clicked.connect(lambda: _scan_selected_face(controller))
    controller.mimic_map_button = QPushButton("Open facial retargeting…")
    controller.mimic_map_button.setToolTip(
        "Map source blendshapes to Dying Light morph descriptors, including many-to-one consolidation."
    )
    controller.mimic_map_button.clicked.connect(lambda: _open_selected_face_mapping(controller))
    clip_row.addWidget(QLabel("Animation"))
    clip_row.addWidget(controller.mimic_clip_combo, 1)
    clip_row.addWidget(controller.mimic_scan_button)
    clip_row.addWidget(controller.mimic_map_button)
    clip_layout.addLayout(clip_row)
    controller.mimic_status = QLabel("Add an FBX animation to begin facial detection.")
    controller.mimic_status.setWordWrap(True)
    clip_layout.addWidget(controller.mimic_status)
    facial_layout.addWidget(clip_group)
    facial_layout.addStretch(1)

    help_index = next(
        (index for index in range(controller.tabs.count()) if controller.tabs.tabText(index) == "Help"),
        controller.tabs.count(),
    )
    controller.tabs.insertTab(help_index, controller.facial_page, "Facial")

    # Advanced source-bone override below the playback-range editor.
    animation_page = controller.animation_table.parentWidget()
    controller.motion_source_group = QGroupBox("Advanced root-motion source")
    motion_form = QFormLayout(controller.motion_source_group)
    controller.motion_source_bone = controller._combo_box()
    controller.motion_source_bone.setEditable(True)
    controller.motion_source_bone.setToolTip(
        "Overrides which source FBX bone position drives target bip01 translation. In motion-"
        "accumulator mode it also drives horizontal OffsetHelper translation; actor yaw remains "
        "derived from the stable body frame. Leave Default mapped Hips for normal characters."
    )
    controller.motion_source_bone.activated.connect(
        lambda _index: _motion_source_changed(controller)
    )
    if controller.motion_source_bone.lineEdit() is not None:
        controller.motion_source_bone.lineEdit().editingFinished.connect(
            lambda: _motion_source_changed(controller)
        )
    motion_form.addRow("Source bone for bip01 motion", controller.motion_source_bone)
    note = QLabel(
        "This changes the source used to author root translation; it does not rename the target "
        "bip01 track. Available only for Skeletal root and Motion accumulator exports."
    )
    note.setWordWrap(True)
    motion_form.addRow(note)
    if animation_page is not None and animation_page.layout() is not None:
        animation_page.layout().addWidget(controller.motion_source_group)

    # Help entry.
    for index in range(controller.tabs.count()):
        if controller.tabs.tabText(index) == "Help":
            page = controller.tabs.widget(index)
            if page.layout() is not None:
                button = QPushButton("Facial animations and mimic mapping")
                button.setToolTip("Open docs/FACIAL_ANIMATIONS.md")
                button.clicked.connect(lambda: controller.open_doc("FACIAL_ANIMATIONS.md"))
                page.layout().insertWidget(max(0, page.layout().count() - 1), button)
            break

    # Wrap animation-table rendering to place Body/Face directly after Root motion.
    # Important: use QTableWidget.insertColumn(). Removing and re-inserting a
    # cell widget can delete the Qt-owned C++ object and caused the Windows crash
    # when the first FBX row was created.
    original_refresh_animation_table = controller._refresh_animation_table

    def refresh_animation_table(self) -> None:
        table = self.animation_table
        # Restore the host table's native nine-column shape before it creates
        # fresh widgets. This cleanly disposes of the previous facial column.
        table.setColumnCount(9)
        original_refresh_animation_table()
        # Qt shifts the newly-created IK and Retarget cells safely and retains
        # ownership; no Python wrapper ever points at a deleted C++ widget.
        table.insertColumn(7)
        table.setHorizontalHeaderLabels([
            "Use", "Display name", "FBX source", "FBX animation", "Resource name",
            "Animation SCR", "Root motion", "Body / face", "IK", "Retarget",
        ])
        for row_index, animation in enumerate(self.project.animations):
            holder = QWidget()
            layout = QHBoxLayout(holder)
            layout.setContentsMargins(1, 1, 1, 1)
            layout.setSpacing(4)
            combo = self._combo_box()
            for label, value, tooltip in _CONTENT_ITEMS:
                combo.addItem(label, value)
                combo.setItemData(combo.count() - 1, tooltip, Qt.ItemDataRole.ToolTipRole)
            settings = _mimic_settings(animation)
            _set_combo_data(combo, settings.get("mode", "auto"))
            combo.setToolTip(
                "Choose body only, mimic only, or both. Auto exports both when animated FBX "
                "blendshapes and a target facial profile are available."
            )
            combo.currentIndexChanged.connect(
                lambda _index, aid=animation.animation_id, widget=combo: _content_mode_changed(
                    self, aid, str(widget.currentData() or "auto")
                )
            )
            face_button = QPushButton("Face…")
            face_button.setMinimumWidth(72)
            face_button.setToolTip(
                "Review detected FBX blendshapes, map them to Dying Light morph descriptors, "
                "and preview broad facial semantics."
            )
            detection = settings.get("last_detection")
            if isinstance(detection, dict):
                animated_count = int(detection.get("animated_shape_count", 0) or 0)
                shape_count = int(detection.get("shape_count", 0) or 0)
                if animated_count:
                    face_button.setText(f"Face {animated_count}")
                elif shape_count:
                    face_button.setText("Face static")
                else:
                    face_button.setText("Face none")
            else:
                face_button.setText("Face auto")
            face_button.clicked.connect(
                lambda _checked=False, aid=animation.animation_id: _open_face_mapping(self, aid)
            )
            layout.addWidget(combo, 1)
            layout.addWidget(face_button)
            table.setCellWidget(row_index, 7, holder)

        header = table.horizontalHeader()
        header.setSectionResizeMode(7, QHeaderView.ResizeMode.Interactive)
        header.setSectionResizeMode(8, QHeaderView.ResizeMode.Interactive)
        header.setSectionResizeMode(9, QHeaderView.ResizeMode.ResizeToContents)
        table.setColumnWidth(7, 230)
        table.setColumnWidth(8, 155)
        _refresh_motion_source_widget(self)
        _refresh_facial_tab(self)

    controller._refresh_animation_table = types.MethodType(refresh_animation_table, controller)

    original_selection_changed = controller._animation_selection_changed

    def animation_selection_changed(self) -> None:
        original_selection_changed()
        _refresh_motion_source_widget(self)
        animation = self._selected_animation()
        if animation is not None and hasattr(self, "mimic_clip_combo"):
            previous = self._refreshing
            self._refreshing = True
            try:
                _set_combo_data(self.mimic_clip_combo, animation.animation_id)
            finally:
                self._refreshing = previous
        _update_facial_status(self)

    controller._animation_selection_changed = types.MethodType(
        animation_selection_changed, controller
    )

    original_set_animation_field = controller._set_animation_field

    def set_animation_field(self, animation_id: str, field_name: str, value: Any) -> None:
        original_set_animation_field(animation_id, field_name, value)
        if field_name == "root_policy":
            _refresh_motion_source_widget(self)

    controller._set_animation_field = types.MethodType(set_animation_field, controller)

    original_duplicate = controller.duplicate_selected_animation

    def duplicate_selected(self) -> None:
        source = self._selected_animation()
        extensions = deepcopy(source.extensions) if source is not None else {}
        original_duplicate()
        if source is not None and self.project.animations:
            self.project.animations[-1].extensions = extensions
            self._refresh_animation_table()

    controller.duplicate_selected_animation = types.MethodType(duplicate_selected, controller)

    original_refresh_all = controller._refresh_all

    def refresh_all(self) -> None:
        original_refresh_all()
        previous = self._refreshing
        self._refreshing = True
        try:
            extensions = self.project.rig.extensions or {}
            _set_combo_data(self.facial_policy_combo, extensions.get("facial_animation_policy", "auto"))
            _set_combo_data(self.mimic_profile_combo, extensions.get("mimic_profile_ref", "auto"))
            self.mimic_profile_path.setText(str(extensions.get("mimic_profile_path", "") or ""))
        finally:
            self._refreshing = previous
        self._apply_advanced_visibility()
        _refresh_motion_source_widget(self)

    controller._refresh_all = types.MethodType(refresh_all, controller)

    original_sync = controller._sync_project_from_ui

    def sync_project_from_ui(self) -> None:
        original_sync()
        _sync_mimic_project_settings(self)

    controller._sync_project_from_ui = types.MethodType(sync_project_from_ui, controller)

    original_advanced_visibility = controller._apply_advanced_visibility

    def apply_advanced_visibility(self) -> None:
        original_advanced_visibility()
        advanced = self.advanced_mode_toggle.isChecked()
        custom = str(self.mimic_profile_combo.currentData() or "auto") == "custom"
        self._mimic_profile_holder.setVisible(advanced and custom)
        if self._mimic_profile_label is not None:
            self._mimic_profile_label.setVisible(advanced and custom)
        _refresh_motion_source_widget(self)

    controller._apply_advanced_visibility = types.MethodType(
        apply_advanced_visibility, controller
    )
    controller._mimic_ui_installed = True


def _refresh_facial_tab(controller: Any, *, preserve_selection: bool = False) -> None:
    if not hasattr(controller, "mimic_clip_combo"):
        return
    current = str(controller.mimic_clip_combo.currentData() or "") if preserve_selection else ""
    if not current:
        selected = controller._selected_animation()
        current = selected.animation_id if selected is not None else ""
    previous = controller._refreshing
    controller._refreshing = True
    try:
        controller.mimic_clip_combo.blockSignals(True)
        controller.mimic_clip_combo.clear()
        for animation in controller.project.animations:
            controller.mimic_clip_combo.addItem(animation.display_name, animation.animation_id)
        if current:
            _set_combo_data(controller.mimic_clip_combo, current)
    finally:
        controller.mimic_clip_combo.blockSignals(False)
        controller._refreshing = previous
    _update_facial_status(controller)


def _facial_clip_selection_changed(controller: Any) -> None:
    if controller._refreshing:
        return
    _update_facial_status(controller)


def _update_facial_status(controller: Any) -> None:
    if not hasattr(controller, "mimic_clip_combo"):
        return
    animation = controller.project.animation_by_id(str(controller.mimic_clip_combo.currentData() or ""))
    enabled = animation is not None
    controller.mimic_scan_button.setEnabled(enabled)
    controller.mimic_map_button.setEnabled(enabled)
    if animation is None:
        controller.mimic_status.setText("Add an FBX animation to begin facial detection.")
        return
    settings = _mimic_settings(animation)
    detection = settings.get("last_detection")
    mode = str(settings.get("mode", "auto"))
    if isinstance(detection, dict):
        shapes = int(detection.get("shape_count", 0) or 0)
        animated = int(detection.get("animated_shape_count", 0) or 0)
        mapped = sum(
            1
            for row in settings.get("mapping", [])
            if isinstance(row, dict) and row.get("enabled", True)
        )
        controller.mimic_status.setText(
            f"{animation.display_name}: {shapes} facial channel(s), {animated} animated, "
            f"{mapped} enabled mapping contribution(s). Export mode: {mode}."
        )
    else:
        controller.mimic_status.setText(
            f"{animation.display_name}: not scanned yet. Export mode: {mode}. "
            "Scan is automatic during build or can be run now."
        )


def _selected_facial_animation(controller: Any) -> Any | None:
    if not hasattr(controller, "mimic_clip_combo"):
        return None
    return controller.project.animation_by_id(str(controller.mimic_clip_combo.currentData() or ""))


def _scan_selected_face(controller: Any) -> None:
    animation = _selected_facial_animation(controller)
    if animation is None:
        return
    try:
        scan = _cached_scan(controller, animation)
        settings = _mimic_settings(animation)
        settings["last_detection"] = scan.summary()
        controller._mark_dirty()
        controller._refresh_animation_table()
        _set_combo_data(controller.mimic_clip_combo, animation.animation_id)
        _refresh_facial_tab(controller, preserve_selection=True)
    except Exception as exc:
        controller._show_error("Facial scan failed", exc)


def _open_selected_face_mapping(controller: Any) -> None:
    animation = _selected_facial_animation(controller)
    if animation is not None:
        _open_face_mapping(controller, animation.animation_id)
        _set_combo_data(controller.mimic_clip_combo, animation.animation_id)
        _refresh_facial_tab(controller, preserve_selection=True)


def _cached_scan(controller: Any, animation: Any) -> FbxFacialScan:
    path = Path(animation.source_fbx)
    stat = path.stat()
    key = (
        str(path.resolve()),
        animation.source_animation_stack,
        animation.fps,
        stat.st_mtime_ns,
        stat.st_size,
    )
    cached = controller._mimic_scan_cache.get(key)
    if cached is None:
        cached = scan_fbx_blendshapes(
            path,
            fps=animation.fps,
            animation_stack=animation.source_animation_stack or None,
        )
        controller._mimic_scan_cache[key] = cached
    return cached


def _content_mode_changed(controller: Any, animation_id: str, value: str) -> None:
    if controller._refreshing:
        return
    animation = controller.project.animation_by_id(animation_id)
    if animation is None:
        return
    _mimic_settings(animation)["mode"] = value
    controller._mark_dirty()



def _mimic_profile_selection_changed(controller: Any) -> None:
    if not controller._refreshing:
        controller._mark_dirty()
    controller._apply_advanced_visibility()


def _browse_mimic_profile(controller: Any) -> None:
    path, _ = QFileDialog.getOpenFileName(
        controller.window,
        "Choose facial mimic profile",
        controller.mimic_profile_path.text().strip() or str(controller.root),
        "DL ReAnimated Mimic Profile (*.dlrmimic.json *.json)",
    )
    if path:
        controller.mimic_profile_path.setText(path)
        _set_combo_data(controller.mimic_profile_combo, "custom")
        controller._mark_dirty()
        controller._apply_advanced_visibility()


def _open_face_mapping(controller: Any, animation_id: str) -> None:
    animation = controller.project.animation_by_id(animation_id)
    if animation is None:
        return
    _sync_mimic_project_settings(controller)
    try:
        scan = _cached_scan(controller, animation)
        profile = resolve_mimic_profile(controller.project)
        if profile is None:
            raise ValueError(
                "The selected target rig has no facial profile. Choose Human / infected common 46, "
                "select a custom .dlrmimic profile in Advanced settings, or embed one in the .crig."
            )
        if not scan.curves:
            QMessageBox.information(
                controller.window,
                "No facial channels detected",
                "This FBX contains no BlendShapeChannel objects. Body export will still work. "
                "Manual mouth animation requires animated FBX shape keys/blendshapes or a future "
                "curve-only importer.",
            )
            return
        settings = _mimic_settings(animation)
        dialog = _FaceMappingDialog(
            controller.window,
            scan=scan,
            profile=profile,
            configured=list(settings.get("mapping", [])),
        )
        if dialog.exec() == QDialog.DialogCode.Accepted:
            settings["mapping"] = [row.to_dict() for row in dialog.mappings()]
            settings["profile_id"] = profile.profile_id
            settings["last_detection"] = scan.summary()
            controller._mark_dirty()
            controller._refresh_animation_table()
    except Exception as exc:
        controller._show_error("Could not open facial mapping", exc)


def _refresh_motion_source_widget(controller: Any) -> None:
    if not hasattr(controller, "motion_source_group"):
        return
    animation = controller._selected_animation()
    advanced = controller.advanced_mode_toggle.isChecked()
    usable = (
        animation is not None
        and animation.root_policy in {"bip01", "motion"}
        and controller.project.rig.retarget_mode == "humanoid"
    )
    controller.motion_source_group.setVisible(advanced and usable)
    controller.motion_source_bone.setEnabled(bool(usable))
    previous = controller._refreshing
    controller._refreshing = True
    try:
        current = "" if animation is None else str(
            animation.extensions.get("root_motion_source_bone", "") or ""
        )
        controller.motion_source_bone.clear()
        controller.motion_source_bone.addItem("Default mapped Hips", "")
        if animation is not None:
            try:
                document = controller._source_document(animation.source_fbx)
                for name in sorted(document.limb_models):
                    controller.motion_source_bone.addItem(name, name)
            except Exception:
                pass
        _set_combo_data(controller.motion_source_bone, current)
    finally:
        controller._refreshing = previous


def _motion_source_changed(controller: Any) -> None:
    if controller._refreshing:
        return
    animation = controller._selected_animation()
    if animation is None:
        return
    data = controller.motion_source_bone.currentData()
    value = str(data if data is not None else controller.motion_source_bone.currentText()).strip()
    if value == "Default mapped Hips":
        value = ""
    animation.extensions["root_motion_source_bone"] = value
    controller._mark_dirty()


__all__ = ["install_mimic_ui"]
