#!/usr/bin/env python3
"""Extract every A021 / R2 quantitative metric from committed evidence.

Reads only pinned Git objects (``git show <sha>:<path>``), never a branch name
or the working tree, so the output is reproducible from the repository's
history alone. Writes ``data/metrics.json`` and ``data/metrics.csv``.

Style: pure functions over immutable data; the only side effects (running
``git`` and writing the two output files) live in ``load_inputs`` and ``main``.

Usage:
    python3 scripts/extract_metrics.py            # write data/metrics.{json,csv}
    python3 scripts/extract_metrics.py --stdout   # print metrics.json only

Conventions
-----------
* ``value`` is ``null`` when a source cannot supply a figure; it is never 0
  unless the source says 0.
* ``completeness``: complete | partial | lower_bound | unknown | not_applicable.
* ``session_role``: implementation | failed_attempt | orchestration | evaluator,
  or a ``+``-joined list of the roles an aggregate includes.
* ``internal_ref`` holds platform session identifiers. It is for internal
  audit only and must be stripped from anonymous artifacts.
"""
from __future__ import annotations

import csv
from collections import Counter
import datetime as dt
import io
import json
import re
import subprocess
import sys
from decimal import Decimal
from functools import reduce
from pathlib import Path
from types import MappingProxyType
from typing import Any, Callable, Iterable, Mapping, NamedTuple, Optional, Sequence

# --------------------------------------------------------------------------
# Pinned sources
# --------------------------------------------------------------------------

BASELINE = "8b4ffa392e93b19bf39f6672a608954c934cb815"
MAIN = "bc5a8d8e99306264c77436d313c5d0caba1d9f32"          # origin/main used for main-line records
A021_GROUPED = "5face886176839da30805c07fa893d2c18f3b5d2"  # experiment/a021-grouped head
A021_CONTROL = "8ea9be22fe6f1f3a7cbadc88f516709c40045f55"  # experiment/a021-control head
R2_GROUPED = "0b5284c37482d16bceafac214fa81f93417e2987"    # experiment/a021-r2-grouped head
R2_CONTROL = "8bb8f78adbaa67273ac507d1d14ad475e46fedd3"    # experiment/a021-r2-control head
R2_ORCH = "063e6b58d7bedce4a7d4a760a5413cb7fbd8de84"       # claude/a021-r2-orchestration-e0gty2 head

R2_FIRST_DAY = "2026-09-30"
FSHARP_BASELINE_TESTS_SOURCE = "A021 evaluation 1, 'Pre-existing tests' row"

H = "research/experiments/EX-ROS-2026-A021-harness"


class Src(NamedTuple):
    commit: str
    path: str


SOURCES: Mapping[str, Src] = MappingProxyType({
    "sessions": Src(MAIN, f"{H}/sessions.json"),
    "blind_mapping": Src(MAIN, f"{H}/blind-mapping.json"),
    "eval1": Src(MAIN, "research/experiments/EX-ROS-2026-A021-evaluation/evaluation.txt"),
    "kit": Src(MAIN, "research/experiments/EX-ROS-2026-A021-evaluation-kit/output/findings.json"),
    "r2_findings": Src(MAIN, "research/experiments/EX-ROS-2026-A021-R2-blind/output/findings.json"),
    "r2_post": Src(MAIN, "research/experiments/EX-ROS-2026-A021-R2-blind/output/POST-UNBLINDING-METRICS.txt"),
    "r2_run": Src(R2_ORCH, "research/experiments/EX-ROS-2026-A021-R2-run.txt"),
    "r2_control_log": Src(R2_CONTROL, "research/experiments/EX-ROS-2026-A021-R2-control/control-run-log.md"),
    "a021_t_grouped": Src(A021_GROUPED, "research/experiments/EX-ROS-2026-A021-grouped/metrics/grouped.json"),
    **{f"a021_t_control-0{i}": Src(A021_CONTROL, f"research/experiments/EX-ROS-2026-A021-control/metrics/control-0{i}.json")
       for i in range(1, 6)},
    "r2_t_grouped": Src(R2_GROUPED, "research/experiments/EX-ROS-2026-A021-R2-grouped/metrics/session-metrics-raw.json"),
    "r2_t_grouped_summary": Src(R2_GROUPED, "research/experiments/EX-ROS-2026-A021-R2-grouped/metrics/grouped.json"),
    **{f"r2_t_control-0{i}": Src(R2_CONTROL, f"research/experiments/EX-ROS-2026-A021-R2-control/metrics/control-0{i}.json")
       for i in range(1, 6)},
})

GIT_ARMS: Mapping[tuple[str, str], str] = MappingProxyType({
    ("A021", "grouped"): A021_GROUPED,
    ("A021", "independent"): A021_CONTROL,
    ("R2", "grouped"): R2_GROUPED,
    ("R2", "independent"): R2_CONTROL,
})

DIFF_PATHS = ("src", "tests", "docs")

# --------------------------------------------------------------------------
# Data model
# --------------------------------------------------------------------------

FIELDS = ("study", "arm", "unit", "metric", "value", "unit_of_measure", "completeness",
          "session_role", "time_definition", "source_path", "source_commit",
          "source_locator", "extraction_method", "notes", "internal_ref")


class Row(NamedTuple):
    study: str
    arm: str
    unit: str
    metric: str
    value: Any
    unit_of_measure: str
    completeness: str
    session_role: str
    time_definition: Optional[str]
    source_path: str
    source_commit: str
    source_locator: str
    extraction_method: str
    notes: str
    internal_ref: Optional[str]

    def key(self) -> tuple[str, str, str, str]:
        return (self.study, self.arm, self.unit, self.metric)


class Session(NamedTuple):
    study: str
    arm: str           # grouped | independent | none
    label: str
    role: str          # implementation | failed_attempt | orchestration | evaluator
    start: dt.datetime
    end: dt.datetime
    end_field: str
    blocked_until: Optional[dt.datetime]
    usage: Mapping[str, Any]   # platform usage (Decimal / int)
    usage_locator: str
    source: Src
    internal_ref: str
    transcript_key: Optional[str]


class Inputs(NamedTuple):
    blobs: Mapping[str, str]
    git: Mapping[tuple[str, str], Mapping[str, int]]


# --------------------------------------------------------------------------
# Small pure helpers
# --------------------------------------------------------------------------

def parse_time(text: str) -> dt.datetime:
    return dt.datetime.fromisoformat(text.replace("Z", "+00:00"))


def seconds(start: dt.datetime, end: dt.datetime) -> int:
    return int(round((end - start).total_seconds()))


def jdict(text: str) -> Any:
    return json.loads(text, parse_float=Decimal)


def dig(data: Any, path: Sequence[Any]) -> Any:
    return reduce(lambda acc, k: (acc.get(k) if isinstance(acc, Mapping) else
                                  (acc[k] if isinstance(acc, list) and isinstance(k, int) and k < len(acc) else None)),
                  path, data)


def locator(path: Sequence[Any]) -> str:
    return "$" + "".join(f"[{k}]" if isinstance(k, int) else f".{k}" for k in path)


