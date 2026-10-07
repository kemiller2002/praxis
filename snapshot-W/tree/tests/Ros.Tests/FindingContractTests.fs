namespace Ros.Tests

open Ros.Contracts.Cli
open Ros.Domain.Artifacts

/// Typed tests for `Ros.Contracts.Cli.FindingContract.repair`, shared by
/// every command that renders findings with a repair hint. Mirrors
/// production `findingRecord`'s three-branch repair logic; the
/// `field = "work_items"` branch was added alongside the unified
/// `validate` command, since `artifacts validate` alone (this function's
/// only prior caller) never produced a `work_items`-fielded finding.
[<RequireQualifiedAccess>]
module FindingContractTests =
    let private finding path field message : ArtifactFinding = { Path = path; Field = field; Message = message }

    let tests =
        [ { Name = "a stale-registry finding repairs with 'registry build', regardless of field"
            Run =
              fun () ->
                  Assert.equal "Run './ros registry build'." (FindingContract.repair (finding "registries/x.json" "" "registry is stale; run 'ros registry build'")) }

          { Name = "a work_items-fielded finding repairs with production's own work-begin instruction"
            Run =
              fun () ->
                  Assert.equal
                      "Run './ros work begin WORK-ID', perform the change, then complete it with configured evidence."
                      (FindingContract.repair (finding ".git" "work_items" "meaningful change has no active or completed work-item attribution")) }

          { Name = "any other finding repairs with the generic correct-and-revalidate instruction"
            Run =
              fun () -> Assert.equal "Correct the named file and field, then run './ros validate' again." (FindingContract.repair (finding "a.md" "id" "invalid identifier")) } ]
