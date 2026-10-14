#!/usr/bin/env python3
"""Structural analysis of kemiller2002/signal's backlog for the A022 gate.

Reads the per-item detail records (.ros/work/items/WI-*.md) of a Signal
checkout and derives, without judgment:

- explicit relations from each record's "## Dependencies" section, split into
  hard edges (depends on / extends / gates / specialized extension of) and
  coupling edges (integrates / collaborates / feeds / provides / consumes /
  interacts / exposes / supplies), with "WI-a through WI-b" ranges expanded;
- scale: requirement-group rows, distinct source documents, acceptance
  criteria, and dated extensions appended after the original migration;
- canonical domain-contract mentions (the WI-0002 contract names);
- every 5-member subset of WI-0002..WI-0020 with no explicit edge between its
  members, under hard edges only and under all explicit relations, and for
  each such subset whether its members still all sit downstream of the
  WI-0002 canonical contracts through hard relations (depends on, extends
  or gates), i.e. share that domain invariant under the A022 low-affinity rule.

Usage: python3 signal_structure.py --signal PATH [--check FILE]
"""
import argparse
import itertools
import json
import re
import sys
from functools import reduce
from pathlib import Path

ITEM_IDS = tuple(f"WI-{n:04d}" for n in range(2, 21))
CONTRACT_ROOT = "WI-0002"
CONTRACT_NAMES = (
    "SurveyTemplate", "SurveyInstance", "SurveyResponse", "SurveySubmission",
    "SurveyGroup", "SurveyResult", "SurveyGroupResult", "AdminReportState",
)
HARD_VERBS = re.compile(
    r"^(depends on|extends|runs alongside and gates|gates|specialized extension of)\b", re.I
)
REQ_GROUP_ROW = re.compile(r"^\|\s*([A-Z]{2,5}-\d{3})\s*\|", re.M)
SOURCE_DOC = re.compile(r"input-documents/([A-Za-z0-9._-]+\.(?:txt|json|md))")
CRITERION = re.compile(r"^- \[[ xX]\] ", re.M)
EXTENSION = re.compile(r"^## .*extension \((\d{4}-\d{2}-\d{2})\)", re.M | re.I)
ID_OR_RANGE = re.compile(r"WI-(\d{4})(?:\s+through\s+WI-(\d{4}))?")


def section(text: str, heading: str) -> str:
    match = re.search(rf"^## {heading}\s*$(.*?)(?=^## |\Z)", text, re.M | re.S)
    return match.group(1).strip() if match else ""


def expand_ids(clause: str) -> tuple:
    def ids(m):
        lo = int(m.group(1))
        hi = int(m.group(2)) if m.group(2) else lo
        return tuple(f"WI-{n:04d}" for n in range(lo, hi + 1))
    return tuple(i for m in ID_OR_RANGE.finditer(clause) for i in ids(m))


def relations(item: str, dependencies: str) -> tuple:
    clauses = [c.strip() for c in re.split(r";", dependencies.replace("\n", " ")) if c.strip()]
    return tuple(
        {"from": item, "to": other, "kind": "hard" if HARD_VERBS.match(c) else "coupling", "clause": c}
        for c in clauses
        for other in expand_ids(c)
        if other != item and other in ITEM_IDS
    )


def describe(signal: Path, item: str) -> dict:
    text = (signal / ".ros" / "work" / "items" / f"{item}.md").read_text()
    deps = section(text, "Dependencies")
    return {
        "id": item,
        "title": text.splitlines()[0].split(":", 1)[1].strip(),
        "requirementGroups": len(set(REQ_GROUP_ROW.findall(text))),
        "sourceDocuments": len(set(SOURCE_DOC.findall(text))),
        "acceptanceCriteria": len(CRITERION.findall(text)),
        "extensions": sorted(set(EXTENSION.findall(text))),
        "contractMentions": sorted(n for n in CONTRACT_NAMES if re.search(rf"\b{n}\b", text)),
        "dependencyText": deps,
        "relations": relations(item, deps),
    }


