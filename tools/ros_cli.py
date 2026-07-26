#!/usr/bin/env python3
"""Small dependency-free ROS validator and registry generator."""

from __future__ import annotations

import argparse
import ast
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

ID_RE = re.compile(
    r"^(RP|JR|EV|HY|TH|EX|DF|CN|GL|MS)-[A-Z0-9]+(?:-[A-Z0-9]+)*-[0-9]{4}-(?:[0-9]{4}|[A-F0-9]{4})$"
)
REFERENCE_FIELDS = {
    "contradicts",
    "contradicting_evidence",
    "depends_on",
    "derived_from",
    "evidence_ids",
    "hypothesis_ids",
    "related_documents",
    "related_mission",
    "related_package",
    "related_theories",
    "supporting_evidence",
    "supports",
    "superseded_by",
    "supersedes",
    "tests_hypotheses",
    "theory_ids",
}
ALLOWED_STATUS = {
    "DF": {"draft", "review", "accepted", "superseded", "withdrawn"},
    "EV": {"draft", "review", "accepted", "superseded", "withdrawn"},
    "EX": {"proposed", "active", "blocked", "completed", "cancelled"},
    "HY": {"proposed", "active", "supported", "rejected", "superseded", "withdrawn"},
    "MS": {"proposed", "approved", "active", "blocked", "completed", "cancelled", "archived"},
    "RP": {"draft", "review", "accepted", "canonical", "deprecated", "archived", "superseded", "withdrawn"},
    "TH": {"candidate", "supported", "established", "challenged", "superseded", "rejected"},
}
CONFIDENCE = {"very-low", "low", "medium", "high", "very-high"}

KIND_CONFIG = {
    "decisions": ("research/decisions", "registries/decisions.json", "DF"),
    "evidence": ("research/evidence", "registries/evidence.json", "EV"),
    "experiments": ("research/experiments", "registries/experiments.json", "EX"),
    "hypotheses": ("research/hypotheses", "registries/hypotheses.json", "HY"),
    "journals": ("research/journals", "registries/journals.json", "JR"),
    "missions": ("missions", "registries/missions.json", "MS"),
    "research-packages": ("research/packages", "registries/research-packages.json", "RP"),
    "theories": ("research/theories", "registries/theories.json", "TH"),
}


@dataclass(frozen=True)
class Artifact:
    path: Path
    relative_path: str
    metadata: dict[str, Any]

    @property
    def identifier(self) -> str:
        return str(self.metadata.get("id", self.metadata.get("identifier", "")))


@dataclass(frozen=True)
class Finding:
    path: str
    field: str
    message: str

    def render(self) -> str:
        location = f"{self.path}:{self.field}" if self.field else self.path
        return f"{location}: {self.message}"


class FrontMatterError(ValueError):
    pass


def scalar(value: str) -> Any:
    value = value.strip()
    if not value:
        return ""
    if value in {"[]", "{}"}:
        return [] if value == "[]" else {}
    if value.startswith("[") and value.endswith("]"):
        try:
            parsed = ast.literal_eval(value)
        except (ValueError, SyntaxError):
            inner = value[1:-1].strip()
            return [] if not inner else [item.strip().strip("'\"") for item in inner.split(",")]
        return parsed
    if value.lower() in {"true", "false"}:
        return value.lower() == "true"
    if re.fullmatch(r"-?\d+(?:\.\d+)?", value):
        return float(value) if "." in value else int(value)
    return value.strip("'\"")


