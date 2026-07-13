from __future__ import annotations

"""Root/Bip01 and mapped-.crig editor embedded in Animations."""

from pathlib import Path
from typing import Any, Callable

from ..bone_maps import BoneMapPair, GenericBoneMap, skeleton_signature
from ..chrome_rig import ChromeRig
from ..oracle.binary_fbx_mixamo import _FbxDocument
from ..retarget_mapping import auto_map_crig_to_fbx, mapping_rows_for_ui
from ..root_mapping import (
    RootMappingSelection,
    choose_hierarchy_root,
    parent_names_from_smd,
    read_smd_hierarchy,
    resolve_source_root,
)


class CrigMappingWorkspace:
    def __init__(
        self,
        qt: dict[str, Any],
        *,
        controller: Any,
        mark_dirty: Callable[[], None],
    ) -> None:
        self.qt = qt
        self.controller = controller
        self.mark_dirty = mark_dirty
        self._refreshing_root = False
        self.widget = qt["QWidget"]()
        layout = qt["QVBoxLayout"](self.widget)
        intro = qt["QLabel"](
            "Bip01/root mapping is available for every animation and every target. The default "
            "uses the mapped Hips/source root and a literal target bip01 when present; custom SMD "
            "targets automatically fall back to pelvis or the best hierarchy root. Custom .crig "
            "animations can also map every target bone to a different source skeleton."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        top = qt["QHBoxLayout"]()
        self.clip_combo = qt["QComboBox"]()
        self.clip_combo.currentIndexChanged.connect(self.refresh)
        top.addWidget(qt["QLabel"]("Animation clip"))
        top.addWidget(self.clip_combo, 1)
        layout.addLayout(top)

        root_group = qt["QGroupBox"]("Bip01 / root motion mapping")
        root_form = qt["QFormLayout"](root_group)
        self.root_source_combo = qt["QComboBox"]()
        self.root_source_combo.setToolTip(
            "Bone in the animation FBX whose motion drives the target skeletal root. Automatic "
            "uses the humanoid Hips mapping, common Hips/root names, then the largest source root."
        )
        self.root_target_combo = qt["QComboBox"]()
        self.root_target_combo.setToolTip(
            "Target SMD or .crig bone that receives the Bip01/root role. Automatic uses bip01, "
            "then pelvis, then the descriptor-backed root with the largest hierarchy."
        )
        self.root_source_combo.currentIndexChanged.connect(self._root_selection_changed)
        self.root_target_combo.currentIndexChanged.connect(self._root_selection_changed)
        root_form.addRow("Source FBX root", self.root_source_combo)
        root_form.addRow("Target Bip01/root bone", self.root_target_combo)
        self.root_status = qt["QLabel"]()
        self.root_status.setWordWrap(True)
        root_form.addRow(self.root_status)
        layout.addWidget(root_group)

        actions = qt["QHBoxLayout"]()
        self.auto_button = qt["QPushButton"]("Auto-map .crig bones")
        self.auto_button.clicked.connect(self.auto_map)
        self.clear_button = qt["QPushButton"]("Clear .crig mapping")
        self.clear_button.clicked.connect(self.clear_mapping)
        self.load_button = qt["QPushButton"]("Load .dlrbmap.json…")
        self.load_button.clicked.connect(self.load_mapping)
        self.save_button = qt["QPushButton"]("Save mapping…")
        self.save_button.clicked.connect(self.save_mapping)
        actions.addWidget(self.auto_button)
        actions.addWidget(self.clear_button)
        actions.addWidget(self.load_button)
        actions.addWidget(self.save_button)
        actions.addStretch(1)
        layout.addLayout(actions)

        filters = qt["QHBoxLayout"]()
        self.filter_edit = qt["QLineEdit"]()
        self.filter_edit.setPlaceholderText("Filter target, source, role, or status")
        self.filter_edit.textChanged.connect(self._filter_rows)
        self.only_unmapped = qt["QCheckBox"]("Show only unmapped")
        self.only_unmapped.toggled.connect(self._filter_rows)
        filters.addWidget(self.filter_edit, 1)
        filters.addWidget(self.only_unmapped)
        layout.addLayout(filters)

        self.table = qt["QTableWidget"](0, 7)
        self.table.setHorizontalHeaderLabels(
            (
                "Target .crig bone", "Target parent", "Role", "Source FBX bone",
                "Confidence", "Method", "Status",
            )
        )
        header = self.table.horizontalHeader()
        header.setSectionResizeMode(0, qt["QHeaderView"].Stretch)
        header.setSectionResizeMode(1, qt["QHeaderView"].Stretch)
        header.setSectionResizeMode(2, qt["QHeaderView"].ResizeToContents)
        header.setSectionResizeMode(3, qt["QHeaderView"].Stretch)
        for column in (4, 5, 6):
            header.setSectionResizeMode(column, qt["QHeaderView"].ResizeToContents)
        self.table.verticalHeader().setVisible(False)
        layout.addWidget(self.table, 1)
        self.status = qt["QLabel"]()
        self.status.setWordWrap(True)
        layout.addWidget(self.status)
        self.reload_clips()

    @property
    def project(self):
        return self.controller.project

    def reload_clips(self) -> None:
        current = self.clip_combo.currentData()
        self.clip_combo.blockSignals(True)
        self.clip_combo.clear()
        for row in self.project.animations:
            self.clip_combo.addItem(row.display_name, row.animation_id)
        index = self.clip_combo.findData(current)
        self.clip_combo.setCurrentIndex(index if index >= 0 else 0)
        self.clip_combo.blockSignals(False)
        self.refresh()

    def _selected_animation(self):
        animation_id = self.clip_combo.currentData()
        return self.project.animation_by_id(str(animation_id)) if animation_id else None

    def _load_rig(self) -> ChromeRig:
        if self.project.rig.retarget_mode != "exact":
            raise ValueError(
                "Per-bone .crig mapping is only used with a custom .crig target. Humanoid source "
                "bones remain editable in the normal Retargeting tab; Bip01/root mapping above "
                "still applies to humanoid builds."
            )
        path = Path(self.project.rig.target_rig_path)
        if not path.is_file():
            raise FileNotFoundError(f"Custom .crig file was not found: {path}")
        return ChromeRig.load(path)

    def _document(self, animation) -> _FbxDocument:
        source = Path(animation.source_fbx)
        if not source.is_file():
            raise FileNotFoundError(f"Animation FBX was not found: {source}")
        document = _FbxDocument(source)
        if animation.source_animation_stack:
            document.select_animation_stack(animation.source_animation_stack)
        return document

    def _target_bone_names(self) -> tuple[list[str], dict[str, str | None]]:
        if self.project.rig.retarget_mode == "exact":
            rig = self._load_rig()
            names = [bone.name for bone in rig.bones]
            parents = {
                bone.name: (rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None)
                for bone in rig.bones
            }
            return names, parents
        smd = Path(self.project.rig.canonical_smd)
        if not smd.is_file():
            raise FileNotFoundError(f"Target SMD was not found: {smd}")
        rows = read_smd_hierarchy(smd)
        return [row.name for row in rows], parent_names_from_smd(rows)

    def _current_profile(self, animation) -> GenericBoneMap | None:
        profile_id = str(animation.mapping_profile_id or "")
        payload = self.project.mapping_profiles.get(profile_id)
        if not payload or payload.get("format") != "dl-reanimated-bone-map":
            return None
        return GenericBoneMap.from_dict(payload)

    def _store_profile(self, animation, profile: GenericBoneMap) -> None:
        self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
        animation.mapping_profile_id = profile.profile_id
        self.mark_dirty()

    def auto_map(self) -> None:
        animation = self._selected_animation()
        if animation is None:
            return
        try:
            rig = self._load_rig()
            document = self._document(animation)
            profile = auto_map_crig_to_fbx(
                rig, document.limb_models.keys(), document.parent_by_name
            )
            self._store_profile(animation, profile)
            self.refresh()
        except Exception as exc:
            self.qt["QMessageBox"].critical(
                self.controller.window, "Auto-map failed", str(exc)
            )

    def clear_mapping(self) -> None:
        animation = self._selected_animation()
        if animation is None:
            return
        try:
            rig = self._load_rig()
            document = self._document(animation)
            profile = GenericBoneMap.create(
                f"Manual map: {animation.display_name}",
                rig.skeleton_hash,
                skeleton_signature(
                    (name, document.parent_by_name.get(name))
                    for name in sorted(document.limb_models)
                ),
                source_rig_ref=rig.rig_id,
            )
            self._store_profile(animation, profile)
            self.refresh()
        except Exception as exc:
            self.status.setText(f"Could not clear the mapping: {exc}")

    def _refresh_root_controls(self, animation, document: _FbxDocument) -> None:
        self._refreshing_root = True
        try:
            selection = RootMappingSelection.from_animation(animation)
            source_names = sorted(document.limb_models, key=str.casefold)
            target_names, target_parents = self._target_bone_names()

            self.root_source_combo.blockSignals(True)
            self.root_source_combo.clear()
            self.root_source_combo.addItem(
                "Automatic — mapped Hips or best source root", ""
            )
            for name in source_names:
                self.root_source_combo.addItem(name, name)
            source_index = self.root_source_combo.findData(selection.source_bone)
            self.root_source_combo.setCurrentIndex(max(0, source_index))
            self.root_source_combo.blockSignals(False)

            self.root_target_combo.blockSignals(True)
            self.root_target_combo.clear()
            self.root_target_combo.addItem(
                "Automatic — bip01, pelvis, or best target root", ""
            )
            for name in target_names:
                self.root_target_combo.addItem(name, name)
            target_index = self.root_target_combo.findData(selection.target_bone)
            self.root_target_combo.setCurrentIndex(max(0, target_index))
            self.root_target_combo.blockSignals(False)

            source_auto, source_method = resolve_source_root(
                source_names,
                document.parent_by_name,
                requested_bone="",
                humanoid_aliases=self._humanoid_aliases(animation),
            )
            target_auto = choose_hierarchy_root(target_names, target_parents)
            source_rendered = selection.source_bone or f"{source_auto} ({source_method})"
            target_rendered = selection.target_bone or f"{target_auto} (automatic)"
            literal = any(name.casefold() == "bip01" for name in target_names)
            note = (
                "Literal bip01 exists in the target."
                if literal
                else "Target has no literal bip01; the selected/fallback target bone will receive the Bip01 role."
            )
            self.root_status.setText(
                f"Resolved source: {source_rendered}. Resolved target: {target_rendered}. {note} "
                "Manual choices are saved per animation clip."
            )
        finally:
            self._refreshing_root = False

    def _humanoid_aliases(self, animation) -> dict[str, str]:
        profile_id = str(animation.mapping_profile_id or "")
        payload = self.project.mapping_profiles.get(profile_id, {})
        if payload.get("format") != "dl-reanimated-retarget-profile":
            return {}
        try:
            from ..retarget_profiles import SourceBoneMappingProfile
            return SourceBoneMappingProfile.from_dict(payload).canonical_aliases()
        except Exception:
            return {}

    def _root_selection_changed(self) -> None:
        if self._refreshing_root:
            return
        animation = self._selected_animation()
        if animation is None:
            return
        selection = RootMappingSelection(
            source_bone=str(self.root_source_combo.currentData() or ""),
            target_bone=str(self.root_target_combo.currentData() or ""),
        )
        selection.store(animation)
        self.mark_dirty()
        try:
            self._refresh_root_controls(animation, self._document(animation))
        except Exception as exc:
            self.root_status.setText(str(exc))

    def refresh(self) -> None:
        qt = self.qt
        animation = self._selected_animation()
        if animation is None:
            self.table.setRowCount(0)
            self.status.setText(
                "Add an animation FBX first. Files with a different skeleton are allowed: "
                "they will be added with an editable auto-map instead of being rejected."
            )
            self.root_status.setText("Add an animation FBX first.")
            return
        try:
            document = self._document(animation)
            self._refresh_root_controls(animation, document)
        except Exception as exc:
            self.table.setRowCount(0)
            self.root_status.setText(str(exc))
            self.status.setText(str(exc))
            return

        exact_mode = self.project.rig.retarget_mode == "exact"
        for widget in (self.auto_button, self.clear_button, self.load_button, self.save_button):
            widget.setEnabled(exact_mode)
        self.table.setEnabled(exact_mode)
        if not exact_mode:
            self.table.setRowCount(0)
            self.status.setText(
                "Humanoid body and finger roles are edited in the Retargeting tab. The Bip01/root "
                "mapping above is active for this clip and prevents custom SMDs without a literal "
                "bip01 from failing with a vague KeyError."
            )
            return

        try:
            rig = self._load_rig()
            profile, rows = mapping_rows_for_ui(
                rig,
                document.limb_models.keys(),
                document.parent_by_name,
                self._current_profile(animation),
            )
        except Exception as exc:
            self.table.setRowCount(0)
            self.status.setText(str(exc))
            return
        source_names = sorted(document.limb_models, key=str.casefold)
        self.table.setRowCount(len(rows))
        for index, row in enumerate(rows):
            self.table.setItem(index, 0, qt["QTableWidgetItem"](row["target_bone"]))
            self.table.setItem(index, 1, qt["QTableWidgetItem"](row["target_parent"] or ""))
            self.table.setItem(index, 2, qt["QTableWidgetItem"](row["role"]))
            combo = qt["QComboBox"]()
            combo.addItem("Unmapped (keep bind pose)", "")
            for name in source_names:
                combo.addItem(name, name)
            combo.setCurrentIndex(max(0, combo.findData(row["source_bone"])))
            combo.currentIndexChanged.connect(
                lambda _value, bone=row["target_bone"], widget=combo: self._set_pair(
                    bone, str(widget.currentData())
                )
            )
            self.table.setCellWidget(index, 3, combo)
            self.table.setItem(index, 4, qt["QTableWidgetItem"](f"{row['confidence']:.2f}"))
            self.table.setItem(index, 5, qt["QTableWidgetItem"](row["method"]))
            self.table.setItem(
                index, 6,
                qt["QTableWidgetItem"](
                    "Mapped"
                    if row["source_bone"]
                    else (
                        "Review: body role is unmapped"
                        if row["role"]
                        else "Bind pose (helper/extra)"
                    )
                ),
            )
        mapped = len(profile.pairs)
        errors = profile.validate()
        mapped_source_names = {pair.target_bone for pair in profile.pairs}
        source_body_names = {
            row.target_bone
            for row in auto_map_crig_to_fbx(
                rig, document.limb_models.keys(), document.parent_by_name
            ).pairs
        }
        unused_body_sources = sorted(source_body_names - mapped_source_names, key=str.casefold)
        review = (
            f" Review {len(unused_body_sources)} recognized source body bone(s) not used by this map: "
            + ", ".join(unused_body_sources[:8])
            + ("…" if len(unused_body_sources) > 8 else "")
            + "."
            if unused_body_sources
            else " All recognized source body bones are covered."
        )
        self.status.setText(
            f"Mapped {mapped} of {len(rig.bones)} target bones. "
            f"Recognized source coverage: "
            f"{len(source_body_names & mapped_source_names)}/{len(source_body_names)} bones. "
            + ("Mapping is structurally valid." if not errors else "Validation: " + "; ".join(errors))
            + review
            + " Unmapped helpers keep their bind-local transform and inherit parent motion; "
            "use the filter above to review them."
        )
        self._filter_rows()

    def _filter_rows(self, *_args) -> None:
        text = self.filter_edit.text().strip().casefold()
        only_unmapped = self.only_unmapped.isChecked()
        for row in range(self.table.rowCount()):
            combo = self.table.cellWidget(row, 3)
            mapped = bool(combo and combo.currentData())
            values = [
                self.table.item(row, column).text()
                for column in (0, 1, 2, 4, 5, 6)
                if self.table.item(row, column) is not None
            ]
            if combo is not None:
                values.append(combo.currentText())
            matches = not text or text in " ".join(values).casefold()
            self.table.setRowHidden(row, not matches or (only_unmapped and mapped))

    def _set_pair(self, target_rig_bone: str, source_fbx_bone: str) -> None:
        animation = self._selected_animation()
        if animation is None:
            return
        try:
            rig = self._load_rig()
            document = self._document(animation)
            profile = self._current_profile(animation) or auto_map_crig_to_fbx(
                rig, document.limb_models.keys(), document.parent_by_name
            )
            profile.pairs = [
                row for row in profile.pairs if row.source_bone != target_rig_bone
            ]
            if source_fbx_bone:
                profile.pairs = [
                    row for row in profile.pairs if row.target_bone != source_fbx_bone
                ]
                target = next(bone for bone in rig.bones if bone.name == target_rig_bone)
                profile.pairs.append(
                    BoneMapPair(
                        target.descriptor,
                        target_rig_bone,
                        source_fbx_bone,
                        1.0,
                        "manual",
                    )
                )
            profile.pairs.sort(
                key=lambda pair: next(
                    bone.index for bone in rig.bones if bone.name == pair.source_bone
                )
            )
            self._store_profile(animation, profile)
            self.refresh()
        except Exception as exc:
            self.status.setText(str(exc))

    def load_mapping(self) -> None:
        animation = self._selected_animation()
        if animation is None:
            return
        path, _ = self.qt["QFileDialog"].getOpenFileName(
            self.controller.window,
            "Load mapped-rig bone profile",
            "",
            "DL ReAnimated Bone Map (*.dlrbmap.json);;JSON (*.json)",
        )
        if not path:
            return
        try:
            profile = GenericBoneMap.load(path)
            rig = self._load_rig()
            if profile.source_skeleton_hash and profile.source_skeleton_hash != rig.skeleton_hash:
                raise ValueError("Mapping targets a different .crig skeleton")
            self._store_profile(animation, profile)
            self.refresh()
        except Exception as exc:
            self.qt["QMessageBox"].critical(
                self.controller.window, "Could not load mapping", str(exc)
            )

    def save_mapping(self) -> None:
        animation = self._selected_animation()
        profile = self._current_profile(animation) if animation else None
        if profile is None:
            self.qt["QMessageBox"].information(
                self.controller.window, "No mapping", "Auto-map or assign at least one bone first."
            )
            return
        path, _ = self.qt["QFileDialog"].getSaveFileName(
            self.controller.window,
            "Save mapped-rig bone profile",
            f"{profile.name}.dlrbmap.json",
            "DL ReAnimated Bone Map (*.dlrbmap.json)",
        )
        if path:
            profile.save(path)


__all__ = ["CrigMappingWorkspace"]
