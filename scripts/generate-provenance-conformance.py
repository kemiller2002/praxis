#!/usr/bin/env python3
"""Regenerates schemas/conformance/provenance-record/ (RQ-ROS-2026-A013, RQ-ROS-2026-A015).

The fixtures are committed; this script is the reviewable source of truth for
them. Run it after changing a case, then run the F# ProvenanceRecordTests and
commit both. Downstream systems vendor the generated files, not this script.
"""
import json, os, copy
root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "schemas", "conformance", "provenance-record")
C = "praxis.provenance-record"
def rec(contribs, subject=None, derived=None, sources=None, version="1.0.0", **extra):
    r = {"contract": C, "version": version}
    if subject: r["subject"] = subject
    r["contributions"] = contribs
    if derived is not None: r["derivedFrom"] = derived
    if sources is not None: r["sources"] = sources
    r.update(extra)
    return r
def agent(i, p, m, rt): return {"kind":"agent","id":i,"provider":p,"model":m,"runtime":rt}
CLAUDE = agent("anthropic/claude-code","anthropic","unknown","claude-code")
CODEX = agent("openai/codex","openai","gpt-5-codex","codex")
GEMINI = agent("google/gemini-cli","google","gemini-2.5-pro","gemini-cli")
GHA = {"kind":"automation","id":"github/github-actions","provider":"github","model":"unknown","runtime":"github-actions"}
VIGILA = {"kind":"automation","id":"echelon/vigila","provider":"echelon","model":"unknown","runtime":"vigila"}
KEVIN = {"kind":"human","id":"kevin"}
def c(ops, at, actor, last=None, reason=None, evidence=None, **extra):
    d = {"operations": ops, "at": at}
    if last: d["last"] = last
    d["actor"] = actor
    if reason: d["reason"] = reason
    if evidence: d["evidence"] = evidence
    d.update(extra)
    return d
EA="EXE-20260926T080000000Z-a1a1a1a1"; EB="EXE-20260926T090000000Z-b2b2b2b2"; EC="EXE-20260926T100000000Z-c3c3c3c3"
EF="EXE-20260926T150000000Z-f6f6f6f6"; EE="EXE-20260926T140000000Z-e5e5e5e5"; ED="EXE-20260926T120000000Z-d4d4d4d4"
cases = []
def write(rel, obj):
    p = os.path.join(root, rel); os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "w") as f: f.write(json.dumps(obj, indent=2, ensure_ascii=False) + "\n")
def case(rel, obj, expect, why):
    write(rel, obj); cases.append({"file": rel, "expect": expect, "why": why})

case("valid/minimal-agent.json", rec({EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE)}, subject="praxis:RQ-APP-2026-A007"), "valid", "one agent creation keyed by its Praxis execution")
case("valid/human-agent-automation.json", rec({
  EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE, reason="Initial capture"),
  EB: c(["modified"],"2026-09-26T09:00:00.000Z",CODEX, reason="Tighten acceptance criteria"),
  "EXE-20260926T093000000Z-99aa99aa": c(["reviewed"],"2026-09-26T09:30:00.000Z",GHA),
  "CTB-20260926-5f2e19aa": c(["approved"],"2026-09-26T10:00:00.000Z",KEVIN)}, subject="praxis:RQ-APP-2026-A007"), "valid", "multiple contributors: two agents, automation, and a human outside any execution")
case("valid/same-agent-two-executions.json", rec({
  EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE),
  EC: c(["modified"],"2026-09-26T10:00:00.000Z",CLAUDE, last="2026-09-26T10:20:00.000Z")}), "valid", "two executions of the same agent stay two entries")
case("valid/unknown-actor-attributes.json", rec({
  EA: c(["created"],"2026-09-26T08:00:00.000Z",agent("unknown","unknown","unknown","unknown")),
  "CTB-20260926-00000000": c(["reviewed"],"2026-09-26T09:00:00.000Z",{"kind":"unknown","id":"unknown","provider":"unknown","model":"unknown","runtime":"unknown"})}), "valid", "unknown is recorded as unknown, never guessed")
case("valid/foreign-execution.json", rec({
  "EXE-aegis.20260926T120000000Z-3f9a1c2e": c(["created"],"2026-09-26T12:00:00.000Z",GEMINI)}, subject="aegis:fault/F-17"), "valid", "a system with no propagated Praxis execution keys its own run as EXE-<system>.<run>")
