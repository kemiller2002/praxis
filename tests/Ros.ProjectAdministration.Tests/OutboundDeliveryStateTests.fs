namespace Ros.ProjectAdministration.Tests

open Ros.ProjectAdministration

[<RequireQualifiedAccess>]
module OutboundDeliveryStateTests =
    let tests =
        [ { Name = "NotReady to Pending is legal"
            Run = fun () -> OutboundDeliveryState.transition NotReady Pending |> Assert.isOk |> ignore }

          { Name = "Pending to Sending is legal"
            Run = fun () -> OutboundDeliveryState.transition Pending Sending |> Assert.isOk |> ignore }

          { Name = "Sending to Delivered is legal"
            Run = fun () -> OutboundDeliveryState.transition Sending Delivered |> Assert.isOk |> ignore }

          { Name = "Sending to Failed is legal"
            Run = fun () -> OutboundDeliveryState.transition Sending (Failed(1, "timeout")) |> Assert.isOk |> ignore }

          { Name = "Failed to Sending is legal -- the retry loop"
            Run = fun () -> OutboundDeliveryState.transition (Failed(1, "timeout")) Sending |> Assert.isOk |> ignore }

          { Name = "NotReady cannot jump straight to Sending"
            Run = fun () -> OutboundDeliveryState.transition NotReady Sending |> Assert.isError |> ignore }

          { Name = "Delivered is terminal -- no transition out of it is legal"
            Run =
              fun () ->
                  OutboundDeliveryState.transition Delivered Sending |> Assert.isError |> ignore
                  OutboundDeliveryState.transition Delivered Pending |> Assert.isError |> ignore }

          { Name = "Pending cannot skip Sending and go straight to Delivered"
            Run = fun () -> OutboundDeliveryState.transition Pending Delivered |> Assert.isError |> ignore } ]
