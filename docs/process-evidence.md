# Process evidence (`work evidence`)

Records how work was carried out, as it happens, in the same append-only log
as every other work event (`.ros/events/events.jsonl`). It exists so that
acceptance obligations about the engineering process (who was assigned, which
decisions were made and by whom, which candidate was selected, what an
independent reviewer found) have machine-readable evidence produced during the
run, not reconstructed after it. Work item: WI-0103 (Echelon development speed
optimization, Priority One).

There is no second evidence store. Execution start, the acting identity and
checkpoints are already recorded by `work start`/`work begin` (`work.started`,
the execution envelope under `.ros/executions/`) and `work checkpoint`
(`work.checkpointed`). `work evidence` records only what those do not.

```bash
./praxis work evidence --id WI-0007 --kind release-selection \
  --summary "Candidate selected after the full acceptance run" \
  --candidate 1b9fb56873fe7a98dffd5172066d909449c17eae \
  --reference evidence/acceptance/run-40620099844-1b9fb56/verdict.json \
  --occurred-at 2026-10-28T02:41:07Z
```

## Kinds

| `--kind` | Required | `--outcome` |
|---|---|---|
| `assignment` | `--subject` (owned paths, requirements) | none |
| `dependency-decision` | `--subject` (the dependency) | none |
| `scope-change` | `--subject` (what changed); `--decision` names the approving decision | none |
| `owner-approval` | `--outcome`; `--decision` (the decision id) and `--subject` (what it covers); `--attestation TYPE:REF` when one exists | `approved`, `rejected` |
| `independent-review` | `--candidate`, `--outcome`; `--attestation TYPE:REF` (the reviewer's pull-request review) | `approved`, `changes-requested`, `rejected` |
| `release-selection` | `--candidate` | none |
| `test-result` | `--candidate`, `--outcome` | `pass`, `fail`, `not-run` |
| `acceptance-reference` | `--candidate` | none |
| `final-disposition` | `--candidate`, `--outcome`; `--attestation TYPE:REF` | `accepted`, `not-accepted`, `incomplete`, `abandoned` |

`--candidate` is always a full 40-character commit SHA. `--attestation` applies to
`owner-approval`, `independent-review` and `final-disposition`, the statements a
verifier anchors outside the log; types are `github-review`, `signed-commit` and
`signed-tag`.

## What is asserted, what is observed

Each event keeps three parts apart:

- `statement`: exactly what the caller asserted (summary, subjects, candidate,
  references, decision, outcome, attestation).
- `observed`: what Praxis itself saw when it wrote the record: `HEAD` and the
  branch, the active execution of this actor for the work item and the role in
  its envelope, and how the identity was established (`mechanism`,
  `actorKindSource`, `assurance`).
- `provenance.class`: which of the two a verifier may rely on.

| Class | When Praxis assigns it |
|---|---|
| `tool-observed` | The actor is CI automation discovered from its environment (`whitelisted-github-actions-environment`) and nothing declared the actor kind. The run it names is recorded as `observed.identity.runId`. |
| `agent-assertion` | Everything an agent states, including an owner decision an agent transcribes (`authenticity: transcribed`), and anything whose identity was declared rather than discovered. |
| `reviewer-finding` | An `independent-review`. Whether the reviewer is independent of the implementer is for a verifier to establish from the executions and the log. |
| `owner-decision` | An `owner-approval` or `final-disposition` recorded by a human actor. `authenticity` is `unattested` unless an attestation is referenced, and Praxis never checks the attestation itself. |
| `verified-acceptance` | Never written by Praxis. Only a verifier computes it. |

Writing a record never upgrades it. An agent's statement is an agent assertion
whatever it says. Every Praxis identity is **self-reported**
(`observed.identity.assurance` is always `self-reported`, as `provenance
identity` says): `--actor-kind human`, `ROS_ACTOR_KIND=human` and a locally set
`GITHUB_ACTIONS=true` are all declarations a process makes about itself. The
class says what the record claims to be; it is not proof. A verifier must
therefore:

- accept an owner decision only through an external attestation it checks
  against its frozen rules (for example a pull-request review by the owner's
  login, fetched by CI in the run it trusts) and that names the decision;
- accept a CI record only when `observed.identity.runId` is the run it trusts,
  ideally a record written by that run itself;
- decide reviewer independence from its own frozen roster and the log, never
  from the role a process chose for itself.

An execution belonging to another actor is refused (`--execution` cannot borrow
it), exactly as for checkpoints. With no active execution of its own the record
is written without one, and says so.

## Consumers

Indy-init's acceptance tooling (`requirements-compiler verify-process`) reads
this log, the execution envelopes and the checkpoints, and decides which
process obligations are verified for a candidate. Praxis only records.
