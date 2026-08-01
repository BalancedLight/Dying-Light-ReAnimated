from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from collections import Counter
from pathlib import Path
from typing import Any, Sequence


RULES_FORMAT = "dl-reanimated-python-suite-audit-rules-v1"
MANIFEST_FORMAT = "dl-reanimated-python-suite-audit-manifest-v1"
RESULT_FORMAT = "dl-reanimated-python-suite-audit-result-v1"
CLASSIFICATIONS = {
    "applicable_mapped",
    "explicit_exclusion",
    "still_pending",
}
MAX_RULES_BYTES = 512 * 1024
MAX_MANIFEST_BYTES = 2 * 1024 * 1024


class AuditError(RuntimeError):
    pass


class _CollectionPlugin:
    def __init__(self) -> None:
        self.node_ids: list[str] = []

    def pytest_collection_finish(self, session: Any) -> None:
        self.node_ids = [
            str(item.nodeid).replace("\\", "/")
            for item in session.items
        ]


def _read_json(path: Path, maximum_bytes: int) -> dict[str, Any]:
    try:
        size = path.stat().st_size
    except FileNotFoundError as exception:
        raise AuditError(f"Required audit file is missing: {path}") from exception
    if size <= 0 or size > maximum_bytes:
        raise AuditError(
            f"Audit file size is outside its bound: {path} ({size} bytes)."
        )
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exception:
        raise AuditError(f"Audit JSON is unreadable: {path}") from exception
    if not isinstance(value, dict):
        raise AuditError(f"Audit JSON root must be an object: {path}")
    return value


