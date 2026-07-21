from __future__ import annotations

import os
from pathlib import Path
from types import SimpleNamespace

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QSettings

from dlanm2_gui import gui
from dlanm2_gui.bone_maps import BoneMapPair, GenericBoneMap
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.fbx_preflight import ERROR, FbxPreflightReport
from dlanm2_gui.game_profiles import DL2_ADVANCED_RIG_REF, DL2_GAME_ID
from dlanm2_gui.retarget_profiles import PROFILE_FORMAT, SourceBoneMappingProfile
from dlanm2_gui.semantic_retarget import migrate_generic_map_to_semantic_profile
from dlanm2_gui.target_retarget_policy import build_target_retarget_policy
from dlanm2_gui.unified_gui import UnifiedMainWindow


ROOT = Path(__file__).resolve().parents[1]


def _application(tmp_path: Path):
    QSettings.setDefaultFormat(QSettings.IniFormat)
    QSettings.setPath(QSettings.IniFormat, QSettings.UserScope, str(tmp_path))
    qt = gui._load_qt()
    app = qt["QApplication"].instance() or qt["QApplication"]([])
    return qt, app


def _partial_humanoid_document() -> SimpleNamespace:
    stack = SimpleNamespace(name="Take 001")
    nodes = (
        SimpleNamespace(name="Hips", parent_name=None, endpoint_likelihood=0.0),
        SimpleNamespace(name="Spine", parent_name="Hips", endpoint_likelihood=0.0),
        SimpleNamespace(name="Head", parent_name="Spine", endpoint_likelihood=0.0),
    )

    def candidate(name: str, side: str = "") -> SimpleNamespace:
        return SimpleNamespace(
            bone_name=name,
            confidence=0.98,
            confidence_margin=0.3,
            side=side,
            evidence=("semantic_role", "topology"),
            ambiguous=False,
        )

    return SimpleNamespace(
        animation_stacks=(stack,),
        animation_stack_names=(stack.name,),
        preferred_animation_stack=lambda: stack,
        select_animation_stack=lambda _name: None,
        selected_animation_stack=stack,
        limb_models={"Hips": 1, "Spine": 2, "Head": 3},
        parent_by_name={"Hips": None, "Spine": "Hips", "Head": "Spine"},
        skeleton_hash="synthetic-partial-humanoid",
        bind_hash="synthetic-bind",
        nodes=nodes,
        semantic_roles={
            "pelvis": candidate("Hips"),
            "spine_1": candidate("Spine"),
            "head": candidate("Head"),
        },
        animated_bones=frozenset({"Hips", "Spine", "Head"}),
        semantic_chains={},
        unresolved_animated_chains=(),
        archetype="humanoid",
        archetype_confidence=1.0,
        animation_domain="upper_body",
        analyzer_version="test-analyzer-v1",
        semantic_lexicon_version="test-lexicon-v1",
        source_family_hints=(),
        source_name_languages_or_scripts=("latin",),
        findings=(),
        animated_components={},
    )


