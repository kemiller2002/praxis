namespace Ros.Integration.Compatibility.Tests

open System.IO
open EchelonFoundry.Ros.Integration

/// Every JSON file under fixtures/v1 is a payload some real or
/// hypothetical V1 producer could have sent. This suite is the
/// mechanical gate the migration spec requires: a future release of
/// `Ros.Integration` must prove every fixture here still deserializes
/// successfully before it is allowed to publish. A fixture is added
/// here and never edited in place -- removing one requires the same
/// approval trail as removing a supported contract version (see
/// docs/migrations/central-integration/INTEGRATION-CONTRACT-STANDARD.md
/// section 4).
[<RequireQualifiedAccess>]
module CompatibilityTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "package.json"))
           && Directory.Exists(Path.Combine(directory.FullName, "tests", "Ros.Integration.Compatibility.Tests", "fixtures", "v1")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let private fixturesV1Directory () =
        let root = repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))
        Path.Combine(root, "tests", "Ros.Integration.Compatibility.Tests", "fixtures", "v1")

    let tests =
        [ { Name = "every fixture under fixtures/v1 still deserializes into a valid ActivityObservation"
            Run =
              fun () ->
                  let fixtures = Directory.GetFiles(fixturesV1Directory(), "*.json")
                  Assert.isTrue (fixtures.Length > 0) "expected at least one fixture under fixtures/v1"

                  for fixture in fixtures do
                      match ActivitySerialization.deserializeActivity (File.ReadAllText fixture) with
                      | Ok _ -> ()
                      | Error error -> failwith $"{Path.GetFileName fixture} failed to deserialize: {error}" }

          { Name = "the legacy '1.0' contractVersion spelling is still accepted, not just '1'"
            Run =
              fun () ->
                  let root = repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))
                  let path = Path.Combine(root, "tests", "Ros.Integration.Compatibility.Tests", "fixtures", "v1", "legacy-contract-version-1.0.json")

                  match ActivitySerialization.deserializeActivity (File.ReadAllText path) with
                  | Ok activity -> Assert.isTrue (ContractVersion.value activity.ContractVersion = "1.0") "expected contractVersion '1.0' to round-trip verbatim"
                  | Error error -> failwith $"expected Ok, got {error}" } ]
