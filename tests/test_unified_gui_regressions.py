from __future__ import annotations

import os
from pathlib import Path
import time
from types import SimpleNamespace

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QEventLoop, QSettings, QThread, QTimer

from dlanm2_gui import gui
from dlanm2_gui.background_tasks import BackgroundTaskRunner
from dlanm2_gui.fbx_preflight import ERROR, FbxPreflightReport
from dlanm2_gui.retarget_profiles import HUMANOID_ROLES, SourceBoneMappingProfile
from dlanm2_gui.unified_gui import UnifiedMainWindow
from dlanm2_gui.workspace_project import ProjectAnimation


def _application(tmp_path):
    QSettings.setDefaultFormat(QSettings.IniFormat)
    QSettings.setPath(QSettings.IniFormat, QSettings.UserScope, str(tmp_path))
    qt = gui._load_qt()
    app = qt["QApplication"].instance() or qt["QApplication"]([])
    return qt, app


def test_advanced_toggle_does_not_retain_deleted_help_buttons(tmp_path) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    assert shell.controller.advanced_help_buttons == []
    for checked in (True, False, True, False):
        shell.controller.advanced_mode_toggle.setChecked(checked)
        assert (shell._animation_tab_index("Root & .crig Mapping") >= 0) is checked
    shell.controller.dirty = False
    shell.window.close()


def test_combo_popup_is_not_closed_by_delayed_refresh(tmp_path) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    shell.window.show()
    combo = shell.controller.target_rig_combo
    combo.showPopup()
    loop = QEventLoop()
    QTimer.singleShot(1100, loop.quit)
    loop.exec()
    assert combo.view().isVisible()
    combo.hidePopup()
    shell.controller.dirty = False
    shell.window.close()


def test_retarget_table_popup_does_not_commit_on_focus_transfer(tmp_path) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    animation = ProjectAnimation.create(str(tmp_path / "source.fbx"))
    document = SimpleNamespace(
        limb_models={"pelvis": object(), "spine": object()},
        parent_by_name={"pelvis": None, "spine": "pelvis"},
    )
    profile = SourceBoneMappingProfile.empty(document.limb_models)
    profile.set_mapping("hips", "pelvis")
    animation.mapping_profile_id = profile.profile_id
    shell.controller.project.animations = [animation]
    shell.controller.project.mapping_profiles[profile.profile_id] = profile.to_dict()
    shell.controller._source_cache[str(Path(animation.source_fbx).resolve())] = document
    shell.controller._refresh_mapping_table(animation, document, profile)
    shell.window.show()

    combo = shell.controller.mapping_table.cellWidget(0, 2)
    combo.showPopup()
    loop = QEventLoop()
    QTimer.singleShot(1100, loop.quit)
    loop.exec()

    assert combo.view().isVisible()
    assert shell.controller.mapping_table.cellWidget(0, 2) is combo
    combo.hidePopup()

    combo.setCurrentIndex(2)
    combo.activated.emit(2)
    assert shell.controller.mapping_table.cellWidget(0, 2) is combo
    assert combo.currentText() == "spine"
    combo.showPopup()
    second_loop = QEventLoop()
    QTimer.singleShot(1100, second_loop.quit)
    second_loop.exec()
    assert combo.view().isVisible()
    assert shell.controller.mapping_table.cellWidget(0, 2) is combo
    combo.hidePopup()
    shell.controller.dirty = False
    shell.window.close()


def test_normal_retarget_tab_shows_and_edits_target_helpers(tmp_path) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    animation = ProjectAnimation.create(str(tmp_path / "source.fbx"))
    document = SimpleNamespace(
        limb_models={"pelvis": object(), "head": object()},
        parent_by_name={"pelvis": None, "head": "pelvis"},
    )
    profile = SourceBoneMappingProfile.empty(document.limb_models)
    profile.set_mapping("hips", "pelvis")
    animation.mapping_profile_id = profile.profile_id
    controller.project.animations = [animation]
    controller.project.mapping_profiles[profile.profile_id] = profile.to_dict()
    controller._source_cache[str(Path(animation.source_fbx).resolve())] = document

    assert controller.show_helper_bones.text() == "Show helper bones"
    controller.show_helper_bones.setChecked(True)
    controller._refresh_mapping_table(animation, document, profile)

    helper_rows = {
        controller.mapping_table.item(row, 1).text(): row
        for row in range(len(HUMANOID_ROLES), controller.mapping_table.rowCount())
    }
    assert "refcamera" in helper_rows
    assert "eyecamera" in helper_rows

    row = helper_rows["refcamera"]
    source_combo = controller.mapping_table.cellWidget(row, 2)
    component_combo = controller.mapping_table.cellWidget(row, 6)
    assert component_combo.currentData() == "translation"
    source_combo.setCurrentIndex(source_combo.findData("head"))
    source_combo.activated.emit(source_combo.currentIndex())

    assert animation.extensions["helper_retarget_rules"] == [
        {
            "target_bone": "refcamera",
            "source_bone": "head",
            "transfer_policy": "rest_relative",
            "component_policy": "translation",
        }
    ]
    assert controller.mapping_table.cellWidget(row, 2) is source_combo
    controller.dirty = False
    shell.window.close()


