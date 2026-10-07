# Known Risks

| Risk | Likelihood | Impact | Mitigation | Owner |
|---|---|---|---|---|
| Prompt or policy drift | Medium | High | Maintain canonical framework files and versions | Unassigned |
| Registry drift | Medium | High | Add automated validation and indexing | Unassigned |
| Generated artifacts treated as canonical | Medium | High | Enforce source references and directory boundaries | Unassigned |
| Accepted artifact mutation is not automatically detected against Git history | Medium | High | Add a trusted-baseline content digest check before broad adoption | Repository governance |
| Provider token/cost semantics differ or change | High | High | Preserve raw snapshots and source; version mappings; compare only compatible normalized scopes | Repository governance |
| Dirty execution baselines misattribute another contributor's changes | Medium | High | Mark execution-delta metrics unavailable; retain only deterministic repository-state facts | Repository governance |
| Runtime hooks or raw exports expose prompts, commands, paths, or credentials | Medium | High | Keep content capture off; redact sensitive keys; bound snapshots; validate and review adapters | Repository governance |
| Per-execution telemetry grows repository history | Medium | Medium | Segment records, cap raw snapshots, measure growth, and defer external storage/retention until scale warrants it | Repository governance |
| Agent-reported R&D, scope, and correction facts are mistaken for mechanical evidence | Medium | High | Preserve source/quality and require factual context; never make tax/legal eligibility claims | Repository governance |
