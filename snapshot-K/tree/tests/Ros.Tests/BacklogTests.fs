namespace Ros.Tests

open Ros.Application.Work
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module BacklogTests =
    let private decide state action reason =
        WorkOperations.decideBacklogTransition
            { State = state
              Action = action
              Reason = reason }

    let tests =
        [ { Name = "backlog transition matrix permits only characterized edges"
            Run =
              fun () ->
                  let states = [ BacklogState.Captured; BacklogState.Ready; BacklogState.Blocked; BacklogState.Abandoned ]
                  let actions = [ BacklogAction.Ready; BacklogAction.Block; BacklogAction.Abandon; BacklogAction.Start ]
                  let legal =
                      Set.ofList
                          [ BacklogState.Captured, BacklogAction.Ready
                            BacklogState.Captured, BacklogAction.Abandon
                            BacklogState.Ready, BacklogAction.Block
                            BacklogState.Ready, BacklogAction.Abandon
                            BacklogState.Ready, BacklogAction.Start
                            BacklogState.Blocked, BacklogAction.Ready
                            BacklogState.Blocked, BacklogAction.Abandon ]

                  for state in states do
                      for action in actions do
                          let allowed =
                              match decide state action (Some "reason") with
                              | BacklogTransitionDecision.Allowed _ -> true
                              | BacklogTransitionDecision.Rejected _ -> false

                          Assert.equal (legal.Contains(state, action)) allowed }
          { Name = "backlog decision distinguishes state changes from live promotion"
            Run =
              fun () ->
                  Assert.equal
                      (BacklogTransitionDecision.Allowed(
                          BacklogTransitionEffect.ChangeState(
                              BacklogState.Ready,
                              BacklogFieldChange.Clear,
                              BacklogFieldChange.Keep)))
                      (decide BacklogState.Blocked BacklogAction.Ready None)

                  Assert.equal
                      (BacklogTransitionDecision.Allowed(
                          BacklogTransitionEffect.ChangeState(
                              BacklogState.Abandoned,
                              BacklogFieldChange.Keep,
                              BacklogFieldChange.Set "obsolete")))
                      (decide BacklogState.Ready BacklogAction.Abandon (Some "obsolete"))

                  Assert.equal
                      (BacklogTransitionDecision.Allowed BacklogTransitionEffect.PromoteToLiveWork)
                      (decide BacklogState.Ready BacklogAction.Start None) }
          { Name = "backlog block guard preserves production empty versus whitespace behavior"
            Run =
              fun () ->
                  Assert.equal
                      (BacklogTransitionDecision.Rejected BacklogTransitionRejection.BlockReasonRequired)
                      (decide BacklogState.Ready BacklogAction.Block (Some ""))

                  Assert.equal
                      (BacklogTransitionDecision.Allowed(
                          BacklogTransitionEffect.ChangeState(
                              BacklogState.Blocked,
                              BacklogFieldChange.Set "  ",
                              BacklogFieldChange.Keep)))
                      (decide BacklogState.Ready BacklogAction.Block (Some "  ")) }
          { Name = "backlog promotion preflights the complete selection without mutating queue state"
            Run =
              fun () ->
                  let request =
                      { WorkItemIds = [ "WI-READY"; "EXT-DIRECT" ]
                        QueueStates = Map.ofList [ "WI-READY", BacklogState.Ready ]
                        WorkType = "feature" }

                  Assert.equal
                      (BacklogPromotionOutcome.Planned
                          { WorkItemIds = request.WorkItemIds
                            WorkType = "feature" })
                      (WorkOperations.planBacklogPromotion request)

                  Assert.equal
                      (BacklogPromotionOutcome.Rejected(
                          BacklogPromotionRejection.BacklogItemNotReady("WI-BLOCKED", BacklogState.Blocked)))
                      (WorkOperations.planBacklogPromotion
                          { request with
                              WorkItemIds = [ "WI-READY"; "WI-BLOCKED" ]
                              QueueStates = request.QueueStates |> Map.add "WI-BLOCKED" BacklogState.Blocked })

                  Assert.equal
                      (BacklogPromotionOutcome.Rejected BacklogPromotionRejection.NoWorkItems)
                      (WorkOperations.planBacklogPromotion { request with WorkItemIds = [] }) } ]