def num(value: Any) -> Any:
    """Normalize Decimal to float for output; keep ints and None."""
    if isinstance(value, Decimal):
        return int(value) if value == value.to_integral_value() and "." not in str(value) else float(value)
    return value


def dsum(values: Iterable[Any]) -> Any:
    vals = tuple(values)
    return None if not vals or any(v is None for v in vals) else reduce(lambda a, b: a + b, vals)


ARM_NAME = MappingProxyType({"grouped": "grouped", "control": "independent", None: "none"})


def role_of(label: str) -> str:
    return ("evaluator" if label == "evaluator" else
            "failed_attempt" if "attempt" in label else "implementation")


def row(**kw: Any) -> Row:
    base = dict(time_definition=None, notes="", internal_ref=None)
    return Row(**{**base, **kw})


# --------------------------------------------------------------------------
# A021 sessions (platform usage from sessions.json)
# --------------------------------------------------------------------------

def a021_sessions(blobs: Mapping[str, str]) -> tuple[Session, ...]:
    doc = jdict(blobs["sessions"])

    def one(index: int, s: Mapping[str, Any]) -> Session:
        end_field = "finishedAt" if s.get("finishedAt") else "blockedAt"
        label = s["label"]
        return Session(
            study="A021", arm=ARM_NAME[s.get("arm")], label=label, role=role_of(label),
            start=parse_time(s["createdAt"]), end=parse_time(s[end_field]), end_field=end_field,
            blocked_until=parse_time(s["abandonedAt"]) if s.get("abandonedAt") else None,
            usage=MappingProxyType(dict(s["platformUsage"])),
            usage_locator=f"$.sessions[{index}].platformUsage", source=SOURCES["sessions"],
            internal_ref=s["session"],
            transcript_key=(f"a021_t_{label}" if f"a021_t_{label}" in SOURCES else None))

    return tuple(one(i, s) for i, s in enumerate(doc["sessions"]))


# --------------------------------------------------------------------------
# R2 sessions (platform usage from the orchestrator's run record, section 4)
# --------------------------------------------------------------------------

R2_ROW = re.compile(
    r"^(?P<code>G|C\d?)\s+.+?\s+(?P<session>session_\w+)\s+(?P<start>\d\d:\d\d:\d\d)\s+"
    r"(?P<end>\d\d:\d\d:\d\d)\s+(?P<input>[\d,]+)\s+(?P<output>[\d,]+)\s+(?P<cread>[\d,]+)\s+"
    r"(?P<cwrite>[\d,]+)\s+(?P<cost>[\d.]+)\s*$", re.M)

R2_LABEL = MappingProxyType({"G": "grouped", "C": "control-orchestrator", **{f"C{i}": f"control-0{i}" for i in range(1, 6)}})


def r2_sessions(blobs: Mapping[str, str]) -> tuple[Session, ...]:
    text = blobs["r2_run"]
    lines = text.splitlines()

    def at(clock: str) -> dt.datetime:
        return parse_time(f"{R2_FIRST_DAY}T{clock}Z")

    def one(m: re.Match[str]) -> Session:
        code = m["code"]
        label = R2_LABEL[code]
        line_no = next(i + 1 for i, l in enumerate(lines) if m["session"] in l and l.startswith(code + " "))
        return Session(
            study="R2", arm="grouped" if code == "G" else "independent", label=label,
            role="orchestration" if code == "C" else "implementation",
            start=at(m["start"]), end=at(m["end"]), end_field="get_session.updated_at after idle",
            blocked_until=None,
            usage=MappingProxyType({
                "input_tokens": int(m["input"].replace(",", "")),
                "output_tokens": int(m["output"].replace(",", "")),
                "cache_read_tokens": int(m["cread"].replace(",", "")),
                "cache_write_tokens": int(m["cwrite"].replace(",", "")),
                "cost_usd": Decimal(m["cost"]),
            }),
            usage_locator=f"section 4 table, line {line_no} (role {code})", source=SOURCES["r2_run"],
            internal_ref=m["session"],
            transcript_key=(f"r2_t_{label}" if f"r2_t_{label}" in SOURCES else None))

    return tuple(one(m) for m in R2_ROW.finditer(text))


def r2_common_start(blobs: Mapping[str, str]) -> tuple[dt.datetime, str]:
    m = re.search(r"both arms started at (\d\d:\d\d:\d\d)Z", blobs["r2_run"])
    if m is None:
        raise ValueError("R2 run record: common start not found")
    return parse_time(f"{R2_FIRST_DAY}T{m[1]}Z"), "section 4, 'Overall: both arms started at'"


# --------------------------------------------------------------------------
# Platform metrics per session
# --------------------------------------------------------------------------

PLATFORM_METRICS = (
    ("cost_usd", "cost_usd_platform", "USD"),
    ("input_tokens", "input_tokens_platform", "tokens"),
    ("output_tokens", "output_tokens_platform", "tokens"),
    ("cache_read_tokens", "cache_read_tokens_platform", "tokens"),
    ("cache_write_tokens", "cache_write_tokens_platform", "tokens"),
    ("context_used_tokens", "context_tokens_at_end_platform", "tokens"),
)


def session_rows(s: Session) -> tuple[Row, ...]:
    common = dict(study=s.study, arm=s.arm, unit=s.label, session_role=s.role,
                  source_path=s.source.path, source_commit=s.source.commit, internal_ref=s.internal_ref)
    a021_note = ("whole-session get_session usage recorded by the orchestrator after the session ended"
                 if s.study == "A021" else
                 "whole-session get_session usage observed by the R2 orchestrator after the session went idle")
    blocked_note = (" Usage recorded at the block (platformUsage equals platformUsageAtBlock); the session "
                    "was abandoned without further turns during the experiment."
                    if s.blocked_until else "")
    usage_rows = tuple(
        row(**common, metric=metric, value=num(s.usage.get(field)), unit_of_measure=uom,
            completeness="complete" if s.usage.get(field) is not None else "unknown",
            source_locator=f"{s.usage_locator}.{field}" if s.study == "A021" else s.usage_locator,
            extraction_method="copied from platform usage record",
            notes=(a021_note + blocked_note if s.usage.get(field) is not None else
                   "not recorded in the committed source for this study") +
                  (" get_session context_usage.used_tokens at the time of recording (final context size); "
                   "with 0 compactions this approximates the session's peak context."
                   if field == "context_used_tokens" else ""))
        for field, metric, uom in PLATFORM_METRICS)
    duration = row(
        **common, metric="session_duration_s", value=seconds(s.start, s.end), unit_of_measure="s",
        completeness="complete", time_definition="session_span",
        source_locator=(f"{s.usage_locator.rsplit('.', 1)[0]}.createdAt..{s.end_field}" if s.study == "A021"
                        else s.usage_locator + " Start..End"),
        extraction_method=f"computed: {s.end_field} - created_at",
        notes=("ends at blockedAt: time after the permission block is counted as blocked, not active"
               if s.end_field == "blockedAt" else
               "end is get_session.updated_at after the session went idle (same definition in A021 and R2)"))
    blocked = (row(**common, metric="blocked_time_s", value=seconds(s.end, s.blocked_until), unit_of_measure="s",
                   completeness="complete", time_definition="blocked",
                   source_locator=f"{s.usage_locator.rsplit('.', 1)[0]}.blockedAt..abandonedAt",
                   extraction_method="computed: abandonedAt - blockedAt",
                   notes="waiting on a permission prompt; no model work"),) if s.blocked_until else ()
    return usage_rows + (duration,) + blocked


