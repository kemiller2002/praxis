namespace Ros.ProjectAdministration.Tests

open Ros.ProjectAdministration

[<RequireQualifiedAccess>]
module ExternalActivityStateTests =
    let tests =
        [ { Name = "Received to Validated is legal"
            Run = fun () -> ExternalActivityState.transition Received Validated |> Assert.isOk |> ignore }

          { Name = "Received to Rejected is legal"
            Run = fun () -> ExternalActivityState.transition Received (Rejected "malformed") |> Assert.isOk |> ignore }

          { Name = "Validated to Recorded is legal"
            Run = fun () -> ExternalActivityState.transition Validated Recorded |> Assert.isOk |> ignore }

          { Name = "Validated to Rejected is legal"
            Run = fun () -> ExternalActivityState.transition Validated (Rejected "no longer legal") |> Assert.isOk |> ignore }

          { Name = "Received to Recorded is illegal -- validation cannot be skipped"
            Run = fun () -> ExternalActivityState.transition Received Recorded |> Assert.isError |> ignore }

          { Name = "Recorded is terminal -- no transition out of it is legal"
            Run =
              fun () ->
                  ExternalActivityState.transition Recorded Validated |> Assert.isError |> ignore
                  ExternalActivityState.transition Recorded Received |> Assert.isError |> ignore }

          { Name = "Rejected is terminal -- no transition out of it is legal"
            Run = fun () -> ExternalActivityState.transition (Rejected "x") Validated |> Assert.isError |> ignore } ]
