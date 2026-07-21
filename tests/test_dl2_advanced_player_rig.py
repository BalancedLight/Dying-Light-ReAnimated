from __future__ import annotations

import hashlib
from pathlib import Path

from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.chrome_rig_registry import ChromeRigRegistry
from dlanm2_gui.dl2_anm2 import parse_dl2_header42
from dlanm2_gui.game_profiles import (
    DL1_GAME_ID,
    DL2_ADVANCED_RIG_REF,
    DL2_GAME_ID,
    DL2_LEGACY_RIG_REF,
    DL2_RIG_REF,
    GAME_PROFILES,
    apply_game_profile_defaults,
    apply_target_package_selection,
)
from dlanm2_gui.oracle.smd_bind_pose import parse_smd_bind_pose
from dlanm2_gui.target_package import validate_target_package
from dlanm2_gui.workspace_project import DlReanimatedProject, ProjectAnimation
from tools.build_dl2_reference_crig import (
    EXPECTED_ADVANCED_SMD_SHA256,
    EXPECTED_REFERENCE_ANM2_SHA256,
    NEWLY_RESOLVED_ADVANCED_NAMES,
    build_reference_crig,
)


ROOT = Path(__file__).resolve().parents[1]
ADVANCED_SMD = ROOT / "reference" / "dl2" / "player_skeleton.smd"
ADVANCED_CRIG = ROOT / "reference" / "dl2" / "player_skeleton.crig"
LEGACY_SMD = ROOT / "reference" / "dl2" / "player_shadow_caster.smd"
LEGACY_CRIG = ROOT / "reference" / "dl2" / "player_shadow_caster.crig"
REFERENCE_ANM2 = ROOT / "reference" / "dl2" / "0_m_fpp_farjump.anm2"


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def _parent_map(path: Path) -> dict[str, str | None]:
    pose = parse_smd_bind_pose(path)
    by_index = pose.by_index
    return {
        bone.name: (
            by_index[bone.parent_index].name
            if bone.parent_index in by_index
            else None
        )
        for bone in pose.bones
    }


def test_advanced_smd_is_the_exact_271_node_single_root_input() -> None:
    assert _sha256(ADVANCED_SMD) == EXPECTED_ADVANCED_SMD_SHA256
    assert _sha256(REFERENCE_ANM2) == EXPECTED_REFERENCE_ANM2_SHA256
    pose = parse_smd_bind_pose(ADVANCED_SMD)
    assert len(pose.bones) == 271
    assert [bone.index for bone in pose.bones] == list(range(271))
    assert [bone.name for bone in pose.bones if bone.parent_index < 0] == ["pelvis"]
    text = ADVANCED_SMD.read_text(encoding="utf-8")
    assert [line.strip() for line in text.splitlines() if line.strip().startswith("time ")] == [
        "time 0"
    ]


def test_advanced_and_legacy_smd_topology_diff_matches_the_audit() -> None:
    advanced = _parent_map(ADVANCED_SMD)
    legacy = _parent_map(LEGACY_SMD)
    common = set(advanced) & set(legacy)
    added = set(advanced) - set(legacy)
    removed = set(legacy) - set(advanced)
    assert len(common) == 78
    assert len(added) == 193
    assert removed == {"l_iktarget", "r_iktarget", "player_shadowcaster"}
    assert all(advanced[name] == legacy[name] for name in common)


def test_advanced_crig_preserves_full_reference_order_and_mapping_contract() -> None:
    rig = ChromeRig.load(ADVANCED_CRIG)
    layout = parse_dl2_header42(REFERENCE_ANM2)
    stock = tuple(layout.descriptors)
    bone_descriptors = {bone.descriptor for bone in rig.bones}
    appended = tuple(
        bone.descriptor for bone in rig.bones if bone.descriptor not in set(stock)
    )
    assert rig.rig_id == DL2_ADVANCED_RIG_REF == DL2_RIG_REF
    assert len(rig.bones) == 271
    assert [bone.name for bone in rig.bones if bone.parent_index < 0] == ["pelvis"]
    assert tuple(rig.descriptors[:189]) == stock
    assert tuple(rig.descriptors[189:]) == appended
    assert len(rig.descriptors) == 368
    assert tuple(rig.extra_track_descriptors) == tuple(
        descriptor for descriptor in stock if descriptor not in bone_descriptors
    )
    assert len(rig.extra_track_descriptors) == 97
    assert sum(descriptor in bone_descriptors for descriptor in stock) == 92
    assert len({bone.descriptor for bone in rig.bones}) == 271
    assert rig.validate().ok


