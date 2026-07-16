from __future__ import annotations

"""Root/Bip01 and mapped-.crig editor embedded in Animations."""

from pathlib import Path
from typing import Any, Callable

from ..animation_targets import resolve_animation_target
from ..bone_maps import (
    BoneMapPair,
    COMPONENT_POLICIES,
    GenericBoneMap,
    MAPPING_KINDS,
    TRANSFER_POLICIES,
    mapping_profile_origin,
    set_mapping_profile_origin,
    skeleton_signature,
)
from ..chrome_rig import ChromeRig
from ..fbx_preflight import classify_target_compatibility
from ..helper_profiles import (
    recognized_helper_names,
    suggested_helper_source,
)
from ..helper_retarget import (
    HelperRetargetRule,
    helper_rules_from_dicts,
    helper_rules_to_dicts,
)
from ..fbx_core import FbxDocument
from ..retarget_mapping import (
    auto_map_crig_to_fbx,
    mapping_rows_for_ui,
    source_mapping_evidence,
)
from ..retarget_profiles import HUMANOID_ROLES
from ..retarget_routing import select_exact_solver
from ..root_mapping import (
    RootMappingSelection,
    choose_hierarchy_root,
    parent_names_from_smd,
    read_smd_hierarchy,
    resolve_source_root,
)


_TRANSFER_LABELS = {
    "default": "Default body solver",
    "rest_relative": "Rest-relative",
    "rotation_delta": "Rotation delta",
    "global_bind_basis": "Global bind basis",
    "copy_local": "Copy local (advanced)",
    "bind": "Bind (leave target unchanged)",
}
_COMPONENT_LABELS = {
    "rotation": "Rotation",
    "translation": "Translation",
    "rotation_translation": "Rotation + translation",
    "scale": "Scale",
    "full_transform": "Full transform",
}


def shared_source_status(source_name: str, mapping_kind: str, use_count: int) -> str:
    if not source_name:
        return "Unmapped — keep bind / inherit parent"
    if mapping_kind == "helper_override":
        return (
            f"Helper override — shared by {use_count} targets"
            if use_count > 1
            else "Helper override"
        )
    return "Mapped — shared source" if use_count > 1 else "Mapped"


