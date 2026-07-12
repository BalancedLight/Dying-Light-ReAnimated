from __future__ import annotations

from dlanm2_gui.anm2_components import decode_samples
from dlanm2_gui.fbx_blendshapes import FbxBlendShapeCurve, FbxFacialScan, _percent_scale
from dlanm2_gui.mimic_builder import build_mimic_anm2
from dlanm2_gui.mimic_profiles import MimicMappingRow, MimicProfile, MimicTarget


def _profile() -> MimicProfile:
    return MimicProfile(
        profile_id="test:face",
        name="Synthetic face",
        targets=(
            MimicTarget(0, 0x11111111, "jaw", "Jaw", aliases=("jawOpen",)),
            MimicTarget(1, 0x22222222, "smile", "Smile", aliases=("smileLeft",)),
        ),
    )


def _scan() -> FbxFacialScan:
    return FbxFacialScan(
        source_path="synthetic.fbx",
        animation_stack="Take 001",
        fps=30,
        frame_count=4,
        curves=(
            FbxBlendShapeCurve("jawOpen", 1, ("jawOpen",), (0.0, 0.25, 0.5, 1.0), 0.0, 1.0, True),
            FbxBlendShapeCurve("mouthOpen", 2, ("mouthOpen",), (0.0, 0.10, 0.20, 0.30), 0.0, 1.0, True),
            FbxBlendShapeCurve("smileLeft", 3, ("smileLeft",), (0.0, 0.20, -0.10, 0.40), 0.0, 1.0, True),
        ),
    )


def test_mimic_writer_uses_tx_scalar_and_consolidates_sources():
    mapping = [
        MimicMappingRow("jawOpen", 0x11111111, 1.0),
        MimicMappingRow("mouthOpen", 0x11111111, 0.5),
        MimicMappingRow("smileLeft", 0x22222222, 1.0),
    ]
    build = build_mimic_anm2(_scan(), _profile(), mapping=mapping)
    decoded = decode_samples(build.payload, [0.0, 1.0, 2.0, 3.0])
    jaw_expected = [0.0, 0.30, 0.60, 1.15]
    smile_expected = [0.0, 0.20, -0.10, 0.40]
    for frame, jaw, smile in zip(decoded.frames, jaw_expected, smile_expected):
        assert abs(frame.tracks[0][3] - jaw) < 2.0e-3
        assert abs(frame.tracks[1][3] - smile) < 2.0e-3
        for track in frame.tracks:
            assert max(abs(track[index]) for index in (0, 1, 2, 4, 5)) < 1.0e-6
            assert all(abs(track[index] - 1.0) < 1.0e-6 for index in (6, 7, 8))
    assert build.report["consolidated_targets"]["0x11111111"] == ["jawOpen", "mouthOpen"]
    assert build.report["unmapped_animated_shapes"] == []


def test_unmapped_activity_is_reported_instead_of_guessed():
    build = build_mimic_anm2(
        _scan(),
        _profile(),
        mapping=[MimicMappingRow("jawOpen", 0x11111111)],
    )
    assert build.report["unmapped_animated_shapes"] == ["mouthOpen", "smileLeft"]
    assert 0.0 < build.report["captured_source_activity_ratio"] < 1.0


def test_fbx_percent_scale_handles_percent_and_normalized_exports():
    assert _percent_scale(0.0, [0.0, 50.0, 100.0]) == 0.01
    assert _percent_scale(0.0, [0.0, 0.5, 1.0]) == 1.0