def test_background_runner_keeps_qt_event_loop_responsive(tmp_path) -> None:
    _qt, app = _application(tmp_path)
    runner = BackgroundTaskRunner()
    ui_ticks: list[str] = []
    results: list[str] = []
    callback_on_gui_thread: list[bool] = []
    loop = QEventLoop()

    assert runner.start(
        lambda _progress: (time.sleep(0.35), "done")[1],
        succeeded=lambda value: (
            results.append(value),
            callback_on_gui_thread.append(QThread.currentThread() is app.thread()),
        ),
        finished=lambda: QTimer.singleShot(20, loop.quit),
    )
    QTimer.singleShot(50, lambda: ui_ticks.append("responsive"))
    QTimer.singleShot(2000, loop.quit)
    loop.exec()

    assert ui_ticks == ["responsive"]
    assert results == ["done"]
    assert callback_on_gui_thread == [True]


def test_cross_rig_animation_is_added_with_editable_map_and_opens_editor(
    tmp_path, monkeypatch
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    source = tmp_path / "mixamo.fbx"
    source.write_bytes(b"fixture")
    target = Path(__file__).resolve().parents[1] / "reference" / "male_npc_infected.crig"
    controller.project.rig.retarget_mode = "exact"
    controller.project.rig.target_rig_ref = "test:custom"
    controller.project.rig.target_rig_path = str(target)
    controller.target_rig_combo.addItem("Test custom rig", "test:custom")
    controller._rig_paths_by_ref["test:custom"] = str(target)
    controller._set_combo_data(controller.target_rig_combo, "test:custom")
    shell._set_crig_tab_visible(False)

    stack = SimpleNamespace(name="Take 001")
    document = SimpleNamespace(
        animation_stacks=(stack,),
        animation_stack_names=(stack.name,),
        limb_models={
            "mixamorig:Hips": 1,
            "mixamorig:Spine": 2,
            "mixamorig:Head": 3,
        },
        parent_by_name={
            "mixamorig:Hips": None,
            "mixamorig:Spine": "mixamorig:Hips",
            "mixamorig:Head": "mixamorig:Spine",
        },
    )
    monkeypatch.setattr(controller, "_source_document", lambda _path: document)
    monkeypatch.setattr(shell.crig_mapping, "_document", lambda _animation: document)
    monkeypatch.setattr(
        qt["QFileDialog"], "getOpenFileNames", lambda *_args: ([str(source)], "")
    )
    monkeypatch.setattr(qt["QMessageBox"], "warning", lambda *_args: None)

    def repairable_preflight(path, **_kwargs):
        report = FbxPreflightReport(str(path), "animation")
        report.add(
            ERROR,
            "required_target_bones_missing",
            "The source uses a different skeleton.",
            "Strict exact matching cannot transfer it by name.",
            "Review the generated .crig map.",
            can_continue=True,
        )
        return report

    monkeypatch.setattr(gui, "preflight_fbx", repairable_preflight)

    controller.add_animations()

    assert len(controller.project.animations) == 1
    animation = controller.project.animations[0]
    assert animation.mapping_profile_id
    assert controller.project.mapping_profiles[animation.mapping_profile_id]["format"] == (
        "dl-reanimated-bone-map"
    )
    assert shell._animation_tab_index("Root & .crig Mapping") >= 0

    controller._open_mapping_for_animation(animation.animation_id)
    assert shell.animation_tabs.tabText(shell.animation_tabs.currentIndex()) == (
        "Root & .crig Mapping"
    )
    assert shell.crig_mapping.table.rowCount() > 0, shell.crig_mapping.status.text()
    controller.dirty = False
    shell.window.close()


def test_animation_build_uses_background_runner(tmp_path, monkeypatch) -> None:
    qt, app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    controller.project_path = tmp_path / "background-build.dlraproj"
    worker_threads = []

    def fake_build(_project, *, progress):
        worker_threads.append(QThread.currentThread())
        progress("worker started")
        time.sleep(0.35)
        return SimpleNamespace(
            pack_path=tmp_path / "test.rpack",
            animation_count=1,
            pack_sha256="0" * 64,
            warnings=[],
            to_dict=lambda: {"status": "ok"},
        )

    monkeypatch.setattr(gui, "build_project", fake_build)
    monkeypatch.setattr(qt["QMessageBox"], "information", lambda *_args: None)
    monkeypatch.setattr(qt["QMessageBox"], "warning", lambda *_args: None)
    controller.build_rpack()
    assert controller.background_tasks.busy

    ui_ticks = []
    loop = QEventLoop()

    def poll() -> None:
        if controller.background_tasks.busy:
            QTimer.singleShot(20, poll)
        else:
            loop.quit()

    QTimer.singleShot(50, lambda: ui_ticks.append("responsive"))
    QTimer.singleShot(20, poll)
    QTimer.singleShot(2000, loop.quit)
    loop.exec()

    assert ui_ticks == ["responsive"]
    assert worker_threads and worker_threads[0] is not app.thread()
    assert "worker started" in controller.build_log.toPlainText()
    controller.dirty = False
    shell.window.close()
