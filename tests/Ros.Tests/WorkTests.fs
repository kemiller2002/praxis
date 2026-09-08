namespace Ros.Tests

open Ros.Application.Work
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module WorkTests =
    let private request state action reason required provided =
        { State = state
          Action = action
          BlockReason = reason
          RequiredEvidence = Set.ofList required
          ProvidedEvidence = Set.ofList provided }

    let private allowed state action target =
        Assert.equal
            (TransitionDecision.Allowed target)
            (WorkOperations.decideTransition (request state action (Some "reason") [] []))

    let tests =
        [ { Name = "live work transition matrix allows only characterized edges"
            Run =
              fun () ->
                  allowed LiveWorkState.Ready WorkAction.Begin LiveWorkState.Active
                  allowed LiveWorkState.Ready WorkAction.Block LiveWorkState.Blocked
                  allowed LiveWorkState.Active WorkAction.Block LiveWorkState.Blocked
                  allowed LiveWorkState.Active WorkAction.Complete LiveWorkState.Complete
                  allowed LiveWorkState.Blocked WorkAction.Resume LiveWorkState.Active

                  for state in [ LiveWorkState.Ready; LiveWorkState.Active; LiveWorkState.Blocked; LiveWorkState.Complete ] do
                      for action in [ WorkAction.Begin; WorkAction.Block; WorkAction.Resume; WorkAction.Complete ] do
                          if not (WorkTransition.allowedActions state |> List.contains action) then
                              match WorkOperations.decideTransition (request state action (Some "reason") [] []) with
                              | TransitionDecision.Rejected(TransitionRejection.IllegalTransition(actualState, actualAction)) ->
                                  Assert.equal state actualState
                                  Assert.equal action actualAction
                              | other -> failwith $"Expected illegal {state}/{action}, received {other}" }
          { Name = "block requires a non-empty reason"
            Run =
              fun () ->
                  for reason in [ None; Some "" ] do
                      Assert.equal
                          (TransitionDecision.Rejected TransitionRejection.BlockReasonRequired)
                          (WorkOperations.decideTransition (request LiveWorkState.Active WorkAction.Block reason [] []))

                  Assert.equal
                      (TransitionDecision.Allowed LiveWorkState.Blocked)
                      (WorkOperations.decideTransition
                          (request LiveWorkState.Active WorkAction.Block (Some "  ") [] [])) }
          { Name = "completion rejects sorted missing evidence types"
            Run =
              fun () ->
                  Assert.equal
                      (TransitionDecision.Rejected(TransitionRejection.MissingEvidence [ "implementation"; "tests" ]))
                      (WorkOperations.decideTransition
                          (request LiveWorkState.Active WorkAction.Complete None [ "tests"; "implementation" ] [])) }
          { Name = "completion accepts every required evidence type"
            Run =
              fun () ->
                  Assert.equal
                      (TransitionDecision.Allowed LiveWorkState.Complete)
                      (WorkOperations.decideTransition
                          (request LiveWorkState.Active WorkAction.Complete None [ "implementation"; "tests" ] [ "tests"; "implementation"; "review" ])) } ]