# --------------------------------------------------------------------------
# Transcript (session_metrics.py) metrics per session
# --------------------------------------------------------------------------

class TMetric(NamedTuple):
    metric: str
    uom: str
    getter: Callable[[Any], Any]
    path_hint: str


def count_governance(doc: Any, *_: Any) -> Any:
    g = doc.get("governanceReads")
    return None if g is None else sum(int(v) for v in g.values())


def int_or_value(x: Any) -> Any:
    return x.get("value") if isinstance(x, Mapping) else x


# Per-study accessor tables. Each entry: label -> path override when a file's
# schema differs. ``None`` means the script value is not recoverable.
R2_SCRIPT_OVERRIDES: Mapping[str, Mapping[str, Optional[tuple[Any, ...]]]] = MappingProxyType({
    "control-01": {"compactions": ("compactions",)},
    "control-02": {"ttfc": ("msToFirstProductionCodeMutation",), "compactions": ("contextCompactions",)},
    "control-03": {"ttfc": ("msToFirstProductionCodeMutation",), "compactions": ("contextCompactions",),
                   "builds": None, "testRuns": ("testRuns", "summarizer"), "toolErrors": None},
    "control-04": {"ttfc": ("raw", "msToFirstCodeMutation"), "compactions": ("raw", "compactions"),
                   "builds": ("raw", "builds"), "testRuns": ("raw", "testRuns"), "toolErrors": ("raw", "toolErrors")},
    "control-05": {"ttfc": ("raw", "msToFirstCodeMutation"), "compactions": ("raw", "compactions"),
                   "builds": ("raw", "builds"), "testRuns": ("raw", "testRuns"), "toolErrors": ("raw", "toolErrors")},
})

DEFAULT_PATHS: Mapping[str, tuple[Any, ...]] = MappingProxyType({
    "modelRequests": ("modelRequests",),
    "tokens.input": ("tokens", "input"),
    "tokens.output": ("tokens", "output"),
    "tokens.cacheRead": ("tokens", "cacheRead"),
    "tokens.cacheCreation": ("tokens", "cacheCreation"),
    "fileReads": ("fileReads",),
    "distinctFilesRead": ("distinctFilesRead",),
    "searches": ("searches",),
    "fileWrites": ("fileWrites",),
    "builds": ("builds",),
    "testRuns": ("testRuns",),
    "toolErrors": ("toolErrors",),
    "compactions": ("compactions",),
    "ttfc": ("msToFirstCodeMutation",),
    "wall": ("wallMs",),
})

TRANSCRIPT_METRICS = (
    # key, metric, unit, transform
    ("modelRequests", "model_requests_transcript", "requests", None),
    ("tokens.input", "input_tokens_transcript", "tokens", None),
    ("tokens.output", "output_tokens_transcript", "tokens", None),
    ("tokens.cacheRead", "cache_read_tokens_transcript", "tokens", None),
    ("tokens.cacheCreation", "cache_write_tokens_transcript", "tokens", None),
    ("fileReads", "file_reads_transcript", "reads", None),
    ("distinctFilesRead", "distinct_files_read_transcript", "files", None),
    ("searches", "searches_transcript", "searches", None),
    ("fileWrites", "file_writes_transcript", "writes", None),
    ("builds", "builds_transcript", "build commands", None),
    ("testRuns", "test_runs_transcript", "test commands", None),
    ("toolErrors", "tool_errors_transcript", "errors", None),
    ("compactions", "compactions_transcript", "compactions", None),
    ("ttfc", "time_to_first_code_s", "s", "ms_to_s"),
    ("wall", "transcript_span_s", "s", "ms_to_s"),
)


def ms_to_s(v: Any) -> Any:
    return None if v is None else round(int(v) / 1000, 3)


def path_for(study: str, label: str, key: str) -> Optional[tuple[Any, ...]]:
    if study == "R2":
        ov = R2_SCRIPT_OVERRIDES.get(label, {})
        if key in ov:
            return ov[key]
    return DEFAULT_PATHS[key]


def tool_calls_total(doc: Any, study: str, label: str) -> tuple[Any, str]:
    src = doc.get("raw", doc) if study == "R2" and label in ("control-04", "control-05") else doc
    tc = src.get("toolCalls")
    return (sum(int(v) for v in tc.values()), "sum of toolCalls") if isinstance(tc, Mapping) else (None, "toolCalls")


def repeated_paths(doc: Any) -> tuple[Any, str]:
    rr = doc.get("repeatedReads", doc.get("repeatedFileReads"))
    return (None, "repeatedReads") if rr is None else (len(rr), "count of repeatedReads keys")


