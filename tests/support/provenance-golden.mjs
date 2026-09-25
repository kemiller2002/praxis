import fs from "node:fs";
import path from "node:path";

// The golden masters in the *-fsharp-differential tests were captured from
// the retired Node CLI, which predates Praxis agent provenance
// (DF-ROS-2026-A036). The F# CLI now additionally stamps a `praxis.actor/1`
// `actor` on work events and execution records and appends
// `praxis.provenance/1` contributions to backlog items. Golden parity is
// asserted on everything else by removing exactly that extension -- an
// `actor` object carrying `kind`, and a `provenance` object carrying
// `contributions` -- never the telemetry collector's own `provenance`
// block or the legacy string `actor` on the work context. Provenance itself
// is asserted by tests/provenance-cli.test.mjs.
export function withoutProvenance(value) {
  if (Array.isArray(value)) return value.map(withoutProvenance);
  if (value === null || typeof value !== "object") return value;
  return Object.fromEntries(
    Object.entries(value)
      .filter(([key, child]) => !(
        (key === "actor" && child !== null && typeof child === "object" && "kind" in child) ||
        (key === "provenance" && child !== null && typeof child === "object" && "contributions" in child)
      ))
      .map(([key, child]) => [key, withoutProvenance(child)])
  );
}


// A greenfield repository enforces provenance for every new record. Golden
// masters that hand-write fixture records encode the pre-provenance
// validation semantics, so their fixtures run in legacy mode (no
// `provenance` policy), which is also how an existing repository behaves
// until it adopts the policy.
export function useLegacyProvenancePolicy(root) {
  const configPath = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
  delete config.provenance;
  fs.writeFileSync(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
}
