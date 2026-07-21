from __future__ import annotations

import inspect
from types import SimpleNamespace

from dlanm2_gui.automatic_retarget import (
    DL2_ADVANCED_RIG_ID,
    AutomaticRetargetValidation,
)
from dlanm2_gui.bone_maps import (
    BoneMapPair,
    GenericBoneMap,
    mapping_profile_origin,
)
from dlanm2_gui.workspaces import animation_mapping as mapping_module
from dlanm2_gui.workspaces.animation_mapping import (
    CrigMappingWorkspace,
    format_verified_dl2_body_map_summary,
    verified_dl2_solver_preview,
)


class _Status:
    def __init__(self) -> None:
        self.value = ""

    def setText(self, value: str) -> None:
        self.value = value


class _Button:
    def __init__(self) -> None:
        self.text = ""
        self.enabled = True
        self.visible = True
        self.tooltip = ""

    def setText(self, value: str) -> None:
        self.text = value

    def setEnabled(self, value: bool) -> None:
        self.enabled = bool(value)

    def setVisible(self, value: bool) -> None:
        self.visible = bool(value)

    def setToolTip(self, value: str) -> None:
        self.tooltip = value


def _certificate(*, status: str = "pass") -> dict[str, object]:
    return {
        "format": "dl2_advanced_body_bridge_v1",
        "mapped_body_row_count": 52,
        "bind_default_row_count": 219,
        "spatial_only_mapping_count": 0,
        "certificate_status": status,
        "status": status,
    }


def _complete_verified_profile() -> GenericBoneMap:
    profile = GenericBoneMap.create(
        "Verified DL2 body map",
        "target-hash",
        "source-hash",
        source_rig_ref=DL2_ADVANCED_RIG_ID,
        origin="automatic_verified",
    )
    profile.pairs = [
        BoneMapPair(
            target_rig_descriptor=index + 1,
            target_rig_bone=f"target_{index}",
            source_fbx_bone=f"source_{index}" if index < 52 else "",
            method=(
                "automatic_verified:direct"
                if index < 52
                else "automatic_verified:static_bind"
            ),
            transfer_policy="global_bind_basis" if index < 52 else "bind",
            component_policy="rotation",
            review_state=(
                "automatic_accepted" if index < 52 else "intentionally_unmapped"
            ),
        )
        for index in range(271)
    ]
    profile.extensions["automatic_retarget_certificate"] = _certificate()
    return profile


def _workspace_for_auto_map(profile: GenericBoneMap):
    critical_messages: list[str] = []
    animation = SimpleNamespace(
        display_name="Run",
        mapping_profile_id="",
    )
    project = SimpleNamespace(
        game_id="dying_light_2",
        mapping_profiles={},
    )
    rig = SimpleNamespace(rig_id=DL2_ADVANCED_RIG_ID)
    document = SimpleNamespace(limb_models={}, parent_by_name={})
    workspace = object.__new__(CrigMappingWorkspace)
    workspace.controller = SimpleNamespace(project=project, window=object())
    workspace.mark_dirty = lambda: None
    workspace.qt = {
        "QMessageBox": SimpleNamespace(
            critical=lambda _parent, _title, message: critical_messages.append(message),
            information=lambda *_args: None,
        )
    }
    workspace.status = _Status()
    workspace._selected_animation = lambda: animation
    workspace._target_selection = lambda _animation=None: SimpleNamespace(
        rig_ref=DL2_ADVANCED_RIG_ID,
        retarget_mode="exact",
    )
    workspace._load_rig = lambda: rig
    workspace._document = lambda _animation: document
    workspace._target_retarget_policy = lambda _rig: SimpleNamespace(policy_id="safe")
    workspace.refresh = lambda: None
    return workspace, animation, project, critical_messages, profile


def test_verified_summary_is_exactly_five_stable_lines() -> None:
    summary = format_verified_dl2_body_map_summary(_certificate())

    assert summary.splitlines() == [
        "Verified DL2 body map",
        "52 body rows mapped",
        "219 target rows held at bind",
        "0 spatial-only mappings",
        "certificate: pass",
    ]


