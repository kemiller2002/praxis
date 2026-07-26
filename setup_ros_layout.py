#!/usr/bin/env python3
"""
Bootstrap a Research Operating System (ROS) repository.

Creates a standard, versionable repository layout based on the
Research Execution Package (REP) Specification v2.0.

Safety:
- Idempotent by default.
- Existing files are never overwritten unless --force is supplied.
- Supports --dry-run.
- Can initialize the current directory or a specified repository path.

Usage:
    python setup_ros_layout.py
    python setup_ros_layout.py --repo /path/to/ros
    python setup_ros_layout.py --repo . --dry-run
    python setup_ros_layout.py --repo . --force
"""

from __future__ import annotations

import argparse
import json
import sys
import textwrap
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, Iterable


ROS_VERSION = "1.0.0"
REP_SPEC_VERSION = "2.0"


DIRECTORIES = [
    # Canonical operating instructions
    "framework",
    "framework/policies",
    "framework/standards",
    "framework/protocols",

    # Project and agent entry points
    "missions",
    "missions/active",
    "missions/backlog",
    "missions/completed",
    "agents",
    "agents/research",
    "agents/engineering",
    "agents/evaluation",
    "agents/specialized",

    # Current shared understanding
    "context",
    "context/projects",
    "context/domains",

    # Canonical research artifacts
    "research",
    "research/journals",
    "research/packages",
    "research/evidence",
    "research/hypotheses",
    "research/theories",
    "research/experiments",
    "research/decisions",
    "research/concepts",
    "research/glossary",

    # Registries and machine-readable indexes
    "registries",
    "schemas",

    # Reusable artifact templates
    "templates",
    "templates/research",
    "templates/missions",
    "templates/agents",

    # Intake and processing
    "input-documents",
    "work",
    "work/staging",
    "work/review",

    # Generated products; canonical truth remains under research/
    "generated",
    "generated/reports",
    "generated/site",
    "generated/presentations",
    "generated/datasets",

    # Automation and validation
    "tools",
    "tools/bootstrap",
    "tools/build",
    "tools/validation",
    "tools/indexing",

    # Quality and operations
    "tests",
    "tests/fixtures",
    "logs",
    "archive",
]


