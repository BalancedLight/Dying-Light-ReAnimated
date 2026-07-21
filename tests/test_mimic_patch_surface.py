from __future__ import annotations

from pathlib import Path


def test_host_integration_markers_are_present():
    root = Path(__file__).resolve().parents[1]
    gui = (root / "dlanm2_gui" / "gui.py").read_text(encoding="utf-8")
    builder = (root / "dlanm2_gui" / "project_builder.py").read_text(encoding="utf-8")
    spec = (root / "DL-ReAnimated.spec").read_text(encoding="utf-8")
    assert "DLR_MIMIC_PROTOTYPE_BEGIN" in gui
    assert "DLR_MIMIC_PROTOTYPE_BODY_CORE" in builder
    assert "dlanm2_gui.mimic_gui" in spec


def test_mimic_gui_uses_safe_qt_column_insertion_and_visible_tab():
    root = Path(__file__).resolve().parents[1]
    source = (root / "dlanm2_gui" / "mimic_gui.py").read_text(encoding="utf-8")
    assert "table.setColumnCount(11)" in source
    assert "table.insertColumn(10)" in source
    assert '"Target rig", "Compatibility / mapping"' in source
    assert "table.setCellWidget(row_index, 10, holder)" in source
    assert "removeCellWidget" not in source
    assert 'controller.tabs.insertTab(help_index, controller.facial_page, "Facial")' in source
    assert "controller._mimic_ui_installed = True" in source


def test_mimic_clip_combo_refresh_blocks_signals():
    root = Path(__file__).resolve().parents[1]
    source = (root / "dlanm2_gui" / "mimic_gui.py").read_text(encoding="utf-8")
    assert "controller.mimic_clip_combo.blockSignals(True)" in source
    assert "controller.mimic_clip_combo.blockSignals(False)" in source
