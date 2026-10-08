#!/usr/bin/env python3
"""Renders the frozen EX-ROS-2026-A024 implementation prompts from one template.

Every prompt shares the same opening, rules and harness note; arms differ only
in the context-strategy block. START_SHA is filled at launch time from the
recorded branch head (for item 01 and arm 1, the sanitized start commit) and is
the only launch-time substitution. Usage: python3 make_prompts.py
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ITEMS = [f"PRAXIS-GROUP-0{n}" for n in range(1, 6)]
HANDOFFS = "research/experiments/EX-ROS-2026-A024-handoffs"
NOTE = open(os.path.join(HERE, "harness-note.txt"), encoding="utf-8").read().strip()

OPENING = ("You are working in a checkout of the kemiller2002/praxis repository on branch {branch}, "
           "which is already checked out. First run `git rev-parse HEAD`; it must print {{START_SHA}}. "
           "If it does not, stop, change nothing, and report the mismatch.")

PROTOCOL = ("Follow AGENTS.md and the Praxis work protocol: `./ros work start`, checkpoint and complete each item "
            "under its own ID, commit with the item ID as the message prefix, and push to origin {branch} "
            "before each checkpoint. Acceptance criteria are in each item's backlog description "
            "(`./ros work show ID`).")


def single(branch, item):
    return (f"Implement work item {item}. Do not implement any other work item. "
            + PROTOCOL.format(branch=branch))


def arm_1(branch):
    return (f"Implement work items {', '.join(ITEMS[:-1])} and {ITEMS[-1]}, in that order, in this one session. "
            + PROTOCOL.format(branch=branch)
            + " Before changing any production code, read all five items and decide the cross-item architecture once. "
              "Then implement the items serially.")


def handoff_block(item, index):
    previous = (f"A structured handoff from the session that completed {ITEMS[index - 1]} on this branch is at "
                f"{HANDOFFS}/{ITEMS[index - 1]}.json. Read it before you start. It is the only handoff you may read: "
                f"do not open any earlier version of a handoff from Git history. "
                if index > 0 else "")
    return (previous
            + f"After {item}'s implementation is committed and pushed, write {HANDOFFS}/{item}.json for a successor session "
              "that will continue this branch with only the repository and your handoff (no transcript). It must conform "
              "exactly to research/experiments/EX-ROS-2026-A024-harness/handoff.schema.json. Record engineering state only: "
              "durable decisions, invariants, reusable abstractions with their paths, changed paths, acceptance and test "
              "status, unresolved risks, and constraints and likely reuse relevant to the remaining members of this work "
              "group. Remove the previous handoff file (if any) with `git rm` in the same commit, so the directory only ever "
              "holds the latest handoff. Do not include private reasoning, a transcript, or anything committed state does not support. "
              "headCommit is the commit that contains the item's implementation. Validate it with "
              f"`python3 research/experiments/EX-ROS-2026-A024-harness/validate_handoff.py {HANDOFFS}/{item}.json --item {item}` "
              "until it exits 0, then commit it with the item ID prefix and push, before your final checkpoint.")


def render(branch, label, body):
    return "\n\n".join([OPENING.format(branch=branch), body, f"Telemetry LABEL={label} BRANCH={branch}", NOTE]) + "\n"


def prompts():
    yield "arm-1.txt", render("experiment/a024-arm-1", "arm-1", arm_1("experiment/a024-arm-1"))
    for index, item in enumerate(ITEMS):
        number = item[-2:]
        yield f"arm-2-{number}.txt", render("experiment/a024-arm-2", f"arm-2-{number}", single("experiment/a024-arm-2", item))
        yield f"arm-3-{number}.txt", render("experiment/a024-arm-3", f"arm-3-{number}",
                                             single("experiment/a024-arm-3", item) + " " + handoff_block(item, index))


if __name__ == "__main__":
    for name, text in prompts():
        with open(os.path.join(HERE, "prompts", name), "w", encoding="utf-8") as handle:
            handle.write(text)
        print(name)