def test_verified_solver_preview_revalidates_before_routing(
    monkeypatch,
) -> None:
    calls: list[str] = []
    verification = AutomaticRetargetValidation(
        "pass",
        certificate=_certificate(),
        live_revalidated=True,
    )
    solver = SimpleNamespace(build_allowed=True)

    def revalidate(profile, document, rig, policy):
        calls.append("revalidate")
        return verification

    def compatibility(document, rig):
        assert calls == ["revalidate"]
        calls.append("compatibility")
        return {"classification": "mapped", "required_missing_bones": ["pelvis"]}

    def select(_compatibility, _profile, *, automatic_verification):
        assert automatic_verification is verification
        assert calls == ["revalidate", "compatibility"]
        calls.append("solver")
        return solver

    monkeypatch.setattr(
        mapping_module,
        "revalidate_verified_dl2_advanced_body_map",
        revalidate,
    )
    monkeypatch.setattr(mapping_module, "classify_target_compatibility", compatibility)
    monkeypatch.setattr(mapping_module, "select_exact_solver", select)
    monkeypatch.setattr(
        mapping_module,
        "auto_map_crig_to_fbx",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(
            AssertionError("legacy spatial mapper must not run")
        ),
    )

    result = verified_dl2_solver_preview(object(), object(), object(), object())

    assert result == (verification, solver)
    assert calls == ["revalidate", "compatibility", "solver"]


def test_advanced_auto_action_stores_complete_verified_profile(
    monkeypatch,
) -> None:
    profile = _complete_verified_profile()
    workspace, animation, project, critical_messages, _ = _workspace_for_auto_map(
        profile
    )
    calls: list[tuple[object, object, object]] = []

    def build(document, rig, policy):
        calls.append((document, rig, policy))
        return profile

    monkeypatch.setattr(
        mapping_module,
        "build_dl2_advanced_body_map_with_local_recipe",
        build,
    )
    monkeypatch.setattr(
        mapping_module,
        "auto_map_crig_to_fbx",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(
            AssertionError("advanced target must not use legacy auto-map")
        ),
    )

    workspace.auto_map()

    assert len(calls) == 1
    assert animation.mapping_profile_id == profile.profile_id
    stored = project.mapping_profiles[profile.profile_id]
    assert stored["extensions"]["origin"] == "automatic_verified"
    assert len(stored["pairs"]) == 271
    assert sum(bool(row["source_fbx_bone"]) for row in stored["pairs"]) == 52
    assert critical_messages == []


def test_advanced_generation_failure_is_focused_and_non_modal(
    monkeypatch,
) -> None:
    existing = _complete_verified_profile()
    workspace, animation, project, critical_messages, _ = _workspace_for_auto_map(
        existing
    )
    project.mapping_profiles[existing.profile_id] = existing.to_dict()
    animation.mapping_profile_id = existing.profile_id
    monkeypatch.setattr(
        mapping_module,
        "build_dl2_advanced_body_map_with_local_recipe",
        lambda *_args: (_ for _ in ()).throw(ValueError("certificate mismatch")),
    )

    workspace.auto_map()

    assert critical_messages == []
    assert "certificate mismatch" in workspace.status.value
    assert "Open Root & .crig Mapping" in workspace.status.value
    assert "existing mapping was not changed" in workspace.status.value
    assert animation.mapping_profile_id == existing.profile_id
    assert len(project.mapping_profiles) == 1


def test_advanced_action_state_hides_verified_approval_and_blocks_legacy_repair() -> None:
    workspace = object.__new__(CrigMappingWorkspace)
    workspace.auto_button = _Button()
    workspace.clear_button = _Button()
    workspace.load_button = _Button()
    workspace.save_button = _Button()
    workspace.import_recipe_button = _Button()
    workspace.export_recipe_button = _Button()
    workspace.review_button = _Button()
    verified = _complete_verified_profile()

    workspace._configure_mapping_actions(
        exact_mode=True,
        advanced_target=True,
        saved_profile=verified,
    )

    assert workspace.auto_button.text == "Regenerate safe DL2 body map"
    assert workspace.import_recipe_button.visible
    assert workspace.import_recipe_button.enabled
    assert workspace.export_recipe_button.visible
    assert workspace.export_recipe_button.enabled
    assert not workspace.review_button.visible
    assert not workspace.review_button.enabled

    workspace._configure_mapping_actions(
        exact_mode=True,
        advanced_target=False,
        saved_profile=None,
    )
    assert workspace.import_recipe_button.visible
    assert workspace.import_recipe_button.enabled
    assert workspace.export_recipe_button.visible
    assert workspace.export_recipe_button.enabled

    workspace._configure_mapping_actions(
        exact_mode=False,
        advanced_target=False,
        saved_profile=None,
    )
    assert not workspace.import_recipe_button.visible
    assert not workspace.export_recipe_button.visible

    repair = GenericBoneMap.create(
        "Old repair", "target", "source", origin="automatic_repair"
    )
    workspace._configure_mapping_actions(
        exact_mode=True,
        advanced_target=True,
        saved_profile=repair,
    )
    assert workspace.review_button.visible
    assert not workspace.review_button.enabled
    assert "cannot be bulk-approved" in workspace.review_button.tooltip

    workspace._configure_mapping_actions(
        exact_mode=True,
        advanced_target=False,
        saved_profile=repair,
    )
    assert workspace.auto_button.text == "Auto-map .crig bones"
    assert workspace.review_button.visible
    assert workspace.review_button.enabled