FILES: Dict[str, str] = {
    "README.md": f"""\
# Research Operating System

**ROS Version:** {ROS_VERSION}  
**REP Specification:** {REP_SPEC_VERSION}

This repository is a shared operating environment for autonomous research
and engineering agents.

## Canonical source hierarchy

1. Scientific Research Journals
2. Research Execution Packages
3. Theory Registry
4. Evidence Registry

Reports, websites, presentations, and training materials are derived from
canonical research artifacts. Generated products must not silently replace
or modify canonical records.

## Agent entry point

Every agent starts with [`BOOTSTRAP.md`](BOOTSTRAP.md).

## Repository principles

- Preserve provenance.
- Prefer stable identifiers over filenames as references.
- Do not overwrite immutable findings.
- Record what evidence supports each important claim.
- Separate observations, evidence, assumptions, inferences, and conclusions.
- Update registries after creating canonical artifacts.
- Leave the repository usable by the next agent.
""",

    "BOOTSTRAP.md": """\
# ROS Agent Bootstrap

Follow this sequence before beginning work.

## 1. Read operating instructions

1. `framework/REP-SPECIFICATION.md`
2. `framework/policies/RESEARCH-POLICY.md`
3. `framework/policies/EVIDENCE-POLICY.md`
4. `framework/policies/OUTPUT-POLICY.md`
5. Any discipline-specific standard named by the mission

## 2. Read current shared context

1. `context/CURRENT-STATE.md`
2. `context/ARCHITECTURE.md`
3. `context/DECISIONS.md`
4. `context/KNOWN-RISKS.md`
5. Relevant project or domain context

## 3. Read the assigned mission

Read only the mission and source records needed to execute it. Do not load
the entire repository without a reason.

## 4. Execute

- Identify the largest material uncertainty.
- Form explicit hypotheses.
- Seek supporting and contradicting evidence.
- Attempt to falsify conclusions.
- Preserve source traceability.
- Use deterministic tools before generative inference where practical.
- Record failures and invalidated assumptions.

## 5. Record results

Create or update the appropriate canonical artifacts:

- Journal: `research/journals/`
- REP: `research/packages/`
- Evidence: `research/evidence/`
- Hypotheses: `research/hypotheses/`
- Theories: `research/theories/`
- Experiments: `research/experiments/`

Update the corresponding registry under `registries/`.

## 6. Handoff

The next agent must be able to reconstruct the work, understand the current
theory, and continue without additional conversational context.
""",

    "framework/REP-SPECIFICATION.md": """\
# Research Execution Package Specification

**Version:** 2.0  
**Status:** Canonical

## Purpose

The Research Execution Package (REP) is the canonical artifact produced at
the completion of every research effort. It serves as a permanent scientific
record, executable handoff, theory update, knowledge transfer artifact, and
synchronization point between autonomous research agents.

## Canonical artifact hierarchy

1. Scientific Research Journal
2. Research Execution Package
3. Theory Registry
4. Evidence Registry

## Stable identifiers

| Artifact | Prefix |
|---|---|
| Research Package | `RP-` |
| Journal Entry | `JR-` |
| Evidence | `EV-` |
| Hypothesis | `HY-` |
| Theory | `TH-` |
| Experiment | `EX-` |
| Decision Framework | `DF-` |
| Concept | `CN-` |
| Glossary | `GL-` |

## Required metadata

Identifier, title, research area, discipline, author agent, version,
confidence, completion, priority, related projects, related documents,
supersedes, superseded by, tags, and keywords.

## Mandatory REP sections

- Research State Snapshot
- Executive Summary
- Original Objective
- Scope
- Repository Context
- Current Understanding
- Key Discoveries
- Evidence Registry
- Hypothesis Registry
- Failed Assumptions
- Open Questions
- Recommended Next Research
- Research Backlog
- Suggested Specialized Research Agents
- Parallel Research Opportunities
- Risks
- Cross-Discipline Opportunities
- Knowledge Relationships
- Theory Impact Assessment
- Research Quality Metrics
- Research Debt
- Repository Updates
- Website Updates
- AI Consumption Notes
- Handoff Instructions
- Research Journal
- Appendix
- Completion Checklist

## Completion standard

A different autonomous research agent must be able to reconstruct the
investigation, understand the current theory, continue immediately, and
produce the next REP without additional context.
""",

    "framework/policies/RESEARCH-POLICY.md": """\
# Research Policy

Research agents must increase understanding rather than merely collect
information.

Use repeated cycles:

1. Review the current state.
2. Identify the largest remaining uncertainty.
3. Generate falsifiable hypotheses.
4. Define supporting and contradicting evidence.
5. Search and inspect evidence.
6. Compare competing explanations.
7. Attempt to falsify the leading conclusion.
8. Revise confidence and theory.
9. Record findings and failures.
10. Select the highest-value next step.

Stop only when additional work is producing diminishing informational value,
the mission boundary has been reached, or a documented blocker prevents
further progress.
""",

    "framework/policies/EVIDENCE-POLICY.md": """\
# Evidence Policy

- Important claims must cite evidence IDs where evidence records exist.
- Distinguish primary evidence from summaries and commentary.
- Record counterexamples and conflicting evidence.
- Never convert an inference into an observation.
- Record uncertainty explicitly.
- Preserve source location, retrieval date, and provenance.
- Prefer original research, standards, official documentation, raw data,
  and direct records over derivative commentary.
- Do not discard evidence merely because it conflicts with the current theory.
""",

    "framework/policies/OUTPUT-POLICY.md": """\
# Output Policy

Canonical knowledge belongs under `research/`.

Generated reports, websites, presentations, and datasets belong under
`generated/` and must reference their canonical source artifacts.

Agents must:

- Avoid duplicating canonical records.
- Use stable identifiers.
- Record dependencies and supersession.
- Keep machine-readable registries synchronized.
- Avoid modifying prior immutable findings; supersede them instead.
- State which files were created, modified, or intentionally left unchanged.
""",

    "framework/standards/NAMING-STANDARD.md": """\
# Naming and Identifier Standard

Recommended identifier format:

`<PREFIX>-<AREA>-<YYYY>-<SEQUENCE>`

Examples:

- `RP-VE-2026-0001`
- `JR-HN-2026-0012`
- `EV-CLARITY-2026-0044`
- `HY-FE-2026-0007`

Recommended artifact filename:

`<identifier>--<short-kebab-title>.md`

Do not use filenames as the sole identity of an artifact.
""",

    "context/CURRENT-STATE.md": """\
# Current State

## Repository status

Newly initialized.

## Active research streams

None registered.

## Highest-confidence areas

None registered.

## Lowest-confidence areas

None registered.

## Largest remaining unknown

Define the first mission and establish the initial theory baseline.

## Recently invalidated ideas

None registered.

## Priority changes

None registered.
""",

    "context/ARCHITECTURE.md": """\
# ROS Architecture

The repository separates:

- **Framework:** stable operating instructions
- **Missions:** bounded work assignments
- **Context:** compact current understanding
- **Research:** canonical scientific records
- **Registries:** machine-readable indexes
- **Templates:** reusable artifact structures
- **Input documents:** unprocessed incoming material
- **Generated:** derived outputs
- **Tools:** deterministic automation and validation
- **Archive:** deprecated or superseded noncanonical material

Canonical artifacts must remain usable independently of any specific model,
agent vendor, or chat history.
""",

    "context/DECISIONS.md": """\
# Decisions

Record accepted architectural and operating decisions here in compact form.

| Date | Decision | Rationale | Related artifacts |
|---|---|---|---|
""",

    "context/KNOWN-RISKS.md": """\
# Known Risks

| Risk | Likelihood | Impact | Mitigation | Owner |
|---|---|---|---|---|
| Prompt or policy drift | Medium | High | Maintain canonical framework files and versions | Unassigned |
| Registry drift | Medium | High | Add automated validation and indexing | Unassigned |
| Generated artifacts treated as canonical | Medium | High | Enforce source references and directory boundaries | Unassigned |
""",

    "templates/missions/MISSION-TEMPLATE.md": """\
---
id: MS-AREA-YYYY-0001
title: Replace with mission title
status: proposed
priority: medium
research_area: replace-me
discipline:
  - replace-me
created: YYYY-MM-DD
owner_agent: unassigned
depends_on: []
related_projects: []
required_framework:
  - framework/REP-SPECIFICATION.md
  - framework/policies/RESEARCH-POLICY.md
  - framework/policies/EVIDENCE-POLICY.md
outputs:
  - research/packages/RP-AREA-YYYY-0001--replace-me.md
---

# Mission

## Objective

## Why this matters

## Scope

### Included

### Excluded

## Existing context

## Initial hypotheses

## Required evidence

## Constraints

## Execution instructions

## Deliverables

## Success criteria

## Stop conditions

## Handoff requirements
""",

    "templates/research/REP-TEMPLATE.md": """\
---
id: RP-AREA-YYYY-0001
title: Replace with research package title
research_area: replace-me
discipline:
  - replace-me
author_agent: replace-me
version: 1.0.0
confidence: low
completion: draft
priority: medium
created: YYYY-MM-DD
updated: YYYY-MM-DD
related_projects: []
related_documents: []
supersedes: []
superseded_by: []
tags: []
keywords: []
---

# Research State Snapshot

- **Theory version:**
- **Knowledge-base version:**
- **Highest-confidence areas:**
- **Lowest-confidence areas:**
- **Largest remaining unknown:**
- **Active research streams:**
- **Recently invalidated ideas:**
- **Priority changes:**

# Executive Summary

# Original Objective

# Scope

# Repository Context

# Current Understanding

# Key Discoveries

# Evidence Registry

# Hypothesis Registry

# Failed Assumptions

# Open Questions

# Recommended Next Research

# Research Backlog

# Suggested Specialized Research Agents

# Parallel Research Opportunities

# Risks

# Cross-Discipline Opportunities

# Knowledge Relationships

# Theory Impact Assessment

## Affected theory records

## Affected engineering principles

## New principle candidates

## Deprecated principles

## Confidence changes

## Predictions created

## Predictions invalidated

## Required theory-registry updates

# Research Quality Metrics

- **Primary sources:**
- **Independent sources:**
- **Counterexamples reviewed:**
- **Competing viewpoints reviewed:**
- **Hypotheses tested:**
- **Failed hypotheses:**
- **Research completeness:**
- **Confidence gain:**
- **Open questions reduced:**

# Research Debt

- **Missing evidence:**
- **Missing experiments:**
- **Missing disciplines:**
- **Weak areas:**
- **Replication needed:**
- **Tool limitations:**
- **Assumptions awaiting evidence:**

# Repository Updates

# Website Updates

# AI Consumption Notes

# Handoff Instructions

# Research Journal

# Appendix

# Completion Checklist

- [ ] Required metadata is complete.
- [ ] Important claims reference evidence, hypothesis, and theory IDs.
- [ ] Competing explanations were considered.
- [ ] Failed assumptions were recorded.
- [ ] Theory impacts were assessed.
- [ ] Research debt was recorded.
- [ ] Registries were updated.
- [ ] The next agent can continue without conversational context.
""",

    "templates/research/JOURNAL-TEMPLATE.md": """\
---
id: JR-AREA-YYYY-0001
title: Replace with journal entry title
research_area: replace-me
author_agent: replace-me
created: YYYY-MM-DD
related_mission:
related_package:
evidence_ids: []
hypothesis_ids: []
theory_ids: []
tags: []
---

# Research Journal Entry

## Objective

## Starting state

## Actions taken

## Observations

## Evidence collected

## Hypotheses considered

## Attempts to falsify

## Decisions and rationale

## Failures and dead ends

## Confidence changes

## Files changed

## Highest-value next step
""",

    "templates/research/EVIDENCE-TEMPLATE.md": """\
---
id: EV-AREA-YYYY-0001
title: Replace with evidence title
research_area: replace-me
evidence_type: primary
source_title:
source_author:
source_uri:
source_date:
retrieved: YYYY-MM-DD
created_by_agent: replace-me
confidence: medium
supports: []
contradicts: []
related_theories: []
tags: []
---

# Evidence Record

## Evidence summary

## Exact claim supported or contradicted

## Source provenance

## Relevant excerpt or data

## Interpretation

## Limitations

## Counterevidence

## Reproduction or verification notes
""",

    "templates/research/HYPOTHESIS-TEMPLATE.md": """\
---
id: HY-AREA-YYYY-0001
title: Replace with hypothesis
research_area: replace-me
status: proposed
confidence: low
created: YYYY-MM-DD
author_agent: replace-me
supporting_evidence: []
contradicting_evidence: []
related_theories: []
supersedes: []
superseded_by: []
---

# Hypothesis

## Statement

## Mechanism

## Predictions

## Evidence that would support it

## Evidence that would contradict it

## Tests performed

## Results

## Falsification attempts

## Current assessment

## Next experiment
""",

    "templates/research/THEORY-TEMPLATE.md": """\
---
id: TH-AREA-YYYY-0001
title: Replace with theory title
research_area: replace-me
version: 1.0.0
status: candidate
confidence: low
created: YYYY-MM-DD
updated: YYYY-MM-DD
derived_from: []
supporting_evidence: []
contradicting_evidence: []
supersedes: []
superseded_by: []
---

# Theory

## Theory statement

## Scope and boundary conditions

## Underlying mechanism

## Supported predictions

## Failed predictions

## Supporting evidence

## Contradicting evidence

## Alternative explanations

## Confidence rationale

## Open questions

## Required updates
""",

    "templates/research/EXPERIMENT-TEMPLATE.md": """\
---
id: EX-AREA-YYYY-0001
title: Replace with experiment title
research_area: replace-me
status: proposed
created: YYYY-MM-DD
author_agent: replace-me
tests_hypotheses: []
related_theories: []
inputs: []
outputs: []
---

# Experiment

## Research question

## Hypotheses tested

## Variables

## Method

## Acceptance criteria

## Falsification criteria

## Controls

## Procedure

## Results

## Analysis

## Threats to validity

## Replication notes

## Conclusion

## Registry updates required
""",

    "registries/research-packages.json": "[]\n",
    "registries/journals.json": "[]\n",
    "registries/evidence.json": "[]\n",
    "registries/hypotheses.json": "[]\n",
    "registries/theories.json": "[]\n",
    "registries/experiments.json": "[]\n",
    "registries/missions.json": "[]\n",
    "registries/agents.json": "[]\n",
    "registries/projects.json": "[]\n",

    "schemas/artifact-metadata.schema.json": json.dumps({
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": "https://example.invalid/ros/artifact-metadata.schema.json",
        "title": "ROS Artifact Metadata",
        "type": "object",
        "required": ["id", "title"],
        "properties": {
            "id": {
                "type": "string",
                "pattern": "^(RP|JR|EV|HY|TH|EX|DF|CN|GL|MS)-[A-Z0-9-]+-[0-9]{4}-[0-9]{4}$"
            },
            "title": {"type": "string", "minLength": 1},
            "research_area": {"type": "string"},
            "version": {"type": "string"},
            "confidence": {
                "enum": ["very-low", "low", "medium", "high", "very-high"]
            },
            "status": {"type": "string"},
            "tags": {"type": "array", "items": {"type": "string"}},
            "supersedes": {"type": "array", "items": {"type": "string"}},
            "superseded_by": {"type": "array", "items": {"type": "string"}}
        },
        "additionalProperties": True
    }, indent=2) + "\n",

    "input-documents/README.md": """\
# Input Documents

Place unprocessed incoming material here.

Processing agents must:

1. Inventory every item.
2. Preserve original bytes where required.
3. Assign provenance.
4. Move or copy accepted material into its canonical location.
5. Record rejected, duplicate, or superseded items.
6. Leave this directory empty after a completed intake run, except for this file.
""",

    "generated/README.md": """\
# Generated Artifacts

Everything in this directory is derived from canonical records.

Generated outputs must identify their source artifact IDs and generation
date. Do not treat this directory as the authoritative research record.
""",

    "archive/README.md": """\
# Archive

Store deprecated, superseded, or historical noncanonical material here.

Do not archive canonical records merely because they were superseded.
Canonical records preserve their history through metadata and registries.
""",

    ".gitignore": """\
# Operating-system files
.DS_Store
Thumbs.db

# Editors
.vscode/
.idea/
*.swp

# Python
__pycache__/
*.py[cod]
.venv/
venv/

# Node
node_modules/

# Generated transient work
work/staging/*
work/review/*
logs/*

# Preserve directory markers and documentation
!work/staging/.gitkeep
!work/review/.gitkeep
!logs/.gitkeep

# Secrets
.env
.env.*
!.env.example
*.pem
*.key
""",

    ".editorconfig": """\
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true

[*.md]
trim_trailing_whitespace = false

[*.{json,yml,yaml}]
indent_style = space
indent_size = 2

[*.py]
indent_style = space
indent_size = 4
""",

    "ros.json": json.dumps({
        "name": "research-operating-system",
        "rosVersion": ROS_VERSION,
        "repSpecificationVersion": REP_SPEC_VERSION,
        "bootstrap": "BOOTSTRAP.md",
        "canonicalRoots": {
            "journals": "research/journals",
            "packages": "research/packages",
            "theories": "research/theories",
            "evidence": "research/evidence"
        },
        "registries": "registries",
        "templates": "templates",
        "generated": "generated"
    }, indent=2) + "\n",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Initialize or repair a standard ROS repository layout."
    )
    parser.add_argument(
        "--repo",
        type=Path,
        default=Path.cwd(),
        help="Repository root. Defaults to the current directory.",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Overwrite managed files that already exist.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Show planned changes without writing them.",
    )
    return parser.parse_args()