def transcript_rows(s: Session, blobs: Mapping[str, str]) -> tuple[Row, ...]:
    common = dict(study=s.study, arm=s.arm, unit=s.label, session_role=s.role, internal_ref=s.internal_ref)
    if s.transcript_key is None:
        if s.role == "orchestration":
            return ()
        reason = ("no transcript metrics exist for this session: "
                  + ("its transcript is not readable (attempt ended without pushing anything)"
                     if s.label.endswith("attempt-1") else
                     "the session blocked before running the metrics script" if s.label.endswith("attempt-2")
                     else "the evaluator did not run the metrics script"))
        return tuple(row(**common, metric=metric, value=None, unit_of_measure=uom, completeness="unknown",
                         source_path=s.source.path, source_commit=s.source.commit, source_locator="n/a",
                         extraction_method="none", notes=reason)
                     for _, metric, uom, _ in TRANSCRIPT_METRICS) if s.role != "evaluator" else ()
    src = SOURCES[s.transcript_key]
    doc = jdict(blobs[s.transcript_key])
    timing_note = ("captured by the session itself before its final checkpoint/completion/commit steps, "
                   "so counts and tokens miss the last minutes of the session")
    a021_04 = s.study == "A021" and s.label == "control-04"
    extra = (" Covers attempt 3 only (bookkeeping); the item-04 implementation happened in attempt 2, "
             "which has no transcript metrics." if a021_04 else "")

    def one(spec: tuple[str, str, str, Optional[str]]) -> Row:
        key, metric, uom, transform = spec
        p = path_for(s.study, s.label, key)
        raw = None if p is None else int_or_value(dig(doc, p))
        value = ms_to_s(raw) if transform == "ms_to_s" else num(raw)
        if key == "ttfc":
            completeness = "complete" if value is not None else "not_applicable"
            note = ("time from the first transcript entry to the first Edit/Write/Bash write under src/ or tests/"
                    if value is not None else "no production-code mutation occurred in this session")
        elif p is None:
            completeness, note = "unknown", "the file reports only a session-corrected value; the script's own count is not recorded"
        else:
            completeness = "lower_bound" if key not in ("compactions",) else "complete"
            note = timing_note
        if key == "builds":
            note += "; script heuristic: Bash commands matching 'dotnet build' or 'build:fsharp' (may include greps)"
        if key == "testRuns":
            note += "; script heuristic: Bash commands matching the test runners (python unittest not matched)"
        return row(**common, metric=metric, value=value, unit_of_measure=uom, completeness=completeness,
                   time_definition="transcript_span" if key in ("wall", "ttfc") else None,
                   source_path=src.path, source_commit=src.commit,
                   source_locator=locator(p) if p is not None else "n/a",
                   extraction_method="copied from session_metrics.py output" + (", ms/1000" if transform else ""),
                   notes=note + extra)

    gov = doc.get("governanceReads") or {}
    tc_value, tc_loc = tool_calls_total(doc, s.study, s.label)
    rp_value, rp_loc = repeated_paths(doc)
    derived = (
        row(**common, metric="governance_reads_transcript", value=count_governance(doc), unit_of_measure="reads",
            completeness="lower_bound", source_path=src.path, source_commit=src.commit,
            source_locator="$.governanceReads (sum of values)",
            extraction_method="sum of governanceReads counts (AGENTS.md, CLAUDE.md, docs/00-governance/, "
                              "docs/work-protocol.md, docs/planning.md, docs/cli.md, requirements/PLANNING-WORK-GROUPS.md, "
                              "docs/development-telemetry.md, docs/agent-provenance.md)",
            notes=timing_note + extra),
        row(**common, metric="agents_md_reads_transcript", value=int(gov.get("AGENTS.md", 0)) if gov else None,
            unit_of_measure="reads", completeness="lower_bound", source_path=src.path, source_commit=src.commit,
            source_locator="$.governanceReads['AGENTS.md']",
            extraction_method="copied; absent key with a present governanceReads map counts 0",
            notes=timing_note + extra),
        row(**common, metric="tool_calls_transcript", value=tc_value, unit_of_measure="calls",
            completeness="lower_bound" if tc_value is not None else "unknown", source_path=src.path,
            source_commit=src.commit, source_locator=f"$.toolCalls ({tc_loc})", extraction_method="sum of per-tool counts",
            notes=timing_note + extra),
        row(**common, metric="repeated_read_paths_transcript", value=rp_value, unit_of_measure="paths",
            completeness="lower_bound" if rp_value is not None else "unknown", source_path=src.path,
            source_commit=src.commit, source_locator=f"$ ({rp_loc})",
            extraction_method="number of paths read more than once in the session", notes=timing_note + extra),
    )
    return tuple(one(spec) for spec in TRANSCRIPT_METRICS) + derived


def r2_subagent_note_rows(blobs: Mapping[str, str], sessions: Sequence[Session]) -> tuple[Row, ...]:
    doc = jdict(blobs["r2_t_control-03"])
    scope = doc.get("tokensScope", "")
    m = re.search(r"additionally used (\d+) tokens", scope)
    s = next(x for x in sessions if x.label == "control-03")
    src = SOURCES["r2_t_control-03"]
    return (row(study="R2", arm="independent", unit="control-03", metric="subagent_tokens_untyped_transcript",
                value=int(m[1]) if m else None, unit_of_measure="tokens", completeness="complete" if m else "unknown",
                session_role="implementation", source_path=src.path, source_commit=src.commit,
                source_locator="$.tokensScope", extraction_method="regex on tokensScope text",
                notes="one Explore subagent's total tokens (no input/output/cache breakdown); not included in the "
                      "transcript token metrics; platform usage of the session includes it",
                internal_ref=s.internal_ref),)


# --------------------------------------------------------------------------
# Aggregates
# --------------------------------------------------------------------------

class AggRule(NamedTuple):
    unit: str
    roles: tuple[str, ...]
    description: str


AGG_WORKERS = AggRule("agg:workers", ("implementation", "failed_attempt"),
                      "all sessions that worked on the arm's items, including failed attempts; "
                      "excludes orchestration and evaluator sessions")
AGG_ALL = AggRule("agg:workers+orchestration", ("implementation", "failed_attempt", "orchestration"),
                  "agg:workers plus the arm's own orchestration session(s); excludes evaluators")

SUM_METRICS = frozenset({
    "cost_usd_platform", "input_tokens_platform", "output_tokens_platform", "cache_read_tokens_platform",
    "cache_write_tokens_platform", "model_requests_transcript", "input_tokens_transcript",
    "output_tokens_transcript", "cache_read_tokens_transcript", "cache_write_tokens_transcript",
    "file_reads_transcript", "searches_transcript", "file_writes_transcript", "builds_transcript",
    "test_runs_transcript", "tool_errors_transcript", "compactions_transcript", "governance_reads_transcript",
    "agents_md_reads_transcript", "tool_calls_transcript", "time_to_first_code_s", "transcript_span_s",
    "blocked_time_s",
})
MAX_METRICS = frozenset({"context_tokens_at_end_platform"})
# The orchestration-inclusive aggregate is defined for platform usage only:
# orchestrators ran no transcript metrics script and overlap the workers in time.
PLATFORM_AGG_METRICS = frozenset({"cost_usd_platform", "input_tokens_platform", "output_tokens_platform",
                                  "cache_read_tokens_platform", "cache_write_tokens_platform"})

RANK = MappingProxyType({"complete": 0, "not_applicable": 0, "partial": 1, "lower_bound": 2, "unknown": 3})


def combine_completeness(rows: Sequence[Row], metric: str) -> str:
    present = tuple(r for r in rows if r.completeness != "not_applicable")
    if not present:
        return "not_applicable"
    if all(r.value is None for r in present):
        return "unknown"
    if any(r.value is None for r in present):
        return "lower_bound" if metric in SUM_METRICS else "partial"
    worst = max(present, key=lambda r: RANK[r.completeness]).completeness
    return worst


def aggregate(rows: Sequence[Row], sessions: Sequence[Session], study: str, arm: str, rule: AggRule) -> tuple[Row, ...]:
    labels = tuple(s.label for s in sessions if s.study == study and s.arm == arm and s.role in rule.roles)
    roles = "+".join(r for r in rule.roles if any(s.role == r for s in sessions
                                                  if s.study == study and s.arm == arm and s.label in labels))
    members = tuple(r for r in rows if r.study == study and r.arm == arm and r.unit in labels)
    allowed = PLATFORM_AGG_METRICS if rule is AGG_ALL else (SUM_METRICS | MAX_METRICS)
    metrics = sorted({r.metric for r in members} & allowed)

    def one(metric: str) -> Row:
        rs = tuple(r for r in members if r.metric == metric)
        vals = tuple(r.value for r in rs if r.value is not None and r.completeness != "not_applicable")
        if metric in MAX_METRICS:
            value = max(vals) if vals else None
            how = "max over included sessions"
        else:
            dec = tuple(Decimal(str(v)) for v in vals)
            total = reduce(lambda a, b: a + b, dec, Decimal(0)) if dec else None
            value = None if total is None else (int(total) if total == total.to_integral_value() and all(isinstance(v, int) for v in vals) else float(total))
            how = "sum over included sessions with a value"
        completeness = combine_completeness(rs, metric)
        missing = sorted(r.unit for r in rs if r.value is None and r.completeness != "not_applicable")
        return row(study=study, arm=arm, unit=rule.unit, metric=metric, value=value,
                   unit_of_measure=rs[0].unit_of_measure, completeness=completeness, session_role=roles,
                   time_definition=rs[0].time_definition if metric != "transcript_span_s" else "transcript_span_sum",
                   source_path=";".join(sorted({r.source_path for r in rs})),
                   source_commit=";".join(sorted({r.source_commit for r in rs})),
                   source_locator="sessions: " + ",".join(sorted(r.unit for r in rs)),
                   extraction_method=f"computed: {how}; rule {rule.unit} = {rule.description}",
                   notes=("missing (null) in: " + ",".join(missing) if missing else "") +
                         ("; " if missing and metric == "time_to_first_code_s" else "") +
                         ("cumulative orientation time before the first production-code change"
                          if metric == "time_to_first_code_s" else ""))
    return tuple(one(m) for m in metrics)


