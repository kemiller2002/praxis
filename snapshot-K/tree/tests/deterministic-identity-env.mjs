// Identity discovery (Ros.Domain.Telemetry.Identity.discover and
// Ros.Domain.Provenance.ActorResolution) whitelists whichever agent or CI
// environment the caller runs in (CLAUDE_CODE_SESSION_ID, GITHUB_ACTIONS,
// ROS_ACTOR, ...). Golden masters that include an execution identity or a
// recorded actor must therefore run the CLI with every such variable
// cleared, so the captured actor is the same on a contributor's machine,
// inside an agent sandbox, and on a real CI runner.
export const IDENTITY_ENV_KEYS = [
  "CLAUDE_CODE_SESSION_ID", "CODEX_SESSION_ID", "CODEX_THREAD_ID",
  "GEMINI_SESSION_ID", "COPILOT_SESSION_ID", "GITHUB_ACTIONS", "GITHUB_RUN_ID",
  "OLLAMA_HOST", "ROS_ACTOR", "ROS_ACTOR_KIND",
  "ROS_TELEMETRY_PROVIDER", "ROS_TELEMETRY_RUNTIME", "ROS_TELEMETRY_MODEL",
  "ROS_TELEMETRY_MODEL_VERSION", "ROS_TELEMETRY_RUNTIME_VERSION",
  "ROS_TELEMETRY_SESSION_ID", "ROS_TELEMETRY_CONVERSATION_ID", "ROS_TELEMETRY_RUN_ID"
];

export function deterministicIdentityEnv(overrides = {}) {
  const env = { ...process.env };
  for (const key of IDENTITY_ENV_KEYS) delete env[key];
  return { ...env, ...overrides };
}

// The actor every CLI invocation resolves to under that environment when
// no identity flag is passed: nothing is known, and nothing is guessed.
export const UNKNOWN_ACTOR = { kind: "unknown", id: "unknown", provider: "unknown", model: "unknown", runtime: "unknown" };

export function unknownActorWithId(id) {
  return { ...UNKNOWN_ACTOR, id };
}
