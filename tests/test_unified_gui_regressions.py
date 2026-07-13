from __future__ import annotations

import os
from pathlib import Path
import time
from types import SimpleNamespace

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QEventLoop, QSettings, QThread, QTimer

from dlanm2_gui import gui
from dlanm2_gui.background_tasks import BackgroundTaskRunner
from dlanm2_gui.retarget_profiles import SourceBoneMappingProfile
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