def test_advanced_crig_records_neutral_categories_and_no_false_split_metadata() -> None:
    rig = ChromeRig.load(ADVANCED_CRIG)
    extensions = rig.extensions
    assert extensions["source_smd_sha256"] == EXPECTED_ADVANCED_SMD_SHA256
    assert extensions["source_reference_anm2_sha256"] == EXPECTED_REFERENCE_ANM2_SHA256
    assert extensions["source_anm2_signature"] == 42
    assert extensions["source_anm2_header_version"] == 2
    assert extensions["reference_descriptor_count"] == 189
    assert extensions["matched_reference_descriptor_count"] == 92
    assert extensions["unmatched_reference_descriptor_count"] == 97
    assert extensions["hash_collision_count"] == 0
    assert extensions["matched_facial_reference_bone_count"] == 0
    assert extensions["advanced_addition_category_counts"] == {
        "attachment": 6,
        "camera": 2,
        "collar": 4,
        "facial": 167,
        "secondary_animation": 14,
    }
    assert tuple(extensions["newly_resolved_reference_bones"]) == (
        NEWLY_RESOLVED_ADVANCED_NAMES
    )
    for obsolete in (
        "active_track_count",
        "active_descriptors",
        "reference_descriptors",
        "format42_active_track_count",
        "format42_reference_track_count",
    ):
        assert obsolete not in extensions


def test_dual_preset_generator_matches_checked_in_bytes() -> None:
    advanced = build_reference_crig("advanced", root=ROOT)
    legacy = build_reference_crig("legacy", root=ROOT)
    assert advanced.to_bytes() == ADVANCED_CRIG.read_bytes()
    assert legacy.to_bytes() == LEGACY_CRIG.read_bytes()
    assert len(legacy.bones) == 81
    assert len(legacy.descriptors) == 82
    assert {bone.name for bone in legacy.bones if bone.parent_index < 0} == {
        "pelvis",
        "l_iktarget",
        "r_iktarget",
        "player_shadowcaster",
    }


def test_registry_resolves_both_bundled_presets_with_stable_labels(tmp_path: Path) -> None:
    registry = ChromeRigRegistry(tmp_path / "installed")
    by_ref = {record.rig_ref: record for record in registry.records()}
    assert by_ref[DL2_ADVANCED_RIG_REF].display_name == (
        "Dying Light 2 Player — Advanced (bundled)"
    )
    assert by_ref[DL2_LEGACY_RIG_REF].display_name == (
        "Dying Light 2 Player — Shadow Caster [Legacy] (bundled)"
    )
    assert registry.resolve(DL2_ADVANCED_RIG_REF) == ADVANCED_CRIG
    assert registry.resolve(DL2_LEGACY_RIG_REF) == LEGACY_CRIG


def test_profile_defaults_advance_new_projects_but_preserve_explicit_legacy() -> None:
    profile = GAME_PROFILES[DL2_GAME_ID]
    assert profile.default_target_rig_ref == DL2_ADVANCED_RIG_REF
    assert profile.compatible_builtin_rig_refs == (
        DL2_ADVANCED_RIG_REF,
        DL2_LEGACY_RIG_REF,
    )

    new_project = DlReanimatedProject.new("new DL2")
    new_project.game_id = DL2_GAME_ID
    apply_game_profile_defaults(
        new_project, ROOT, previous_game_id=DL1_GAME_ID
    )
    assert new_project.rig.target_rig_ref == DL2_ADVANCED_RIG_REF
    assert Path(new_project.rig.target_rig_path) == ADVANCED_CRIG
    assert Path(new_project.rig.canonical_smd) == ADVANCED_SMD

    legacy = DlReanimatedProject.new("existing legacy DL2")
    legacy.game_id = DL2_GAME_ID
    legacy.rig.target_rig_ref = DL2_LEGACY_RIG_REF
    legacy.rig.target_rig_path = str(LEGACY_CRIG)
    legacy.rig.canonical_smd = str(LEGACY_SMD)
    legacy.rig.target_template_anm2 = str(REFERENCE_ANM2)
    legacy.rig.retarget_mode = "exact"
    assert legacy.validate() == []
    serialized = legacy.to_dict()
    reloaded = DlReanimatedProject.from_dict(serialized)
    apply_game_profile_defaults(reloaded, ROOT)
    assert reloaded.rig.target_rig_ref == DL2_LEGACY_RIG_REF
    assert Path(reloaded.rig.target_rig_path) == LEGACY_CRIG
    assert Path(reloaded.rig.canonical_smd) == LEGACY_SMD

    apply_game_profile_defaults(reloaded, ROOT, force=True)
    assert reloaded.rig.target_rig_ref == DL2_ADVANCED_RIG_REF
    assert Path(reloaded.rig.target_rig_path) == ADVANCED_CRIG


