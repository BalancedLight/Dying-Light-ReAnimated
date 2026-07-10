from __future__ import annotations

import argparse
import json
from pathlib import Path

from dlanm2_gui.project_builder import build_project
from dlanm2_gui.workspace_project import DlReanimatedProject


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Build a versioned DL ReAnimated .dlraproj project."
    )
    parser.add_argument("project", type=Path)
    parser.add_argument(
        "--output-directory",
        type=Path,
        help="Temporarily override the project's output directory",
    )
    args = parser.parse_args()
    try:
        project = DlReanimatedProject.load(args.project)
        if args.output_directory:
            project.export.output_directory = str(args.output_directory.resolve())
        result = build_project(project, progress=lambda message: print(message, flush=True))
    except Exception as exc:
        parser.error(str(exc))
        return
    print(json.dumps(result.to_dict(), indent=2))


if __name__ == "__main__":
    main()