def parse_front_matter(text: str) -> dict[str, Any]:
    lines = text.splitlines()
    if not lines or lines[0].strip() != "---":
        raise FrontMatterError("missing opening '---'")
    try:
        end = next(index for index in range(1, len(lines)) if lines[index].strip() == "---")
    except StopIteration as exc:
        raise FrontMatterError("missing closing '---'") from exc

    result: dict[str, Any] = {}
    stack: list[tuple[int, Any]] = [(-1, result)]
    index = 1
    while index < end:
        raw = lines[index]
        index += 1
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        indent = len(raw) - len(raw.lstrip(" "))
        stripped = raw.strip()
        while stack[-1][0] >= indent:
            stack.pop()
        parent = stack[-1][1]
        if stripped.startswith("- "):
            if not isinstance(parent, list):
                raise FrontMatterError(f"line {index}: list item has no list field")
            parent.append(scalar(stripped[2:]))
            continue
        if ":" not in stripped or not isinstance(parent, dict):
            raise FrontMatterError(f"line {index}: expected 'field: value'")
        key, raw_value = stripped.split(":", 1)
        key = key.strip()
        if not key:
            raise FrontMatterError(f"line {index}: empty field name")
        if raw_value.strip():
            parent[key] = scalar(raw_value)
            continue
        next_is_list = (
            index < end
            and len(lines[index]) - len(lines[index].lstrip(" ")) > indent
            and lines[index].strip().startswith("- ")
        )
        child: Any = [] if next_is_list else {}
        parent[key] = child
        stack.append((indent, child))
    return result


def artifact_files(root: Path) -> Iterable[Path]:
    seen: set[Path] = set()
    for directory, _, _ in KIND_CONFIG.values():
        base = root / directory
        if not base.exists():
            continue
        for path in sorted(base.rglob("*.md")):
            if path.name.startswith(".") or path in seen:
                continue
            seen.add(path)
            yield path


def load_artifacts(root: Path) -> tuple[list[Artifact], list[Finding]]:
    artifacts: list[Artifact] = []
    findings: list[Finding] = []
    for path in artifact_files(root):
        relative = path.relative_to(root).as_posix()
        try:
            metadata = parse_front_matter(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, FrontMatterError) as exc:
            findings.append(Finding(relative, "front_matter", str(exc)))
            continue
        artifacts.append(Artifact(path, relative, metadata))
    return artifacts, findings


def prefix(identifier: str) -> str:
    return identifier.split("-", 1)[0] if "-" in identifier else ""


def references(value: Any) -> list[str]:
    if value in ("", None, [], {}):
        return []
    if isinstance(value, str):
        return [value] if ID_RE.fullmatch(value) else []
    if isinstance(value, list):
        return [item for item in value if isinstance(item, str) and ID_RE.fullmatch(item)]
    return []


def validate(root: Path, check_registries: bool = True) -> list[Finding]:
    artifacts, findings = load_artifacts(root)
    by_id: dict[str, list[Artifact]] = {}
    for artifact in artifacts:
        identifier = artifact.identifier
        if not identifier:
            findings.append(Finding(artifact.relative_path, "id", "required field is missing"))
            continue
        if not ID_RE.fullmatch(identifier):
            findings.append(Finding(artifact.relative_path, "id", f"invalid identifier '{identifier}'"))
        by_id.setdefault(identifier, []).append(artifact)
        if not artifact.metadata.get("title"):
            findings.append(Finding(artifact.relative_path, "title", "required field is missing"))
        if not artifact.path.name.startswith(identifier + "--"):
            findings.append(
                Finding(artifact.relative_path, "id", f"filename must start with '{identifier}--'")
            )
        status = artifact.metadata.get("status")
        allowed = ALLOWED_STATUS.get(prefix(identifier))
        if status and allowed and status not in allowed:
            findings.append(
                Finding(artifact.relative_path, "status", f"'{status}' is not allowed for {prefix(identifier)}")
            )
        confidence = artifact.metadata.get("confidence")
        if isinstance(confidence, str) and confidence not in CONFIDENCE:
            findings.append(Finding(artifact.relative_path, "confidence", f"unknown label '{confidence}'"))

    for identifier, records in by_id.items():
        if len(records) > 1:
            paths = ", ".join(record.relative_path for record in records)
            for record in records:
                findings.append(Finding(record.relative_path, "id", f"duplicate '{identifier}' also in {paths}"))

    known = set(by_id)
    for artifact in artifacts:
        for field in REFERENCE_FIELDS:
            for target in references(artifact.metadata.get(field)):
                if target not in known:
                    findings.append(Finding(artifact.relative_path, field, f"broken reference '{target}'"))
                if target == artifact.identifier and field in {"supersedes", "superseded_by"}:
                    findings.append(Finding(artifact.relative_path, field, "artifact cannot supersede itself"))
        for target in references(artifact.metadata.get("supersedes")):
            if target in by_id and artifact.identifier not in references(
                by_id[target][0].metadata.get("superseded_by")
            ):
                findings.append(Finding(artifact.relative_path, "supersedes", f"'{target}' is not reciprocal"))
        for target in references(artifact.metadata.get("superseded_by")):
            if target in by_id and artifact.identifier not in references(
                by_id[target][0].metadata.get("supersedes")
            ):
                findings.append(Finding(artifact.relative_path, "superseded_by", f"'{target}' is not reciprocal"))

    if check_registries:
        findings.extend(registry_findings(root, artifacts))
    return sorted(findings, key=lambda item: (item.path, item.field, item.message))


