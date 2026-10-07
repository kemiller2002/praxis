#!/usr/bin/env python3
"""Runs the architecture-audit CLI probes against one built arm.

The probes P1-P10 and the concurrent-create probe (CC) are the runtime
evidence behind architecture-findings.json (D2, D5, D6 and the local
correctness items). This script makes them repeatable:

  run_probes.py --impl A021-grouped --cli PATH/ros-fs.dll --source ARM_CHECKOUT \
                --scratch DIR --out data/probes/A021-grouped.json [--group-flag=--group] \
                [--head SHA] [--concurrency-runs 5] [--processes 8]

--source is a checkout (for example a detached worktree) of the arm's head; it
is cloned afresh into --scratch for P1-P10 and once more for every concurrency
run, so the arm's own tree is never written. Every command uses a fixed
--occurred-at and a fixed actor, and output is normalized (scratch paths and
operational-failure reference IDs are replaced), so P1-P10 produce the same
JSON on every run. The concurrency probe is NOT deterministic: the result
records each run's counts and their totals, and says so.

Python 3 standard library only; requires git and dotnet on PATH.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

SCHEMA = "a021-publication.architecture-probes/1"
OCCURRED_AT = "2026-10-07T00:00:00.000Z"
ACTOR_ENV = {"ROS_ACTOR_KIND": "agent", "ROS_ACTOR": "probe", "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"}

ITEMS = (
    ("PRAXIS-PROBE-01", "Probe local one", "local work"),
    ("PRAXIS-PROBE-02", "Probe external", "Implemented in an external repository"),
    ("PRAXIS-PROBE-03", "Probe local two", "local work"),
    ("PRAXIS-PROBE-04", "Probe local three", "local work"),
)

# (id, description, argument template). {G} is the arm's group-ID flag.
PROBES = (
    ("P1", "create a local group {01,03}",
     "work group create {G} GROUP-PROBE-ONE --member PRAXIS-PROBE-01 --member PRAXIS-PROBE-03 --occurred-at {T}"),
    ("P2", "add 02 (description names an external repository; planner infers UnknownExternal) to that local group",
     "work group add {G} GROUP-PROBE-ONE --member PRAXIS-PROBE-02 --occurred-at {T}"),
    ("P3", "create a local group {01,02} (02 inferred external)",
     "work group create {G} GROUP-PROBE-TWO --member PRAXIS-PROBE-01 --member PRAXIS-PROBE-02 --occurred-at {T}"),
    ("P4", "create with the invalid group ID 'bad-id' (--json)",
     "work group create {G} bad-id --member PRAXIS-PROBE-01 --member PRAXIS-PROBE-03 --occurred-at {T} --json"),
    ("P5", "add to an undeclared group (--json)",
     "work group add {G} GROUP-PROBE-NONE --member PRAXIS-PROBE-04 --occurred-at {T} --json"),
    ("P6", "checkpoint an undeclared group (--json)",
     "work group checkpoint {G} GROUP-PROBE-NONE --occurred-at {T} --summary s --next-action n --json"),
    ("P7", "show an undeclared group (--json)",
     "work group show GROUP-PROBE-NONE --json"),
    ("P8", "create with an unknown member (--json)",
     "work group create {G} GROUP-PROBE-THREE --member PRAXIS-PROBE-01 --member PRAXIS-NOPE-99 --occurred-at {T} --json"),
    ("P9", "create with --execution-repository elsewhere and local members {01,03}",
     "work group create {G} GROUP-PROBE-FOUR --member PRAXIS-PROBE-01 --member PRAXIS-PROBE-03 --execution-repository elsewhere --occurred-at {T}"),
    ("P10", "add local 04 to the P9 group (if P9 created it)",
     "work group add {G} GROUP-PROBE-FOUR --member PRAXIS-PROBE-04 --occurred-at {T}"),
)

REFERENCE = re.compile(r"Reference [0-9A-Z]{26}")


def normalize(text, scratch):
    return REFERENCE.sub("Reference <ID>", text.replace(str(scratch), "<SCRATCH>"))


def environment():
    return {**os.environ, **ACTOR_ENV}


def clone(source, target):
    shutil.rmtree(target, ignore_errors=True)
    subprocess.run(["git", "clone", "-q", "-c", "advice.detachedHead=false", str(source), str(target)], check=True,
                   capture_output=True)
    return target


def command(cli, root, arguments):
    return ["dotnet", str(cli), "--root", str(root), *arguments]


def run(cli, root, arguments):
    result = subprocess.run(command(cli, root, arguments), capture_output=True, text=True, env=environment())
    return result.returncode, result.stdout, result.stderr


def capture_items(cli, root):
    return tuple(run(cli, root, ["work", "capture", "--title", title, "--id", item, "--description", description,
                                 "--occurred-at", OCCURRED_AT])[0]
                 for item, title, description in ITEMS)


def arguments_of(template, group_flag):
    return template.format(G=group_flag, T=OCCURRED_AT).split(" ")


def probe_record(cli, root, scratch, group_flag, probe):
    probe_id, description, template = probe
    arguments = arguments_of(template, group_flag)
    code, out, err = run(cli, root, arguments)
    return {"id": probe_id, "description": description, "arguments": " ".join(arguments), "exit_code": code,
            "stdout": normalize(out, scratch), "stderr": normalize(err, scratch)}


def stored_conc_groups(root):
    store = Path(root) / ".ros" / "work" / "groups.json"
    return len(set(re.findall(r"GROUP-CONC-\d+", store.read_text(encoding="utf-8")))) if store.exists() else 0


def concurrent_run(cli, source, scratch, group_flag, processes, index):
    root = clone(source, scratch / f"concurrency-{index}")
    capture_items(cli, root)
    launched = [subprocess.Popen(command(cli, root, arguments_of(
        f"work group create {{G}} GROUP-CONC-{n:03d} --member PRAXIS-PROBE-01 --member PRAXIS-PROBE-03 --occurred-at {{T}}",
        group_flag)), stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True, env=environment())
        for n in range(1, processes + 1)]
    finished = [(process.wait(), process.stderr.read()) for process in launched]
    exit_zero = sum(1 for code, _ in finished if code == 0)
    stored = stored_conc_groups(root)
    failures = sorted({normalize(err.strip(), scratch) for code, err in finished if code != 0})
    return {"run": index, "processes": processes, "exit_zero": exit_zero, "exit_nonzero": processes - exit_zero,
            "groups_stored": stored, "lost_updates": max(exit_zero - stored, 0), "failure_messages": failures}


def summarize(runs):
    return {key: sum(run[key] for run in runs) for key in ("exit_zero", "exit_nonzero", "groups_stored", "lost_updates")}


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--impl", required=True)
    parser.add_argument("--cli", required=True, type=Path)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--group-flag", default="--id")
    parser.add_argument("--head", default=None)
    parser.add_argument("--concurrency-runs", type=int, default=5)
    parser.add_argument("--processes", type=int, default=8)
    options = parser.parse_args(argv[1:])

    scratch = options.scratch.resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    root = clone(options.source.resolve(), scratch / "probes")
    captured = capture_items(options.cli, root)
    probes = [probe_record(options.cli, root, scratch, options.group_flag, probe) for probe in PROBES]
    runs = [concurrent_run(options.cli, options.source.resolve(), scratch, options.group_flag, options.processes, index)
            for index in range(1, options.concurrency_runs + 1)]

    document = {
        "schema": SCHEMA,
        "implementation": options.impl,
        "head_commit": options.head,
        "group_flag": options.group_flag,
        "occurred_at": OCCURRED_AT,
        "actor": {"kind": ACTOR_ENV["ROS_ACTOR_KIND"], "id": ACTOR_ENV["ROS_ACTOR"]},
        "setup": {"captured_items": [{"id": item, "description": description, "exit_code": code}
                                     for (item, _, description), code in zip(ITEMS, captured)]},
        "probes": probes,
        "concurrency": {
            "id": "CC",
            "description": f"{options.processes} concurrent 'work group create' processes on a fresh clone, repeated {options.concurrency_runs} times",
            "deterministic": False,
            "note": "Process interleaving is not controlled: per-run counts vary between executions; compare totals and the presence or absence of lost updates, not exact counts.",
            "runs": runs,
            "totals": summarize(runs),
        },
    }
    options.out.parent.mkdir(parents=True, exist_ok=True)
    options.out.write_text(json.dumps(document, indent=2, sort_keys=True, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{options.impl}: {len(probes)} probes, {len(runs)} concurrency runs -> {options.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
