# Echelon toolchain manifest

A repository may pin its engineering infrastructure in .echelon/toolchain.json.

Example:

    {
      "schemaVersion": 1,
      "ordo": "1.4.0",
      "praxis": "3.4.0"
    }

Running echelon setup from that repository installs those exact stable releases. If the manifest is absent, echelon setup installs the latest stable Ordo and Praxis releases.

## Who writes the Praxis pin

`praxis init` and `praxis upgrade` own the `praxis` property. They set it to the release performing the init or upgrade, which is the same version they record as `installedVersion` in `.echelon/ros.json`; the scaffold template (`templates/echelon-toolchain.json`) renders it from that version and never carries a literal one (`WI-0070`).

- With no manifest, they create one containing `schemaVersion` and `praxis`.
- With an existing manifest, they change only `praxis`. Every other property, including `ordo` and fields from future Echelon capabilities, keeps its value and position. A manifest already pinned to the running release is left byte for byte.
- A manifest that is not a plain JSON object (malformed, or carrying comments) is preserved untouched, like any other shared file.
- `praxis upgrade --dry-run` lists the re-pin as an `update-managed-file` change before anything is written.

### The Praxis source repository (PRX-QUAL-010)

In the Praxis source repository the pin is not tied to `release.json`.
Source may advance `release.json` to a release that is not published yet
(`scripts/praxis-release-bump.sh` changes only `release.json`). The pin names
a *published* release, and advances only through
`scripts/praxis-remote-enable.sh --version X.Y.Z --skip-release`, which first
verifies the release's checksum and build-provenance attestation and then
records the release's declared state compatibility (`release.json`
`compatibility` at its tag) in `quality/release-compatibility.json`.
Compatibility is the authority: a premerge fence requires that the pinned
release's recorded `reads` cover every state-schema version the committed
state declares, or that the gap is an owned, unexpired exception in that
file. A remote executor refuses a repository whose state it does not read
with `incompatible-state`, before any mutation.

Praxis does not write an `ordo` pin, because it cannot know which Ordo release a repository uses. Releases up to 3.7.0 seeded `"ordo": "1.4.0"` and `"praxis": "3.4.0"` literally; an existing `ordo` pin is kept as it is.

The manifest is intentionally small. It records toolchain requirements, not machine state. It contains no credentials, local paths, timestamps, or host identifiers and is safe to commit.

Future Echelon capabilities may add optional fields. Version 1 consumers must ignore fields they do not understand.

Browser-facing packages such as Limen and Forma may continue to use npm because their consumers are JavaScript/browser build systems. The native toolchain manifest is for engineering infrastructure executed as machine tooling.
