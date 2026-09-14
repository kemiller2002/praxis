namespace Ros.ProjectAdministration.Tests

open System
open Ros.ProjectAdministration

[<RequireQualifiedAccess>]
module OutboundIntegrationTests =
    let private now = DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)
    let private later = now.AddMinutes 5.0

    let tests =
        [ { Name = "create with no adapter ready starts NotReady"
            Run =
              fun () ->
                  let entry = OutboundIntegration.create "OUT-0001" Chrona "ACT-0001" "1" false now
                  Assert.equal NotReady entry.Status
                  Assert.equal 0 entry.AttemptCount }

          { Name = "create with an adapter ready starts Pending"
            Run =
              fun () ->
                  let entry = OutboundIntegration.create "OUT-0002" Chrona "ACT-0002" "1" true now
                  Assert.equal Pending entry.Status }

          { Name = "markReady moves a NotReady entry to Pending"
            Run =
              fun () ->
                  let entry = OutboundIntegration.create "OUT-0003" Chrona "ACT-0003" "1" false now
                  let ready = OutboundIntegration.markReady entry |> Assert.isOk
                  Assert.equal Pending ready.Status }

          { Name = "beginSending moves Pending to Sending, incrementing AttemptCount and stamping LastAttemptAt"
            Run =
              fun () ->
                  let entry = OutboundIntegration.create "OUT-0004" Chrona "ACT-0004" "1" true now
                  let sending = OutboundIntegration.beginSending later entry |> Assert.isOk

                  Assert.equal Sending sending.Status
                  Assert.equal 1 sending.AttemptCount
                  Assert.equal (Some later) sending.LastAttemptAt }

          { Name = "markDelivered moves Sending to Delivered, stamping CompletedAt and clearing LastError"
            Run =
              fun () ->
                  let entry =
                      OutboundIntegration.create "OUT-0005" Chrona "ACT-0005" "1" true now
                      |> OutboundIntegration.beginSending later
                      |> Assert.isOk

                  let delivered = OutboundIntegration.markDelivered later entry |> Assert.isOk
                  Assert.equal Delivered delivered.Status
                  Assert.equal (Some later) delivered.CompletedAt
                  Assert.equal None delivered.LastError }

          { Name = "markFailed moves Sending to Failed, recording the attempt count and the error"
            Run =
              fun () ->
                  let entry =
                      OutboundIntegration.create "OUT-0006" Chrona "ACT-0006" "1" true now
                      |> OutboundIntegration.beginSending later
                      |> Assert.isOk

                  let failed = OutboundIntegration.markFailed "timeout" entry |> Assert.isOk
                  Assert.equal (Failed(1, "timeout")) failed.Status
                  Assert.equal (Some "timeout") failed.LastError }

          { Name = "beginSending retries from Failed back to Sending, incrementing AttemptCount again"
            Run =
              fun () ->
                  let entry =
                      OutboundIntegration.create "OUT-0007" Chrona "ACT-0007" "1" true now
                      |> OutboundIntegration.beginSending later
                      |> Assert.isOk
                      |> OutboundIntegration.markFailed "timeout"
                      |> Assert.isOk

                  let retried = OutboundIntegration.beginSending later entry |> Assert.isOk
                  Assert.equal Sending retried.Status
                  Assert.equal 2 retried.AttemptCount }

          { Name = "beginSending on a NotReady entry is rejected -- an adapter must be installed first"
            Run =
              fun () ->
                  let entry = OutboundIntegration.create "OUT-0008" Chrona "ACT-0008" "1" false now
                  OutboundIntegration.beginSending later entry |> Assert.isError |> ignore }

          { Name = "markDelivered on a Delivered entry is rejected -- it is terminal"
            Run =
              fun () ->
                  let entry =
                      OutboundIntegration.create "OUT-0009" Chrona "ACT-0009" "1" true now
                      |> OutboundIntegration.beginSending later
                      |> Assert.isOk
                      |> OutboundIntegration.markDelivered later
                      |> Assert.isOk

                  OutboundIntegration.markDelivered later entry |> Assert.isError |> ignore } ]
