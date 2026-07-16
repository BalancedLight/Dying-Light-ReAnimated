from __future__ import annotations

import math
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest

from dlanm2_gui.anm2_components import decode_samples as real_decode_samples
from dlanm2_gui.bone_maps import BoneMapPair, GenericBoneMap, skeleton_signature
from dlanm2_gui.chrome_rig_builder import build_chrome_rig_from_fbx
from dlanm2_gui.fbx_core import FBX_TICKS_PER_SECOND
from dlanm2_gui.retarget_engines.exact_rig import build_exact_rig_anm2
from dlanm2_gui.retarget_engines.mapped_rig import build_mapped_rig_anm2
from dlanm2_gui.retarget_engines.output_validation import (
    DECODED_COMPONENT_ERROR_LIMIT,
)


class _AnimationDocument:
    def __init__(self, _path: Path) -> None:
        self.limb_models = {"root": 1, "child": 2}
        self.parent_by_name = {"root": None, "child": "root"}
        self.meters_per_unit = 0.01

    def frame_count(self, *, fps: int) -> int:
        assert fps == 30
        return 3

    def _local_matrix(
        self,
        object_id: int,
        *,
        tick: int,
        use_animation: bool,
    ) -> np.ndarray:
        frame = int(round(tick * 30 / FBX_TICKS_PER_SECOND)) if use_animation else 0
        matrix = np.eye(4, dtype=float)
        if object_id == 1:
            matrix[0, 3] = float(frame)
        else:
            angle = math.radians(frame * 10.0)
            matrix[0, 0] = matrix[1, 1] = math.cos(angle)
            matrix[0, 1] = -math.sin(angle)
            matrix[1, 0] = math.sin(angle)
            matrix[1, 3] = 10.0
        return matrix


def _rig_and_document(tmp_path: Path):
    rig = build_chrome_rig_from_fbx(
        tmp_path / "model.fbx",
        document_factory=_AnimationDocument,
    )
    return rig, _AnimationDocument(tmp_path / "animation.fbx")


def _bone_map(rig, document: _AnimationDocument) -> GenericBoneMap:
    profile = GenericBoneMap.create(
        "Reviewed map",
        rig.skeleton_hash,
        skeleton_signature(
            (name, document.parent_by_name.get(name))
            for name in sorted(document.limb_models)
        ),
        source_rig_ref=rig.rig_id,
    )
    profile.pairs = [
        BoneMapPair(
            bone.descriptor,
            bone.name,
            bone.name,
            1.0,
            "manual",
            review_state="manually_reviewed",
        )
        for bone in rig.bones
    ]
    return profile


def _corrupting_decoder(component_value):
    def decode(payload: bytes, times: list[float]):
        decoded = real_decode_samples(payload, times)
        frames = []
        for frame_index, frame in enumerate(decoded.frames):
            tracks = [list(track) for track in frame.tracks]
            if frame_index == 0:
                tracks[0][0] = component_value(float(tracks[0][0]))
            frames.append(
                SimpleNamespace(tracks=tuple(tuple(track) for track in tracks))
            )
        return SimpleNamespace(frames=tuple(frames))

    return decode


@pytest.mark.parametrize(
    ("engine", "module_name"),
    (
        ("legacy_exact", "dlanm2_gui.retarget_engines.legacy_exact_rig"),
        ("mapped", "dlanm2_gui.retarget_engines.mapped_rig"),
    ),
)
def test_retarget_engines_reject_nonfinite_packed_decode(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    engine: str,
    module_name: str,
) -> None:
    module = __import__(module_name, fromlist=["decode_samples"])
    monkeypatch.setattr(
        module,
        "decode_samples",
        _corrupting_decoder(lambda _value: float("nan")),
    )
    rig, document = _rig_and_document(tmp_path)

    with pytest.raises(
        ValueError,
        match="decoded a non-finite packed value.*frame 0, track 0, component 0.*rejected before output",
    ):
        if engine == "legacy_exact":
            build_exact_rig_anm2(
                tmp_path / "animation.fbx",
                rig,
                document=document,
            )
        else:
            build_mapped_rig_anm2(
                tmp_path / "animation.fbx",
                rig,
                _bone_map(rig, document),
                document=document,
                transfer_policy="mapped_local_rotation_delta",
            )


@pytest.mark.parametrize(
    ("engine", "module_name", "engine_label"),
    (
        (
            "legacy_exact",
            "dlanm2_gui.retarget_engines.legacy_exact_rig",
            "ExactRigRetargetEngine",
        ),
        (
            "mapped",
            "dlanm2_gui.retarget_engines.mapped_rig",
            "MappedRigRetargetEngine",
        ),
    ),
)
def test_retarget_engines_enforce_existing_decode_tolerance(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    engine: str,
    module_name: str,
    engine_label: str,
) -> None:
    module = __import__(module_name, fromlist=["decode_samples"])
    monkeypatch.setattr(
        module,
        "decode_samples",
        _corrupting_decoder(lambda value: value + 0.01),
    )
    rig, document = _rig_and_document(tmp_path)

    with pytest.raises(
        ValueError,
        match=(
            rf"{engine_label} packed ANM2 decode error .*not below .*0\.004.*"
            "frame 0, track 0, component 0.*rejected before output"
        ),
    ):
        if engine == "legacy_exact":
            build_exact_rig_anm2(
                tmp_path / "animation.fbx",
                rig,
                document=document,
            )
        else:
            build_mapped_rig_anm2(
                tmp_path / "animation.fbx",
                rig,
                _bone_map(rig, document),
                document=document,
                transfer_policy="mapped_local_rotation_delta",
            )


def test_successful_legacy_exact_build_reports_decode_tolerance(tmp_path: Path) -> None:
    rig, document = _rig_and_document(tmp_path)

    build = build_exact_rig_anm2(
        tmp_path / "animation.fbx",
        rig,
        document=document,
    )

    assert build.report["decoded_max_component_error"] < DECODED_COMPONENT_ERROR_LIMIT
    assert (
        build.report["decoded_component_error_tolerance"]
        == DECODED_COMPONENT_ERROR_LIMIT
    )