def span_rows(sessions: Sequence[Session], study: str, arm: str, rule: AggRule,
              common_start: Optional[tuple[dt.datetime, str]]) -> tuple[Row, ...]:
    chosen = tuple(s for s in sessions if s.study == study and s.arm == arm and s.role in rule.roles)
    if not chosen:
        return ()
    src = chosen[0].source
    roles = "+".join(sorted({s.role for s in chosen}))
    refs = None
    first, last = min(s.start for s in chosen), max(s.end for s in chosen)
    active = sum(seconds(s.start, s.end) for s in chosen)
    base = dict(study=study, arm=arm, unit=rule.unit, unit_of_measure="s", completeness="complete",
                session_role=roles, source_path=src.path, source_commit=src.commit, internal_ref=refs)
    active_rows = () if rule is AGG_ALL else (
        row(**base, metric="active_session_sum_s", value=active, time_definition="active_session_sum",
            source_locator="sessions: " + ",".join(sorted(s.label for s in chosen)),
            extraction_method=f"computed: sum of session spans (created_at to end; blocked sessions end at blockedAt); "
                              f"rule {rule.unit} = {rule.description}",
            notes="sessions ran serially; spans do not overlap"),)
    rows = active_rows + (
        row(**base, metric="wall_clock_span_s", value=seconds(first, last), time_definition="wall_clock_span",
            source_locator="min(created_at)..max(end) over " + ",".join(sorted(s.label for s in chosen)),
            extraction_method=f"computed: last end - first start over the included sessions; rule {rule.unit}",
            notes="includes orchestration gaps between sessions and any blocked time"),
    )
    if common_start is not None and rule is AGG_ALL:
        start, where = common_start
        rows = rows + (row(**base, metric="wall_clock_span_from_common_start_s", value=seconds(start, last),
                           time_definition="wall_clock_span",
                           source_locator=f"{where} .. max(end)",
                           extraction_method="computed: last end of the arm - the run record's common arm start "
                                             "(the grouped session's created_at)",
                           notes="definition used by POST-UNBLINDING-METRICS / EV-A070 for 'elapsed'; it starts at the "
                                 "grouped session's creation, 2 s before the control orchestrator was created"),)
    return rows


# --------------------------------------------------------------------------
# Session counts
# --------------------------------------------------------------------------

def count_rows(sessions: Sequence[Session]) -> tuple[Row, ...]:
    def one(study: str, arm: str, role: str) -> Row:
        chosen = tuple(s for s in sessions if s.study == study and s.arm == arm and s.role == role)
        src = SOURCES["sessions"] if study == "A021" else SOURCES["r2_run"]
        return row(study=study, arm=arm, unit="agg:arm", metric=f"sessions_{role}", value=len(chosen),
                   unit_of_measure="sessions", completeness="complete", session_role=role,
                   source_path=src.path, source_commit=src.commit,
                   source_locator="$.sessions[*].label" if study == "A021" else "section 4 table",
                   extraction_method="count of sessions by role",
                   notes=("A021 had one orchestrator shared by both arms (not attributable to an arm; its usage was "
                          "not recorded in the experiment's sources)" if study == "A021" and role == "orchestration"
                          else ""))
    return tuple(one(st, arm, role) for st in ("A021", "R2") for arm in ("grouped", "independent")
                 for role in ("implementation", "failed_attempt", "orchestration"))


def a021_orchestration_placeholders() -> tuple[Row, ...]:
    src = SOURCES["sessions"]
    return tuple(row(study="A021", arm=arm, unit="agg:workers+orchestration", metric=metric, value=None,
                     unit_of_measure=uom, completeness="unknown", session_role="implementation+failed_attempt+orchestration",
                     source_path=src.path, source_commit=src.commit, source_locator="$.orchestrator",
                     extraction_method="none",
                     notes="A021 used one orchestrator session for both arms and the evaluator; its usage is not "
                           "recorded in the experiment's sources and cannot be split by arm")
                 for arm in ("grouped", "independent")
                 for metric, uom in (("cost_usd_platform", "USD"), ("output_tokens_platform", "tokens")))


# --------------------------------------------------------------------------
# Git-derived arm metrics
# --------------------------------------------------------------------------

def git_rows(git: Mapping[tuple[str, str], Mapping[str, int]]) -> tuple[Row, ...]:
    specs = (("commits", "commits", "git rev-list --count BASE..HEAD"),
             ("merge_commits", "commits", "git rev-list --merges --count BASE..HEAD"),
             ("diff_files_changed", "files", "git diff --shortstat BASE HEAD -- src tests docs"),
             ("diff_insertions", "lines", "git diff --shortstat BASE HEAD -- src tests docs"),
             ("diff_deletions", "lines", "git diff --shortstat BASE HEAD -- src tests docs"))
    return tuple(
        row(study=study, arm=arm, unit="agg:arm", metric=metric, value=git[(study, arm)][metric],
            unit_of_measure=uom, completeness="complete", session_role="implementation+failed_attempt+orchestration",
            source_path="(git history)", source_commit=GIT_ARMS[(study, arm)],
            source_locator=f"BASE={BASELINE}", extraction_method=how.replace("HEAD", GIT_ARMS[(study, arm)][:9]),
            notes=("all commits on the arm branch after the baseline, including Praxis-state, metrics and run-log commits"
                   if metric == "commits" else ""))
        for (study, arm) in sorted(GIT_ARMS) for metric, uom, how in specs)