def mapping_row_visible(
    *,
    is_helper: bool,
    show_helpers: bool,
    matches_filter: bool,
    only_unmapped: bool,
    mapped: bool,
) -> bool:
    """Pure visibility rule used by the retargeting table and GUI tests."""

    if is_helper and not show_helpers:
        return False
    if not matches_filter:
        return False
    return not (only_unmapped and mapped)


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
        self.review_button = qt["QPushButton"]("Approve mapped solver")
        self.review_button.setToolTip(
            "Marks automatic cross-rig suggestions as explicitly reviewed. Incompatible "
            "hierarchies cannot build through the mapped solver until approved."
        )
        self.review_button.clicked.connect(self.approve_mapping)
        actions.addWidget(self.auto_button)
        actions.addWidget(self.clear_button)
        actions.addWidget(self.load_button)
        actions.addWidget(self.save_button)
        actions.addWidget(self.review_button)
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

        helper_options = qt["QHBoxLayout"]()
        self.show_helper_bones = qt["QCheckBox"]("Show helper bones")
        self.show_helper_bones.setToolTip(
            "Shows helper targets from the selected target rig, including refcamera, "
            "eyecamera, hand holders, and twist helpers. Mapping remains optional."
        )
        self.show_helper_bones.toggled.connect(self._filter_rows)
        self.advanced_helpers = qt["QCheckBox"]("Show advanced helper policies")
        self.advanced_helpers.setToolTip(
            "Shows mapping-kind, transfer, and component controls. Helper mappings remain opt-in."
        )
        self.advanced_helpers.toggled.connect(self._advanced_helpers_changed)
        helper_options.addWidget(self.show_helper_bones)
        helper_options.addWidget(self.advanced_helpers)
        helper_options.addStretch(1)
        layout.addLayout(helper_options)

        self.table = qt["QTableWidget"](0, 10)
        self.table.setHorizontalHeaderLabels(
            (
                "Target .crig bone", "Target parent", "Role", "Source FBX bone",
                "Mapping kind", "Transfer", "Components", "Confidence", "Method",
                "Status",
            )
        )
        header = self.table.horizontalHeader()
        header.setSectionResizeMode(0, qt["QHeaderView"].Stretch)
        header.setSectionResizeMode(1, qt["QHeaderView"].Stretch)
        header.setSectionResizeMode(2, qt["QHeaderView"].ResizeToContents)
        header.setSectionResizeMode(3, qt["QHeaderView"].Stretch)
        for column in (4, 5, 6, 7, 8, 9):
            header.setSectionResizeMode(column, qt["QHeaderView"].ResizeToContents)
        self.table.verticalHeader().setVisible(False)
        layout.addWidget(self.table, 1)
        self.status = qt["QLabel"]()
        self.status.setWordWrap(True)
        layout.addWidget(self.status)
        self._advanced_helpers_changed(False)
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

    def _target_selection(self, animation: Any | None = None):
        selected = animation or self._selected_animation()
        if selected is None:
            raise ValueError("Select an animation clip first.")
        return resolve_animation_target(
            self.project,
            selected,
            rig_paths=getattr(self.controller, "_rig_paths_by_ref", {}),
        )

    def _load_rig(self, animation: Any | None = None) -> ChromeRig:
        selection = self._target_selection(animation)
        if selection.retarget_mode != "exact":
            raise ValueError(
                "Per-bone .crig mapping is only used with a custom .crig target. Humanoid source "
                "bones remain editable in the normal Retargeting tab; Bip01/root mapping above "
                "still applies to humanoid builds."
            )
        path = Path(selection.rig_path) if selection.rig_path else None
        if path is None or not path.is_file():
            registry = getattr(self.controller, "rig_registry", None)
            resolved = registry.resolve(selection.rig_ref) if registry is not None else None
            path = Path(resolved) if resolved is not None else path
        if path is None:
            raise FileNotFoundError(
                f"No CRIG path can be resolved for animation target {selection.rig_ref!r}."
            )
        if not path.is_file():
            raise FileNotFoundError(
                f"Animation target .crig was not found: {path}. Choose another target or "
                "select Inherit project target."
            )
        return ChromeRig.load(path)

    def _document(self, animation) -> FbxDocument:
        source = Path(animation.source_fbx)
        if not source.is_file():
            raise FileNotFoundError(f"Animation FBX was not found: {source}")
        document = FbxDocument(source)
        if animation.source_animation_stack:
            document.select_animation_stack(animation.source_animation_stack)
        return document

    def _target_bone_names(
        self, animation: Any | None = None
    ) -> tuple[list[str], dict[str, str | None]]:
        if self._target_selection(animation).retarget_mode == "exact":
            rig = self._load_rig(animation)
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
        refresh_table = getattr(self.controller, "_refresh_animation_table", None)
        if callable(refresh_table):
            refresh_table()

    def auto_map(self) -> None:
        animation = self._selected_animation()
        if animation is None:
            return
        try:
            rig = self._load_rig()
            document = self._document(animation)
            profile = auto_map_crig_to_fbx(
                rig,
                document.limb_models.keys(),
                document.parent_by_name,
                **source_mapping_evidence(document),
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
                origin="manually_reviewed",
            )
            self._store_profile(animation, profile)
            self.refresh()
        except Exception as exc:
            self.status.setText(f"Could not clear the mapping: {exc}")

    def _refresh_root_controls(self, animation, document: FbxDocument) -> None:
        self._refreshing_root = True
        try:
            selection = RootMappingSelection.from_animation(animation)
            source_names = sorted(document.limb_models, key=str.casefold)
            target_names, target_parents = self._target_bone_names(animation)

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

    def _advanced_helpers_changed(self, *_args) -> None:
        if not hasattr(self, "table"):
            return
        visible = bool(self.advanced_helpers.isChecked())
        for column in (4, 5, 6):
            self.table.setColumnHidden(column, not visible)

    def _normal_helper_rows(
        self, animation: Any, document: FbxDocument
    ) -> tuple[list[dict[str, Any]], str]:
        hierarchy = read_smd_hierarchy(Path(self.project.rig.canonical_smd))
        by_name = {row.name: row for row in hierarchy}
        parent_names = parent_names_from_smd(hierarchy)
        rules = {
            rule.target_bone: rule
            for rule in helper_rules_from_dicts(
                animation.extensions.get("helper_retarget_rules", ()) or ()
            )
        }
        source_names = sorted(document.limb_models, key=str.casefold)
        rows: list[dict[str, Any]] = []
        for name in recognized_helper_names(by_name):
            rule = rules.get(name)
            suggestion = suggested_helper_source(name, source_names)
            rows.append(
                {
                    "target_bone": name,
                    "target_parent": parent_names.get(name),
                    "role": "helper",
                    "source_bone": rule.source_bone if rule else "",
                    "mapping_kind": "helper_override",
                    "transfer_policy": (
                        rule.transfer_policy if rule else "rest_relative"
                    ),
                    "component_policy": (
                        rule.component_policy
                        if rule
                        else (suggestion[1] if suggestion else "full_transform")
                    ),
                    "confidence": 1.0 if rule else 0.0,
                    "method": "manual" if rule else "suggested_not_enabled",
                    "suggested_source": suggestion[0] if suggestion else "",
                    "target_helper": True,
                }
            )
        return (
            rows,
            "Helper bones come directly from the selected target rig's canonical SMD.",
        )

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

        exact_mode = self._target_selection(animation).retarget_mode == "exact"
        for widget in (
            self.auto_button,
            self.clear_button,
            self.load_button,
            self.save_button,
            self.review_button,
        ):
            widget.setEnabled(exact_mode)
        self.table.setEnabled(True)

        rig: ChromeRig | None = None
        profile: GenericBoneMap | None = None
        helper_profile_description = ""
        try:
            if exact_mode:
                rig = self._load_rig(animation)
                profile, rows = mapping_rows_for_ui(
                    rig,
                    document.limb_models.keys(),
                    document.parent_by_name,
                    self._current_profile(animation),
                    **source_mapping_evidence(document),
                )
            else:
                rows, helper_profile_description = self._normal_helper_rows(
                    animation, document
                )
        except Exception as exc:
            self.table.setRowCount(0)
            self.status.setText(str(exc))
            return
        source_names = sorted(document.limb_models, key=str.casefold)
        normal_body_targets_by_source: dict[str, list[str]] = {}
        if not exact_mode:
            aliases = self._humanoid_aliases(animation)
            for role in HUMANOID_ROLES:
                source_name = aliases.get(role.canonical_source_name)
                if source_name:
                    normal_body_targets_by_source.setdefault(source_name, []).append(
                        role.target_name
                    )
        source_use_counts = {
            name: sum(1 for row in rows if row["source_bone"] == name)
            + len(normal_body_targets_by_source.get(name, ()))
            for name in source_names
        }
        targets_by_source = {
            name: [
                *normal_body_targets_by_source.get(name, ()),
                *(row["target_bone"] for row in rows if row["source_bone"] == name),
            ]
            for name in source_names
        }
        self.table.setRowCount(len(rows))
        for index, row in enumerate(rows):
            self.table.setItem(index, 0, qt["QTableWidgetItem"](row["target_bone"]))
            self.table.setItem(index, 1, qt["QTableWidgetItem"](row["target_parent"] or ""))
            self.table.setItem(index, 2, qt["QTableWidgetItem"](row["role"]))
            source_combo = qt["QComboBox"]()
            suggested = str(row.get("suggested_source", "") or "")
            unmapped_label = "Unmapped (keep bind / inherit parent)"
            if suggested:
                unmapped_label += f" — Suggested: {suggested} (not enabled)"
            source_combo.addItem(unmapped_label, "")
            for name in source_names:
                source_combo.addItem(name, name)
            source_combo.setCurrentIndex(
                max(0, source_combo.findData(row["source_bone"]))
            )
            if row["source_bone"]:
                driven = targets_by_source[row["source_bone"]]
                source_combo.setToolTip(
                    f"{row['source_bone']} drives:\n- " + "\n- ".join(driven)
                )
            self.table.setCellWidget(index, 3, source_combo)

            kind_combo = qt["QComboBox"]()
            for value in MAPPING_KINDS:
                kind_combo.addItem(
                    "Helper override" if value == "helper_override" else "Body/bone map",
                    value,
                )
            kind_combo.setCurrentIndex(max(0, kind_combo.findData(row["mapping_kind"])))
            kind_combo.setEnabled(exact_mode)
            self.table.setCellWidget(index, 4, kind_combo)

            transfer_combo = qt["QComboBox"]()
            for value in TRANSFER_POLICIES:
                if row["mapping_kind"] == "helper_override" and value == "default":
                    continue
                transfer_combo.addItem(_TRANSFER_LABELS[value], value)
            transfer_combo.setCurrentIndex(
                max(0, transfer_combo.findData(row["transfer_policy"]))
            )
            self.table.setCellWidget(index, 5, transfer_combo)

            component_combo = qt["QComboBox"]()
            for value in COMPONENT_POLICIES:
                component_combo.addItem(_COMPONENT_LABELS[value], value)
            component_combo.setCurrentIndex(
                max(0, component_combo.findData(row["component_policy"]))
            )
            self.table.setCellWidget(index, 6, component_combo)

            callback = (
                lambda _value,
                bone=row["target_bone"],
                source=source_combo,
                kind=kind_combo,
                transfer=transfer_combo,
                components=component_combo,
                exact=exact_mode: self._row_mapping_changed(
                    exact,
                    bone,
                    str(source.currentData() or ""),
                    str(kind.currentData() or "bone"),
                    str(transfer.currentData() or "rest_relative"),
                    str(components.currentData() or "full_transform"),
                )
            )
            for widget in (source_combo, kind_combo, transfer_combo, component_combo):
                widget.currentIndexChanged.connect(callback)

            self.table.setItem(index, 7, qt["QTableWidgetItem"](f"{row['confidence']:.2f}"))
            self.table.setItem(index, 8, qt["QTableWidgetItem"](row["method"]))
            status_text = shared_source_status(
                row["source_bone"],
                row["mapping_kind"],
                source_use_counts.get(row["source_bone"], 0),
            )
            if not row["source_bone"] and suggested:
                status_text = "Suggested — not enabled"
            if row.get("review_required"):
                status_text += " — review required"
            status_item = qt["QTableWidgetItem"](status_text)
            evidence = row.get("mapping_evidence") or {}
            top = evidence.get("top_candidate") or {}
            runner = evidence.get("runner_up_candidate") or {}
            if evidence:
                status_item.setToolTip(
                    "Automatic evidence\n"
                    f"Top: {top.get('source_fbx_bone', '')} "
                    f"({float(top.get('score', 0.0)):.2f})\n"
                    f"Runner-up: {runner.get('source_fbx_bone', '')} "
                    f"({float(runner.get('score', 0.0)):.2f})\n"
                    f"Margin: {float(evidence.get('score_margin', 0.0)):.2f}\n"
                    f"Review state: {row.get('review_state', '')}\n"
                    + str(evidence.get("spatial_evidence_note", ""))
                )
            self.table.setItem(index, 9, status_item)

        self._advanced_helpers_changed()
        if not exact_mode:
            active = sum(1 for row in rows if row["source_bone"])
            shared = sum(1 for count in source_use_counts.values() if count > 1)
            visibility_note = (
                "Helper rows are visible below."
                if self.show_helper_bones.isChecked()
                else "Enable Show helper bones to display and map them."
            )
            self.status.setText(
                f"{helper_profile_description} Active helper overrides: {active}. "
                f"Shared source bones: {shared}. Suggestions are never enabled automatically. "
                "Humanoid body/finger retargeting and root-motion policy remain unchanged. "
                + visibility_note
            )
            self._filter_rows()
            return

        assert rig is not None and profile is not None
        mapped = len(profile.pairs)
        errors = profile.validate()
        evidence_rows = profile.extensions.get("automatic_mapping_evidence_v2", ()) or ()
        review_required_count = sum(
            1 for row in evidence_rows if bool(row.get("review_required", False))
        )
        mapped_source_names = {pair.source_fbx_bone for pair in profile.pairs}
        source_body_names = {
            row.source_fbx_bone
            for row in auto_map_crig_to_fbx(
                rig,
                document.limb_models.keys(),
                document.parent_by_name,
                **source_mapping_evidence(document),
            ).pairs
        }
        unused_body_sources = sorted(source_body_names - mapped_source_names, key=str.casefold)
        compatibility = classify_target_compatibility(document, rig)
        solver = select_exact_solver(compatibility, profile)
        solver_text = (
            f"Selected solver: {solver.selected_engine} / {solver.selected_policy}."
            if solver.build_allowed
            else f"Build blocked: {solver.blocking_error}"
        )
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
            "use the filter above to review them. "
            + (
                f"Automatic evidence requires review on {review_required_count} row(s). "
                if review_required_count
                else "Automatic evidence has no ambiguous mapped rows. "
            )
            + f"Map origin: {mapping_profile_origin(profile)}. {solver_text}"
        )
        self._filter_rows()

    def _row_mapping_changed(
        self,
        exact_mode: bool,
        target_rig_bone: str,
        source_fbx_bone: str,
        mapping_kind: str,
        transfer_policy: str,
        component_policy: str,
    ) -> None:
        if exact_mode:
            self._set_pair(
                target_rig_bone,
                source_fbx_bone,
                mapping_kind=mapping_kind,
                transfer_policy=transfer_policy,
                component_policy=component_policy,
            )
        else:
            self._set_helper_pair(
                target_rig_bone,
                source_fbx_bone,
                transfer_policy=transfer_policy,
                component_policy=component_policy,
            )

    def _filter_rows(self, *_args) -> None:
        text = self.filter_edit.text().strip().casefold()
        only_unmapped = self.only_unmapped.isChecked()
        show_helpers = self.show_helper_bones.isChecked()
        for row in range(self.table.rowCount()):
            combo = self.table.cellWidget(row, 3)
            mapped = bool(combo and combo.currentData())
            role_item = self.table.item(row, 2)
            is_helper = bool(
                role_item and role_item.text().strip().casefold() == "helper"
            )
            values = [
                self.table.item(row, column).text()
                for column in (0, 1, 2, 7, 8, 9)
                if self.table.item(row, column) is not None
            ]
            for column in (3, 4, 5, 6):
                widget = self.table.cellWidget(row, column)
                if widget is not None:
                    values.append(widget.currentText())
            matches = not text or text in " ".join(values).casefold()
            self.table.setRowHidden(
                row,
                not mapping_row_visible(
                    is_helper=is_helper,
                    show_helpers=show_helpers,
                    matches_filter=matches,
                    only_unmapped=only_unmapped,
                    mapped=mapped,
                ),
            )

    def _set_pair(
        self,
        target_rig_bone: str,
        source_fbx_bone: str,
        *,
        mapping_kind: str = "bone",
        transfer_policy: str = "default",
        component_policy: str = "full_transform",
    ) -> None:
        animation = self._selected_animation()
        if animation is None:
            return
        try:
            rig = self._load_rig()
            document = self._document(animation)
            profile = self._current_profile(animation) or auto_map_crig_to_fbx(
                rig,
                document.limb_models.keys(),
                document.parent_by_name,
                **source_mapping_evidence(document),
            )
            profile.pairs = [
                row
                for row in profile.pairs
                if row.target_rig_bone != target_rig_bone
            ]
            if source_fbx_bone:
                target = next(bone for bone in rig.bones if bone.name == target_rig_bone)
                profile.pairs.append(
                    BoneMapPair(
                        target.descriptor,
                        target_rig_bone,
                        source_fbx_bone,
                        1.0,
                        "manual",
                        transfer_policy,
                        component_policy,
                        mapping_kind,
                    )
                )
            profile.pairs.sort(
                key=lambda pair: next(
                    bone.index
                    for bone in rig.bones
                    if bone.name == pair.target_rig_bone
                )
            )
            set_mapping_profile_origin(profile, "manually_reviewed")
            self._store_profile(animation, profile)
            self.refresh()
        except Exception as exc:
            self.status.setText(str(exc))

    def _set_helper_pair(
        self,
        target_bone: str,
        source_bone: str,
        *,
        transfer_policy: str,
        component_policy: str,
    ) -> None:
        animation = self._selected_animation()
        if animation is None:
            return
        try:
            rules = helper_rules_from_dicts(
                animation.extensions.get("helper_retarget_rules", ()) or ()
            )
            rules = [rule for rule in rules if rule.target_bone != target_bone]
            if source_bone:
                rules.append(
                    HelperRetargetRule(
                        target_bone,
                        source_bone,
                        transfer_policy,
                        component_policy,
                    )
                )
            animation.extensions["helper_retarget_rules"] = helper_rules_to_dicts(rules)
            self.mark_dirty()
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
            expected_bind = profile.target_bind_hash or profile.source_skeleton_hash
            if expected_bind and expected_bind != rig.skeleton_hash:
                raise ValueError("Mapping targets a different .crig skeleton")
            self._store_profile(animation, profile)
            self.refresh()
        except Exception as exc:
            self.qt["QMessageBox"].critical(
                self.controller.window, "Could not load mapping", str(exc)
            )

    def approve_mapping(self) -> None:
        animation = self._selected_animation()
        profile = self._current_profile(animation) if animation else None
        if profile is None:
            self.qt["QMessageBox"].information(
                self.controller.window,
                "No mapping",
                "Run Auto-map or assign at least one target bone before approving the mapped solver.",
            )
            return
        set_mapping_profile_origin(profile, "manually_reviewed")
        self._store_profile(animation, profile)
        self.refresh()

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


__all__ = ["CrigMappingWorkspace", "mapping_row_visible", "shared_source_status"]
