from __future__ import annotations

import os
from pathlib import Path
import time
from types import SimpleNamespace

import pytest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QEventLoop, QSettings, QThread, QTimer

from dlanm2_gui import gui
from dlanm2_gui.background_tasks import BackgroundTaskRunner
from dlanm2_gui.bone_maps import BoneMapPair, GenericBoneMap
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.fbx_preflight import ERROR, FbxPreflightReport
from dlanm2_gui.retarget_profiles import HUMANOID_ROLES, SourceBoneMappingProfile
from dlanm2_gui.unified_gui import UnifiedMainWindow
from dlanm2_gui.workspace_project import (
    Anm2ToFbxItem,
    DlReanimatedProject,
    ProjectAnimation,
)


def _application(tmp_path):
    QSettings.setDefaultFormat(QSettings.IniFormat)
    QSettings.setPath(QSettings.IniFormat, QSettings.UserScope, str(tmp_path))
    qt = gui._load_qt()
    app = qt["QApplication"].instance() or qt["QApplication"]([])
    return qt, app


def test_timing_controls_preserve_fractional_standard_rates(tmp_path) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    input_rate = 24_000.0 / 1_001.0
    output_rate = 30_000.0 / 1_001.0

    assert controller.fps_spin.decimals() == 9
    assert controller.sample_fps_spin.decimals() == 9
    reverse = Anm2ToFbxItem.create(tmp_path / "fractional.anm2")
    reverse.anm2_input_fps = input_rate
    reverse.fbx_output_fps = output_rate
    controller.project.anm2_to_fbx.items.append(reverse)
    controller._reverse_refresh_table()
    input_widget = controller.reverse_table.cellWidget(0, 5)
    output_widget = controller.reverse_table.cellWidget(0, 6)

    assert input_widget.decimals() == 9
    assert output_widget.decimals() == 9
    controller._sync_reverse_from_ui()
    assert reverse.anm2_input_fps == pytest.approx(input_rate, abs=5.0e-10)
    assert reverse.fbx_output_fps == pytest.approx(output_rate, abs=5.0e-10)
    controller.dirty = False
    shell.window.close()

def test_advanced_toggle_does_not_retain_deleted_help_buttons(tmp_path) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    assert shell.controller.advanced_help_buttons == []
    for checked in (True, False, True, False):
        shell.controller.advanced_mode_toggle.setChecked(checked)
        assert shell._animation_tab_index("Root & .crig Mapping") < 0

    # Solver mode does not own the editor. A bundled humanoid remains semantic
    # even when exact execution is selected.
    shell.controller.project.rig.retarget_mode = "exact"
    shell.controller.advanced_mode_toggle.setChecked(True)
    assert shell._animation_tab_index("Root & .crig Mapping") < 0

    custom, custom_path = _custom_rig(tmp_path, "custom:advanced-tab-test")
    shell.controller.project.rig.target_rig_ref = custom.rig_id
    shell.controller.project.rig.target_rig_path = str(custom_path)
    shell._set_crig_tab_visible(True)
    assert shell._animation_tab_index("Root & .crig Mapping") >= 0
    shell.controller.dirty = False
    shell.window.close()


def test_dl2_auto_mapping_opens_retargeting_without_crig_tab(tmp_path) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    previous_game = controller.project.game_id
    controller.project.game_id = "dying_light_2"
    gui.apply_game_profile_defaults(
        controller.project,
        controller.resource_root,
        previous_game_id=previous_game,
        force=True,
    )
    animation = ProjectAnimation.create(tmp_path / "known_humanoid.fbx")
    controller.project.animations.append(animation)
    controller._bundled_semantic_state = lambda _animation: SimpleNamespace(
        rows=tuple(range(52))
    )
    controller._refresh_bundled_semantic_table = (
        lambda _animation, state: (
            controller.mapping_table.setRowCount(len(state.rows)),
            controller.mapping_status.setText(
                "Ready — automatically retargeted; 52 editable semantic roles"
            ),
        )
    )
    controller._reload_target_rig_combo()
    controller._refresh_retarget_clip_combo()

    controller.advanced_mode_toggle.setChecked(True)
    assert controller.project.rig.retarget_mode == "auto"
    assert shell._animation_tab_index("Root & .crig Mapping") < 0

    shell._open_animation_mapping(animation.animation_id)

    assert shell.animation_tabs.tabText(shell.animation_tabs.currentIndex()) == "Retargeting"
    assert controller.mapping_table.rowCount() == 52
    assert "editable semantic roles" in controller.mapping_status.text()
    assert shell._animation_tab_index("Root & .crig Mapping") < 0
    controller.dirty = False
    shell.window.close()


