from __future__ import annotations

"""Root/Bip01 and mapped-.crig editor embedded in Animations."""

from pathlib import Path
from typing import Any, Callable, Mapping

from ..animation_targets import resolve_animation_target
from ..automatic_retarget import (
    DL2_ADVANCED_RIG_ID,
    AutomaticRetargetValidation,
    build_automatic_retarget_plan,
    build_dl2_advanced_body_map_with_local_recipe,
    revalidate_verified_dl2_advanced_body_map,
)
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
from ..retarget_recipes import (
    build_reviewed_retarget_recipe_from_profile,
    default_retarget_recipe_store,
    load_retarget_recipe,
    materialize_reviewed_retarget_recipe,
    revalidate_materialized_retarget_recipe,
    retarget_recipe_has_reviewed_provenance,
    save_retarget_recipe,
)
from ..retarget_routing import select_exact_solver
from ..root_mapping import (
    RootMappingSelection,
    choose_hierarchy_root,
    parent_names_from_smd,
    read_smd_hierarchy,
    resolve_source_root,
)
from ..target_retarget_policy import (
    TargetRetargetPolicy,
    build_target_retarget_policy,
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

_LEGACY_AUTO_BUTTON_LABEL = "Auto-map .crig bones"
_DL2_ADVANCED_AUTO_BUTTON_LABEL = "Regenerate safe DL2 body map"
_DEFAULT_REVIEW_TOOLTIP = (
    "Marks automatic cross-rig suggestions as explicitly reviewed. Incompatible "
    "hierarchies cannot build through the mapped solver until approved."
)


def _certificate_payload(
    value: AutomaticRetargetValidation | Mapping[str, Any],
) -> Mapping[str, Any]:
    if isinstance(value, AutomaticRetargetValidation):
        return value.certificate
    certificate = value.get("certificate")
    return certificate if isinstance(certificate, Mapping) else value


def format_verified_dl2_body_map_summary(
    value: AutomaticRetargetValidation | Mapping[str, Any],
) -> str:
    """Render the compact, stable five-line summary used by the mapping workspace."""

    certificate = _certificate_payload(value)
    mapped = int(certificate.get("mapped_body_row_count", 0) or 0)
    bind = int(
        certificate.get(
            "bind_default_row_count",
            certificate.get("bind_row_count", 0),
        )
        or 0
    )
    spatial = int(
        certificate.get(
            "spatial_only_mapping_count",
            certificate.get("spatial_only_row_count", 0),
        )
        or 0
    )
    status = str(
        certificate.get(
            "certificate_status",
            certificate.get("status", "unknown"),
        )
        or "unknown"
    )
    return "\n".join(
        (
            "Verified DL2 body map",
            f"{mapped} body rows mapped",
            f"{bind} target rows held at bind",
            f"{spatial} spatial-only mappings",
            f"certificate: {status}",
        )
    )


def verified_dl2_solver_preview(
    document_or_analysis: Any,
    rig: ChromeRig,
    profile: GenericBoneMap,
    policy: TargetRetargetPolicy,
) -> tuple[AutomaticRetargetValidation, Any]:
    """Live-revalidate a verified profile before asking routing for a preview."""

    verification = revalidate_verified_dl2_advanced_body_map(
        profile,
        document_or_analysis,
        rig,
        policy,
    )
    compatibility = classify_target_compatibility(document_or_analysis, rig)
    solver = select_exact_solver(
        compatibility,
        profile,
        automatic_verification=verification,
    )
    return verification, solver


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
        self._auto_map_busy = False
        self._fresh_verified_profiles: dict[str, AutomaticRetargetValidation] = {}
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
        self.auto_button = qt["QPushButton"](_LEGACY_AUTO_BUTTON_LABEL)
        self.auto_button.clicked.connect(self.auto_map)
        self.clear_button = qt["QPushButton"]("Clear .crig mapping")
        self.clear_button.clicked.connect(self.clear_mapping)
        self.load_button = qt["QPushButton"]("Load .dlrbmap.json…")
        self.load_button.clicked.connect(self.load_mapping)
        self.save_button = qt["QPushButton"]("Save mapping…")
        self.save_button.clicked.connect(self.save_mapping)
        self.import_recipe_button = qt["QPushButton"]("Import retarget recipe…")
        self.import_recipe_button.setToolTip(
            "Import an explicitly reviewed recipe and live-revalidate it against "
            "this source, target, bind pose, and policy before storing it locally."
        )
        self.import_recipe_button.clicked.connect(self.import_retarget_recipe)
        self.export_recipe_button = qt["QPushButton"]("Export reviewed recipe…")
        self.export_recipe_button.setToolTip(
            "Export only explicitly reviewed manual source assignments as a "
            "versioned local retarget recipe."
        )
        self.export_recipe_button.clicked.connect(self.export_retarget_recipe)
        self.review_button = qt["QPushButton"]("Approve mapped solver")
        self.review_button.setToolTip(_DEFAULT_REVIEW_TOOLTIP)
        self.review_button.clicked.connect(self.approve_mapping)
        actions.addWidget(self.auto_button)
        actions.addWidget(self.clear_button)
        actions.addWidget(self.load_button)
        actions.addWidget(self.save_button)
        actions.addWidget(self.import_recipe_button)
        actions.addWidget(self.export_recipe_button)
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
        cached_loader = getattr(self.controller, "_source_document", None)
        document = (
            cached_loader(str(source))
            if callable(cached_loader)
            else FbxDocument(source)
        )
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
        getattr(self, "_fresh_verified_profiles", {}).pop(profile.profile_id, None)
        self.project.mapping_profiles[profile.profile_id] = profile.to_dict()
        animation.mapping_profile_id = profile.profile_id
        self.mark_dirty()
        refresh_table = getattr(self.controller, "_refresh_animation_table", None)
        if callable(refresh_table):
            refresh_table()

    def _is_dl2_advanced_selection(self, animation: Any) -> bool:
        return self._target_selection(animation).rig_ref == DL2_ADVANCED_RIG_ID

    def _target_retarget_policy(self, rig: ChromeRig) -> TargetRetargetPolicy:
        return build_target_retarget_policy(
            rig,
            game_id=str(getattr(self.project, "game_id", "") or ""),
            clip_domain="body",
        )

    def _empty_advanced_display_profile(
        self,
        animation: Any,
        rig: ChromeRig,
        document: FbxDocument,
    ) -> GenericBoneMap:
        """Return an unsaved placeholder so refresh never invokes the legacy mapper."""

        profile = GenericBoneMap.create(
            f"Pending safe map: {animation.display_name}",
            rig.skeleton_hash,
            skeleton_signature(
                (name, document.parent_by_name.get(name))
                for name in sorted(document.limb_models)
            ),
            source_rig_ref=rig.rig_id,
        )
        profile.target_bind_hash = rig.skeleton_hash
        return profile

    def _configure_mapping_actions(
        self,
        *,
        exact_mode: bool,
        advanced_target: bool,
        saved_profile: GenericBoneMap | None,
    ) -> None:
        self.auto_button.setText(
            _DL2_ADVANCED_AUTO_BUTTON_LABEL
            if advanced_target
            else _LEGACY_AUTO_BUTTON_LABEL
        )
        for widget in (
            self.auto_button,
            self.clear_button,
            self.load_button,
            self.save_button,
        ):
            widget.setEnabled(exact_mode)
        for widget in (
            getattr(self, "import_recipe_button", None),
            getattr(self, "export_recipe_button", None),
        ):
            if widget is not None:
                widget.setVisible(exact_mode)
                widget.setEnabled(exact_mode)

        origin = mapping_profile_origin(saved_profile)
        verified_advanced = advanced_target and origin == "automatic_verified"
        legacy_advanced_repair = advanced_target and origin == "automatic_repair"
        self.review_button.setVisible(not verified_advanced)
        self.review_button.setEnabled(
            exact_mode
            and saved_profile is not None
            and not verified_advanced
            and not legacy_advanced_repair
        )
        if legacy_advanced_repair:
            self.review_button.setToolTip(
                "Legacy DL2 automatic repair maps cannot be bulk-approved. Use "
                "Regenerate safe DL2 body map."
            )
        else:
            self.review_button.setToolTip(_DEFAULT_REVIEW_TOOLTIP)

    def _set_dl2_advanced_profile_status(
        self,
        document: FbxDocument,
        rig: ChromeRig,
        saved_profile: GenericBoneMap | None,
    ) -> bool:
        """Handle statuses that must not fall through to legacy spatial coverage."""

        if saved_profile is None:
            self.status.setText(
                "No safe DL2 body map is saved for this clip.\n"
                "Use Regenerate safe DL2 body map in Root & .crig Mapping."
            )
            return True

        origin = mapping_profile_origin(saved_profile)
        if origin == "automatic_repair":
            self.status.setText(
                "This clip has a legacy DL2 automatic_repair map. It cannot be "
                "bulk-approved.\nUse Regenerate safe DL2 body map to replace it with "
                "a complete, live-verified body bridge."
            )
            return True
        if origin != "automatic_verified":
            return False

        fresh_verification = getattr(self, "_fresh_verified_profiles", {}).get(
            saved_profile.profile_id
        )
        if (
            fresh_verification is not None
            and fresh_verification.ok
            and fresh_verification.live_revalidated
        ):
            self.status.setText(format_verified_dl2_body_map_summary(fresh_verification))
            return True

        try:
            policy = self._target_retarget_policy(rig)
            verification, solver = verified_dl2_solver_preview(
                document,
                rig,
                saved_profile,
                policy,
            )
        except Exception as exc:
            self.status.setText(
                f"Verified DL2 body map could not be revalidated: {exc}\n"
                "Use Regenerate safe DL2 body map or inspect this clip in Root & "
                ".crig Mapping."
            )
            return True

        if verification.ok and verification.live_revalidated and solver.build_allowed:
            self.status.setText(format_verified_dl2_body_map_summary(verification))
            return True

        reason = (
            verification.errors[0]
            if verification.errors
            else solver.blocking_error
            or "The live source/target certificate did not pass."
        )
        self.status.setText(
            f"Verified DL2 body map needs regeneration: {reason}\n"
            "Use Regenerate safe DL2 body map; automatic verification cannot be "
            "replaced by bulk approval."
        )
        return True

    def auto_map(self) -> None:
        animation = self._selected_animation()
        if animation is None:
            return
        runner = getattr(self.controller, "background_tasks", None)
        if runner is None:
            self._auto_map_sync(animation)
            return
        if runner.busy or self._auto_map_busy:
            self.status.setText(
                "Wait for the current animation operation to finish before regenerating the map."
            )
            return
        advanced_target = False
        try:
            advanced_target = self._is_dl2_advanced_selection(animation)
            selection = self._target_selection(animation)
            rig_path = str(selection.rig_path or "")
            if not rig_path:
                registry = getattr(self.controller, "rig_registry", None)
                resolved = registry.resolve(selection.rig_ref) if registry is not None else None
                rig_path = str(resolved or "")
            if not rig_path:
                raise FileNotFoundError("No CRIG path can be resolved for the selected target.")
            source_path = str(Path(animation.source_fbx).resolve())
            stack_name = str(animation.source_animation_stack or "")
            game_id = str(getattr(self.project, "game_id", "") or "")
            animation_id = str(animation.animation_id)
        except Exception as exc:
            self._auto_map_failed(advanced_target, exc)
            return

        self._set_auto_map_busy(True)
        self.status.setText("Generating the retarget map in the background…")

        def work(progress):
            progress("Loading the source animation…")
            document = FbxDocument(Path(source_path))
            if stack_name:
                document.select_animation_stack(stack_name)
            progress("Loading the target rig…")
            rig = ChromeRig.load(rig_path)
            is_advanced = advanced_target or rig.rig_id == DL2_ADVANCED_RIG_ID
            if is_advanced:
                progress("Building and verifying the DL2 body map…")
                policy = build_target_retarget_policy(
                    rig, game_id=game_id, clip_domain="body"
                )
                profile = build_dl2_advanced_body_map_with_local_recipe(
                    document, rig, policy
                )
                verification = revalidate_verified_dl2_advanced_body_map(
                    profile, document, rig, policy
                )
            else:
                progress("Matching source and target bones…")
                profile = auto_map_crig_to_fbx(
                    rig,
                    document.limb_models.keys(),
                    document.parent_by_name,
                    **source_mapping_evidence(document),
                )
                verification = None
            return document, profile, is_advanced, verification

        def succeeded(result) -> None:
            document, profile, is_advanced, verification = result
            current = self.project.animation_by_id(animation_id)
            if current is None:
                return
            source_cache = getattr(self.controller, "_source_cache", None)
            if isinstance(source_cache, dict):
                source_cache[str(Path(current.source_fbx).resolve())] = document
            self._store_profile(current, profile)
            if verification is not None:
                cache = getattr(self, "_fresh_verified_profiles", None)
                if cache is None:
                    cache = {}
                    self._fresh_verified_profiles = cache
                cache[profile.profile_id] = verification
            self.refresh()
            self.status.setText(
                "Verified DL2 body map regenerated."
                if is_advanced
                else "Automatic .crig mapping completed."
            )

        def failed(failure) -> None:
            self._auto_map_failed(advanced_target, failure)

        if not runner.start(
            work,
            progress=self.status.setText,
            succeeded=succeeded,
            failed=failed,
            finished=lambda: self._set_auto_map_busy(False),
        ):
            self._set_auto_map_busy(False)
            self.status.setText("Another animation operation is already running.")

    def _auto_map_sync(self, animation: Any) -> None:
        """Fallback for non-Qt test hosts that do not expose a task runner."""

        advanced_target = False
        try:
            advanced_target = self._is_dl2_advanced_selection(animation)
            rig = self._load_rig()
            document = self._document(animation)
            if advanced_target or rig.rig_id == DL2_ADVANCED_RIG_ID:
                advanced_target = True
                profile = build_dl2_advanced_body_map_with_local_recipe(
                    document, rig, self._target_retarget_policy(rig)
                )
            else:
                profile = auto_map_crig_to_fbx(
                    rig,
                    document.limb_models.keys(),
                    document.parent_by_name,
                    **source_mapping_evidence(document),
                )
            self._store_profile(animation, profile)
            self.refresh()
        except Exception as exc:
            self._auto_map_failed(advanced_target, exc)

    def _auto_map_failed(self, advanced_target: bool, failure: Any) -> None:
        message = (
            failure.display_message(False)
            if hasattr(failure, "display_message")
            else str(failure)
        )
        if advanced_target:
            self.status.setText(
                f"Safe DL2 body map was not generated: {message}\n"
                "Open Root & .crig Mapping for this clip, confirm the selected "
                "DL2 advanced target package, then regenerate. The existing mapping "
                "was not changed."
            )
        else:
            self.qt["QMessageBox"].critical(
                self.controller.window, "Auto-map failed", message
            )

    def _set_auto_map_busy(self, busy: bool) -> None:
        self._auto_map_busy = bool(busy)
        for widget in (
            getattr(self, "auto_button", None),
            getattr(self, "clear_button", None),
            getattr(self, "load_button", None),
            getattr(self, "import_recipe_button", None),
        ):
            if widget is not None:
                widget.setEnabled(not busy)

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
            self.auto_button.setText(_LEGACY_AUTO_BUTTON_LABEL)
            for widget in (
                getattr(self, "import_recipe_button", None),
                getattr(self, "export_recipe_button", None),
            ):
                if widget is not None:
                    widget.setVisible(False)
            self.review_button.setVisible(True)
            self.review_button.setEnabled(False)
            self.status.setText(
                "Add an animation FBX first. Files with a different skeleton are allowed: "
                "they will be added with an editable auto-map instead of being rejected."
            )
            self.root_status.setText("Add an animation FBX first.")
            return
        advanced_target = False
        try:
            advanced_target = self._is_dl2_advanced_selection(animation)
            self.auto_button.setText(
                _DL2_ADVANCED_AUTO_BUTTON_LABEL
                if advanced_target
                else _LEGACY_AUTO_BUTTON_LABEL
            )
            document = self._document(animation)
            self._refresh_root_controls(animation, document)
        except Exception as exc:
            self.table.setRowCount(0)
            self.root_status.setText(str(exc))
            self.status.setText(str(exc))
            return

        exact_mode = self._target_selection(animation).retarget_mode == "exact"
        self.table.setEnabled(True)

        rig: ChromeRig | None = None
        profile: GenericBoneMap | None = None
        saved_profile: GenericBoneMap | None = None
        helper_profile_description = ""
        try:
            if exact_mode:
                rig = self._load_rig(animation)
                advanced_target = advanced_target or rig.rig_id == DL2_ADVANCED_RIG_ID
                saved_profile = self._current_profile(animation)
                display_profile = saved_profile
                if advanced_target and display_profile is None:
                    display_profile = self._empty_advanced_display_profile(
                        animation,
                        rig,
                        document,
                    )
                profile, rows = mapping_rows_for_ui(
                    rig,
                    document.limb_models.keys(),
                    document.parent_by_name,
                    display_profile,
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
        self._configure_mapping_actions(
            exact_mode=exact_mode,
            advanced_target=advanced_target,
            saved_profile=saved_profile,
        )
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
        if (
            saved_profile is not None
            and isinstance(
                saved_profile.extensions.get("local_retarget_recipe"), dict
            )
        ):
            recipe_validation = revalidate_materialized_retarget_recipe(
                saved_profile,
                document,
                rig,
                self._target_retarget_policy(rig),
                clip_domain="body",
            )
            if not recipe_validation.ok:
                self.status.setText(
                    "Reviewed retarget recipe needs attention: "
                    + (
                        recipe_validation.errors[0]
                        if recipe_validation.errors
                        else "live recipe revalidation did not pass"
                    )
                    + "\nThe saved mapping was preserved; review or import a "
                    "matching recipe before export."
                )
                self._filter_rows()
                return
        if advanced_target and self._set_dl2_advanced_profile_status(
            document,
            rig,
            saved_profile,
        ):
            self._filter_rows()
            return

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
            profile.extensions.pop("local_retarget_recipe", None)
            animation_extensions = getattr(animation, "extensions", None)
            if isinstance(animation_extensions, dict):
                animation_extensions.pop(
                    "local_retarget_recipe_validation", None
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
        advanced_target = bool(
            animation is not None and self._is_dl2_advanced_selection(animation)
        )
        origin = mapping_profile_origin(profile)
        if advanced_target and origin == "automatic_verified":
            self.status.setText(
                "Verified DL2 body maps are authorized only by live certificate "
                "revalidation; bulk approval is not available."
            )
            return
        if advanced_target and origin == "automatic_repair":
            self.status.setText(
                "Legacy DL2 automatic_repair maps cannot be bulk-approved. Use "
                "Regenerate safe DL2 body map."
            )
            return
        profile.extensions.pop("local_retarget_recipe", None)
        animation_extensions = getattr(animation, "extensions", None)
        if isinstance(animation_extensions, dict):
            animation_extensions.pop(
                "local_retarget_recipe_validation", None
            )
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

    def import_retarget_recipe(self) -> None:
        """Import and cache a reviewed recipe only after live revalidation."""

        animation = self._selected_animation()
        if animation is None:
            return
        path, _ = self.qt["QFileDialog"].getOpenFileName(
            self.controller.window,
            "Import reviewed retarget recipe",
            "",
            "DL ReAnimated Retarget Recipe (*.dlrrecipe.json);;JSON (*.json)",
        )
        if not path:
            return
        try:
            recipe = load_retarget_recipe(path)
            if not retarget_recipe_has_reviewed_provenance(recipe):
                raise ValueError(
                    "The recipe has no explicit reviewed provenance and cannot be reused."
                )
            rig = self._load_rig()
            document = self._document(animation)
            policy = self._target_retarget_policy(rig)
            profile = materialize_reviewed_retarget_recipe(
                recipe,
                document,
                rig,
                policy,
                clip_domain="body",
                profile_name=f"Reviewed recipe: {animation.display_name}",
            )
            store = default_retarget_recipe_store()
            store.save(recipe)
            self._store_profile(animation, profile)
            self.refresh()
        except Exception as exc:
            self.qt["QMessageBox"].critical(
                self.controller.window,
                "Could not import retarget recipe",
                str(exc),
            )

    def export_retarget_recipe(self) -> None:
        """Export a live-validated recipe from explicitly reviewed corrections."""

        animation = self._selected_animation()
        profile = self._current_profile(animation) if animation else None
        if animation is None or profile is None:
            self.qt["QMessageBox"].information(
                self.controller.window,
                "No reviewed mapping",
                "Make and review a manual mapping correction before exporting a recipe.",
            )
            return
        try:
            rig = self._load_rig()
            document = self._document(animation)
            policy = self._target_retarget_policy(rig)
            fresh = build_automatic_retarget_plan(
                document,
                rig,
                policy,
                clip_domain="body",
            )
            recipe = build_reviewed_retarget_recipe_from_profile(
                fresh,
                profile,
                document,
                rig,
                policy,
                notes=f"Reviewed mapping for {animation.display_name}",
            )
            path, _ = self.qt["QFileDialog"].getSaveFileName(
                self.controller.window,
                "Export reviewed retarget recipe",
                f"{animation.display_name}.dlrrecipe.json",
                "DL ReAnimated Retarget Recipe (*.dlrrecipe.json)",
            )
            if not path:
                return
            destination = save_retarget_recipe(recipe, path)
            default_retarget_recipe_store().save(recipe)
            self.status.setText(
                f"Exported and cached reviewed retarget recipe: {destination}"
            )
        except Exception as exc:
            self.qt["QMessageBox"].critical(
                self.controller.window,
                "Could not export retarget recipe",
                str(exc),
            )


__all__ = [
    "CrigMappingWorkspace",
    "format_verified_dl2_body_map_summary",
    "mapping_row_visible",
    "shared_source_status",
    "verified_dl2_solver_preview",
]