case("valid/unknown-fields-preserved.json", rec({
  EA: c(["created"],"2026-09-26T08:00:00.000Z",dict(CLAUDE, **{"x-team":"platform"}), attestation={"type":"x-future","value":"opaque"})},
  subject="praxis:RQ-APP-2026-A007", **{"x-origin-system":"praxis","x-future-envelope":{"a":1}}), "valid", "fields this version does not model are legal and must survive")
REQ02 = rec({EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE)}, subject="praxis:RQ-APP-2026-A007")
case("valid/derived-with-sources.json", rec({EC: c(["created"],"2026-09-26T10:00:00.000Z",CLAUDE)}, subject="git:commit/abc123",
  derived=["praxis:RQ-APP-2026-A007","praxis:DF-APP-2026-A001"], sources={"praxis:RQ-APP-2026-A007": REQ02}), "valid", "lineage names sources; the snapshot keeps the source's originator without making it an author")
case("valid/extension-kind-and-operation.json", rec({
  "CTB-20260926-7777aaaa": c(["created"],"2026-09-26T08:00:00.000Z",{"kind":"x-bot","id":"acme/triage-bot","provider":"acme","model":"unknown","runtime":"triage"}),
  "CTB-20260926-8888bbbb": c(["x-resolved"],"2026-09-26T09:00:00.000Z",KEVIN)}), "valid", "namespaced x- kinds and operations are extensions, not errors")
case("valid/minor-version.json", rec({EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE)}, version="1.4.2", **{"x-added-in-1.4":{"note":"future minor field"}}), "valid", "a newer minor of a supported major is read; its new fields are preserved")
case("unversioned/legacy-registry-block.json", {"contributions": {EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE)}}, "valid-unversioned", "an artifact's provenance as projected into a Praxis registry, without the envelope")
case("unsupported/future-major.json", {"contract": C, "version": "2.0.0", "contributions": [{"execution": EA, "actor": {"kind":"agent"}}], "x-v2": True}, "unsupported-version", "a future major is preserved verbatim, never interpreted or extended")
bad = lambda contribs, **kw: rec(contribs, **kw)
case("invalid/wrong-contract.json", dict(rec({EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE)}), contract="acme.provenance"), "invalid", "contract discriminator mismatch")
case("invalid/bad-version.json", rec({EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE)}, version="1.0"), "invalid", "version must be MAJOR.MINOR.PATCH")
case("invalid/agent-missing-model.json", bad({EA: c(["created"],"2026-09-26T08:00:00.000Z",{"kind":"agent","id":"openai/codex","provider":"openai","runtime":"codex"})}), "invalid", "an agent must state provider, model, and runtime (unknown when unknown)")
case("invalid/agent-without-execution.json", bad({"CTB-20260926-12345678": c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE)}), "invalid", "an agent contribution must be keyed by an execution")
case("invalid/two-created.json", bad({EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE), EB: c(["created"],"2026-09-26T09:00:00.000Z",CODEX)}), "invalid", "at most one contribution may claim created")
case("invalid/created-after-modified.json", bad({EA: c(["modified"],"2026-09-26T08:00:00.000Z",CLAUDE), EB: c(["created"],"2026-09-26T09:00:00.000Z",CODEX)}), "invalid", "nothing may precede created")
case("invalid/unknown-kind.json", bad({EA: c(["created"],"2026-09-26T08:00:00.000Z",{"kind":"robot","id":"r2"})}), "invalid", "kind outside the vocabulary")
case("invalid/unknown-operation.json", bad({EA: c(["authored"],"2026-09-26T08:00:00.000Z",CLAUDE)}), "invalid", "operation outside the vocabulary (extensions must be x-...)")
case("invalid/bad-timestamp.json", bad({EA: c(["created"],"2026-09-26 08:00",CLAUDE)}), "invalid", "at must be ISO-8601 UTC")
case("invalid/empty-actor-id.json", bad({EA: c(["created"],"2026-09-26T08:00:00.000Z",agent("","anthropic","unknown","claude-code"))}), "invalid", "empty id; unknown must be spelled unknown")
case("invalid/bad-key.json", bad({"RUN-123": c(["created"],"2026-09-26T08:00:00.000Z",KEVIN)}), "invalid", "keys are EXE- or CTB-")
case("invalid/missing-contributions.json", {"contract": C, "version": "1.0.0", "subject": "x:y"}, "invalid", "contributions is required")
case("invalid/credential-in-actor.json", bad({EA: c(["created"],"2026-09-26T08:00:00.000Z",agent("anthropic/claude-code","anthropic","sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAA","claude-code"))}), "invalid", "identity must never carry a credential")
case("invalid/credential-in-reason.json", bad({EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE, reason="ran with token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")}), "invalid", "a reason must never carry a credential")
case("invalid/source-not-in-lineage.json", rec({EC: c(["created"],"2026-09-26T10:00:00.000Z",CLAUDE)}, derived=["praxis:A"], sources={"praxis:B": REQ02}), "invalid", "a lineage snapshot must be for a declared lineage reference")
case("invalid/self-lineage.json", rec({EC: c(["created"],"2026-09-26T10:00:00.000Z",CLAUDE)}, subject="x:1", derived=["x:1"]), "invalid", "a subject cannot derive from itself")
case("invalid/malformed-source-snapshot.json", rec({EC: c(["created"],"2026-09-26T10:00:00.000Z",CLAUDE)}, derived=["praxis:A"], sources={"praxis:A": {"contract": C, "version": "1.0.0", "contributions": {EA: c(["created"],"2026-09-26T08:00:00.000Z",{"kind":"agent","id":"x"})}}}), "invalid", "a malformed lineage snapshot is reported, not silently dropped")

