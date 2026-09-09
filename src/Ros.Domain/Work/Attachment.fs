namespace Ros.Domain.Work

open System.Text.RegularExpressions

type WorkAttachmentRequest =
    { Id: string
      QueueContainsId: bool
      ContextContainsId: bool
      DisplayName: string
      Size: int64
      ExistingAttachmentSequences: int list
      OccurredAt: string }

type WorkAttachmentRecord =
    { Id: string
      Seq: int
      Name: string
      File: string
      Size: int64
      ContentType: string option
      UploadedAt: string }

type WorkAttachmentPlan =
    { UpsertNew: bool
      Record: WorkAttachmentRecord
      UpdatedAt: string }

[<RequireQualifiedAccess>]
type WorkAttachmentRejection =
    | InvalidId of id: string
    | NotFound of id: string

[<RequireQualifiedAccess>]
type WorkAttachmentOutcome =
    | Planned of WorkAttachmentPlan
    | Rejected of WorkAttachmentRejection

/// Mirrors production `attachFileUnlocked`/`sanitizeFileComponent`/
/// `findOrCreateQueueEntry` (`tools/ros_cli.mjs`). The actual file bytes are
/// a Tier 4 concern (`Ros.Infrastructure.Work.FileBacklogQueueRepository`
/// reads them and computes `Size`); this module decides everything about
/// the resulting attachment record and item mutation from that observed
/// size onward -- production's `contentType` is always `null` here too,
/// since the real `work attach` CLI command never threads one through
/// (only the separate HTTP upload path, out of scope, ever supplies one).
[<RequireQualifiedAccess>]
module WorkAttachment =
    /// Mirrors production `sanitizeFileComponent`: the last path segment
    /// (POSIX `/`, matching `path.basename` on this platform), trimmed,
    /// with every run of characters outside `[A-Za-z0-9._-]` collapsed to a
    /// single `_`, defaulting to `"file"` when nothing survives.
    let sanitizeFileComponent (value: string) : string =
        let lastSlash = value.LastIndexOf '/'
        let baseName = if lastSlash >= 0 then value.Substring(lastSlash + 1) else value
        let cleaned = Regex.Replace(baseName.Trim(), "[^A-Za-z0-9._-]+", "_")
        if cleaned = "" then "file" else cleaned

    let plan (request: WorkAttachmentRequest) : WorkAttachmentOutcome =
        let buildPlan upsertNew =
            let nextSeq = (0 :: request.ExistingAttachmentSequences) |> List.max |> (+) 1
            let storedFile = $"{nextSeq}-{sanitizeFileComponent request.DisplayName}"

            WorkAttachmentOutcome.Planned
                { UpsertNew = upsertNew
                  Record =
                    { Id = $"ATT-{nextSeq}"
                      Seq = nextSeq
                      Name = request.DisplayName
                      File = storedFile
                      Size = request.Size
                      ContentType = None
                      UploadedAt = request.OccurredAt }
                  UpdatedAt = request.OccurredAt }

        if request.QueueContainsId then
            buildPlan false
        elif not (WorkItemId.isValid request.Id) then
            WorkAttachmentOutcome.Rejected(WorkAttachmentRejection.InvalidId request.Id)
        elif not request.ContextContainsId then
            WorkAttachmentOutcome.Rejected(WorkAttachmentRejection.NotFound request.Id)
        else
            buildPlan true
