from __future__ import annotations

import io
import json
import zipfile

import pytest

from dlanm2_gui.model_importer.crig import build_crig_from_source_msh_bytes
from dlanm2_gui.model_importer.vendor.chrome_mesh_tools.writer import (
    SourceMsh,
    SourceNode,
)


def _translation(x: float, y: float, z: float) -> tuple[float, ...]:
    return (
        1.0,
        0.0,
        0.0,
        x,
        0.0,
        1.0,
        0.0,
        y,
        0.0,
        0.0,
        1.0,
        z,
    )


def test_crig_uses_exact_authored_msh_bind_and_preserves_helpers() -> None:
    source = SourceMsh(
        materials=("test.mat",),
        surface_names=("Flesh",),
        nodes=(
            SourceNode("armature_root", node_type=4, local_matrix=_translation(0.0, 0.0, 0.0)),
            SourceNode(
                "pelvis",
                node_type=8,
                parent_index=0,
                local_matrix=_translation(0.0, 0.8, 0.0),
            ),
            SourceNode("body", node_type=2),
            SourceNode("bounds", node_type=1),
        ),
    )

    payload, report = build_crig_from_source_msh_bytes(
        source,
        name="test_model",
        source_model_name="test_model.fbx",
        source_sha256="abcd",
        aliases_by_name={"pelvis": ("Hips",)},
        resolved_orientation_policy="none",
    )

    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        skeleton = json.loads(archive.read("skeleton.json"))
        manifest = json.loads(archive.read("manifest.json"))

    assert [row["name"] for row in skeleton["bones"]] == ["armature_root", "pelvis"]
    assert skeleton["bones"][0]["helper"] is True
    assert skeleton["bones"][0]["deform"] is False
    assert skeleton["bones"][1]["helper"] is False
    assert skeleton["bones"][1]["deform"] is True
    assert skeleton["bones"][1]["parent_index"] == 0
    assert skeleton["bones"][1]["bind_translation"] == pytest.approx([0.0, 0.8, 0.0])
    assert skeleton["bones"][1]["aliases"] == ["Hips"]
    assert manifest["extensions"]["bind_source"] == "exact_authored_source_msh_animation_entities"
    assert manifest["extensions"]["requires_bind_basis_retarget"] is True
    assert manifest["extensions"]["resolved_model_axis_conversion"] == "none"
    assert report["resolved_orientation_policy"] == "none"
    assert report["deform_bone_count"] == 1
    assert report["helper_count"] == 1


def test_crig_identity_changes_when_authored_bind_changes() -> None:
    def build(pelvis_height: float) -> tuple[str, str]:
        source = SourceMsh(
            materials=("test.mat",),
            surface_names=("Flesh",),
            nodes=(
                SourceNode(
                    "armature_root",
                    node_type=4,
                    local_matrix=_translation(0.0, 0.0, 0.0),
                ),
                SourceNode(
                    "pelvis",
                    node_type=8,
                    parent_index=0,
                    local_matrix=_translation(0.0, pelvis_height, 0.0),
                ),
                SourceNode("body", node_type=2),
            ),
        )
        payload, _report = build_crig_from_source_msh_bytes(
            source,
            name="same_named_model",
            source_model_name="same_named_model.fbx",
        )
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            manifest = json.loads(archive.read("manifest.json"))
        return manifest["rig_id"], manifest["skeleton_sha256"]

    first_id, first_hash = build(0.8)
    second_id, second_hash = build(0.9)

    assert first_hash != second_hash
    assert first_id != second_id
