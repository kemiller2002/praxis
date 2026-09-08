# ROS F# migration traceability

This table links current behavior to its authority, planned/new typed owner, and
verification. A planned owner is not authoritative until its slice and switch
decision are accepted.

| Responsibility | Current requirement/authority | SDE constraint | F# authority | Verification/status |
|---|---|---|---|---|
| Canonical Markdown and generated registries | `DF-ROS-2026-A001`, A002; Node/Python behavior | boundary preservation; explicit semantic authority | `Ros.Domain.Artifacts`, `Ros.Application.Artifacts` | MIG-03/04 planned; old/new byte differential required |
| Artifact ID/kind/status/confidence/reference validation | schemas, `tools/ros_cli.mjs`, `tools/ros_cli.py` | pure decision; explicit codecs; negative proof | `Ros.Domain.Artifacts` + front-matter contract mapper | fixtures and malformed/reference/adversarial cases planned |
| Backlog triage and live-state authority | `DF-ROS-2026-A006`, A008 | legal transitions, guards, evidence, retry | future `Ros.Domain.Work` | deferred behind MIG-05/06 |
| Completion evidence and Git attribution | A006; `ros.json`; work kernel | effects isolated; unavailable distinct from clean | future Work/Application and typed Git port | deferred |
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
| Canonical inputs never modified | A001 | before/after hashes in isolated differential cases |
| Registries deterministic and equivalent | A001/A002/current behavior | byte comparison across Node/Python/F# and repeated build |
| Invalid boundary data rejected explicitly | A002/SDE verification method | positive and negative malformed ID/status/reference/front-matter cases |
| Domain depends inward only | SDE-DOCTRINE-003/008 | project-reference architecture check plus intentional rejection |
| Public encoding deliberate | SDE-DOCTRINE-004 | golden JSON bytes and explicit writer tests |
| Rollback remains available | migration non-goal/safety | execute Node gates after F# additions; no authority switch |

Traceability will be updated with exact symbols and evidence ID A028 after the
slice lands.
