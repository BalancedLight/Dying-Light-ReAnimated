from __future__ import annotations

import hashlib
from types import SimpleNamespace

import pytest

from dlanm2_gui.workspaces import models
from dlanm2_gui.workspaces.models import ModelEntry, ModelWorkspace


def _config(tmp_path) -> dict[str, object]:
    return {
        "output_path": str(tmp_path),
        "material_mode": "test",
        "test_material": "test.mat",
        "surface_name": "Flesh",
        "flip_v": False,
        "retain_skeleton": True,
        "create_crig": True,
        "animation_script": "",
        "target_smd": "",
    }


class _PassingPreflight:
    def require_buildable(self) -> None:
        return None

    def to_dict(self) -> dict[str, object]:
        return {"blocking": False, "purpose": "model"}


def test_crig_bind_rejection_happens_before_model_workspace_writes(
    tmp_path,
    monkeypatch,
) -> None:
    write_calls: list[object] = []
    scene = SimpleNamespace(inventory=lambda: {})
    result = SimpleNamespace(
        report={"effective_mode": "exact_rig"},
        authored_rig_contract=object(),
        write=lambda output: write_calls.append(output),
    )
    monkeypatch.setattr(
        models,
        "preflight_fbx",
        lambda *args, **kwargs: _PassingPreflight(),
    )
    monkeypatch.setattr(
        models,
        "build_source_from_fbx",
        lambda *args, **kwargs: result,
    )
    monkeypatch.setattr(
        models,
        "build_crig_from_rig_contract_bytes",
        lambda *args, **kwargs: (_ for _ in ()).throw(
            ValueError("unsupported shear on bone_hinge")
        ),
    )
    entry = ModelEntry("door.fbx", "door", mode="exact_rig", scene=scene)

    with pytest.raises(ValueError, match="unsupported shear on bone_hinge") as raised:
        ModelWorkspace._build_entry_for_job(
            SimpleNamespace(), entry, _config(tmp_path), lambda _message: None
        )

    assert "No MSH or CRIG was written" in str(raised.value)
    assert write_calls == []
    assert not (tmp_path / "sources" / "door").exists()


def test_generated_rig_handoff_rejects_a_changed_model_source(tmp_path) -> None:
    source = tmp_path / "model.fbx"
    source.write_bytes(b"changed source")
    crig = tmp_path / "model.crig"
    crig.write_bytes(b"placeholder")
    entry = ModelEntry(
        str(source),
        "model",
        installed_crig_ref="custom:model",
        installed_crig_path=crig,
        build_report={
            "authored_rig_contract": {
                "source_fbx_sha256": hashlib.sha256(b"original source").hexdigest(),
            }
        },
    )

    error = ModelWorkspace._generated_rig_handoff_error(entry)

    assert "changed after" in error
    assert "rebuild" in error.casefold()


def test_generated_rig_handoff_rejects_a_different_authored_bind(
    tmp_path,
    monkeypatch,
) -> None:
    source = tmp_path / "model.fbx"
    source.write_bytes(b"source")
    source_msh = tmp_path / "model.msh"
    source_msh.write_bytes(b"msh")
    crig = tmp_path / "model.crig"
    crig.write_bytes(b"crig")
    contract = {
        "source_fbx_sha256": hashlib.sha256(b"source").hexdigest(),
        "bind_hash": "expected-bind",
        "contract_id": "expected-contract",
        "skeleton_hash": "expected-skeleton",
        "descriptor_hash": "expected-descriptors",
    }
    entry = ModelEntry(
        str(source),
        "model",
        source_msh=source_msh,
        installed_crig_ref="custom:model",
        installed_crig_path=crig,
        build_report={
            "msh_sha256": hashlib.sha256(b"msh").hexdigest(),
            "authored_rig_contract": contract,
        },
    )
    monkeypatch.setattr(
        models.ChromeRig,
        "load",
        lambda _path: SimpleNamespace(
            extensions={
                "authored_bind_hash": "different-bind",
                "authored_rig_contract_id": "expected-contract",
                "authored_skeleton_hash": "expected-skeleton",
                "authored_descriptor_hash": "expected-descriptors",
            }
        ),
    )

    error = ModelWorkspace._generated_rig_handoff_error(entry)

    assert "does not match" in error
    assert "authored_bind_hash" in error
    assert "no animation target was changed" in error
