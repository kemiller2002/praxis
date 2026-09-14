namespace Ros.Integration.Consumer.Tests

open System
open EchelonFoundry.Ros.Integration

/// Simulates an external application producing ROS activity data, using
/// only the package's public API -- exactly as a real consumer, with no
/// access to ROS's internals, would have to. If any scenario here needed
/// something not exposed by `Ros.Integration`, that would mean the
/// public contract boundary is wrong, per
/// docs/migrations/central-integration/INTEGRATION-CONTRACT-STANDARD.md.
/// This project intentionally never references `Ros.Domain`,
/// `Ros.Application`, or `Ros.Infrastructure`.
[<RequireQualifiedAccess>]
module ConsumerScenarioTests =
    let tests =
        [ { Name = "a producer builds, validates, and serializes an observation using only public functions"
            Run =
              fun () ->
                  let input: ActivityObservationInput =
                      { ContractVersion = "1"
                        ActivityId = "ACT-CONSUMER-0001"
                        OrganizationId = "ORG-ECHELON"
                        ProjectId = "PROJ-ROS"
                        RepositoryId = Some "REPO-ROS-CORE"
                        WorkItemId = None
                        ActorId = Some "a-producer-app"
                        StartedAt = Some(DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero))
                        EndedAt = Some(DateTimeOffset(2026, 2, 1, 12, 45, 0, TimeSpan.Zero))
                        Description = Some "reviewed a pull request"
                        Evidence = [ { Kind = "pull-request-review"; Reference = "PR-99" } ] }

                  match ActivityObservation.create input with
                  | Error errors -> failwith $"expected Ok, got {errors}"
                  | Ok activity ->
                      let wire = ActivitySerialization.serializeActivity activity
                      Assert.isTrue (wire.Contains "\"activityId\": \"ACT-CONSUMER-0001\"") "expected the activityId to appear on the wire" }

          { Name = "a producer receives structural errors it can act on without any ROS-internal type"
            Run =
              fun () ->
                  let input: ActivityObservationInput =
                      { ContractVersion = "1"
                        ActivityId = ""
                        OrganizationId = "ORG-ECHELON"
                        ProjectId = "PROJ-ROS"
                        RepositoryId = None
                        WorkItemId = None
                        ActorId = None
                        StartedAt = None
                        EndedAt = None
                        Description = None
                        Evidence = [] }

                  match ActivityObservation.create input with
                  | Ok _ -> failwith "expected Error"
                  | Error errors -> Assert.isTrue (List.contains MissingActivityId errors) "expected MissingActivityId" }

          { Name = "a producer can parse a response-shaped payload it receives back, using only deserializeActivity"
            Run =
              fun () ->
                  let json =
                      """{"contractVersion":"1","activityId":"ACT-CONSUMER-0002","organizationId":"ORG-ECHELON","projectId":"PROJ-ROS","evidence":[]}"""

                  match ActivitySerialization.deserializeActivity json with
                  | Ok activity -> Assert.equal "ACT-CONSUMER-0002" (ActivityId.value activity.ActivityId)
                  | Error error -> failwith $"expected Ok, got {error}" } ]