def undirected(edges) -> frozenset:
    return frozenset(frozenset((e["from"], e["to"])) for e in edges)


def hard_linked_to(root: str, edges) -> frozenset:
    """Items that reach `root` through hard relations (depends on, extends, gates)."""
    hard = [(e["from"], e["to"]) for e in edges if e["kind"] == "hard"]
    def step(reached):
        return reached | {a for a, b in hard if b in reached}
    closure = reduce(lambda acc, _: step(acc), ITEM_IDS, frozenset({root}))
    return closure - {root}


def edge_free_subsets(size: int, pairs: frozenset) -> list:
    return [
        list(s) for s in itertools.combinations(ITEM_IDS, size)
        if not any(frozenset(p) in pairs for p in itertools.combinations(s, 2))
    ]


def analyse(signal: Path) -> dict:
    items = [describe(signal, i) for i in ITEM_IDS]
    edges = [e for i in items for e in i["relations"]]
    hard_pairs = undirected(e for e in edges if e["kind"] == "hard")
    all_pairs = undirected(edges)
    contract_linked = hard_linked_to(CONTRACT_ROOT, edges)
    gated_by_program = {e["to"] for e in edges if e["from"] in ("WI-0010", "WI-0020") and e["kind"] == "hard"}

    def annotate(subset):
        return {
            "members": subset,
            "allHardLinkedToContracts": all(m in contract_linked for m in subset),
            "membersHardLinkedToContracts": sum(m in contract_linked for m in subset),
        }

    free_hard = [annotate(s) for s in edge_free_subsets(5, hard_pairs)]
    free_all = [annotate(s) for s in edge_free_subsets(5, all_pairs)]
    degree = {i: sum(1 for p in all_pairs if i in p) for i in ITEM_IDS}
    return {
        "repository": "kemiller2002/signal",
        "items": [{k: v for k, v in i.items() if k != "relations"} for i in items],
        "relations": edges,
        "summary": {
            "itemCount": len(items),
            "explicitRelations": len(edges),
            "hardRelations": sum(e["kind"] == "hard" for e in edges),
            "undirectedPairsAll": len(all_pairs),
            "undirectedPairsHard": len(hard_pairs),
            "possiblePairs": len(ITEM_IDS) * (len(ITEM_IDS) - 1) // 2,
            "degreeAll": degree,
            "isolatedItems": sorted(i for i, d in degree.items() if d == 0),
            "hardLinkedToContractRoot": sorted(contract_linked),
            "gatedByVerificationPrograms": sorted(gated_by_program),
            "requirementGroups": {i["id"]: i["requirementGroups"] for i in items},
            "acceptanceCriteria": {i["id"]: i["acceptanceCriteria"] for i in items},
            "sourceDocuments": {i["id"]: i["sourceDocuments"] for i in items},
            "edgeFree5SubsetsHardOnly": len(free_hard),
            "edgeFree5SubsetsAllRelations": len(free_all),
            "edgeFree5SubsetsHardOnlyNotAllHardLinkedToContracts": sum(
                not s["allHardLinkedToContracts"] for s in free_hard
            ),
            "edgeFree5SubsetsAllRelationsNotAllHardLinkedToContracts": sum(
                not s["allHardLinkedToContracts"] for s in free_all
            ),
        },
        "edgeFree5SubsetsHardOnly": free_hard,
        "edgeFree5SubsetsAllRelations": free_all,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--signal", required=True, type=Path)
    parser.add_argument("--check", type=Path)
    args = parser.parse_args()
    doc = analyse(args.signal)
    if args.check:
        same = doc == json.loads(args.check.read_text())
        print("signal structure matches" if same else "signal structure DIFFERS")
        return 0 if same else 1
    print(json.dumps(doc, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