def merge_conflict_rows(blobs: Mapping[str, str], git: Mapping[tuple[str, str], Mapping[str, int]]) -> tuple[Row, ...]:
    sess = jdict(blobs["sessions"])
    a021_dev = tuple(i for i, d in enumerate(sess["deviations"]) if re.search(r"conflict in", d))
    r2 = tuple((label, dig(jdict(blobs[f"r2_t_{label}"]), ("mergeConflicts", "value")))
               for label in ("control-01", "control-02", "control-03", "control-04", "control-05"))
    r2_with = tuple((l, v) for l, v in r2 if v)
    zero = lambda study: row(
        study=study, arm="grouped", unit="agg:arm", metric="merges_with_conflicts", value=0,
        unit_of_measure="merges", completeness="complete", session_role="implementation",
        source_path="(git history)", source_commit=GIT_ARMS[(study, "grouped")], source_locator=f"BASE={BASELINE}",
        extraction_method="0 because the arm branch has no merge commits (git rev-list --merges)",
        notes="") if git[(study, "grouped")]["merge_commits"] == 0 else None
    src_r2 = SOURCES["r2_t_control-02"]
    return tuple(r for r in (
        zero("A021"), zero("R2"),
        row(study="A021", arm="independent", unit="agg:arm", metric="merges_with_conflicts", value=len(a021_dev),
            unit_of_measure="merges", completeness="complete", session_role="implementation",
            source_path=SOURCES["sessions"].path, source_commit=SOURCES["sessions"].commit,
            source_locator=",".join(f"$.deviations[{i}]" for i in a021_dev),
            extraction_method="count of recorded deviations describing a merge conflict",
            notes="control-05 merged the branch (1aa11fb) with a conflict in Program.fs; the branch has "
                  f"{git[('A021', 'independent')]['merge_commits']} merge commit(s)"),
        row(study="R2", arm="independent", unit="agg:arm", metric="merges_with_conflicts", value=len(r2_with),
            unit_of_measure="merges", completeness="complete", session_role="implementation",
            source_path=src_r2.path.replace("control-02", "control-0*"), source_commit=src_r2.commit,
            source_locator="$.mergeConflicts.value per worker file",
            extraction_method="count of worker metrics files reporting mergeConflicts.value > 0",
            notes="; ".join(f"{l}: {v} conflicted files" for l, v in r2_with) +
                  " (includes .ros state files; C3's conflicts were .ros state only)"),
        row(study="R2", arm="independent", unit="agg:arm", metric="conflicted_files", value=sum(v for _, v in r2_with),
            unit_of_measure="files", completeness="complete", session_role="implementation",
            source_path=src_r2.path.replace("control-02", "control-0*"), source_commit=src_r2.commit,
            source_locator="$.mergeConflicts.value per worker file", extraction_method="sum",
            notes="files with textual conflicts across both merges, including Praxis state files"),
    ) if r is not None)


# --------------------------------------------------------------------------
# Quality / evaluation metrics
# --------------------------------------------------------------------------

def table_cells(text: str, first_cell: str) -> tuple[tuple[str, ...], int]:
    for i, line in enumerate(text.splitlines()):
        cells = tuple(c.strip() for c in line.strip().strip("|").split("|"))
        if line.startswith("|") and cells and cells[0] == first_cell:
            return cells, i + 1
    raise ValueError(f"table row not found: {first_cell}")


def section(text: str, start: str, end: str) -> str:
    a = text.index(start)
    return text[a:text.index(end, a)]


def ints(s: str) -> tuple[int, ...]:
    return tuple(int(x.replace(",", "")) for x in re.findall(r"\d[\d,]*", s))


def eval1_rows(blobs: Mapping[str, str]) -> tuple[Row, ...]:
    text = blobs["eval1"]
    mapping = json.loads(blobs["blind_mapping"])
    arm_of = {"X": ARM_NAME[mapping["arm-x"]], "Y": ARM_NAME[mapping["arm-y"]]}
    src = SOURCES["eval1"]

    def mk(arm: str, metric: str, value: Any, uom: str, loc: str, how: str, notes: str = "",
           completeness: str = "complete") -> Row:
        return row(study="A021", arm=arm, unit="agg:arm", metric=metric, value=value, unit_of_measure=uom,
                   completeness=completeness, session_role="evaluator", source_path=src.path, source_commit=src.commit,
                   source_locator=loc, extraction_method=how,
                   notes=(notes + "; " if notes else "") + "A021 evaluation 1 (protocol brief); blind arm "
                         f"{'X' if arm == arm_of['X'] else 'Y'} mapped by blind-mapping.json")

    tests, l1 = table_cells(text, "Test run")
    added, l2 = table_cells(text, "Tests added")
    pre, l3 = table_cells(text, "Pre-existing tests")
    lines, l4 = table_cells(text, "Lines added (src / tests / docs)")
    srcf, l5 = table_cells(text, "Files changed under `src/`")
    sec_x = section(text, "## Arm X", "## Arm Y")
    sec_y = section(text, "## Arm Y", "## Side-by-side comparison")
    acc = lambda sec: section(sec, "### Acceptance criteria", "### Consistency assessment")
    verdicts = lambda sec: tuple(c[1] for c in (tuple(x.strip() for x in l.strip().strip("|").split("|"))
                                                for l in acc(sec).splitlines() if l.startswith("|"))
                                 if len(c) > 1 and c[1] not in ("Verdict",) and not set(c[1]) <= set("-: "))
    out = tuple(
        r for col, blind in ((1, "X"), (2, "Y")) for r in (
            mk(arm_of[blind], "fsharp_tests_passed_final", ints(tests[col])[0], "tests", f"build/test table row 'Test run' (line {l1})",
               "regex: first integer in cell"),
            mk(arm_of[blind], "fsharp_tests_failed_final", ints(tests[col])[1], "tests", f"row 'Test run' (line {l1})",
               "regex: second integer in cell"),
            mk(arm_of[blind], "tests_added", ints(added[col])[0], "tests", f"row 'Tests added' (line {l2})",
               "regex: first integer in cell", "work-group tests added by the arm"),
            mk(arm_of[blind], "fsharp_tests_preexisting", ints(pre[col])[0], "tests", f"row 'Pre-existing tests' (line {l3})",
               "regex: first integer in cell"),
            mk(arm_of[blind], "lines_added_src", ints(lines[col])[0], "lines", f"row 'Lines added' (line {l4})", "regex"),
            mk(arm_of[blind], "lines_added_tests", ints(lines[col])[1], "lines", f"row 'Lines added' (line {l4})", "regex"),
            mk(arm_of[blind], "lines_added_docs", ints(lines[col])[2], "lines", f"row 'Lines added' (line {l4})", "regex"),
            mk(arm_of[blind], "src_files_changed", ints(srcf[col])[0], "files", f"row 'Files changed under src/' (line {l5})", "regex"),
        ))
    ac = tuple(
        r for blind, sec in (("X", sec_x), ("Y", sec_y)) for r in (
            mk(arm_of[blind], "acceptance_rows_assessed_eval1", len(verdicts(sec)), "criteria rows",
               f"'## Arm {blind}' / '### Acceptance criteria' tables", "count of verdict cells"),
            mk(arm_of[blind], "acceptance_rows_partially_met_eval1",
               sum(1 for v in verdicts(sec) if v.startswith("Partially")), "criteria rows",
               f"'## Arm {blind}' / '### Acceptance criteria' tables", "count of verdict cells starting 'Partially'"),
            mk(arm_of[blind], "acceptance_rows_not_met_eval1",
               sum(1 for v in verdicts(sec) if v.startswith("Not met")), "criteria rows",
               f"'## Arm {blind}' / '### Acceptance criteria' tables", "count of verdict cells starting 'Not met'"),
            mk(arm_of[blind], "defects_explicitly_labelled_eval1",
               sum(1 for l in sec.splitlines() if l.startswith("Defect:")), "defects", f"'## Arm {blind}' section",
               "count of paragraphs starting 'Defect:'",
               "evaluation 1 gives no defect tally; this counts only paragraphs it labels 'Defect:'. Other confirmed "
               "behavioural issues are reported as caveats or inconsistencies", completeness="partial"),
        ))
    return out + ac


