from __future__ import annotations

import ast
import locale
from pathlib import Path

from dlanm2_gui.pack_manifest import PackManifest, manifest_path_for_pack


ROOT = Path(__file__).resolve().parents[1]


def test_utf8_pack_manifest_load_is_independent_of_cp936_locale(
    tmp_path: Path,
    monkeypatch,
) -> None:
    pack = tmp_path / "animations.rpack"
    manifest_path_for_pack(pack).write_text(
        """{
  "pack_name": "动作 — export",
  "pack_sha256": "00",
  "project_id": "project—测试",
  "animation_resources": [{
    "resource_name": "walk",
    "script_resource": "anims_test",
    "source_fbx": "walk.fbx",
    "root_policy": "inplace",
    "frame_count": 31,
    "fps": 30,
    "sha256": "11"
  }],
  "animation_scripts": []
}
""",
        encoding="utf-8",
    )
    monkeypatch.setattr(locale, "getencoding", lambda: "cp936")

    loaded = PackManifest.load_for_pack(pack)

    assert loaded is not None
    assert loaded.animation_resources[0].sample_fps is None
    assert loaded.animation_resources[0].playback_fps is None
    assert loaded.pack_name == "动作 — export"
    assert loaded.project_id == "project—测试"


def test_production_path_text_calls_declare_an_encoding() -> None:
    violations: list[str] = []
    for package in (ROOT / "dlanm2_gui", ROOT / "tools"):
        for path in package.rglob("*.py"):
            tree = ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))
            for node in ast.walk(tree):
                if not isinstance(node, ast.Call):
                    continue
                if not isinstance(node.func, ast.Attribute):
                    continue
                if node.func.attr not in {"read_text", "write_text"}:
                    continue
                if not any(keyword.arg == "encoding" for keyword in node.keywords):
                    violations.append(
                        f"{path.relative_to(ROOT)}:{node.lineno} {node.func.attr}"
                    )

    assert violations == []


def test_production_text_mode_open_calls_declare_an_encoding() -> None:
    violations: list[str] = []
    for package in (ROOT / "dlanm2_gui", ROOT / "tools"):
        for path in package.rglob("*.py"):
            tree = ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))
            for node in ast.walk(tree):
                if not isinstance(node, ast.Call):
                    continue
                is_builtin = isinstance(node.func, ast.Name) and node.func.id == "open"
                is_path_open = (
                    isinstance(node.func, ast.Attribute) and node.func.attr == "open"
                )
                if not (is_builtin or is_path_open):
                    continue
                keywords = {keyword.arg: keyword.value for keyword in node.keywords}
                mode_node = keywords.get("mode")
                mode_index = 1 if is_builtin else 0
                if mode_node is None and len(node.args) > mode_index:
                    mode_node = node.args[mode_index]
                mode = (
                    str(mode_node.value)
                    if isinstance(mode_node, ast.Constant)
                    else "r"
                )
                if "b" in mode:
                    continue
                if "encoding" not in keywords:
                    violations.append(f"{path.relative_to(ROOT)}:{node.lineno}")

    assert violations == []


def test_text_mode_subprocesses_declare_utf8_and_replacement_errors() -> None:
    violations: list[str] = []
    for package in (ROOT / "dlanm2_gui", ROOT / "tools"):
        for path in package.rglob("*.py"):
            tree = ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))
            for node in ast.walk(tree):
                if not isinstance(node, ast.Call):
                    continue
                keywords = {keyword.arg: keyword.value for keyword in node.keywords}
                text = keywords.get("text")
                if not isinstance(text, ast.Constant) or text.value is not True:
                    continue
                encoding = keywords.get("encoding")
                errors = keywords.get("errors")
                if not (
                    isinstance(encoding, ast.Constant)
                    and encoding.value == "utf-8"
                    and isinstance(errors, ast.Constant)
                    and errors.value == "replace"
                ):
                    violations.append(f"{path.relative_to(ROOT)}:{node.lineno}")

    assert violations == []
