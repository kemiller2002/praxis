# Evidence matrix

Every substantive manuscript claim must map to a committed artifact.

| Claim / observation | Primary evidence | Status / caution |
| --- | --- | --- |
| A021 used a frozen protocol with grouped and independent arms from the same baseline | `research/experiments/EX-ROS-2026-A021--grouped-versus-independent-execution.md` | Direct |
| Common baseline is `8b4ffa392e93b19bf39f6672a608954c934cb815` | A021 protocol; R2 findings | Direct |
| Original grouped run used 1 session; control used 7 | `research/evidence/EV-ROS-2026-A064--grouping-experiment-results.md` | Direct |
| Original platform cost: grouped $9.48 vs control $21.51 | EV-A064, Resources table | Direct; platform estimate |
| Original active session time: grouped 61 min vs control 112 min | EV-A064 | Direct |
| Original control elapsed first-start to last-finish includes orchestration gaps and permission block | EV-A064 | Must not compare this elapsed definition directly with R2 elapsed without qualification |
| Original grouped architecture used one store/join rule/classification/error style/mutation pipeline | EV-A064 blind-evaluator findings | Evaluator judgment backed by implementation evidence |
| Original control architecture used multiple stores/join rules/grammars/classifications/rejection/JSON patterns | EV-A064 | Evaluator judgment backed by implementation evidence |
| Original independent execution was stronger on several individual criteria and added 37 tests vs 19 | EV-A064 | Direct; test count is not test quality |
| Original had no compactions/context resets | EV-A064 | Direct |
| R2 evaluator remained blind to mapping while evaluating | `research/experiments/EX-ROS-2026-A021-R2-blind/output/findings.json` | Direct |
| R2 exact blind SHAs passed available CI | R2 findings JSON | Direct, subject to recorded build-command deviation |
| R2 grouped mapping was Arm N and control Arm M after unblinding | `POST-UNBLINDING-METRICS.txt` | Direct post-unblinding mapping |
| R2 grouped cost $10.7536464 vs control $26.0492768 | POST-UNBLINDING-METRICS | Direct |
| R2 grouped elapsed 55m53s vs control 2h23m35s | POST-UNBLINDING-METRICS | Direct |
| R2 grouped cost reduction 58.72% | POST-UNBLINDING-METRICS | Direct; recompute in analysis script |
| R2 grouped elapsed reduction 61.08% | POST-UNBLINDING-METRICS | Direct; recompute in analysis script |
| R2 output tokens grouped 132,902 vs control 262,893 | POST-UNBLINDING-METRICS | Direct |
| R2 cache-read grouped 27,911,832 vs control 62,960,709 | POST-UNBLINDING-METRICS | Direct |
| R2 had two confirmed acceptance defects in each arm | R2 findings JSON | Direct |
| R2 grouped stronger on shared admission, repository default/enforcement, locked mutation path, one store/envelope | R2 findings + post-unblinding metrics | Direct after mapping |
| R2 independent stronger on checkpoint ownership/attribution validation and some checkpoint detail | R2 findings + post-unblinding metrics | Direct after mapping |
| R2 is an execution replication, not independent-domain replication | EV-A070 / POST-UNBLINDING-METRICS | Required limitation |
| R2 control had stale-checkout deviation | POST-UNBLINDING-METRICS | Required threat |
| Current hypothesis confidence remains low | `research/hypotheses/HY-ROS-2026-A028--grouped-execution-value.md` | Direct |
| A022 is intended to separate affinity from generic setup-cost amortization | HY-A028; EX-A022 | Direct |

## Quantitative table to generate for the paper

Do not hand-copy numbers into the final manuscript. Generate a CSV/JSON table from committed source artifacts and render the paper table from that file.

Minimum columns:

- execution_id
- replication (A021/R2)
- arm (grouped/independent)
- implementing_sessions
- orchestration_sessions
- platform_cost_usd
- active_time_seconds
- elapsed_time_seconds
- output_tokens
- cache_read_tokens
- cache_write_tokens
- model_requests
- file_reads
- searches
- builds
- test_runs
- governance_reads
- agent_instruction_reads
- compactions
- final_test_count
- confirmed_acceptance_defects
- measurement_notes

Unknown values stay null/unknown. Never infer zero.

## Claim discipline

Use three labels internally while drafting:

- **Observed:** directly measured or counted.
- **Evaluated:** independent evaluator judgment grounded in code/tests.
- **Inferred:** author explanation/mechanism.

The paper must not silently turn an inferred mechanism into an observed fact.