def write_file(path: Path, content: str, force: bool, dry_run: bool) -> str:
    if path.exists() and not force:
        return "skip"

    if dry_run:
        return "would-overwrite" if path.exists() else "would-create"

    path.parent.mkdir(parents=True, exist_ok=True)
    normalized = textwrap.dedent(content).lstrip()
    path.write_text(normalized, encoding="utf-8", newline="\n")
    return "overwrite" if path.exists() else "create"


def ensure_gitkeep(directory: Path, dry_run: bool) -> bool:
    gitkeep = directory / ".gitkeep"
    if gitkeep.exists():
        return False
    if not dry_run:
        gitkeep.touch()
    return True


def main() -> int:
    args = parse_args()
    repo = args.repo.expanduser().resolve()

    if args.dry_run:
        print(f"[dry-run] ROS repository root: {repo}")
    else:
        repo.mkdir(parents=True, exist_ok=True)
        print(f"ROS repository root: {repo}")

    created_dirs = 0
    for relative in DIRECTORIES:
        directory = repo / relative
        if not directory.exists():
            created_dirs += 1
            if args.dry_run:
                print(f"  mkdir  {relative}")
            else:
                directory.mkdir(parents=True, exist_ok=True)

    actions = {"create": 0, "overwrite": 0, "skip": 0,
               "would-create": 0, "would-overwrite": 0}

    for relative, content in FILES.items():
        path = repo / relative
        existed_before = path.exists()

        if existed_before and not args.force:
            action = "skip"
        elif args.dry_run:
            action = "would-overwrite" if existed_before else "would-create"
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            normalized = textwrap.dedent(content).lstrip()
            path.write_text(normalized, encoding="utf-8", newline="\n")
            action = "overwrite" if existed_before else "create"

        actions[action] += 1
        if action != "skip":
            print(f"  {action:15} {relative}")

    # Retain intentionally empty operational directories in Git.
    for relative in [
        "missions/active",
        "missions/backlog",
        "missions/completed",
        "work/staging",
        "work/review",
        "logs",
        "tests/fixtures",
    ]:
        directory = repo / relative
        gitkeep = directory / ".gitkeep"
        if not gitkeep.exists():
            if args.dry_run:
                print(f"  would-create    {relative}/.gitkeep")
            else:
                directory.mkdir(parents=True, exist_ok=True)
                gitkeep.touch()

    timestamp = datetime.now(timezone.utc).isoformat()
    print()
    print("Summary")
    print(f"  Repository:       {repo}")
    print(f"  ROS version:      {ROS_VERSION}")
    print(f"  REP specification:{REP_SPEC_VERSION}")
    print(f"  Directories added:{created_dirs}")
    print(f"  Files created:    {actions['create']}")
    print(f"  Files overwritten:{actions['overwrite']}")
    print(f"  Files skipped:    {actions['skip']}")
    if args.dry_run:
        print(f"  Would create:     {actions['would-create']}")
        print(f"  Would overwrite:  {actions['would-overwrite']}")
    print(f"  Completed at:     {timestamp}")

    if not args.dry_run:
        print()
        print("Next steps")
        print("  1. Review BOOTSTRAP.md.")
        print("  2. Customize context/CURRENT-STATE.md.")
        print("  3. Copy templates/missions/MISSION-TEMPLATE.md into missions/active/.")
        print("  4. Commit the initialized structure to Git.")

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\nCancelled.", file=sys.stderr)
        raise SystemExit(130)
