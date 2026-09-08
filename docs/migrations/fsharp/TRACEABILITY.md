# ROS F# migration traceability

This table links current behavior to its authority, planned/new typed owner, and
verification. A planned owner is not authoritative until its slice and switch
decision are accepted.

| Responsibility | Current requirement/authority | SDE constraint | F# authority | Verification/status |
|---|---|---|---|---|
| Canonical Markdown and generated registries | `DF-ROS-2026-A001`, A002; Node/Python behavior | boundary preservation; explicit semantic authority | `Ros.Domain.Artifacts`, `Ros.Application.Artifacts`, `Ros.Contracts.Artifacts` | MIG-03/04 complete; Node/F# byte differential and repeatability pass; Node remains authority |
| Artifact ID/kind/status/confidence/reference validation | schemas, `tools/ros_cli.mjs`, `tools/ros_cli.py` | pure decision; explicit codecs; negative proof | `Ros.Domain.Artifacts` + `Ros.Infrastructure.Artifacts.FrontMatter` | valid/invalid frozen fixtures, malformed front matter, reference/status/ID rejection, and current-repo smoke pass |
| Backlog triage and live-state authority | `DF-ROS-2026-A006`, A008 | legal transitions, guards, evidence, retry | future `Ros.Domain.Work` | deferred behind MIG-05/06 |
| Git status/provenance observation | A006; work/telemetry kernels; porcelain-v1 behavior | effects isolated; unavailable distinct from clean; explicit boundary contract | `Ros.Domain.Git`, `Ros.Application.Git`, `Ros.Infrastructure.Git`, `Ros.Contracts.Git` | MIG-06 shadow sub-slice complete; clean/changed, rename/copy, malformed output, missing tool, non-repository, and Node porcelain differential pass; Node callers retained |
| Completion evidence and Git attribution | A006; `ros.json`; work kernel | effects isolated; unavailable distinct from clean | future Work handler consuming typed Git observation | deferred until production consumer migration |
| Execution lifecycle and telemetry | `DF-ROS-2026-A010`; metric catalog | stable closed semantics + open extensions/provenance | future `Ros.Domain.Execution/Metrics` | deferred; Node remains authority |
| Provider telemetry mapping | A010 and provider adapters | open boundary, raw preservation | infrastructure adapters emitting typed observation batches | retained Node edge initially |
| Installation/profile composition | `DF-ROS-2026-A003`; profile manifests | platform adapter versus semantic initialization | future initialization/upgrade handler; npm adapter retained | deferred distribution experiment |
| Hub aggregation | `DF-ROS-2026-A009` | separate semantic area and authority | no `Ros.Domain` owner yet | deferred; spoke contract tests retained |
| GitHub validation/release | workflow and package contract | platform declarations stay at edge | future validation/release-plan commands only | deferred hosted evidence |
| Project Administration -> Time Entry | A006/A007/A009 boundary | do not invent/couple absent domain | explicit future integrations only | blocked pending authoritative requirements |

## First-slice acceptance linkage

| Acceptance | Requirement source | Planned proof |
|---|---|---|
| Node remains production authority | `DF-ROS-2026-A027` | unchanged `ros`/starter launchers and package manifest diff |
| Canonical inputs never modified | A001 | before/after hashes in isolated fixture differential case |
| Registries deterministic and equivalent | A001/A002/current behavior | Node/Python/F# fixture bytes, F# repeat build, and current-repository smoke |
| Invalid boundary data rejected explicitly | A002/SDE verification method | positive/negative frozen fixture; malformed front matter; status/reference/ID cases |
| Domain depends inward only | SDE-DOCTRINE-003/008 | project-reference architecture check plus intentional rejection |
| Public encoding deliberate | SDE-DOCTRINE-004 | golden JSON bytes and explicit writer tests |
| Partial effect is not hidden | SDE-DOCTRINE-004 | injected second-write failure yields typed `Indeterminate` incomplete outcome |
| Git unavailability is not clean | SDE boundary/verification method; work and telemetry manifests | non-repository and missing-executable tests return `Unavailable`; CLI exits 1; malformed rename record is rejected |
| Rename/copy provenance is retained | characterized porcelain-v1 `-z` contract | parser unit test retains both paths; real-repository differential matches Node/Git bytes semantically |
| Rollback remains available | migration non-goal/safety | Node gates remain; no authority switch or legacy deletion |

The final migration evidence record `EV-ROS-2026-A028` records the exact
commands, results, and remaining authority boundary.