def test_file_menu_tracks_and_opens_recent_projects(tmp_path) -> None:
    qt, _app = _application(tmp_path)
    first_path = tmp_path / "one" / "first.dlraproj"
    second_path = tmp_path / "two" / "second.dlraproj"
    DlReanimatedProject.new("First recent").save(first_path)
    DlReanimatedProject.new("Second recent").save(second_path)

    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    controller.clear_recent_projects()
    assert controller._load_project(first_path)
    assert controller._load_project(second_path)
    assert controller._load_project(first_path)
    assert controller.recent_project_paths() == [
        first_path.resolve(),
        second_path.resolve(),
    ]

    shell._refresh_recent_projects_menu()
    recent_actions = [
        row for row in shell.recent_projects_menu.actions() if row.data()
    ]
    assert [Path(row.data()) for row in recent_actions] == [
        first_path.resolve(),
        second_path.resolve(),
    ]
    assert recent_actions[0].text().startswith("1. first.dlraproj")

    recent_actions[1].trigger()
    assert controller.project.name == "Second recent"
    assert controller.project_path == second_path.resolve()
    assert controller.recent_project_paths()[0] == second_path.resolve()

    controller.dirty = False
    shell.window.close()

    reopened = UnifiedMainWindow(qt, gui)
    assert reopened.controller.recent_project_paths() == [
        second_path.resolve(),
        first_path.resolve(),
    ]
    reopened.controller.clear_recent_projects()
    assert len(reopened.recent_projects_menu.actions()) == 1
    assert reopened.recent_projects_menu.actions()[0].text() == "No Recent Projects"
    reopened.controller.dirty = False
    reopened.window.close()