case("invalid/snapshot-subject-mismatch.json", rec({EC: c(["created"],"2026-09-26T10:00:00.000Z",CLAUDE)}, subject="git:commit/abc123", derived=["praxis:RQ-APP-2026-A007"], sources={"praxis:RQ-APP-2026-A007": rec({EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE)}, subject="praxis:RQ-OTHER-2026-A001")}), "invalid", "a lineage snapshot must be the named source's own provenance, not another subject's")

# successor pairs
succ = []
def pair(name, before, after, expect, why):
    write(f"successor/{name}.before.json", before); write(f"successor/{name}.after.json", after)
    succ.append({"before": f"successor/{name}.before.json", "after": f"successor/{name}.after.json", "expect": expect, "why": why})
B1 = rec({EA: c(["created"],"2026-09-26T08:00:00.000Z",dict(CLAUDE, **{"x-team":"platform"}), attestation={"type":"x-future"})}, subject="praxis:RQ-APP-2026-A007", derived=["praxis:EV-1"], sources={"praxis:EV-1": rec({"CTB-20260925-11112222": c(["created"],"2026-09-25T08:00:00.000Z",KEVIN)})}, **{"x-top":"keep"})
A = copy.deepcopy(B1); A["contributions"][EB] = c(["modified"],"2026-09-26T09:00:00.000Z",CODEX)
pair("append-contribution", B1, A, "preserved", "another agent's contribution is appended; everything else is untouched")
A = copy.deepcopy(B1); A["contributions"][EA]["operations"] = ["created","modified"]; A["contributions"][EA]["last"]="2026-09-26T08:30:00.000Z"
pair("merge-own-entry", B1, A, "preserved", "the same execution extends its own entry")
A = copy.deepcopy(B1); A["derivedFrom"].append("praxis:EV-2")
pair("add-lineage", B1, A, "preserved", "lineage may grow")
A = copy.deepcopy(B1); del A["contributions"][EA]; A["contributions"][EB] = c(["created"],"2026-09-26T09:00:00.000Z",CODEX)
pair("replaced-history", B1, A, "destructive", "contribution history replaced and originator changed")
A = copy.deepcopy(B1); A["contributions"][EA]["actor"] = CODEX
pair("overwritten-actor", B1, A, "destructive", "the original actor was overwritten by a later one")
A = copy.deepcopy(B1); e = A["contributions"].pop(EA); A["contributions"]["CTB-20260926-a1a1a1a1"] = dict(e, actor=KEVIN)
pair("lost-execution", B1, A, "destructive", "execution identity lost when the entry was re-keyed")
A = copy.deepcopy(B1); del A["contributions"][EA]["attestation"]; del A["contributions"][EA]["actor"]["x-team"]
pair("dropped-unknown-fields", B1, A, "destructive", "fields the consumer did not model were dropped")
A = copy.deepcopy(B1); del A["x-top"]
pair("dropped-top-level-field", B1, A, "destructive", "an unknown top-level field was dropped")
A = copy.deepcopy(B1); A["derivedFrom"] = []; del A["sources"]
pair("dropped-lineage", B1, A, "destructive", "lineage and its snapshot were stripped")
A = copy.deepcopy(B1); A["sources"]["praxis:EV-1"]["contributions"]["CTB-20260925-11112222"]["actor"] = CLAUDE
pair("rewritten-snapshot", B1, A, "destructive", "a lineage snapshot was rewritten instead of carried verbatim")
A = copy.deepcopy(B1); A["contributions"][EA]["operations"] = ["modified"]
pair("removed-operation", B1, A, "destructive", "an operation (the creation) was removed")
V2 = {"contract": C, "version": "2.0.0", "contributions": [{"execution": EA}], "x-v2": True}
pair("unsupported-carried-verbatim", V2, copy.deepcopy(V2), "preserved", "an unsupported major carried verbatim")
A = copy.deepcopy(V2); A["contributions"].append({"execution": EB})
pair("unsupported-modified", V2, A, "destructive", "an unsupported major must not be modified")
A = copy.deepcopy(B1); A["version"] = "2.0.0"
pair("major-changed-in-place", B1, A, "destructive", "a record's major version never changes in place")
B3 = copy.deepcopy(B1); B3["version"] = "1.4.0"
A = copy.deepcopy(B3); A["version"] = "1.0.0"
pair("version-lowered", B3, A, "destructive", "a newer minor record must not be relabelled as an older version")
A = copy.deepcopy(B1); A["contributions"][EA]["reason"] = "rewritten"
B2 = copy.deepcopy(B1); B2["contributions"][EA]["reason"] = "original"
pair("rewritten-reason", B2, A, "destructive", "another contributor's reason was rewritten")

