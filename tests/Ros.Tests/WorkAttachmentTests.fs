namespace Ros.Tests

open Ros.Domain.Work

[<RequireQualifiedAccess>]
module WorkAttachmentTests =
    let private request =
        { Id = "WI-0001"
          QueueContainsId = true
          ContextContainsId = false
          DisplayName = "report.txt"
          Size = 42L
          ExistingAttachmentSequences = []
          OccurredAt = "2026-09-09T00:00:00.000Z" }

    let private planned outcome =
        match outcome with
        | WorkAttachmentOutcome.Planned plan -> plan
        | WorkAttachmentOutcome.Rejected rejection -> failwith $"expected a plan but was rejected: {rejection}"

    let private rejected outcome =
        match outcome with
        | WorkAttachmentOutcome.Rejected rejection -> rejection
        | WorkAttachmentOutcome.Planned plan -> failwith $"expected a rejection but was planned: {plan}"

    let tests =
        [ { Name = "sanitizeFileComponent strips any path segments, keeping only the last one"
            Run = fun () -> Assert.equal "b.txt" (WorkAttachment.sanitizeFileComponent "a/b.txt") }
          { Name = "sanitizeFileComponent trims and collapses every run of disallowed characters to a single underscore"
            Run = fun () -> Assert.equal "My_Cool_File_.txt" (WorkAttachment.sanitizeFileComponent "  My Cool File!.txt  ") }
          { Name = "sanitizeFileComponent collapses an entirely-disallowed name to a single underscore, not 'file'"
            Run = fun () -> Assert.equal "_" (WorkAttachment.sanitizeFileComponent "???") }
          { Name = "sanitizeFileComponent falls back to 'file' only when basename-then-trim leaves nothing at all"
            Run =
              fun () ->
                  Assert.equal "file" (WorkAttachment.sanitizeFileComponent "   ")
                  Assert.equal "file" (WorkAttachment.sanitizeFileComponent "")
                  Assert.equal "file" (WorkAttachment.sanitizeFileComponent "/") }
          { Name = "an id already in the queue attaches in place, never upserting"
            Run =
              fun () ->
                  let plan = planned (WorkAttachment.plan request)
                  Assert.equal false plan.UpsertNew }
          { Name = "an id absent from the queue but present in the live context is upserted"
            Run =
              fun () ->
                  let plan = planned (WorkAttachment.plan { request with QueueContainsId = false; ContextContainsId = true })
                  Assert.equal true plan.UpsertNew }
          { Name = "an id in neither the queue nor the live context, but shaped like a valid id, is 'not found'"
            Run =
              fun () ->
                  Assert.equal
                      (WorkAttachmentRejection.NotFound "WI-0001")
                      (rejected (WorkAttachment.plan { request with QueueContainsId = false; ContextContainsId = false })) }
          { Name = "an id shaped invalidly is rejected before checking the live context"
            Run =
              fun () ->
                  Assert.equal
                      (WorkAttachmentRejection.InvalidId "not-an-id")
                      (rejected (
                          WorkAttachment.plan
                              { request with
                                  Id = "not-an-id"
                                  QueueContainsId = false
                                  ContextContainsId = true }
                      )) }
          { Name = "the first attachment for an item is sequence 1, matching production's zero-based fallback plus one"
            Run =
              fun () ->
                  let plan = planned (WorkAttachment.plan { request with ExistingAttachmentSequences = [] })
                  Assert.equal 1 plan.Record.Seq
                  Assert.equal "ATT-1" plan.Record.Id
                  Assert.equal "1-report.txt" plan.Record.File }
          { Name = "the next attachment's sequence is one past the highest existing sequence, not the count"
            Run =
              fun () ->
                  let plan = planned (WorkAttachment.plan { request with ExistingAttachmentSequences = [ 1; 5; 3 ] })
                  Assert.equal 6 plan.Record.Seq
                  Assert.equal "ATT-6" plan.Record.Id
                  Assert.equal "6-report.txt" plan.Record.File }
          { Name = "the display name is preserved verbatim in the record even though the stored filename is sanitized"
            Run =
              fun () ->
                  let plan = planned (WorkAttachment.plan { request with DisplayName = "My Cool File!.txt" })
                  Assert.equal "My Cool File!.txt" plan.Record.Name
                  Assert.equal "1-My_Cool_File_.txt" plan.Record.File }
          { Name = "contentType is always absent, matching production's own CLI path (never threaded through)"
            Run = fun () -> Assert.equal None (planned (WorkAttachment.plan request)).Record.ContentType }
          { Name = "size and the uploaded/updated timestamps pass through unchanged"
            Run =
              fun () ->
                  let plan = planned (WorkAttachment.plan { request with Size = 12345L })
                  Assert.equal 12345L plan.Record.Size
                  Assert.equal request.OccurredAt plan.Record.UploadedAt
                  Assert.equal request.OccurredAt plan.UpdatedAt } ]
