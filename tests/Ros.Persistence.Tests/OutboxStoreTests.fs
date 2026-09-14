namespace Ros.Persistence.Tests

open System
open System.IO
open Ros.ProjectAdministration
open Ros.Persistence

[<RequireQualifiedAccess>]
module OutboxStoreTests =
    let private now = DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)

    let private withTempRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-central-outbox-store-{Guid.NewGuid():N}")

        try
            run root
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)

    let tests =
        [ { Name = "tryFind returns None against a store that does not exist yet"
            Run = fun () -> withTempRoot (fun root -> Assert.equal None (OutboxStore.tryFind root "OUT-0001")) }

          { Name = "insert then tryFind round-trips a NotReady entry"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let entry = OutboundIntegration.create "OUT-0001" Chrona "ACT-0001" "1" false now
                      OutboxStore.insert root entry |> Assert.isOk

                      match OutboxStore.tryFind root "OUT-0001" with
                      | Some found ->
                          Assert.equal NotReady found.Status
                          Assert.equal Chrona found.Target
                      | None -> failwith "expected the entry to be found") }

          { Name = "inserting a duplicate id fails rather than overwriting"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let entry = OutboundIntegration.create "OUT-0002" Chrona "ACT-0002" "1" false now
                      OutboxStore.insert root entry |> Assert.isOk
                      OutboxStore.insert root entry |> Assert.isError |> ignore) }

          { Name = "update persists a state transition, including a Failed entry's attempts and error"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let entry = OutboundIntegration.create "OUT-0003" Chrona "ACT-0003" "1" true now
                      OutboxStore.insert root entry |> Assert.isOk

                      let sending = OutboundIntegration.beginSending now entry |> Assert.isOk
                      OutboxStore.update root sending |> Assert.isOk

                      let failed = OutboundIntegration.markFailed "timeout" sending |> Assert.isOk
                      OutboxStore.update root failed |> Assert.isOk

                      match OutboxStore.tryFind root "OUT-0003" with
                      | Some found ->
                          Assert.equal (Failed(1, "timeout")) found.Status
                          Assert.equal (Some "timeout") found.LastError
                      | None -> failwith "expected the entry to be found") }

          { Name = "update on an id that was never inserted fails"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let entry = OutboundIntegration.create "OUT-0004" Chrona "ACT-0004" "1" true now
                      OutboxStore.update root entry |> Assert.isError |> ignore) }

          { Name = "listDeliverable returns Pending and Failed entries, never NotReady, Sending, or Delivered"
            Run =
              fun () ->
                  withTempRoot (fun root ->
                      let notReady = OutboundIntegration.create "OUT-A" Chrona "ACT-A" "1" false now
                      let pending = OutboundIntegration.create "OUT-B" Chrona "ACT-B" "1" true now
                      let sending = OutboundIntegration.create "OUT-C" Chrona "ACT-C" "1" true now |> OutboundIntegration.beginSending now |> Assert.isOk

                      let delivered =
                          OutboundIntegration.create "OUT-D" Chrona "ACT-D" "1" true now
                          |> OutboundIntegration.beginSending now
                          |> Assert.isOk
                          |> OutboundIntegration.markDelivered now
                          |> Assert.isOk

                      let failed =
                          OutboundIntegration.create "OUT-E" Chrona "ACT-E" "1" true now
                          |> OutboundIntegration.beginSending now
                          |> Assert.isOk
                          |> OutboundIntegration.markFailed "timeout"
                          |> Assert.isOk

                      [ notReady; pending; sending; delivered; failed ] |> List.iter (OutboxStore.insert root >> Assert.isOk)

                      let deliverable = OutboxStore.listDeliverable root |> List.map (fun entry -> entry.Id) |> Set.ofList
                      Assert.equal (Set.ofList [ "OUT-B"; "OUT-E" ]) deliverable) } ]