def test_project_load_with_animations_does_not_parse_fbx_on_gui_thread(
    tmp_path,
    monkeypatch,
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    project = DlReanimatedProject.new("Lazy animation project")
    for index in range(4):
        row = ProjectAnimation.create(tmp_path / f"clip_{index}.fbx")
        row.source_animation_stack = f"Take {index}"
        project.animations.append(row)
    project_path = project.save(tmp_path / "lazy.dlraproj")

    parse_attempts: list[str] = []

    def forbidden_parse(path: str):
        parse_attempts.append(path)
        raise AssertionError("project loading must not parse animation FBXs")

    monkeypatch.setattr(controller, "_source_document", forbidden_parse)

    assert controller._load_project(project_path)
    assert controller.animation_table.rowCount() == 4
    assert controller.retarget_clip_combo.count() == 4
    assert parse_attempts == []
    assert "deferred" in controller.mapping_status.text().casefold()
    controller.dirty = False
    shell.window.close()


def test_import_result_refresh_does_not_rebuild_retarget_editor(
    tmp_path,
    monkeypatch,
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    source = tmp_path / "streamed.fbx"
    row = ProjectAnimation.create(source)
    row.source_animation_stack = "Take 001"
    document = SimpleNamespace(animation_stack_names=("Take 001",))

    analysis_attempts: list[bool] = []
    monkeypatch.setattr(
        controller,
        "_retarget_clip_changed",
        lambda: analysis_attempts.append(True),
    )

    controller._apply_animation_import_result(
        gui._AnimationImportResult(
            rows=[row],
            documents={str(source.resolve()): document},
        )
    )

    assert controller.animation_table.rowCount() == 1
    assert controller.retarget_clip_combo.count() == 1
    assert analysis_attempts == []
    controller.dirty = False
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
    busy_during_finished: list[bool] = []
    loop = QEventLoop()

    assert runner.start(
        lambda _progress: (time.sleep(0.35), "done")[1],
        succeeded=lambda value: (
            results.append(value),
            callback_on_gui_thread.append(QThread.currentThread() is app.thread()),
        ),
        finished=lambda: (
            busy_during_finished.append(runner.busy),
            QTimer.singleShot(20, loop.quit),
        ),
    )
    QTimer.singleShot(50, lambda: ui_ticks.append("responsive"))
    QTimer.singleShot(2000, loop.quit)
    loop.exec()

    assert ui_ticks == ["responsive"]
    assert results == ["done"]
    assert callback_on_gui_thread == [True]
    assert busy_during_finished == [False]


def test_background_runner_failure_clears_busy_before_finished(tmp_path) -> None:
    _qt, _app = _application(tmp_path)
    runner = BackgroundTaskRunner()
    failures = []
    busy_during_finished: list[bool] = []
    loop = QEventLoop()

    def fail(_progress):
        raise RuntimeError("broken import")

    assert runner.start(
        fail,
        failed=failures.append,
        finished=lambda: (
            busy_during_finished.append(runner.busy),
            loop.quit(),
        ),
    )
    QTimer.singleShot(2000, loop.quit)
    loop.exec()

    assert len(failures) == 1
    assert failures[0].message == "broken import"
    assert busy_during_finished == [False]
    assert not runner.busy


def test_visible_multifile_import_streams_rows_and_can_be_cancelled(
    tmp_path, monkeypatch
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    shell.show()
    controller = shell.controller
    paths = tuple(tmp_path / f"clip_{index}.fbx" for index in range(4))
    for path in paths:
        path.write_bytes(b"fixture")
    monkeypatch.setattr(
        qt["QFileDialog"],
        "getOpenFileNames",
        lambda *_args: ([str(path) for path in paths], ""),
    )

    def fake_prepare(request, progress, partial):
        aggregate = gui._AnimationImportResult()
        for index, raw in enumerate(request.paths, start=1):
            progress(f"Importing animation {index}/{len(request.paths)}")
            row = ProjectAnimation.create(raw)
            aggregate.rows.append(row)
            partial(gui._AnimationImportResult(rows=[row]))
            time.sleep(0.15)
            progress(f"Finished animation {index}/{len(request.paths)}")
        return aggregate

    monkeypatch.setattr(gui, "_prepare_animation_import", fake_prepare)
    controller.add_animations()

    assert controller.background_tasks.busy
    assert not controller.add_animations_button.isEnabled()
    assert not controller.cancel_animation_operation_button.isHidden()

    observed_counts: list[int] = []
    cancelled = False
    loop = QEventLoop()

    def poll() -> None:
        nonlocal cancelled
        count = len(controller.project.animations)
        observed_counts.append(count)
        if count == 1 and not cancelled:
            cancelled = True
            controller.cancel_animation_operation_button.click()
        if controller.background_tasks.busy:
            QTimer.singleShot(20, poll)
        else:
            loop.quit()

    QTimer.singleShot(20, poll)
    QTimer.singleShot(4000, loop.quit)
    loop.exec()

    assert 1 in observed_counts
    assert len(controller.project.animations) == 1
    assert not controller.background_tasks.busy
    assert controller.add_animations_button.isEnabled()
    assert controller.cancel_animation_operation_button.isHidden()
    assert "cancelled" in controller.status.currentMessage().casefold()
    controller.dirty = False
    shell.window.close()


def test_prepare_multifile_import_publishes_each_completed_file(
    tmp_path, monkeypatch
) -> None:
    paths = tuple(tmp_path / f"streamed_{index}.fbx" for index in range(2))
    for path in paths:
        path.write_bytes(b"fixture")
    stack = SimpleNamespace(name="Take 001")
    document = SimpleNamespace(
        animation_stacks=(stack,),
        preferred_animation_stack=lambda: stack,
        declared_timebase=None,
        limb_models={"root": 1},
        parent_by_name={"root": None},
    )
    monkeypatch.setattr(gui, "FbxDocument", lambda *_args, **_kwargs: document)
    monkeypatch.setattr(
        gui,
        "preflight_fbx",
        lambda path, **_kwargs: FbxPreflightReport(str(path), "animation"),
    )
    request = gui._AnimationImportRequest(
        paths=tuple(str(path) for path in paths),
        existing=set(),
        game_id="test",
        retarget_mode="disabled",
        target_rig_path="",
        resource_root=tmp_path,
        resource_prefix="",
        tolerance=gui.FbxImportTolerance.RECOMMENDED,
    )
    partials = []

    result = gui._prepare_animation_import(
        request,
        lambda _message: None,
        partials.append,
    )

    assert len(result.rows) == 2
    assert [len(part.rows) for part in partials] == [1, 1]
    assert [Path(part.rows[0].source_fbx).name for part in partials] == [
        path.name for path in paths
    ]


def test_close_during_animation_import_cancels_then_closes(
    tmp_path, monkeypatch
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    shell.show()
    controller = shell.controller
    paths = tuple(tmp_path / f"closing_{index}.fbx" for index in range(3))
    for path in paths:
        path.write_bytes(b"fixture")
    monkeypatch.setattr(
        qt["QFileDialog"],
        "getOpenFileNames",
        lambda *_args: ([str(path) for path in paths], ""),
    )

    def fake_prepare(request, progress, partial):
        aggregate = gui._AnimationImportResult()
        for index, raw in enumerate(request.paths, start=1):
            progress(f"Importing {index}")
            row = ProjectAnimation.create(raw)
            aggregate.rows.append(row)
            partial(gui._AnimationImportResult(rows=[row]))
            time.sleep(0.15)
            progress(f"Finished {index}")
        return aggregate

    monkeypatch.setattr(gui, "_prepare_animation_import", fake_prepare)
    monkeypatch.setattr(controller, "_confirm_discard_changes", lambda: True)
    messages: list[tuple[str, str]] = []
    monkeypatch.setattr(
        qt["QMessageBox"],
        "information",
        lambda _parent, title, message: messages.append((title, message)),
    )
    controller.add_animations()
    loop = QEventLoop()
    close_requested = False

    def poll() -> None:
        nonlocal close_requested
        if len(controller.project.animations) == 1 and not close_requested:
            close_requested = True
            shell.window.close()
        if shell.window.isVisible():
            QTimer.singleShot(20, poll)
        else:
            loop.quit()

    QTimer.singleShot(20, poll)
    QTimer.singleShot(4000, loop.quit)
    loop.exec()

    assert close_requested
    assert not controller.background_tasks.busy
    assert not shell.window.isVisible()
    assert messages and messages[0][0] == "Cancelling animation work"


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
    assert shell._animation_tab_index("Root & .crig Mapping") < 0
    assert controller.animation_table.cellWidget(0, 11).text() == "Fix mapping…"

    controller._open_mapping_for_animation(animation.animation_id)
    assert shell.animation_tabs.tabText(shell.animation_tabs.currentIndex()) == (
        "Root & .crig Mapping"
    )
    assert shell.crig_mapping.table.rowCount() > 0, shell.crig_mapping.status.text()
    controller.dirty = False
    shell.window.close()


def test_batch_animation_import_keeps_valid_clip_and_selects_unique_changing_stack(
    tmp_path, monkeypatch
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    valid = tmp_path / "valid.fbx"
    corrupt = tmp_path / "corrupt.fbx"
    valid.write_bytes(b"fixture")
    corrupt.write_bytes(b"broken")
    take = SimpleNamespace(name="Take 001")
    useful = SimpleNamespace(name="mixamo.com")
    document = SimpleNamespace(
        animation_stacks=(take, useful),
        animation_stack_names=(take.name, useful.name),
        preferred_animation_stack=lambda: useful,
        limb_models={"Hips": 1, "Spine": 2, "Head": 3},
        parent_by_name={"Hips": None, "Spine": "Hips", "Head": "Spine"},
    )

    def source_document(path: str):
        if Path(path).name == corrupt.name:
            raise ValueError("truncated binary node stream")
        return document

    def preflight(path, **_kwargs):
        report = FbxPreflightReport(str(path), "animation")
        if Path(path).name == corrupt.name:
            report.add(
                ERROR,
                "fbx_unreadable",
                "The FBX has a truncated binary node stream.",
                "Required animation objects cannot be parsed.",
                "Re-export the corrupt file.",
            )
        else:
            report.add(
                "informational",
                "model_geometry_ignored_for_animation",
                "Model geometry was not loaded for animation import.",
                "The selected skeletal clip does not consume it.",
                "No action is required.",
                group="ignored",
            )
        return report

    critical_messages: list[str] = []
    warning_messages: list[str] = []
    information_messages: list[str] = []
    monkeypatch.setattr(controller, "_source_document", source_document)
    monkeypatch.setattr(gui, "preflight_fbx", preflight)
    monkeypatch.setattr(
        qt["QFileDialog"],
        "getOpenFileNames",
        lambda *_args: ([str(corrupt), str(valid)], ""),
    )
    monkeypatch.setattr(
        qt["QMessageBox"],
        "critical",
        lambda _parent, _title, message: critical_messages.append(message),
    )
    monkeypatch.setattr(
        qt["QMessageBox"],
        "warning",
        lambda _parent, _title, message: warning_messages.append(message),
    )
    monkeypatch.setattr(
        qt["QMessageBox"],
        "information",
        lambda _parent, _title, message: information_messages.append(message),
    )

    controller.add_animations()

    assert len(controller.project.animations) == 1
    animation = controller.project.animations[0]
    assert Path(animation.source_fbx).name == valid.name
    assert animation.source_animation_stack == "mixamo.com"
    assert animation.extensions["import_state"]["status"] == "ready"
    assert "model_geometry_ignored_for_animation" in (
        controller.animation_import_diagnostics.toPlainText()
    )
    assert critical_messages == []
    assert "Cannot read" in controller.status.currentMessage()
    assert warning_messages == []
    assert information_messages == []
    status = controller._animation_target_status(animation)[0]
    assert status.startswith("Ready")
    controller.dirty = False
    shell.window.close()


def test_static_animation_import_stays_enabled_and_uses_ready_state(
    tmp_path,
    monkeypatch,
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    source = tmp_path / "static_pose.fbx"
    source.write_bytes(b"fixture")
    document = SimpleNamespace(
        animation_stacks=(),
        animation_stack_names=(),
        preferred_animation_stack=lambda: None,
        limb_models={"root": 1},
        parent_by_name={"root": None},
    )
    monkeypatch.setattr(controller, "_source_document", lambda _path: document)
    monkeypatch.setattr(
        qt["QFileDialog"],
        "getOpenFileNames",
        lambda *_args: ([str(source)], ""),
    )

    def static_preflight(path, **_kwargs):
        report = FbxPreflightReport(str(path), "animation")
        report.add(
            "informational",
            "static_bind_pose_clip",
            "Ready — static/bind-pose clip.",
            "Two identical finite frames are valid.",
            "No action is required.",
            group="ignored",
        )
        return report

    monkeypatch.setattr(gui, "preflight_fbx", static_preflight)
    critical_messages: list[str] = []
    monkeypatch.setattr(
        qt["QMessageBox"],
        "critical",
        lambda _parent, _title, message: critical_messages.append(message),
    )

    controller.add_animations()

    assert len(controller.project.animations) == 1
    animation = controller.project.animations[0]
    assert animation.enabled
    assert animation.extensions["import_state"]["level"] == "ready"
    assert animation.extensions["import_state"]["label"] == (
        "Ready — static/bind-pose clip"
    )
    assert critical_messages == []
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


def _custom_rig(tmp_path: Path, rig_id: str) -> tuple[ChromeRig, Path]:
    source = Path(__file__).resolve().parents[1] / "reference" / "male_npc_infected.crig"
    rig = ChromeRig.load(source)
    rig.rig_id = rig_id
    rig.name = rig_id.rsplit(":", 1)[-1]
    return rig, rig.save(tmp_path / f"{rig.name}.crig")


def test_animation_target_column_overrides_only_one_clip(tmp_path) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    rig, rig_path = _custom_rig(tmp_path, "custom:clip-target")
    first = ProjectAnimation.create(str(tmp_path / "first.fbx"))
    second = ProjectAnimation.create(str(tmp_path / "second.fbx"))
    controller.project.animations = [first, second]
    controller._rig_paths_by_ref[rig.rig_id] = str(rig_path)
    controller._rig_labels_by_ref[rig.rig_id] = rig.name
    controller._refresh_animation_table()

    combo = controller.animation_table.cellWidget(0, 6)
    assert combo.itemText(0).startswith("Inherit project target")
    combo.setCurrentIndex(combo.findData(rig.rig_id))

    assert first.target_rig_ref == rig.rig_id
    assert Path(first.target_rig_path) == rig_path
    assert second.target_rig_ref == ""
    assert second.target_rig_path == ""
    assert controller.animation_table.item(0, 7).text().startswith("Ready")
    assert "Override target" in controller.animation_table.item(0, 7).toolTip()
    assert "exact skeleton match" in controller.animation_table.item(0, 7).text()
    assert shell.crig_mapping._load_rig(first).skeleton_hash == rig.skeleton_hash

    controller.dirty = False
    shell.window.close()


def test_generated_model_rig_handoff_preserves_unrelated_clip_target_and_map(
    tmp_path,
) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    controller = shell.controller
    new_rig, new_path = _custom_rig(tmp_path, "custom:model-target")
    first = ProjectAnimation.create(str(tmp_path / "first.fbx"))
    second = ProjectAnimation.create(str(tmp_path / "second.fbx"))
    profile = GenericBoneMap.create(
        "Shared old target map",
        new_rig.skeleton_hash,
        "source-signature",
        source_rig_ref=controller.project.rig.target_rig_ref,
        origin="manually_reviewed",
    )
    root = new_rig.bones[0]
    profile.pairs = [BoneMapPair(root.descriptor, root.name, "source_root")]
    first.mapping_profile_id = second.mapping_profile_id = profile.profile_id
    controller.project.mapping_profiles[profile.profile_id] = profile.to_dict()
    controller.project.animations = [first, second]
    original_default = controller.project.rig.target_rig_ref
    original_payload = controller.project.mapping_profiles[profile.profile_id]
    controller._refresh_animation_table()
    controller.animation_table.selectRow(0)

    shell._model_rigs_installed(
        [
            SimpleNamespace(
                installed_crig_path=new_path,
                crig_path=new_path,
            )
        ]
    )

    assert controller.project.rig.target_rig_ref == original_default
    assert first.target_rig_ref == new_rig.rig_id
    assert Path(first.target_rig_path) == new_path
    assert second.target_rig_ref == ""
    assert second.mapping_profile_id == profile.profile_id
    assert first.mapping_profile_id != profile.profile_id
    assert controller.project.mapping_profiles[profile.profile_id] == original_payload
    migrated = GenericBoneMap.from_dict(
        controller.project.mapping_profiles[first.mapping_profile_id]
    )
    assert migrated.source_rig_ref == new_rig.rig_id
    assert migrated.target_bind_hash == new_rig.skeleton_hash

    controller.dirty = False
    shell.window.close()


def test_model_workspace_roundtrip_preserves_unknown_extension_fields(tmp_path) -> None:
    qt, _app = _application(tmp_path)
    shell = UnifiedMainWindow(qt, gui)
    payload = {
        "format": "dl-reanimated-model-workspace",
        "schema_version": 2,
        "future_workspace": {"keep": True},
        "models": [
            {
                "path": str(tmp_path / "model.fbx"),
                "resource_name": "model",
                "future_model": [1, 2, 3],
            }
        ],
        "settings": {
            "material_mode": "test",
            "future_setting": {"vendor": "kept"},
        },
    }
    shell.models.restore(payload)
    serialized = shell.models.serialize()

    assert serialized["future_workspace"] == {"keep": True}
    assert serialized["models"][0]["future_model"] == [1, 2, 3]
    assert serialized["settings"]["future_setting"] == {"vendor": "kept"}
    assert shell.models.use_generated_rig_button.text() == (
        "Use generated rig in Animations"
    )

    shell.controller.dirty = False
    shell.window.close()