# e2e chain
e2e = []
def step(name, obj, prev=None, why=""):
    write(f"e2e/{name}.json", obj); e2e.append({"file": f"e2e/{name}.json", "successorOf": (f"e2e/{prev}.json" if prev else None), "why": why})
R1 = rec({EA: c(["created"],"2026-09-26T08:00:00.000Z",CLAUDE, reason="Captured from customer interview")}, subject="praxis:RQ-APP-2026-A007")
step("01-requirement-created", R1, None, "Agent A (Claude Code) creates the requirement in execution A")
R2 = copy.deepcopy(R1); R2["contributions"][EB] = c(["modified"],"2026-09-26T09:00:00.000Z",CODEX, reason="Tighten acceptance criteria")
step("02-requirement-modified", R2, "01-requirement-created", "Agent B (Codex) modifies it in execution B; A stays the originator")
I3 = rec({EC: c(["created"],"2026-09-26T10:00:00.000Z",CLAUDE, reason="Implement RQ-APP-2026-A007", evidence=["git:commit/abc123"])}, subject="git:commit/abc123", derived=["praxis:RQ-APP-2026-A007"], sources={"praxis:RQ-APP-2026-A007": R2})
step("03-implementation", I3, None, "An implementation execution C (same agent as A, different run) produces the change")
M4 = rec({"EXE-dokimos.gh-run-9001": c(["created"],"2026-09-26T11:00:00.000Z",GHA, reason="Measured by CI; measurement actor is not the code author")}, subject="dokimos:snapshot/app:abc123", derived=["git:commit/abc123"], sources={"git:commit/abc123": I3})
step("04-dokimos-measurement", M4, None, "Dokimos analyzes the change; the measuring automation is recorded, the code author only as lineage")
F5 = rec({ED: c(["created"],"2026-09-26T12:00:00.000Z",GEMINI, reason="Security review", evidence=["dokimos:snapshot/app:abc123"])}, subject="aegis:fault/F-17", derived=["git:commit/abc123","dokimos:snapshot/app:abc123"], sources={"git:commit/abc123": I3, "dokimos:snapshot/app:abc123": M4})
step("05-aegis-finding", F5, None, "Aegis records a finding discovered by agent D (Gemini) in execution D")
V6 = rec({"EXE-vigila.20260926T130000000Z-0c0c0c0c": c(["created"],"2026-09-26T13:00:00.000Z",VIGILA, reason="Follow-up generated from aegis:fault/F-17")}, subject="vigila:item/ITEM-42", derived=["aegis:fault/F-17"], sources={"aegis:fault/F-17": F5})
step("06-vigila-followup", V6, None, "Vigila (a system, recorded as automation) generates the follow-up; the discovering agent stays in the snapshot")
V7 = copy.deepcopy(V6); V7["contributions"][EE] = c(["x-handled"],"2026-09-26T14:00:00.000Z",CODEX, reason="Picked up the follow-up")
step("07-followup-handled", V7, "06-vigila-followup", "Agent B (Codex) handles it in execution E")
V8 = copy.deepcopy(V7); V8["contributions"][EF] = c(["x-resolved"],"2026-09-26T15:00:00.000Z",CLAUDE, reason="Fixed in def456", evidence=["git:commit/def456"])
step("08-followup-resolved", V8, "07-followup-handled", "Another execution F (Claude Code) resolves it")
V9 = copy.deepcopy(V8); V9["contributions"]["CTB-20260926-5f2e19aa"] = c(["x-validated","approved"],"2026-09-26T16:00:00.000Z",KEVIN, reason="Verified the fix")
step("09-followup-validated", V9, "08-followup-resolved", "A human validates the resolution outside any execution")
F10 = copy.deepcopy(F5); F10["contributions"][EF] = c(["x-remediated"],"2026-09-26T15:00:00.000Z",CLAUDE, evidence=["git:commit/def456"]); F10["contributions"]["EXE-20260926T153000000Z-0a0a0a0a"] = c(["x-validated"],"2026-09-26T15:30:00.000Z",GHA, reason="Regression test passed")
step("10-aegis-finding-remediated", F10, "05-aegis-finding", "The finding records its remediation actor (F) and validation actor (CI) without displacing the discoverer (D)")
expected = {"final": "e2e/09-followup-validated.json", "originatorOfFinal": {"key": "EXE-vigila.20260926T130000000Z-0c0c0c0c", "actor": "echelon/vigila"},
  "chain": [
    {"subject":"vigila:item/ITEM-42","key":"EXE-vigila.20260926T130000000Z-0c0c0c0c","actor":"echelon/vigila","operations":["created"]},
    {"subject":"vigila:item/ITEM-42","key":EE,"actor":"openai/codex","operations":["x-handled"]},
    {"subject":"vigila:item/ITEM-42","key":EF,"actor":"anthropic/claude-code","operations":["x-resolved"]},
    {"subject":"vigila:item/ITEM-42","key":"CTB-20260926-5f2e19aa","actor":"kevin","operations":["x-validated","approved"]},
    {"subject":"aegis:fault/F-17","key":ED,"actor":"google/gemini-cli","operations":["created"]},
    {"subject":"git:commit/abc123","key":EC,"actor":"anthropic/claude-code","operations":["created"]},
    {"subject":"praxis:RQ-APP-2026-A007","key":EA,"actor":"anthropic/claude-code","operations":["created"]},
    {"subject":"praxis:RQ-APP-2026-A007","key":EB,"actor":"openai/codex","operations":["modified"]},
    {"subject":"dokimos:snapshot/app:abc123","key":"EXE-dokimos.gh-run-9001","actor":"github/github-actions","operations":["created"]},
    {"subject":"git:commit/abc123","key":EC,"actor":"anthropic/claude-code","operations":["created"]},
    {"subject":"praxis:RQ-APP-2026-A007","key":EA,"actor":"anthropic/claude-code","operations":["created"]},
    {"subject":"praxis:RQ-APP-2026-A007","key":EB,"actor":"openai/codex","operations":["modified"]}]}
manifest = {"contract": C, "version": "1.0.0",
  "description": "Conformance fixtures for the Praxis provenance interchange record (RQ-ROS-2026-A013, RQ-ROS-2026-A015). Any Echelon system that reads or writes the record should run its codec against these cases; vendored copies record the Praxis commit they came from.",
  "cases": cases, "successors": succ, "e2e": {"steps": e2e, "expected": expected}}
write("manifest.json", manifest)
print(len(cases), len(succ), len(e2e))
