from pathlib import Path
from types import SimpleNamespace

from dlanm2_gui import project_builder
from dlanm2_gui.workspace_project import DlReanimatedProject


def test_export_project_anm2_files_copies_only_generated_payloads(tmp_path, monkeypatch):
    project = DlReanimatedProject.new()
    project.export.mode = "append"
    project.export.output_directory = "original-output"
    project.export.pack_filename = "original.rpack"
    project.export.existing_rpack = "existing.rpack"
    project.export.include_validation_controls = True
    project.export.write_intermediate_anm2 = False
    generated_sources: list[Path] = []

    def fake_build(export_project, *, progress=None):
        assert export_project is not project
        assert export_project.export.mode == "new"
        assert export_project.export.pack_filename == "anm2_export_work.rpack"
        assert export_project.export.existing_rpack == ""
        assert export_project.export.include_validation_controls is False
        assert export_project.export.write_intermediate_anm2 is True
        source_dir = Path(export_project.export.output_directory) / "dl_reanimated_build" / "animations"
        source_dir.mkdir(parents=True)
        rows = []
        for name, payload in (("walk", b"walk-anm2"), ("run", b"run-anm2")):
            source = source_dir / f"{name}.anm2"
            source.write_bytes(payload)
            generated_sources.append(source)
            rows.append(SimpleNamespace(resource_name=name, anm2_path=str(source)))
        return SimpleNamespace(built_animations=rows)

    monkeypatch.setattr(project_builder, "build_project", fake_build)

    destination = tmp_path / "export"
    paths = project_builder.export_project_anm2_files(project, destination)

    assert paths == [destination / "walk.anm2", destination / "run.anm2"]
    assert (destination / "walk.anm2").read_bytes() == b"walk-anm2"
    assert (destination / "run.anm2").read_bytes() == b"run-anm2"
    assert sorted(path.name for path in destination.iterdir()) == ["run.anm2", "walk.anm2"]
    assert all(not source.exists() for source in generated_sources)
    assert project.export.mode == "append"
    assert project.export.output_directory == "original-output"
    assert project.export.write_intermediate_anm2 is False