def kit_rows(blobs: Mapping[str, str]) -> tuple[Row, ...]:
    doc = json.loads(blobs["kit"])
    mapping = json.loads(blobs["blind_mapping"])
    arm_of = {"arm-X": ARM_NAME[mapping["arm-x"]], "arm-Y": ARM_NAME[mapping["arm-y"]]}
    src = SOURCES["kit"]
    return tuple(
        row(study="A021", arm=arm_of[blind], unit="agg:arm", metric=f"findings_{sev}_kit",
            value=sum(1 for f in doc if f["arm"] == blind and f["severity"] == sev), unit_of_measure="findings",
            completeness="complete", session_role="evaluator", source_path=src.path, source_commit=src.commit,
            source_locator=f"$[?(@.arm=='{blind}' && @.severity=='{sev}')]",
            extraction_method="count of findings by arm and severity",
            notes="A021 second (kit) evaluation, EV-ROS-2026-A067; blinding compromised by two unscrubbed strings. "
                  "Findings with arm 'both' are not counted. arm-X = grouped (same blind branches as evaluation 1; "
                  "its diffstat 2279/-7 equals the grouped arm's)")
        for blind in ("arm-X", "arm-Y") for sev in ("defect", "inconsistency", "debt"))


def r2_quality_rows(blobs: Mapping[str, str]) -> tuple[Row, ...]:
    doc = json.loads(blobs["r2_findings"])
    post = blobs["r2_post"]
    arm_of = {letter: ("grouped" if re.search(rf"Arm {letter} = grouped", post) else
                       "independent" if re.search(rf"Arm {letter} = independent", post) else None)
              for letter in ("M", "N")}
    if None in arm_of.values():
        raise ValueError("R2 unblinding mapping not found")
    src = SOURCES["r2_findings"]

    def mk(letter: str, metric: str, value: Any, uom: str, loc: str, how: str, notes: str = "") -> Row:
        return row(study="R2", arm=arm_of[letter], unit="agg:arm", metric=metric, value=value, unit_of_measure=uom,
                   completeness="complete", session_role="evaluator", source_path=src.path, source_commit=src.commit,
                   source_locator=loc, extraction_method=how,
                   notes=(notes + "; " if notes else "") + f"R2 blind evaluation; Arm {letter} = {arm_of[letter]} "
                         "per POST-UNBLINDING-METRICS.txt")

    out = tuple(r for L in ("M", "N") for r in (
        mk(L, "confirmed_acceptance_defects_r2", doc["confirmedAcceptanceDefectCount"][L], "defects",
           f"$.confirmedAcceptanceDefectCount.{L}", "copied"),
        mk(L, "design_risks_r2", len(doc["arms"][L]["designRisks"]), "risks", f"$.arms.{L}.designRisks", "count"),
        mk(L, "acceptance_items_with_defect_r2",
           sum(1 for a in doc["arms"][L]["acceptanceCriteria"] if a["result"] == "defect"), "work items",
           f"$.arms.{L}.acceptanceCriteria[*].result", "count of items with result 'defect'"),
        mk(L, "fsharp_tests_passed_final", doc["arms"][L]["tests"]["fsharpHarness"]["passed"], "tests",
           f"$.arms.{L}.tests.fsharpHarness.passed", "copied", "exact-SHA CI run reported by the evaluator"),
        mk(L, "fsharp_tests_failed_final", doc["arms"][L]["tests"]["fsharpHarness"]["failed"], "tests",
           f"$.arms.{L}.tests.fsharpHarness.failed", "copied"),
    ))
    return out


def tests_added_net_rows(rows: Sequence[Row]) -> tuple[Row, ...]:
    pre = {r.arm: r.value for r in rows if r.study == "A021" and r.metric == "fsharp_tests_preexisting"}
    base = pre.get("grouped") if pre.get("grouped") == pre.get("independent") else None
    return tuple(
        row(study=r.study, arm=r.arm, unit="agg:arm", metric="fsharp_tests_net_added", value=(r.value - base) if base is not None else None,
            unit_of_measure="tests", completeness="complete" if base is not None else "unknown",
            session_role="evaluator", source_path=r.source_path, source_commit=r.source_commit,
            source_locator=r.source_locator + f" minus baseline ({FSHARP_BASELINE_TESTS_SOURCE})",
            extraction_method="computed: final F# tests passed - pre-existing F# tests at the shared baseline",
            notes=f"baseline {base} F# tests at {BASELINE[:7]} (both studies share this baseline); net count, "
                  "so a removed test would offset an added one")
        for r in rows if r.metric == "fsharp_tests_passed_final")


# --------------------------------------------------------------------------
# Derived comparisons
# --------------------------------------------------------------------------

class Pair(NamedTuple):
    study: str
    metric: str
    unit: str
    comparable_to_other_study: bool
    note: str


DERIVED_PAIRS: tuple[Pair, ...] = tuple(
    [Pair("A021", m, "agg:workers", True, "") for m in (
        "cost_usd_platform", "input_tokens_platform", "output_tokens_platform", "cache_read_tokens_platform",
        "cache_write_tokens_platform", "active_session_sum_s")] +
    [Pair("A021", "wall_clock_span_s", "agg:workers", True,
          "wall-clock span includes orchestration gaps and, for the independent arm, a 2 h 7 min permission block")] +
    [Pair("A021", m, "agg:workers", False,
          "transcript script figures; the independent arm misses item-04 attempts 1-2 entirely, so the ratio is not a "
          "bound on the true ratio") for m in (
        "model_requests_transcript", "output_tokens_transcript", "cache_read_tokens_transcript", "file_reads_transcript",
        "searches_transcript", "builds_transcript", "test_runs_transcript", "governance_reads_transcript",
        "agents_md_reads_transcript", "time_to_first_code_s")] +
    [Pair("R2", m, "agg:workers", True, "excludes the independent arm's orchestrator session (definition matches A021)")
     for m in ("cost_usd_platform", "input_tokens_platform", "output_tokens_platform", "cache_read_tokens_platform",
               "cache_write_tokens_platform", "active_session_sum_s", "wall_clock_span_s")] +
    [Pair("R2", m, "agg:workers+orchestration", False,
          "includes the independent arm's orchestrator session (C), which has no counterpart in the grouped arm or in "
          "A021's per-arm figures; this is the definition EV-A070 reports")
     for m in ("cost_usd_platform", "input_tokens_platform", "output_tokens_platform", "cache_read_tokens_platform",
               "cache_write_tokens_platform", "wall_clock_span_s", "wall_clock_span_from_common_start_s")] +
    [Pair("R2", m, "agg:workers", False,
          "transcript script figures captured before each session's final steps; R2 workers' schemas differ and "
          "one script value (control-03 builds) is unrecoverable")
     for m in ("model_requests_transcript", "output_tokens_transcript", "cache_read_tokens_transcript",
               "file_reads_transcript", "searches_transcript", "builds_transcript", "test_runs_transcript",
               "governance_reads_transcript", "agents_md_reads_transcript", "time_to_first_code_s")]
)


