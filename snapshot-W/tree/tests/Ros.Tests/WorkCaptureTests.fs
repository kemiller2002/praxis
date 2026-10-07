namespace Ros.Tests

open Ros.Domain.Work

[<RequireQualifiedAccess>]
module WorkCaptureTests =
    let private request =
        { Title = "A new task"
          ExplicitId = None
          Priority = None
          Description = None
          Tags = []
          Actor = "unknown"
          Source = None
          SourceReference = None
          ExistingQueueIds = Set.empty
          ExistingContextIds = Set.empty
          NextSeq = 1
          OccurredAt = "2026-09-09T00:00:00.000Z" }

    let private planned outcome =
        match outcome with
        | WorkCaptureOutcome.Planned plan -> plan
        | WorkCaptureOutcome.Rejected rejection -> failwith $"expected a plan but was rejected: {rejection}"

    let private rejected outcome =
        match outcome with
        | WorkCaptureOutcome.Rejected rejection -> rejection
        | WorkCaptureOutcome.Planned plan -> failwith $"expected a rejection but was planned: {plan}"

    let tests =
        [ { Name = "an empty or whitespace-only title is rejected"
            Run =
              fun () ->
                  Assert.equal WorkCaptureRejection.EmptyTitle (rejected (WorkCapture.plan { request with Title = "" }))
                  Assert.equal WorkCaptureRejection.EmptyTitle (rejected (WorkCapture.plan { request with Title = "   " })) }
          { Name = "the title is trimmed before being stored"
            Run =
              fun () ->
                  let plan = planned (WorkCapture.plan { request with Title = "  padded  " })
                  Assert.equal "padded" plan.Item.Title }
          { Name = "an unrecognized priority is rejected, but only when provided"
            Run =
              fun () ->
                  Assert.equal
                      (WorkCaptureRejection.InvalidPriority "urgent")
                      (rejected (WorkCapture.plan { request with Priority = Some "urgent" }))

                  Assert.equal "medium" (planned (WorkCapture.plan request)).Item.Priority }
          { Name = "an explicit id must match the shared work-item id pattern"
            Run =
              fun () ->
                  Assert.equal
                      (WorkCaptureRejection.InvalidId "not-an-id")
                      (rejected (WorkCapture.plan { request with ExplicitId = Some "not-an-id" })) }
          { Name = "an explicit id already in the queue is rejected before checking the live context"
            Run =
              fun () ->
                  let outcome =
                      WorkCapture.plan
                          { request with
                              ExplicitId = Some "WI-0001"
                              ExistingQueueIds = Set.ofList [ "WI-0001" ]
                              ExistingContextIds = Set.ofList [ "WI-0001" ] }

                  Assert.equal (WorkCaptureRejection.DuplicateInQueue "WI-0001") (rejected outcome) }
          { Name = "an explicit id already live in the context (but not the queue) is rejected"
            Run =
              fun () ->
                  let outcome =
                      WorkCapture.plan { request with ExplicitId = Some "WI-0001"; ExistingContextIds = Set.ofList [ "WI-0001" ] }

                  Assert.equal (WorkCaptureRejection.DuplicateInContext "WI-0001") (rejected outcome) }
          { Name = "an explicit id is used verbatim and never advances nextSeq"
            Run =
              fun () ->
                  let plan = planned (WorkCapture.plan { request with ExplicitId = Some "CUSTOM-ID"; NextSeq = 7 })
                  Assert.equal "CUSTOM-ID" plan.Item.Id
                  Assert.equal 7 plan.NextSeq }
          { Name = "an omitted id is generated sequentially from nextSeq, skipping any collision"
            Run =
              fun () ->
                  let plan =
                      planned (WorkCapture.plan { request with NextSeq = 3; ExistingQueueIds = Set.ofList [ "WI-0003"; "WI-0004" ] })

                  Assert.equal "WI-0005" plan.Item.Id
                  Assert.equal 6 plan.NextSeq }
          { Name = "a generated id never checks the live context, only the queue"
            Run =
              fun () ->
                  let plan =
                      planned (WorkCapture.plan { request with NextSeq = 1; ExistingContextIds = Set.ofList [ "WI-0001" ] })

                  Assert.equal "WI-0001" plan.Item.Id }
          { Name = "an empty or whitespace-only description is stored as absent, matching production's falsy check"
            Run =
              fun () ->
                  Assert.equal None (planned (WorkCapture.plan { request with Description = Some "   " })).Item.Description
                  Assert.equal (Some "kept") (planned (WorkCapture.plan { request with Description = Some "  kept  " })).Item.Description }
          { Name = "source, actor, and priority all fall back to production's own defaults"
            Run =
              fun () ->
                  let plan = planned (WorkCapture.plan request)
                  Assert.equal "manual" plan.Item.Source
                  Assert.equal "unknown" plan.Item.CreatedBy
                  Assert.equal "medium" plan.Item.Priority
                  Assert.equal "captured" plan.Item.Status
                  Assert.equal request.OccurredAt plan.Item.CreatedAt
                  Assert.equal request.OccurredAt plan.Item.UpdatedAt } ]
