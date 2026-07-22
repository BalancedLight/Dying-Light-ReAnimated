from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.anm2_provenance import (
    build_anm2_provenance,
    load_anm2_provenance,
    write_anm2_provenance,
)
from dlanm2_gui.anm2_writer import build_payload_from_values
from dlanm2_gui.blender_fbx import FbxExportResult, export_anm2_to_fbx
from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.trackmap import dl_name_hash
from dlanm2_gui.workspace_project import Anm2ToFbxItem


def _payload(data: bytes) -> dict:
    return build_anm2_provenance(
        data,
        source_fbx="source.fbx",
        source_fbx_sha256="AB" * 32,
        source_fbx_fps=24.0,
        sample_fps=24.0,
        playback_fps=30.0,
        source_duration_seconds=12.5,
        frame_count=376,
        root_motion_mode="in_place",
        root_heading_mode="lock_initial_heading",
    )


def test_provenance_is_deterministic_and_hash_gated(tmp_path: Path) -> None:
    anm2 = tmp_path / "clip.anm2"
    anm2.write_bytes(b"animation")
    sidecar = write_anm2_provenance(anm2, _payload(anm2.read_bytes()))
    first = sidecar.read_bytes()
    write_anm2_provenance(anm2, _payload(anm2.read_bytes()))
    assert sidecar.read_bytes() == first
    assert first.endswith(b"\n")
    loaded = load_anm2_provenance(anm2)
    assert loaded.valid
    assert loaded.payload["root_motion_mode"] == "in_place"
    assert loaded.payload["root_heading_mode"] == "lock_initial_heading"
    reverse = Anm2ToFbxItem.create(anm2)
    assert reverse.anm2_input_fps == 24.0
    assert reverse.fbx_output_fps == 24.0

    anm2.write_bytes(b"changed")
    mismatch = load_anm2_provenance(anm2)
    assert mismatch.status == "hash_mismatch"
    assert mismatch.payload == {}
    assert len(mismatch.warnings) == 1


def test_missing_and_malformed_provenance_are_nonfatal(tmp_path: Path) -> None:
    anm2 = tmp_path / "clip.anm2"
    anm2.write_bytes(b"animation")
    assert load_anm2_provenance(anm2).status == "missing"
    sidecar = Path(str(anm2) + ".dlrmeta.json")
    sidecar.write_text("{}", encoding="utf-8")
    invalid = load_anm2_provenance(anm2)
    assert invalid.status == "invalid"
    assert invalid.payload == {}
    assert len(invalid.warnings) == 1


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("sample_fps", True),
        ("source_duration_seconds", False),
        ("frame_count", True),
        ("schema_version", True),
        ("playback_fps", 10**400),
        ("source_duration_seconds", 10**400),
    ],
)
def test_malformed_scalar_metadata_is_one_nonfatal_advisory(
    tmp_path: Path,
    field: str,
    value: object,
) -> None:
    anm2 = tmp_path / f"{field}.anm2"
    anm2.write_bytes(b"animation")
    payload = _payload(anm2.read_bytes())
    payload[field] = value
    sidecar = write_anm2_provenance(anm2, payload)
    if field == "schema_version":
        rendered = json.loads(sidecar.read_text(encoding="utf-8"))
        rendered[field] = value
        sidecar.write_text(json.dumps(rendered), encoding="utf-8")

    loaded = load_anm2_provenance(anm2)

    assert loaded.status == "invalid"
    assert loaded.payload == {}
    assert len(loaded.warnings) == 1


def test_valid_provenance_drives_reverse_input_and_output_rates(
    tmp_path: Path, monkeypatch,
) -> None:
    descriptor = dl_name_hash("root")
    rig = ChromeRig(
        "test:timing", "Timing", "Test",
        (ChromeRigBone(
            0, "root", -1, descriptor, (0.0, 0.0, 0.0),
            (1.0, 0.0, 0.0, 0.0), (1.0, 1.0, 1.0),
        ),),
        0,
    )
    values = [rig.bind_track_values() for _ in range(381)]
    for frame, rows in enumerate(values):
        rows[0][3] = frame / 380.0
    payload = build_payload_from_values(
        rig.make_header(frame_count=381),
        [descriptor],
        values,
        [[False, False, False, True, False, False, False, False, False]],
    )
    source = tmp_path / "timed.anm2"
    source.write_bytes(payload)
    metadata = build_anm2_provenance(
        payload,
        source_fbx="timed.fbx",
        source_fbx_sha256="CD" * 32,
        source_fbx_fps=24.0,
        sample_fps=30.0,
        playback_fps=60.0,
        source_duration_seconds=380.0 / 30.0,
        frame_count=381,
        root_motion_mode="in_place",
        root_heading_mode="lock_initial_heading",
    )
    write_anm2_provenance(source, metadata)

    captured = {}

    def fake_export(scene, output_path, **_kwargs):
        captured["scene"] = scene
        return FbxExportResult(
            str(output_path), scene.frame_count, scene.fps, len(scene.bones),
            tuple(scene.warnings), "",
        )

    monkeypatch.setattr("dlanm2_gui.blender_fbx.run_blender_export", fake_export)
    result = export_anm2_to_fbx(source, rig, tmp_path / "timed.fbx")
    assert result.anm2_input_fps == 30.0
    assert result.fbx_output_fps == 24.0
    assert result.frame_count == 305
    assert result.timing_metadata_status == "valid"
    assert captured["scene"].translations[[0, -1]] == pytest.approx(
        np.asarray([[[0.0, 0.0, 0.0]], [[1.0, 0.0, 0.0]]])
    )