def derive(rows: Sequence[Row]) -> tuple[Mapping[str, Any], ...]:
    index = {r.key(): r for r in rows}

    def one(p: Pair) -> Mapping[str, Any]:
        g = index.get((p.study, "grouped", p.unit, p.metric))
        if g is None and p.unit == "agg:workers+orchestration":
            g = index.get((p.study, "grouped", "agg:workers", p.metric)) or index.get(
                (p.study, "grouped", "agg:workers+orchestration", p.metric))
        i = index.get((p.study, "independent", p.unit, p.metric))
        reason = None
        if g is None or i is None or g.value is None or i.value is None:
            reason = "operand missing or unknown"
        elif g.value == 0:
            reason = "grouped value is 0; ratio undefined"
        if reason:
            ratio = reduction = None
        else:
            gv, iv = Decimal(str(g.value)), Decimal(str(i.value))
            ratio = float(round(iv / gv, 6))
            reduction = float(round((iv - gv) / iv, 6))
        comp = ("unknown" if reason else
                "complete" if g.completeness == i.completeness == "complete" else "partial")
        return MappingProxyType({
            "study": p.study, "metric": p.metric, "unit": p.unit,
            "grouped": None if g is None else g.value, "independent": None if i is None else i.value,
            "grouped_completeness": None if g is None else g.completeness,
            "independent_completeness": None if i is None else i.completeness,
            "ratio_independent_over_grouped": ratio,
            "reduction_grouped_vs_independent": reduction,
            "completeness": comp,
            "comparable_to_other_study": p.comparable_to_other_study,
            "null_reason": reason,
            "notes": p.note,
        })
    return tuple(one(p) for p in DERIVED_PAIRS)


# --------------------------------------------------------------------------
# Assembly
# --------------------------------------------------------------------------

def build(inputs: Inputs) -> Mapping[str, Any]:
    blobs = inputs.blobs
    sessions = a021_sessions(blobs) + r2_sessions(blobs)
    per_session = tuple(r for s in sessions for r in session_rows(s) + transcript_rows(s, blobs))
    per_session = per_session + r2_subagent_note_rows(blobs, sessions)
    common = r2_common_start(blobs)
    aggs = tuple(r for study in ("A021", "R2") for arm in ("grouped", "independent")
                 for rule in ((AGG_WORKERS,) if study == "A021" else (AGG_WORKERS, AGG_ALL))
                 for r in aggregate(per_session, sessions, study, arm, rule)
                 + span_rows(sessions, study, arm, rule, common if study == "R2" else None))
    quality = eval1_rows(blobs) + kit_rows(blobs) + r2_quality_rows(blobs)
    rows = (per_session + aggs + count_rows(sessions) + a021_orchestration_placeholders()
            + git_rows(inputs.git) + merge_conflict_rows(blobs, inputs.git) + quality)
    rows = rows + tests_added_net_rows(rows)
    rows = tuple(sorted(rows, key=lambda r: (r.study, r.arm, r.unit, r.metric)))
    dupes = [k for k, n in Counter(r.key() for r in rows).items() if n > 1]
    if dupes:
        raise ValueError(f"duplicate metric keys: {dupes}")
    return MappingProxyType({
        "schema": "a021-publication.metrics/1",
        "conventions": {
            "null": "value unavailable from the committed sources (never 0)",
            "completeness": ["complete", "partial", "lower_bound", "unknown", "not_applicable"],
            "arms": {"grouped": "one session for all five items",
                     "independent": "one fresh session per item (protocol name: control)"},
            "aggregation_rules": {AGG_WORKERS.unit: AGG_WORKERS.description, AGG_ALL.unit: AGG_ALL.description,
                                  "agg:arm": "arm-level figure from Git or an evaluation (no session breakdown)"},
            "time_definitions": {
                "session_span": "one session: platform created_at to its end (updated_at after idle; blockedAt for a blocked session)",
                "active_session_sum": "sum of session spans of the included sessions",
                "wall_clock_span": "first start to last end of the included sessions (includes gaps between sessions)",
                "blocked": "time a session waited on a permission prompt",
                "transcript_span": "first to last transcript entry as measured by session_metrics.py (ends before final steps)",
            },
            "internal_ref": "platform session identifier; internal audit only, strip before anonymous release",
        },
        "sources": {k: {"commit": v.commit, "path": v.path} for k, v in sorted(SOURCES.items())},
        "baseline_commit": BASELINE,
        "rows": [dict(r._asdict()) for r in rows],
        "derived": [dict(d) for d in derive(rows)],
    })


def to_json(doc: Mapping[str, Any]) -> str:
    return json.dumps(doc, indent=2, sort_keys=True, ensure_ascii=False, default=dict) + "\n"


def csv_cell(v: Any) -> str:
    return "null" if v is None else (json.dumps(v) if isinstance(v, bool) else str(v))


def to_csv(doc: Mapping[str, Any]) -> str:
    buf = io.StringIO()
    writer = csv.writer(buf, lineterminator="\n")
    writer.writerow(FIELDS)
    tuple(writer.writerow([csv_cell(r[f]) for f in FIELDS]) for r in doc["rows"])
    return buf.getvalue()


# --------------------------------------------------------------------------
# I/O boundary
# --------------------------------------------------------------------------

def repo_root() -> Path:
    return Path(subprocess.run(["git", "-C", str(Path(__file__).resolve().parent), "rev-parse", "--show-toplevel"], capture_output=True, text=True,
                               check=True).stdout.strip())


def git_out(root: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, check=True).stdout


def parse_shortstat(text: str) -> Mapping[str, int]:
    grab = lambda pat: int(m[1]) if (m := re.search(pat, text)) else 0
    return MappingProxyType({"diff_files_changed": grab(r"(\d+) files? changed"),
                             "diff_insertions": grab(r"(\d+) insertions?"),
                             "diff_deletions": grab(r"(\d+) deletions?")})


def load_inputs(root: Path) -> Inputs:
    blobs = {k: git_out(root, "show", f"{s.commit}:{s.path}") for k, s in SOURCES.items()}
    git = {key: MappingProxyType({
        "commits": int(git_out(root, "rev-list", "--count", f"{BASELINE}..{sha}")),
        "merge_commits": int(git_out(root, "rev-list", "--merges", "--count", f"{BASELINE}..{sha}")),
        **parse_shortstat(git_out(root, "diff", "--shortstat", BASELINE, sha, "--", *DIFF_PATHS)),
    }) for key, sha in GIT_ARMS.items()}
    return Inputs(MappingProxyType(blobs), MappingProxyType(git))


def outputs(root: Path) -> Mapping[str, str]:
    doc = build(load_inputs(root))
    base = "research/publications/a021-grouped-execution/data"
    return MappingProxyType({f"{base}/metrics.json": to_json(doc), f"{base}/metrics.csv": to_csv(doc)})


def main(argv: Sequence[str]) -> int:
    root = repo_root()
    files = outputs(root)
    if "--stdout" in argv:
        sys.stdout.write(next(v for k, v in files.items() if k.endswith(".json")))
        return 0
    tuple((root / p).write_text(text, encoding="utf-8") for p, text in files.items())
    print("\n".join(sorted(files)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
