namespace Praxis.Contracts.Work

open System
open System.Globalization
open System.Text
open System.Text.Json
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module LocalIntegrationJournalJson =
    let renderPlan (plan: LocalIntegrationPlan) =
        JsonSerializer.Serialize
            {| schemaVersion = plan.SchemaVersion; repositoryIdentity = plan.RepositoryIdentity
               dispatchId = plan.DispatchId; attemptId = plan.AttemptId; packetDigest = plan.PacketDigest
               submissionDigest = plan.SubmissionDigest; targetRef = plan.TargetRef
               beforeCommit = plan.BeforeCommit; candidateCommit = plan.CandidateCommit; preparedAt = plan.PreparedAt.ToString("O") |}
    let private read (json: string) names decode =
        try
            if isNull json || UTF8Encoding(false, true).GetByteCount json > 16384 then invalidOp "oversized integration record"
            use document = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = 2))
            let root = document.RootElement
            if root.ValueKind <> JsonValueKind.Object then invalidOp "expected integration object"
            let actual = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            if actual.Length <> List.length names || Set.ofList actual <> Set.ofList names then invalidOp "missing, extra or duplicate integration fields"
            let text name =
                let field = root.GetProperty(name: string)
                if field.ValueKind <> JsonValueKind.String then invalidOp "expected integration string"
                let content = field.GetString()
                if content.Length > 1024 then invalidOp "oversized integration field"
                content
            let time name =
                match DateTimeOffset.TryParseExact(text name, "O", CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, at when at.Offset = TimeSpan.Zero -> at
                | _ -> invalidOp "expected canonical UTC integration time"
            Ok(decode text time)
        with e -> Error("invalid integration record: " + e.GetType().Name)
    let readPlan json =
        read json [ "schemaVersion"; "repositoryIdentity"; "dispatchId"; "attemptId"; "packetDigest"; "submissionDigest"; "targetRef"; "beforeCommit"; "candidateCommit"; "preparedAt" ]
            (fun text time ->
                { SchemaVersion = text "schemaVersion"; RepositoryIdentity = text "repositoryIdentity"
                  DispatchId = text "dispatchId"; AttemptId = text "attemptId"; PacketDigest = text "packetDigest"
                  SubmissionDigest = text "submissionDigest"; TargetRef = text "targetRef"
                  BeforeCommit = text "beforeCommit"; CandidateCommit = text "candidateCommit"; PreparedAt = time "preparedAt" })
        |> Result.bind (fun plan -> LocalIntegrationJournal.validate { Plan = plan; AppliedAt = None } |> Result.map (fun () -> plan))
    let renderApplied plan (at: DateTimeOffset) = JsonSerializer.Serialize {| schemaVersion = "praxis.local-integration-applied/1"; planDigest = LocalWorkerSubmission.digest (Encoding.UTF8.GetBytes(renderPlan plan)); appliedAt = at.ToString("O") |}
    let readApplied plan json =
        read json [ "schemaVersion"; "planDigest"; "appliedAt" ] (fun text time ->
            if text "schemaVersion" <> "praxis.local-integration-applied/1" || text "planDigest" <> LocalWorkerSubmission.digest (Encoding.UTF8.GetBytes(renderPlan plan)) then invalidOp "integration receipt differs from plan"
            time "appliedAt")
        |> Result.bind (fun at -> LocalIntegrationJournal.validate { Plan = plan; AppliedAt = Some at } |> Result.map (fun () -> at))