def test_advanced_workspace_exposes_typed_recipe_import_export_controls() -> None:
    constructor = inspect.getsource(CrigMappingWorkspace.__init__)
    importer = inspect.getsource(CrigMappingWorkspace.import_retarget_recipe)
    exporter = inspect.getsource(CrigMappingWorkspace.export_retarget_recipe)

    assert "Import retarget recipe…" in constructor
    assert "Export reviewed recipe…" in constructor
    assert "load_retarget_recipe" in importer
    assert "materialize_reviewed_retarget_recipe" in importer
    assert "default_retarget_recipe_store" in importer
    assert importer.index("materialize_reviewed_retarget_recipe") < importer.index(
        "store.save(recipe)"
    ) < importer.index("self._store_profile")
    assert "build_reviewed_retarget_recipe_from_profile" in exporter
    assert "save_retarget_recipe" in exporter
    row_editor = inspect.getsource(CrigMappingWorkspace._set_pair)
    approver = inspect.getsource(CrigMappingWorkspace.approve_mapping)
    assert 'pop("local_retarget_recipe", None)' in row_editor
    assert 'pop("local_retarget_recipe", None)' in approver

def test_old_advanced_repair_cannot_be_programmatically_bulk_approved() -> None:
    repair = GenericBoneMap.create(
        "Old repair", "target", "source", origin="automatic_repair"
    )
    animation = SimpleNamespace(mapping_profile_id=repair.profile_id)
    stored: list[GenericBoneMap] = []
    workspace = object.__new__(CrigMappingWorkspace)
    workspace.status = _Status()
    workspace._selected_animation = lambda: animation
    workspace._current_profile = lambda _animation: repair
    workspace._target_selection = lambda _animation=None: SimpleNamespace(
        rig_ref=DL2_ADVANCED_RIG_ID
    )
    workspace._store_profile = lambda _animation, profile: stored.append(profile)
    workspace.refresh = lambda: None
    workspace.qt = {"QMessageBox": SimpleNamespace(information=lambda *_args: None)}
    workspace.controller = SimpleNamespace(window=object())

    workspace.approve_mapping()

    assert stored == []
    assert mapping_profile_origin(repair) == "automatic_repair"
    assert "Regenerate safe DL2 body map" in workspace.status.value


def test_custom_legacy_repair_retains_explicit_approval() -> None:
    repair = GenericBoneMap.create(
        "Custom repair", "target", "source", origin="automatic_repair"
    )
    animation = SimpleNamespace(mapping_profile_id=repair.profile_id)
    stored: list[GenericBoneMap] = []
    refreshed: list[bool] = []
    workspace = object.__new__(CrigMappingWorkspace)
    workspace.status = _Status()
    workspace._selected_animation = lambda: animation
    workspace._current_profile = lambda _animation: repair
    workspace._target_selection = lambda _animation=None: SimpleNamespace(
        rig_ref="custom:hero"
    )
    workspace._store_profile = lambda _animation, profile: stored.append(profile)
    workspace.refresh = lambda: refreshed.append(True)
    workspace.qt = {"QMessageBox": SimpleNamespace(information=lambda *_args: None)}
    workspace.controller = SimpleNamespace(window=object())

    workspace.approve_mapping()

    assert stored == [repair]
    assert refreshed == [True]
    assert mapping_profile_origin(repair) == "manually_reviewed"
