# State-Directed Engineering

This project uses State-Directed Engineering (SDE).

Before engineering a change:

1. Read `method/CONSTRUCTION-METHOD.md`.
2. Classify the requested work using `method/CHANGE-CLASSIFICATION.md`.
3. Follow `method/AGENT-EXECUTION-RULES.md`.
4. Apply the architecture documents in `architecture/` relevant to the change.
5. Follow `method/VERIFICATION-METHOD.md` before declaring completion.

Templates for recording work are in `templates/`. Term definitions and
engineering metrics are in `reference/`.

Files under `.sde/` are versioned methodology inputs. Do not modify them
directly. If this project needs to deviate from SDE, record that decision
outside this directory rather than editing these files.

Installed SDE version: see `VERSION`.
Installation provenance and file hashes: see `MANIFEST.json`.

Check installation status at any time: `npx @echelon-foundry/sde status`.
