# EX-ROS-2026-A022 target-repository gate artifacts

Selection evidence for `EX-ROS-2026-A022`, summarized in
`EV-ROS-2026-A072`. These files record a gate that **no candidate passed**.
They are not experiment arms, and nothing here was executed against any
candidate repository's work.

| File | What it is |
| --- | --- |
| `inventory.py` | Read-only inventory: runs Praxis `work list --json` on a throwaway copy of each candidate at its frozen SHA |
| `inventory.json` | Its output for the 14 inspected repositories (13 allowlisted plus Signal); `--check` reproduces it |
| `signal/signal_structure.py` | Extracts Signal's explicit relations, scale and edge-free 5-subsets from its item detail records |
| `signal/signal-structure.json` | Its output for `kemiller2002/signal@637218a` |
| `signal/plan-groups.txt`, `.json`, `signal/plan-analyze.json` | Deterministic planner (Praxis 3.6.0) on a copy of Signal, `--as-of 2026-10-02T12:30:00Z` |
| `signal/gh5-state-investigation.txt` | Investigation of Signal's GH-5 `queue.md` / `current.json` difference |

The gate criteria are those in
`research/experiments/EX-ROS-2026-A022--affinity-by-execution-mode.md`,
unchanged.
