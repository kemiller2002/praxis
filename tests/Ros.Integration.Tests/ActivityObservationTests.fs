namespace Ros.Integration.Tests

open System
open EchelonFoundry.Ros.Integration

[<RequireQualifiedAccess>]
module ActivityObservationTests =
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
          Description = Some "did the thing"
          Evidence = [ { Kind = "commit"; Reference = "abc123" } ] }

    let tests =
        [ { Name = "a structurally valid input produces an ActivityObservation"
            Run =
              fun () ->
                  match ActivityObservation.create validInput with
                  | Ok activity ->
                      Assert.equal "ACT-0001" (ActivityId.value activity.ActivityId)
                      Assert.equal "PROJ-ROS" (ProjectId.value activity.ProjectId)
                  | Error errors -> failwith $"expected Ok, got {errors}" }

          { Name = "a supported contract version of '1.0' is also accepted"
            Run =
              fun () ->
                  match ActivityObservation.create { validInput with ContractVersion = "1.0" } with
                  | Ok _ -> ()
                  | Error errors -> failwith $"expected Ok, got {errors}" }

          { Name = "a blank activityId is rejected as MissingActivityId"
            Run =
              fun () ->
                  match ActivityObservation.create { validInput with ActivityId = "  " } with
                  | Error errors -> Assert.isTrue (List.contains MissingActivityId errors) "expected MissingActivityId"
                  | Ok _ -> failwith "expected Error" }

          { Name = "a blank projectId is rejected as MissingProjectId"
            Run =
              fun () ->
                  match ActivityObservation.create { validInput with ProjectId = "" } with
                  | Error errors -> Assert.isTrue (List.contains MissingProjectId errors) "expected MissingProjectId"
                  | Ok _ -> failwith "expected Error" }

          { Name = "an end before its start is rejected as EndBeforeStart"
            Run =
              fun () ->
                  let input =
                      { validInput with
                          StartedAt = Some(DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero))
                          EndedAt = Some(DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero)) }

                  match ActivityObservation.create input with
                  | Error errors -> Assert.isTrue (List.contains EndBeforeStart errors) "expected EndBeforeStart"
                  | Ok _ -> failwith "expected Error" }

          { Name = "an unrecognized contract version is rejected by name, not silently accepted"
            Run =
              fun () ->
                  match ActivityObservation.create { validInput with ContractVersion = "99" } with
                  | Error errors -> Assert.isTrue (List.contains (UnsupportedContractVersion "99") errors) "expected UnsupportedContractVersion \"99\""
                  | Ok _ -> failwith "expected Error" }

          { Name = "every applicable violation is reported together, not just the first"
            Run =
              fun () ->
                  let input =
                      { validInput with
                          ContractVersion = "99"
                          ActivityId = ""
                          ProjectId = "" }

                  match ActivityObservation.create input with
                  | Error errors -> Assert.equal 3 errors.Length
                  | Ok _ -> failwith "expected Error" }

          { Name = "an absent start or end never trips EndBeforeStart"
            Run =
              fun () ->
                  match ActivityObservation.create { validInput with StartedAt = None; EndedAt = None } with
                  | Ok _ -> ()
                  | Error errors -> failwith $"expected Ok, got {errors}" } ]
