# Echelon toolchain manifest

A repository may pin its engineering infrastructure in .echelon/toolchain.json.

Example:

    {
      "schemaVersion": 1,
      "ordo": "1.4.0",
      "praxis": "3.2.0"
    }

Running echelon setup from that repository installs those exact stable releases. If the manifest is absent, echelon setup installs the latest stable Ordo and Praxis releases.

The manifest is intentionally small. It records toolchain requirements, not machine state. It contains no credentials, local paths, timestamps, or host identifiers and is safe to commit.

Future Echelon capabilities may add optional fields. Version 1 consumers must ignore fields they do not understand.

Browser-facing packages such as Limen and Forma may continue to use npm because their consumers are JavaScript/browser build systems. The native toolchain manifest is for engineering infrastructure executed as machine tooling.