def _load_rules(path: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    document = _read_json(path, MAX_RULES_BYTES)
    if document.get("format") != RULES_FORMAT:
        raise AuditError(f"Unexpected Python suite rules format: {path}")
    rows = document.get("rules")
    if not isinstance(rows, list) or not rows:
        raise AuditError("Python suite audit rules must be a non-empty array.")

    seen_ids: set[str] = set()
    compiled: list[dict[str, Any]] = []
    for index, raw in enumerate(rows):
        if not isinstance(raw, dict):
            raise AuditError(f"Audit rule {index} must be an object.")
        rule_id = raw.get("id")
        pattern = raw.get("pattern")
        classification = raw.get("classification")
        area = raw.get("area")
        rationale = raw.get("rationale")
        evidence = raw.get("csharpEvidence")
        if not isinstance(rule_id, str) or not rule_id:
            raise AuditError(f"Audit rule {index} has no stable id.")
        if rule_id in seen_ids:
            raise AuditError(f"Duplicate audit rule id: {rule_id}")
        seen_ids.add(rule_id)
        if classification not in CLASSIFICATIONS:
            raise AuditError(
                f"Audit rule '{rule_id}' has invalid classification "
                f"'{classification}'."
            )
        if not isinstance(area, str) or not area:
            raise AuditError(f"Audit rule '{rule_id}' has no area.")
        if not isinstance(rationale, str) or not rationale.strip():
            raise AuditError(f"Audit rule '{rule_id}' has no rationale.")
        if not isinstance(evidence, list) or any(
            not isinstance(value, str) or not value
            for value in evidence
        ):
            raise AuditError(
                f"Audit rule '{rule_id}' has invalid C# evidence."
            )
        if classification == "applicable_mapped" and not evidence:
            raise AuditError(
                f"Mapped audit rule '{rule_id}' must name C# evidence."
            )
        if not isinstance(pattern, str) or not pattern:
            raise AuditError(f"Audit rule '{rule_id}' has no pattern.")
        try:
            expression = re.compile(pattern)
        except re.error as exception:
            raise AuditError(
                f"Audit rule '{rule_id}' has invalid regex: {exception}"
            ) from exception
        compiled.append(
            {
                **raw,
                "_expression": expression,
            }
        )
    return document, compiled


def _collect_node_ids(repository_root: Path) -> list[str]:
    try:
        import pytest
    except ImportError as exception:
        raise AuditError(
            "pytest is required to collect the tracked Python regression suite."
        ) from exception

    plugin = _CollectionPlugin()
    previous_directory = Path.cwd()
    inserted = False
    root_text = str(repository_root)
    try:
        os.chdir(repository_root)
        if root_text not in sys.path:
            sys.path.insert(0, root_text)
            inserted = True
        exit_code = int(
            pytest.main(
                [
                    "--collect-only",
                    "-p",
                    "no:terminal",
                ],
                plugins=[plugin],
            )
        )
    finally:
        os.chdir(previous_directory)
        if inserted:
            sys.path.remove(root_text)
    if exit_code != 0:
        raise AuditError(
            "pytest collection failed with exit code "
            f"{exit_code}; rerun `py -3 -m pytest --collect-only -q` "
            "for the collection diagnostic."
        )
    if not plugin.node_ids:
        raise AuditError("pytest collected no Python regressions.")
    duplicates = [
        node_id
        for node_id, count in Counter(plugin.node_ids).items()
        if count != 1
    ]
    if duplicates:
        raise AuditError(
            "pytest returned duplicate node IDs: "
            + ", ".join(sorted(duplicates)[:20])
        )
    invalid = [
        node_id
        for node_id in plugin.node_ids
        if not node_id.startswith("tests/test_") or "::" not in node_id
    ]
    if invalid:
        raise AuditError(
            "pytest returned node IDs outside the reviewed test shape: "
            + ", ".join(invalid[:20])
        )
    return plugin.node_ids


def _source_rows(repository_root: Path) -> list[dict[str, Any]]:
    paths = sorted(
        repository_root.joinpath("tests").glob("test_*.py"),
        key=lambda path: path.as_posix(),
    )
    if not paths:
        raise AuditError("No Python test source files were found.")
    rows: list[dict[str, Any]] = []
    for path in paths:
        data = path.read_bytes()
        rows.append(
            {
                "path": path.relative_to(repository_root)
                    .as_posix(),
                "bytes": len(data),
                "sha256": hashlib.sha256(data)
                    .hexdigest()
                    .upper(),
            }
        )
    return rows


def _canonical_sha256(value: Any) -> str:
    data = json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")
    return hashlib.sha256(data).hexdigest().upper()


def _rule_for_node(
    node_id: str,
    rules: Sequence[dict[str, Any]],
) -> dict[str, Any]:
    for rule in rules:
        expression = rule["_expression"]
        if expression.fullmatch(node_id):
            return rule
    raise AuditError(
        "Python regression has no classification rule: "
        f"{node_id}"
    )


def _summary(entries: Sequence[dict[str, Any]]) -> dict[str, Any]:
    by_classification = Counter(
        str(entry["classification"]) for entry in entries
    )
    by_area = Counter(str(entry["area"]) for entry in entries)
    by_file: dict[str, Counter[str]] = {}
    for entry in entries:
        path = str(entry["nodeId"]).split("::", 1)[0]
        by_file.setdefault(path, Counter())[str(entry["classification"])] += 1

    file_rows = []
    for path in sorted(by_file):
        counts = by_file[path]
        file_rows.append(
            {
                "path": path,
                "total": sum(counts.values()),
                "applicableMapped": counts["applicable_mapped"],
                "explicitExclusion": counts["explicit_exclusion"],
                "stillPending": counts["still_pending"],
            }
        )
    return {
        "total": len(entries),
        "byClassification": {
            classification: by_classification[classification]
            for classification in sorted(CLASSIFICATIONS)
        },
        "byArea": {
            area: by_area[area]
            for area in sorted(by_area)
        },
        "byFile": file_rows,
    }


def _build_manifest(
    repository_root: Path,
    rules_path: Path,
    rules: Sequence[dict[str, Any]],
    node_ids: Sequence[str],
) -> dict[str, Any]:
    source_rows = _source_rows(repository_root)
    source_paths = {str(row["path"]) for row in source_rows}
    node_paths = {
        node_id.split("::", 1)[0]
        for node_id in node_ids
    }
    missing_sources = sorted(node_paths - source_paths)
    if missing_sources:
        raise AuditError(
            "Collected node IDs do not have audited test sources: "
            + ", ".join(missing_sources)
        )

    entries = []
    for index, node_id in enumerate(node_ids):
        rule = _rule_for_node(node_id, rules)
        entries.append(
            {
                "index": index,
                "nodeId": node_id,
                "classification": rule["classification"],
                "ruleId": rule["id"],
                "area": rule["area"],
            }
        )
    collection_bytes = (
        "\n".join(node_ids) + "\n"
    ).encode("utf-8")
    return {
        "format": MANIFEST_FORMAT,
        "scope": {
            "game": "Dying Light 1",
            "pytestCommand": (
                f"{Path(sys.executable).name} -m pytest "
                "--collect-only -p no:terminal"
            ),
            "rulesPath": rules_path
                .relative_to(repository_root)
                .as_posix(),
            "notes": [
                "Every collected node ID is stored exactly once.",
                "Mapped means only that the named node has reviewed bounded C# evidence; it does not complete its broader feature family.",
                "Pending is intentionally conservative and remains a release gap.",
                "New, removed, reordered, reclassified, or source-modified Python regressions fail the checked audit.",
            ],
        },
        "rulesSha256": hashlib.sha256(rules_path.read_bytes())
            .hexdigest()
            .upper(),
        "pythonTestSources": source_rows,
        "pythonTestSourcesSha256": _canonical_sha256(source_rows),
        "collectionSha256": hashlib.sha256(collection_bytes)
            .hexdigest()
            .upper(),
        "summary": _summary(entries),
        "entries": entries,
    }


def _atomic_write_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(
        value,
        ensure_ascii=False,
        indent=2,
        sort_keys=True,
        allow_nan=False,
    ) + "\n"
    encoded = text.encode("utf-8")
    if len(encoded) > MAX_MANIFEST_BYTES:
        raise AuditError(
            f"Generated Python suite manifest is {len(encoded)} bytes; "
            f"maximum is {MAX_MANIFEST_BYTES}."
        )
    temporary = path.with_name(
        f".{path.name}.{os.getpid()}.tmp"
    )
    try:
        with temporary.open("xb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def _manifest_node_ids(manifest: dict[str, Any]) -> list[str]:
    if manifest.get("format") != MANIFEST_FORMAT:
        raise AuditError("Unexpected Python suite audit manifest format.")
    entries = manifest.get("entries")
    if not isinstance(entries, list) or not entries:
        raise AuditError("Python suite audit manifest has no entries.")
    node_ids: list[str] = []
    for index, entry in enumerate(entries):
        if not isinstance(entry, dict):
            raise AuditError(f"Manifest entry {index} is not an object.")
        if entry.get("index") != index:
            raise AuditError(
                f"Manifest entry index is not contiguous at {index}."
            )
        node_id = entry.get("nodeId")
        if not isinstance(node_id, str) or not node_id:
            raise AuditError(f"Manifest entry {index} has no node ID.")
        node_ids.append(node_id)
    if len(set(node_ids)) != len(node_ids):
        raise AuditError("Python suite audit manifest has duplicate node IDs.")
    return node_ids


def _check_manifest(
    manifest_path: Path,
    generated: dict[str, Any],
) -> dict[str, Any]:
    checked = _read_json(manifest_path, MAX_MANIFEST_BYTES)
    checked_ids = _manifest_node_ids(checked)
    generated_ids = _manifest_node_ids(generated)
    checked_set = set(checked_ids)
    generated_set = set(generated_ids)
    added = sorted(generated_set - checked_set)
    removed = sorted(checked_set - generated_set)
    if added:
        raise AuditError(
            "New Python regressions are unclassified until manifest review "
            "and regeneration:\n  " + "\n  ".join(added[:50])
        )
    if removed:
        raise AuditError(
            "Checked Python regressions disappeared and require manifest "
            "review:\n  " + "\n  ".join(removed[:50])
        )
    if checked_ids != generated_ids:
        raise AuditError(
            "Python regression collection order changed and requires "
            "manifest review."
        )
    if checked != generated:
        changed = []
        for key in (
            "rulesSha256",
            "pythonTestSourcesSha256",
            "collectionSha256",
            "summary",
            "entries",
        ):
            if checked.get(key) != generated.get(key):
                changed.append(key)
        label = ", ".join(changed) if changed else "manifest metadata"
        raise AuditError(
            "Python suite audit drift requires reviewed regeneration: "
            f"{label}."
        )
    summary = generated["summary"]
    by_classification = summary["byClassification"]
    return {
        "format": RESULT_FORMAT,
        "status": "passed",
        "total": summary["total"],
        "applicableMapped": by_classification["applicable_mapped"],
        "explicitExclusion": by_classification["explicit_exclusion"],
        "stillPending": by_classification["still_pending"],
        "collectionSha256": generated["collectionSha256"],
        "pythonTestSourcesSha256": generated[
            "pythonTestSourcesSha256"
        ],
    }


def _resolve_inside_repository(
    repository_root: Path,
    value: Path,
) -> Path:
    resolved = (
        value
        if value.is_absolute()
        else repository_root / value
    ).resolve()
    try:
        resolved.relative_to(repository_root)
    except ValueError as exception:
        raise AuditError(
            f"Audit path escapes the repository: {resolved}"
        ) from exception
    return resolved


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Collect and fail-closed audit the exact Python regression suite "
            "against reviewed DL1 C# mapping rules."
        )
    )
    parser.add_argument(
        "--repository-root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
    )
    parser.add_argument(
        "--rules",
        type=Path,
        default=Path(
            "tests/fixtures/"
            "dl1_python_suite_audit_rules_v1.json"
        ),
    )
    parser.add_argument(
        "--manifest",
        type=Path,
        default=Path(
            "tests/fixtures/"
            "dl1_python_suite_audit_v1.json"
        ),
    )
    parser.add_argument(
        "--write-manifest",
        action="store_true",
        help=(
            "Atomically replace the exact checked manifest after deliberate "
            "review of collection and rule changes."
        ),
    )
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    try:
        repository_root = args.repository_root.resolve()
        if not (
            (repository_root / "pyproject.toml").is_file()
            and (repository_root / "dlanm2_gui").is_dir()
            and (repository_root / "tests").is_dir()
        ):
            raise AuditError(
                "Archived DL ReAnimated Python root was not found: "
                f"{repository_root}"
            )
        rules_path = _resolve_inside_repository(
            repository_root,
            args.rules,
        )
        manifest_path = _resolve_inside_repository(
            repository_root,
            args.manifest,
        )
        _, rules = _load_rules(rules_path)
        node_ids = _collect_node_ids(repository_root)
        generated = _build_manifest(
            repository_root,
            rules_path,
            rules,
            node_ids,
        )
        if args.write_manifest:
            _atomic_write_json(manifest_path, generated)
            summary = generated["summary"]
            classifications = summary["byClassification"]
            result = {
                "format": RESULT_FORMAT,
                "status": "manifest-written",
                "path": manifest_path
                    .relative_to(repository_root)
                    .as_posix(),
                "total": summary["total"],
                "applicableMapped": classifications[
                    "applicable_mapped"
                ],
                "explicitExclusion": classifications[
                    "explicit_exclusion"
                ],
                "stillPending": classifications["still_pending"],
                "collectionSha256": generated["collectionSha256"],
            }
        else:
            result = _check_manifest(manifest_path, generated)
        sys.stdout.write(
            json.dumps(
                result,
                sort_keys=True,
                separators=(",", ":"),
            )
            + "\n"
        )
        return 0
    except AuditError as exception:
        sys.stderr.write(f"Python suite audit failed: {exception}\n")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
