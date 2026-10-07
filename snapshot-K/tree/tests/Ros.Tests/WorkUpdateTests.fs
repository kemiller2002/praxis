namespace Ros.Tests

open Ros.Domain.Work

[<RequireQualifiedAccess>]
module WorkUpdateTests =
    let private request =
        { Id = "WI-0001"
          QueueContainsId = true
          ContextContainsId = false
          Title = None
          Description = None
          Tags = None
          Priority = None
          OccurredAt = "2026-09-09T00:00:00.000Z" }

    let private planned outcome =
        match outcome with
        | WorkUpdateOutcome.Planned plan -> plan
        | WorkUpdateOutcome.Rejected rejection -> failwith $"expected a plan but was rejected: {rejection}"

    let private rejected outcome =
        match outcome with
        | WorkUpdateOutcome.Rejected rejection -> rejection
        | WorkUpdateOutcome.Planned plan -> failwith $"expected a rejection but was planned: {plan}"

    let tests =
        [ { Name = "an id already in the queue is updated in place, never upserted"
            Run =
              fun () ->
                  let plan = planned (WorkUpdate.plan request)
                  Assert.equal false plan.UpsertNew }
          { Name = "an id absent from the queue but present in the live context is upserted"
            Run =
              fun () ->
                  let plan = planned (WorkUpdate.plan { request with QueueContainsId = false; ContextContainsId = true })
                  Assert.equal true plan.UpsertNew }
          { Name = "an id in neither the queue nor the live context, but shaped like a valid id, is 'not found'"
            Run =
              fun () ->
                  Assert.equal
                      (WorkUpdateRejection.NotFound "WI-0001")
                      (rejected (WorkUpdate.plan { request with QueueContainsId = false; ContextContainsId = false })) }
          { Name = "an id absent from the queue and shaped invalidly is rejected before checking the live context"
            Run =
              fun () ->
                  Assert.equal
                      (WorkUpdateRejection.InvalidId "not-an-id")
                      (rejected (
                          WorkUpdate.plan
                              { request with
                                  Id = "not-an-id"
                                  QueueContainsId = false
                                  ContextContainsId = true }
                      )) }
          { Name = "no fields provided means every change is Keep"
            Run =
              fun () ->
                  let plan = planned (WorkUpdate.plan request)
                  Assert.equal WorkTitleChange.Keep plan.Title
                  Assert.equal WorkDescriptionChange.Keep plan.Description
                  Assert.equal WorkTagsChange.Keep plan.Tags
                  Assert.equal WorkPriorityChange.Keep plan.Priority }
          { Name = "a provided title is trimmed and set, but empty after trimming is rejected"
            Run =
              fun () ->
                  let plan = planned (WorkUpdate.plan { request with Title = Some "  padded  " })
                  Assert.equal (WorkTitleChange.Set "padded") plan.Title
                  Assert.equal WorkUpdateRejection.EmptyTitle (rejected (WorkUpdate.plan { request with Title = Some "   " })) }
          { Name = "a provided description trims to a value, or to an explicit clear when only whitespace"
            Run =
              fun () ->
                  let setPlan = planned (WorkUpdate.plan { request with Description = Some "  kept  " })
                  Assert.equal (WorkDescriptionChange.Set(Some "kept")) setPlan.Description

                  let clearPlan = planned (WorkUpdate.plan { request with Description = Some "   " })
                  Assert.equal (WorkDescriptionChange.Set None) clearPlan.Description }
          { Name = "provided tags replace verbatim, including an explicit empty list"
            Run =
              fun () ->
                  let plan = planned (WorkUpdate.plan { request with Tags = Some [ "a"; "b" ] })
                  Assert.equal (WorkTagsChange.Set [ "a"; "b" ]) plan.Tags

                  let clearedPlan = planned (WorkUpdate.plan { request with Tags = Some [] })
                  Assert.equal (WorkTagsChange.Set []) clearedPlan.Tags }
          { Name = "an unrecognized priority is rejected; a recognized one is set"
            Run =
              fun () ->
                  Assert.equal
                      (WorkUpdateRejection.InvalidPriority "urgent")
                      (rejected (WorkUpdate.plan { request with Priority = Some "urgent" }))

                  let plan = planned (WorkUpdate.plan { request with Priority = Some "low" })
                  Assert.equal (WorkPriorityChange.Set "low") plan.Priority }
          { Name = "title is validated before priority, matching production's own check order"
            Run =
              fun () ->
                  let outcome = WorkUpdate.plan { request with Title = Some "   "; Priority = Some "urgent" }
                  Assert.equal WorkUpdateRejection.EmptyTitle (rejected outcome) } ]
