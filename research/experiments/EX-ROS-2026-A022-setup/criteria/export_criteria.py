#!/usr/bin/env python3
"""EX-ROS-2026-A022: export each frozen cohort's work items verbatim from the
baseline backlog (`git show BASELINE:.ros/work/queue.json`).

Deterministic and read-only with respect to the repository. Writes
cohort-high.txt and cohort-low.txt next to this script.

Usage: python3 export_criteria.py [--check]
"""
import json
import pathlib
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
BASELINE = "f806f817e4656e00550313d839b59ceea87a682d"
# Members in frozen execution order: ordinal by ID, which also satisfies the
# one declared dependency (PRAXIS-CTL-06 after PRAXIS-CTL-02).
COHORTS = {
    "high": ("PRAXIS-CTL-02", "PRAXIS-CTL-03", "PRAXIS-CTL-04", "PRAXIS-CTL-05", "PRAXIS-CTL-06"),
    "low": ("ACTOR-KIND-ANCHOR", "ATTR-RECONCILE-SYMLINK-SUBMODULE", "PRAXIS-CN-GL-KINDS",
            "PRAXIS-PLAN-ERROR-HISTORY", "PRAXIS-STATE-MERGE-01"),
}


def baseline_items():
    text = subprocess.run(["git", "show", f"{BASELINE}:.ros/work/queue.json"], cwd=HERE,
                          check=True, capture_output=True, text=True).stdout
    return {item["id"]: item for item in json.loads(text)["items"]}


def render(items, ids):
    block = lambda item: "\n".join([
        f"{item['id']}: {item['title']}",
        f"  status at baseline: {item['status']}; tags: {', '.join(item.get('tags', []))}",
        "",
        item["description"],
    ])
    header = [f"Work items, verbatim from .ros/work/queue.json at {BASELINE}.",
              "The acceptance criteria are the 'Acceptance:' text in each description.", ""]
    return "\n".join(header + ["\n\n".join(block(items[i]) for i in ids)]) + "\n"


def main(argv):
    items = baseline_items()
    fresh = {f"cohort-{name}.txt": render(items, ids) for name, ids in COHORTS.items()}
    if "--check" in argv:
        stale = [n for n, t in fresh.items() if not (HERE / n).exists() or (HERE / n).read_text(encoding="utf-8") != t]
        print("stale: " + ", ".join(stale) if stale else "criteria exports are current")
        return 1 if stale else 0
    for name, text in fresh.items():
        (HERE / name).write_text(text, encoding="utf-8")
        print(f"wrote {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