def test_dl2_import_opens_52_editable_semantic_roles_and_manual_edit_clears_cache(
    tmp_path: Path,
    monkeypatch,
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    game_index = controller.game_combo.findData(DL2_GAME_ID)
    controller.game_combo.setCurrentIndex(game_index)
    assert controller.project.rig.target_rig_ref == DL2_ADVANCED_RIG_REF

    source = tmp_path / "foreign_humanoid.fbx"
    source.write_bytes(b"synthetic fixture")
    document = _partial_humanoid_document()
    monkeypatch.setattr(controller, "_source_document", lambda _path: document)
    monkeypatch.setattr(
        qt["QFileDialog"], "getOpenFileNames", lambda *_args: ([str(source)], "")
    )

    def repairable_preflight(path, **_kwargs):
        report = FbxPreflightReport(str(path), "animation")
        report.add(
            ERROR,
            "required_target_bones_missing",
            "The source uses a partial humanoid skeleton.",
            "Exact track identity is unavailable.",
            "Use automatic semantic retargeting.",
            can_continue=True,
        )
        return report

    monkeypatch.setattr(gui, "preflight_fbx", repairable_preflight)
    critical_messages: list[str] = []
    monkeypatch.setattr(
        qt["QMessageBox"],
        "critical",
        lambda _parent, _title, message: critical_messages.append(message),
    )

    controller.add_animations()

    assert len(controller.project.animations) == 1
    animation = controller.project.animations[0]
    payload = controller.project.mapping_profiles[animation.mapping_profile_id]
    profile = SourceBoneMappingProfile.from_dict(payload)
    assert payload["format"] == PROFILE_FORMAT
    assert profile.target_policy_id == "dl2_advanced_body_bridge_v1"
    assert profile.target_rig_id == DL2_ADVANCED_RIG_REF
    assert controller.mapping_table.rowCount() == 52, controller.mapping_status.text()
    assert controller.mapping_table.cellWidget(0, 2).count() >= 6
    assert "editable semantic roles" in controller.mapping_status.text()
    assert critical_messages == []

    animation.extensions["compiled_target_map_profile_id"] = "stale-cache"
    controller._semantic_mapping_changed(animation.animation_id, "hips", "Hips")
    updated = SourceBoneMappingProfile.from_dict(
        controller.project.mapping_profiles[animation.mapping_profile_id]
    )
    assert updated.role_mode("hips") == "direct"
    assert updated.manual_override_count == 1
    assert "compiled_target_map_profile_id" not in animation.extensions
    assert controller.mapping_table.rowCount() == 52

    controller.dirty = False
    shell.window.close()


def test_reviewed_generic_map_migrates_to_semantic_profile_without_deleting_source() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    policy = build_target_retarget_policy(rig, game_id=DL2_GAME_ID)
    old = GenericBoneMap.create(
        "Reviewed old map",
        rig.skeleton_hash,
        "source",
        source_rig_ref=rig.rig_id,
        origin="manually_reviewed",
    )
    old.pairs = [
        BoneMapPair(
            next(bone.descriptor for bone in rig.bones if bone.name == "pelvis"),
            "pelvis",
            "CustomHips",
            review_state="manually_reviewed",
            extensions={
                "automatic_retarget_decision": {
                    "semantic_role": "pelvis",
                    "target_bone": "pelvis",
                    "source_bones": ["CustomHips"],
                    "confidence": 1.0,
                    "mode": "direct",
                }
            },
        )
    ]
    old_payload = old.to_dict()

    migrated = migrate_generic_map_to_semantic_profile(
        old,
        ("CustomHips",),
        {"CustomHips": None},
        policy,
    )

    assert migrated.role_to_bone["hips"] == "CustomHips"
    assert migrated.role_mode("hips") == "direct"
    assert migrated.extensions["migration_audit"]["source_profile_id"] == old.profile_id
    assert migrated.extensions["migration_audit"]["source_profile_retained_for_audit"]
    assert old.to_dict() == old_payload


def test_schema_v1_profile_migrates_manual_and_automatic_assignments() -> None:
    payload = {
        "format": PROFILE_FORMAT,
        "schema_version": 1,
        "profile_id": "old-profile",
        "name": "Old reusable profile",
        "source_skeleton_hash": "source-hash",
        "role_to_bone": {"hips": "Pelvis", "spine": "Spine"},
        "confidence_by_role": {"hips": 1.0, "spine": 0.93},
        "method_by_role": {"hips": "manual", "spine": "alias"},
    }

    profile = SourceBoneMappingProfile.from_dict(payload)

    assert profile.schema_version == 2
    assert profile.role_mode("hips") == "direct"
    assert profile.role_mode("spine") == "auto"
    assert profile.role_to_bone == {"hips": "Pelvis", "spine": "Spine"}
    assert profile.extensions["schema_migration"][0]["from"] == 1
