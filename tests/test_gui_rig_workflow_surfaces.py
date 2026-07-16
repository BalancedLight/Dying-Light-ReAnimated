from __future__ import annotations

import os
from pathlib import Path
from types import SimpleNamespace

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QSettings

from dlanm2_gui import gui
from dlanm2_gui.unified_gui import UnifiedMainWindow
from dlanm2_gui.workspace_project import ProjectAnimation
from dlanm2_gui.workspaces import models as models_module
from dlanm2_gui.workspaces.models import ModelEntry


def _application(tmp_path: Path):
    QSettings.setDefaultFormat(QSettings.IniFormat)
    QSettings.setPath(QSettings.IniFormat, QSettings.UserScope, str(tmp_path))
    qt = gui._load_qt()
    app = qt["QApplication"].instance() or qt["QApplication"]([])
    return qt, app


def test_model_details_and_artifact_actions_expose_rig_build_evidence(
    tmp_path: Path,
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    source = tmp_path / "hero.fbx"
    source.write_bytes(b"fbx")
    source_msh = tmp_path / "hero.msh"
    source_msh.write_bytes(b"msh")
    report_path = source_msh.with_suffix(".model_import.json")
    report_path.write_text("{}", encoding="utf-8")
    crig_path = tmp_path / "hero.crig"
    crig_path.write_bytes(b"crig")
    entry = ModelEntry(
        path=str(source),
        resource_name="hero",
        mode="exact_rig",
        inventory={
            "detected_mode": "skinned",
            "fbx_version": 7400,
            "meters_per_unit": 0.01,
            "axis_settings": {"UpAxis": 1, "FrontAxis": 2},
            "mesh_geometry_count": 2,
            "limb_node_count": 322,
            "weighted_bone_count": 180,
            "material_count": 3,
            "armature_roots": ["ArmatureRoot"],
            "geometries": [
                {"triangle_count": 8},
                {"triangle_count": 5},
            ],
            "warnings": [],
        },
        build_report={
            "msh_path": str(source_msh),
            "effective_mode": "exact_rig",
            "total_vertices": 91,
            "total_triangles": 13,
            "total_hierarchy_node_count": 322,
            "bone_count": 180,
            "helper_count": 142,
            "skin_partitions": {
                "partition_count": 4,
                "maximum_local_palette_size": 96,
            },
            "authored_rig_contract": {
                "roots": [0],
                "nodes": [{"name": "AuthoredRoot"}],
            },
            "authored_rig_validation": {"status": "pass"},
            "warnings": [],
        },
        source_msh=source_msh,
        crig_path=crig_path,
        installed_crig_ref="custom:hero",
        installed_crig_path=crig_path,
        status="Source built",
    )
    shell.models.entries = [entry]
    shell.models._refresh_table()
    shell.models.model_table.selectRow(0)

    details = shell.models.details.toPlainText()
    for expected in (
        "Scene units (meters/unit): 0.01",
        "Scene orientation metadata:",
        "Mesh geometries: 2",
        "Source triangles: 13",
        "Emitted vertices: 91",
        "Hierarchy nodes: 322",
        "Deform bones: 180",
        "Helper bones: 142",
        "Hierarchy roots: AuthoredRoot",
        "Skin partitions: 4",
        "Maximum subset palette: 96 / 256 local entries",
        "Generated CRIG ref: custom:hero",
        "Animation compatibility: authored MSH/CRIG bind contract pass",
    ):
        assert expected in details

    opened: list[Path] = []
    shell.models._open_local_path = lambda path: opened.append(Path(path))
    shell.models.open_model_report_button.click()
    shell.models.open_generated_crig_button.click()
    assert opened == [report_path, tmp_path]

    shell.controller.dirty = False
    shell.window.close()


def test_model_mapping_review_table_shows_policy_and_resets_only_safe_rows(
    tmp_path: Path,
    monkeypatch,
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    workspace = shell.models
    scene = SimpleNamespace(
        geometries=[SimpleNamespace(clusters=[SimpleNamespace(bone_id=1)])],
        model_names={1: "safe_bone", 2: "ambiguous_bone"},
        depth_first_bones_for_weighted_ids=lambda _weighted: (1, 2),
    )
    target_nodes = [SimpleNamespace(name="safe_target"), SimpleNamespace(name="maybe_target")]

    monkeypatch.setattr(
        models_module,
        "source_skin_weight_usage",
        lambda _scene, _ids: {
            "bone_weight_totals": {1: 90.0, 2: 10.0},
            "total_normalized_weight": 100.0,
        },
    )

    def fake_mapping(
        _scene,
        source_ids,
        _nodes,
        *,
        manual_mapping=None,
        source_weight_totals=None,
    ):
        del source_weight_totals
        automatic = {1: 0, 2: 1}
        names = scene.model_names
        targets = {node.name: index for index, node in enumerate(target_nodes)}
        mapping = dict(automatic)
        if manual_mapping is not None:
            for bone_id in source_ids:
                name = names[bone_id]
                if name in manual_mapping:
                    mapping[bone_id] = targets.get(manual_mapping[name])
        rows = []
        for bone_id in source_ids:
            manual = manual_mapping is not None and names[bone_id] in manual_mapping
            rows.append(
                {
                    "source_bone": names[bone_id],
                    "role": "pelvis" if bone_id == 1 else "accessory",
                    "confidence": 0.99 if bone_id == 1 else 0.62,
                    "method": "manual" if manual else "semantic",
                    "manual_role_mismatch": False,
                }
            )
        return mapping, {
            "source_bone_count": 2,
            "directly_mapped_count": 2,
            "rows": rows,
        }

    monkeypatch.setattr(models_module, "humanoid_bone_mapping", fake_mapping)
    entry = ModelEntry(
        path=str(tmp_path / "fitted.fbx"),
        resource_name="fitted",
        mode="dying_light_humanoid",
        scene=scene,
        inventory={"detected_mode": "skinned"},
        humanoid_bone_map={
            "safe_bone": "safe_target",
            "ambiguous_bone": "maybe_target",
        },
        extensions={
            "humanoid_mapping_review_v1": {
                "safe_bone": "automatic_unreviewed",
                "ambiguous_bone": "automatic_unreviewed",
            }
        },
    )
    workspace.entries = [entry]
    workspace._target_smd_nodes = lambda: target_nodes
    workspace._refresh_table()
    workspace._refresh_mapping_model_combo()

    headers = [
        workspace.model_mapping_table.horizontalHeaderItem(index).text()
        for index in range(workspace.model_mapping_table.columnCount())
    ]
    assert headers == [
        "Source FBX bone",
        "Source weight %",
        "Semantic role",
        "Automatic target",
        "Final target",
        "Transfer",
        "Components",
        "Confidence",
        "Review",
        "Method",
        "Status",
    ]
    assert workspace.model_mapping_table.item(0, 1).text() == "90.00%"
    assert workspace.model_mapping_table.item(0, 5).text() == "Skin-weight transfer"
    assert workspace.model_mapping_table.item(0, 6).text() == "Authored bind T/R/S"
    assert workspace.model_mapping_table.item(1, 8).text() == "Automatic - review"

    workspace.review_ambiguous_button.setChecked(True)
    assert workspace.model_mapping_table.isRowHidden(0)
    assert not workspace.model_mapping_table.isRowHidden(1)

    workspace.reset_safe_model_suggestions()
    assert "safe_bone" not in entry.humanoid_bone_map
    assert entry.humanoid_bone_map["ambiguous_bone"] == "maybe_target"
    workspace._set_model_map_row("ambiguous_bone", "")
    assert entry.humanoid_bone_map["ambiguous_bone"] == ""
    assert (
        entry.extensions["humanoid_mapping_review_v1"]["ambiguous_bone"]
        == "intentionally_unmapped"
    )

    shell.controller.dirty = False
    shell.window.close()


def test_animation_target_filter_group_and_model_jump_are_per_crig(
    tmp_path: Path,
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    controller.project.rig.target_rig_ref = "custom:alpha"
    controller.project.rig.target_rig_name = "Alpha"
    controller._rig_labels_by_ref.update(
        {"custom:alpha": "Alpha", "custom:beta": "Beta"}
    )
    first = ProjectAnimation.create(tmp_path / "first.fbx")
    second = ProjectAnimation.create(tmp_path / "second.fbx")
    third = ProjectAnimation.create(tmp_path / "third.fbx")
    second.target_rig_ref = "custom:beta"
    third.target_rig_ref = "custom:alpha"
    controller.project.animations = [second, first, third]
    controller._refresh_animation_table()

    assert controller.animation_target_filter.findData("custom:alpha") >= 0
    assert controller.animation_target_filter.findData("custom:beta") >= 0
    assert controller.set_animation_target_filter("custom:alpha") == 2
    visible_ids = {
        str(controller.animation_table.item(row, 2).data(qt["Qt"].UserRole))
        for row in range(controller.animation_table.rowCount())
        if not controller.animation_table.isRowHidden(row)
    }
    assert visible_ids == {first.animation_id, third.animation_id}

    controller.set_animation_target_filter("")
    controller.animation_target_group.setChecked(True)
    grouped_ids = [
        str(controller.animation_table.item(row, 2).data(qt["Qt"].UserRole))
        for row in range(controller.animation_table.rowCount())
    ]
    assert grouped_ids == [first.animation_id, third.animation_id, second.animation_id]

    shell._show_animations_targeting_model(
        ModelEntry(
            path=str(tmp_path / "hero.fbx"),
            resource_name="hero",
            installed_crig_ref="custom:beta",
        )
    )
    assert shell.main_tabs.currentIndex() == 0
    assert controller.animation_target_filter.currentData() == "custom:beta"
    assert sum(
        not controller.animation_table.isRowHidden(row)
        for row in range(controller.animation_table.rowCount())
    ) == 1
    assert "Showing 1 animation clip" in controller.status.currentMessage()

    controller.dirty = False
    shell.window.close()