def test_explicit_builtin_selection_applies_one_coherent_package() -> None:
    project = DlReanimatedProject.new("switching DL2 preset")
    project.game_id = DL2_GAME_ID
    assert apply_target_package_selection(project, ROOT, DL2_LEGACY_RIG_REF)
    assert project.rig.target_rig_ref == DL2_LEGACY_RIG_REF
    assert project.rig.retarget_mode == "auto"
    assert Path(project.rig.target_rig_path) == LEGACY_CRIG
    assert Path(project.rig.canonical_smd) == LEGACY_SMD
    assert Path(project.rig.target_template_anm2) == REFERENCE_ANM2
    assert project.validate() == []

    assert apply_target_package_selection(project, ROOT, DL2_ADVANCED_RIG_REF)
    assert project.rig.target_rig_ref == DL2_ADVANCED_RIG_REF
    assert project.rig.retarget_mode == "auto"
    assert Path(project.rig.target_rig_path) == ADVANCED_CRIG
    assert Path(project.rig.canonical_smd) == ADVANCED_SMD
    assert project.validate() == []


def test_legacy_example_paths_resolve_from_its_own_directory() -> None:
    example = ROOT / "examples" / "dl2_player_shadow_caster.example.dlraproj"
    project = DlReanimatedProject.load(example)
    assert project.rig.target_rig_ref == DL2_LEGACY_RIG_REF
    assert Path(project.rig.target_rig_path) == LEGACY_CRIG
    assert Path(project.rig.canonical_smd) == LEGACY_SMD
    assert Path(project.rig.target_template_anm2) == REFERENCE_ANM2
    assert all(
        path.is_file()
        for path in (
            Path(project.rig.target_rig_path),
            Path(project.rig.canonical_smd),
            Path(project.rig.target_template_anm2),
        )
    )
    assert project.validate() == []


def test_stale_legacy_builtin_path_falls_back_to_the_registry(tmp_path: Path) -> None:
    from dlanm2_gui.project_builder import _animation_target_context

    project = DlReanimatedProject.new("moved legacy project")
    project.game_id = DL2_GAME_ID
    project.rig.target_rig_ref = DL2_LEGACY_RIG_REF
    project.rig.target_rig_path = str(tmp_path / "missing" / "player_shadow_caster.crig")
    project.rig.retarget_mode = "exact"
    animation = ProjectAnimation.create(str(tmp_path / "clip.fbx"), "clip")

    context = _animation_target_context(
        project,
        animation,
        game_default_target_rig_ref=DL2_ADVANCED_RIG_REF,
        cache={},
    )
    assert context.rig_ref == DL2_LEGACY_RIG_REF
    assert Path(context.rig_path) == LEGACY_CRIG
    assert context.rig is not None
    assert context.rig.rig_id == DL2_LEGACY_RIG_REF


def test_project_coherence_accepts_legacy_but_rejects_crossed_package_paths() -> None:
    project = DlReanimatedProject.new("legacy")
    project.game_id = DL2_GAME_ID
    project.rig.target_rig_ref = DL2_LEGACY_RIG_REF
    project.rig.target_rig_path = str(LEGACY_CRIG)
    project.rig.canonical_smd = str(LEGACY_SMD)
    project.rig.target_template_anm2 = str(REFERENCE_ANM2)
    project.rig.retarget_mode = "exact"
    assert project.validate() == []

    project.rig.target_rig_path = str(ADVANCED_CRIG)
    project.rig.canonical_smd = str(ADVANCED_SMD)
    errors = project.validate()
    assert any("paired with the target CRIG" in error for error in errors)
    assert any("paired with the canonical SMD" in error for error in errors)


def test_target_package_validation_selects_advanced_or_legacy_by_rig_id() -> None:
    profile = GAME_PROFILES[DL2_GAME_ID]
    advanced = validate_target_package(profile, ROOT)
    legacy = validate_target_package(profile, ROOT, rig_ref=DL2_LEGACY_RIG_REF)
    assert advanced.status == legacy.status == "pass"
    assert advanced.rig_ref == DL2_ADVANCED_RIG_REF
    assert legacy.rig_ref == DL2_LEGACY_RIG_REF
    assert advanced.smd_bone_count == 271
    assert legacy.smd_bone_count == 81
    assert advanced.roots == ["pelvis"]
    assert set(legacy.roots) == {
        "pelvis", "l_iktarget", "r_iktarget", "player_shadowcaster"
    }
