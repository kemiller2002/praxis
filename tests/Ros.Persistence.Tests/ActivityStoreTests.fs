namespace Ros.Persistence.Tests

open System
open System.IO
open EchelonFoundry.Ros.Integration
open Ros.ProjectAdministration
open Ros.Persistence

[<RequireQualifiedAccess>]
module ActivityStoreTests =
    let private input: ActivityObservationInput =
        { ContractVersion = "1"
          ActivityId = "ACT-STORE-0001"
          OrganizationId = "ORG-ECHELON"
          ProjectId = "PROJ-ROS"
          RepositoryId = None
          WorkItemId = None
          ActorId = None
          StartedAt = None
          EndedAt = None
          Description = None
          Evidence = [] }

    let private activity =
        match ActivityObservation.create input with
        | Ok activity -> activity
        | Error errors -> failwith $"fixture input expected to be valid, got {errors}"

    let private withTempRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-central-activity-store-{Guid.NewGuid():N}")

        try
            run root
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)

    let tests =
        [ { Name = "tryFind returns None against a store that does not exist yet"
            Run = fun () -> withTempRoot (fun root -> Assert.equal None (ActivityStore.tryFind root "http" "ACT-STORE-0001")) }

          { Name = "save then tryFind round-trips the stored activity, including its state"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let stored =
                          { Source = "http"
                            Activity = activity
                            State = Recorded
                            ReceivedAt = DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) }

                      ActivityStore.save root stored |> Assert.isOk

                      match ActivityStore.tryFind root "http" "ACT-STORE-0001" with
                      | Some found ->
                          Assert.equal "ACT-STORE-0001" (ActivityId.value found.Activity.ActivityId)
                          Assert.equal Recorded found.State
                          Assert.equal "http" found.Source
                      | None -> failwith "expected the stored activity to be found") }

          { Name = "saving the same (source, activityId) pair twice fails rather than duplicating"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let stored =
                          { Source = "http"
                            Activity = activity
                            State = Recorded
                            ReceivedAt = DateTimeOffset.UtcNow }

                      ActivityStore.save root stored |> Assert.isOk
                      ActivityStore.save root stored |> Assert.isError |> ignore) }

          { Name = "the same activityId from a different source is a distinct record"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let stored =
                          { Source = "http"
                            Activity = activity
                            State = Recorded
                            ReceivedAt = DateTimeOffset.UtcNow }

                      ActivityStore.save root stored |> Assert.isOk
                      ActivityStore.save root { stored with Source = "webhook" } |> Assert.isOk

                      Assert.isTrue (ActivityStore.tryFind root "http" "ACT-STORE-0001" |> Option.isSome) "expected the http-sourced record"
                      Assert.isTrue (ActivityStore.tryFind root "webhook" "ACT-STORE-0001" |> Option.isSome) "expected the webhook-sourced record") }

          { Name = "a Rejected state preserves its reason across a round trip"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let stored =
                          { Source = "http"
                            Activity = activity
                            State = Rejected "unknown project"
                            ReceivedAt = DateTimeOffset.UtcNow }

                      ActivityStore.save root stored |> Assert.isOk

                      match ActivityStore.tryFind root "http" "ACT-STORE-0001" with
                      | Some found -> Assert.equal (Rejected "unknown project") found.State
                      | None -> failwith "expected the stored activity to be found") } ]
