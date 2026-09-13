namespace Ros.Integration.Tests

open System
open System.IO
open EchelonFoundry.Ros.Integration

[<RequireQualifiedAccess>]
module SerializationTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "package.json"))
           && Directory.Exists(Path.Combine(directory.FullName, "tests", "contracts")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let private goldenFixturePath () =
        let root = repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))
        Path.Combine(root, "tests", "contracts", "activity-observation-v1.json")

    let private validInput: ActivityObservationInput =
        { ContractVersion = "1"
          ActivityId = "ACT-0001"
          OrganizationId = "ORG-ECHELON"
          ProjectId = "PROJ-ROS"
          RepositoryId = Some "REPO-ROS-CORE"
          WorkItemId = Some "WI-0100"
          ActorId = Some "octocat"
          StartedAt = Some(DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero))
          EndedAt = Some(DateTimeOffset(2026, 1, 1, 10, 30, 0, TimeSpan.Zero))
          Description = Some "Implemented the WI-2 governance note and the Ros.Integration package skeleton."
          Evidence =
            [ { Kind = "commit"; Reference = "abc123" }
              { Kind = "pull-request"
                Reference = "https://github.com/kemiller2002/repository-operating-system/pull/51" } ] }

    let private validActivity =
        match ActivityObservation.create validInput with
        | Ok activity -> activity
        | Error errors -> failwith $"fixture input is expected to be valid, got {errors}"

    let tests =
        [ { Name = "serializeActivity produces exactly the golden V1 fixture (object to JSON)"
            Run =
              fun () ->
                  let expected = (File.ReadAllText(goldenFixturePath())).TrimEnd('\n')
                  let actual = ActivitySerialization.serializeActivity validActivity
                  Assert.equal expected actual }

          { Name = "deserializeActivity on the golden V1 fixture reproduces the same observation (JSON to object)"
            Run =
              fun () ->
                  let json = File.ReadAllText(goldenFixturePath())

                  match ActivitySerialization.deserializeActivity json with
                  | Ok activity -> Assert.equal validActivity activity
                  | Error error -> failwith $"expected Ok, got {error}" }

          { Name = "the V1 fixture is also the current package's own contract version (old V1 JSON to current package)"
            Run =
              fun () ->
                  let json = File.ReadAllText(goldenFixturePath())

                  match ActivitySerialization.deserializeActivity json with
                  | Ok activity -> Assert.equal "1" (ContractVersion.value activity.ContractVersion)
                  | Error error -> failwith $"expected Ok, got {error}" }

          { Name = "an unrecognized field in the payload is ignored, not rejected (unknown-fields policy)"
            Run =
              fun () ->
                  let json =
                      """{"contractVersion":"1","activityId":"ACT-0001","organizationId":"ORG-ECHELON","projectId":"PROJ-ROS","somethingFromTheFuture":{"nested":true},"evidence":[]}"""

                  match ActivitySerialization.deserializeActivity json with
                  | Ok activity -> Assert.equal "ACT-0001" (ActivityId.value activity.ActivityId)
                  | Error error -> failwith $"expected Ok, got {error}" }

          { Name = "a payload missing a required field is rejected, not defaulted (missing-required-fields rejection)"
            Run =
              fun () ->
                  let json = """{"contractVersion":"1","organizationId":"ORG-ECHELON","projectId":"PROJ-ROS","evidence":[]}"""

                  match ActivitySerialization.deserializeActivity json with
                  | Error(InvalidActivity errors) -> Assert.isTrue (List.contains MissingActivityId errors) "expected MissingActivityId"
                  | other -> failwith $"expected Error(InvalidActivity ...), got {other}" }

          { Name = "an unparseable document is rejected as malformed, not as a missing-field error"
            Run =
              fun () ->
                  match ActivitySerialization.deserializeActivity "{ not json" with
                  | Error(MalformedJson _) -> ()
                  | other -> failwith $"expected Error(MalformedJson ...), got {other}" }

          { Name = "deserializing the same payload twice never mints a new activityId (duplicate-processing-ID preservation)"
            Run =
              fun () ->
                  let json = File.ReadAllText(goldenFixturePath())

                  match ActivitySerialization.deserializeActivity json, ActivitySerialization.deserializeActivity json with
                  | Ok first, Ok second -> Assert.equal (ActivityId.value first.ActivityId) (ActivityId.value second.ActivityId)
                  | other -> failwith $"expected both Ok, got {other}" }

          { Name = "a round trip through serialize then deserialize reproduces the original observation"
            Run =
              fun () ->
                  let json = ActivitySerialization.serializeActivity validActivity

                  match ActivitySerialization.deserializeActivity json with
                  | Ok activity -> Assert.equal validActivity activity
                  | Error error -> failwith $"expected Ok, got {error}" } ]
