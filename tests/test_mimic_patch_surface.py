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
