# EX-ROS-2026-A024 run deviations and orchestration events

Append-only log kept by the orchestrator. Nothing here changes the frozen
treatments, thresholds, blinding or stop rules.

## D1 — stale platform checkout for item-02 sessions (orchestration-only failure)

- `arm-3-02` (session_01L3387JwywHTVoiRoDNM7Yq, created 17:19:53Z, about 1.5
  minutes after the arm-3 head `c85eb3e` was pushed) and `arm-2-02`
  (session_01ERXqXbLeUNsj9e7wLTr1iM, created 17:23:39Z, about 1.5 minutes after
  `3e535f3` was pushed) were checked out by the platform at `ddda837`, the
  branch heads' initial value, not the recorded heads.
- The frozen start check in each prompt worked: both sessions stopped and
  changed nothing (remote heads unchanged, verified with `git ls-remote`).
- Classification: orchestration-only failure (platform checkout, before any
  repository change). Both attempts are preserved in `sessions.json`.
- Diagnosis: two checkout probes at 18:06Z
  (session_01MVRn1vTzapPSsrmoQ6jYfH by branch name and
  session_01Job2qpbHJC8PMCFjZNi2LJ by commit SHA) both checked out `c85eb3e`,
  so the stale checkout was a transient lag between a push and the platform's
  source resolution.
- Action under the frozen retry policy: one retry each, with a new telemetry
  label (`arm-3-02r`, `arm-2-02r`); the prompt is otherwise byte-identical to
  the frozen prompt with the same START_SHA. The labels are the only other
  launch-time substitution, as the retry policy requires.
- Launch-procedure change for later sessions (orchestration, not treatment):
  before each B/C launch the orchestrator confirms that a checkout of the
  branch name resolves to the recorded head (a checkout probe, or a wait of
  at least 10 minutes after the predecessor's final push). It applies to both
  arms equally.