def registry_entries(artifacts: list[Artifact], artifact_prefix: str) -> list[dict[str, Any]]:
    entries = []
    for artifact in artifacts:
        if prefix(artifact.identifier) != artifact_prefix:
            continue
        entry = dict(artifact.metadata)
        entry["id"] = artifact.identifier
        entry.pop("identifier", None)
        entry["path"] = artifact.relative_path
        entries.append(entry)
    return sorted(entries, key=lambda item: item["id"])


def rendered_registries(root: Path, artifacts: list[Artifact]) -> dict[Path, str]:
    rendered: dict[Path, str] = {}
    for _, registry, artifact_prefix in KIND_CONFIG.values():
        path = root / registry
        entries = registry_entries(artifacts, artifact_prefix)
        rendered[path] = json.dumps(entries, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    return rendered


def registry_findings(root: Path, artifacts: list[Artifact]) -> list[Finding]:
    findings = []
    for path, expected in rendered_registries(root, artifacts).items():
        relative = path.relative_to(root).as_posix()
        actual = path.read_text(encoding="utf-8") if path.exists() else None
        if actual != expected:
            findings.append(Finding(relative, "", "registry is stale; run 'ros registry build'"))
    return findings


def build_registries(root: Path, dry_run: bool = False) -> int:
    artifacts, findings = load_artifacts(root)
    if findings:
        for finding in findings:
            print(f"ERROR {finding.render()}", file=sys.stderr)
        return 1
    changed = 0
    for path, content in rendered_registries(root, artifacts).items():
        actual = path.read_text(encoding="utf-8") if path.exists() else None
        if actual == content:
            continue
        changed += 1
        print(("WOULD WRITE " if dry_run else "WROTE ") + path.relative_to(root).as_posix())
        if not dry_run:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
    print(f"{changed} registry file(s) {'would change' if dry_run else 'changed'}")
    return 0


def command_validate(root: Path) -> int:
    findings = validate(root)
    if findings:
        for finding in findings:
            print(f"ERROR {finding.render()}", file=sys.stderr)
        print(f"validation failed with {len(findings)} error(s)", file=sys.stderr)
        return 1
    print("validation passed")
    return 0


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(prog="ros")
    result.add_argument("--root", type=Path, default=Path.cwd(), help="ROS repository root")
    commands = result.add_subparsers(dest="command", required=True)
    commands.add_parser("validate", help="validate canonical artifacts and registries")
    registry = commands.add_parser("registry", help="manage generated registries")
    registry_commands = registry.add_subparsers(dest="registry_command", required=True)
    build = registry_commands.add_parser("build", help="rebuild registries")
    build.add_argument("--dry-run", action="store_true")
    registry_commands.add_parser("check", help="check registry freshness")
    return result


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    root = args.root.resolve()
    if args.command == "validate":
        return command_validate(root)
    if args.registry_command == "build":
        return build_registries(root, args.dry_run)
    artifacts, findings = load_artifacts(root)
    findings.extend(registry_findings(root, artifacts))
    if findings:
        for finding in findings:
            print(f"ERROR {finding.render()}", file=sys.stderr)
        return 1
    print("registries are current")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
