namespace Ros.Host.Tests

open System
open System.Collections.Generic
open EchelonFoundry.Ros.Integration
open Ros.Persistence
open Ros.Host

/// Exercises `ActivityIngestion.handle` directly, with an in-memory fake
/// standing in for `Ros.Persistence.ActivityStore` -- per the migration
/// spec's own instruction not to hide core behavior behind HTTP-level
/// integration tests. `tests/Ros.Persistence.Tests` separately proves
/// the real file-backed store this fake stands in for.
[<RequireQualifiedAccess>]
module ActivityIngestionTests =
    let private validJson activityId =
        $"""{{"contractVersion":"1","activityId":"{activityId}","organizationId":"ORG-ECHELON","projectId":"PROJ-ROS","evidence":[]}}"""

    let private newFakeStore () =
        let table = Dictionary<string * string, StoredActivity>()

        let findExisting source activityId =
            match table.TryGetValue((source, activityId)) with
            | true, value -> Some value
            | false, _ -> None

        let save (stored: StoredActivity) =
            let key = stored.Source, ActivityId.value stored.Activity.ActivityId

            if table.ContainsKey key then
                Error "already recorded"
            else
                table.[key] <- stored
                Ok()

        findExisting, save, table

    let private fixedNow = fun () -> DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)

    let tests =
        [ { Name = "a valid activity is accepted and persisted"
            Run =
              fun () ->
                  let findExisting, save, table = newFakeStore ()
                  let outcome = ActivityIngestion.handle findExisting save fixedNow "http" (validJson "ACT-0001")

                  Assert.equal (Accepted "ACT-0001") outcome
                  Assert.equal 1 table.Count }

          { Name = "malformed JSON is reported as Malformed, not as a missing-field error"
            Run =
              fun () ->
                  let findExisting, save, _ = newFakeStore ()
                  let outcome = ActivityIngestion.handle findExisting save fixedNow "http" "{ not json"

                  match outcome with
                  | Malformed _ -> ()
                  | other -> failwith $"expected Malformed, got {other}" }

          { Name = "a payload missing a required field is reported as Invalid, carrying the specific violation"
            Run =
              fun () ->
                  let findExisting, save, _ = newFakeStore ()
                  let json = """{"contractVersion":"1","organizationId":"ORG-ECHELON","projectId":"PROJ-ROS","evidence":[]}"""
                  let outcome = ActivityIngestion.handle findExisting save fixedNow "http" json

                  match outcome with
                  | Invalid errors -> Assert.isTrue (List.contains MissingActivityId errors) "expected MissingActivityId"
                  | other -> failwith $"expected Invalid, got {other}" }

          { Name = "an unsupported contract version is reported as Invalid, not silently accepted"
            Run =
              fun () ->
                  let findExisting, save, _ = newFakeStore ()

                  let json =
                      """{"contractVersion":"99","activityId":"ACT-0002","organizationId":"ORG-ECHELON","projectId":"PROJ-ROS","evidence":[]}"""

                  match ActivityIngestion.handle findExisting save fixedNow "http" json with
                  | Invalid errors -> Assert.isTrue (List.contains (UnsupportedContractVersion "99") errors) "expected UnsupportedContractVersion \"99\""
                  | other -> failwith $"expected Invalid, got {other}" }

          { Name = "an idempotent retry of the same (source, activityId) is accepted again, never duplicated"
            Run =
              fun () ->
                  let findExisting, save, table = newFakeStore ()
                  let json = validJson "ACT-0003"

                  let first = ActivityIngestion.handle findExisting save fixedNow "http" json
                  let second = ActivityIngestion.handle findExisting save fixedNow "http" json

                  Assert.equal (Accepted "ACT-0003") first
                  Assert.equal (Accepted "ACT-0003") second
                  Assert.equal 1 table.Count }

          { Name = "the same activityId from a different source is not treated as a duplicate"
            Run =
              fun () ->
                  let findExisting, save, table = newFakeStore ()
                  let json = validJson "ACT-0004"

                  ActivityIngestion.handle findExisting save fixedNow "http" json |> ignore
                  ActivityIngestion.handle findExisting save fixedNow "webhook" json |> ignore

                  Assert.equal 2 table.Count }

          { Name = "a persistence failure surfaces as Conflict, not as a silent success"
            Run =
              fun () ->
                  let findExisting = fun (_: string) (_: string) -> None
                  let save = fun (_: StoredActivity) -> Error "disk full"

                  match ActivityIngestion.handle findExisting save fixedNow "http" (validJson "ACT-0005") with
                  | Conflict message -> Assert.equal "disk full" message
                  | other -> failwith $"expected Conflict, got {other}" } ]
