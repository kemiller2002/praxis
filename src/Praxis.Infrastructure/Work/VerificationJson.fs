namespace Praxis.Infrastructure.Work

open System
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Work

/// One JSON document (and exit code) for each F1/F3/F4 command:
/// `work verify`, `work complete-verified`, `work completion-identity`,
/// `work claims migrate` and `verifier run`. Exit codes: 0 done, 3 refused
/// or not passed, 1 storage failure.
[<RequireQualifiedAccess>]
module VerificationJson =
    let private render (node: JsonNode) =
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))

    let private str (value: string) : JsonNode = JsonValue.Create value

    let private strings (values: string list) : JsonNode =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(str value))
        array

    let private objectOf (members: (string * JsonNode) list) =
        let node = JsonObject()
        members |> List.iter (fun (name, value) -> node[name] <- value)
        node

    let private refusal (code: string) (detail: string) =
        render (objectOf [ "outcome", str "refused"; "code", str code; "detail", str detail ]), 3

    let private failed (detail: string) = render (objectOf [ "outcome", str "failed"; "detail", str detail ]), 1

    /// `work verify`: compact receipt list (digest, status, duration).
    let verify (outcome: WorkVerifyOutcome) =
        match outcome with
        | WorkVerifyOutcome.Refused finding -> refusal finding.Code finding.Detail
        | WorkVerifyOutcome.Failed reason -> failed reason
        | WorkVerifyOutcome.Verified(request, receipts, diagnostics) ->
            let array = JsonArray()

            for receipt in receipts do
                array.Add(
                    objectOf
                        [ "evidenceId", str receipt.Payload.EvidenceId
                          "validation", str receipt.Payload.Validation
                          "status", str (CheckStatus.code receipt.Payload.Status)
                          "digest", str receipt.Digest
                          "durationMs", JsonValue.Create receipt.Payload.DurationMs
                          "path", str receipt.Path ]
                    :> JsonNode
                )

            let passed = not receipts.IsEmpty && receipts |> List.forall (fun receipt -> receipt.Payload.Status = CheckStatus.Passed)

            render (
                objectOf
                    [ "outcome", str (if passed then "passed" else "not-passed")
                      "workItemId", str request.WorkItemId
                      "generation", JsonValue.Create request.FencingGeneration
                      "commit", str request.Commit
                      "receipts", array
                      "diagnostics", strings diagnostics ]
            ),
            (if passed then 0 else 3)

    /// `work complete-verified`: the claim outcome plus the clean identity
    /// (`verifiedCommit`, `metadataCommit`) and per-step `timings` in ms.
    let completion (report: DerivedCompletionReport) =
        let text, code = WorkClaimOutcomeJson.completionOutcome report.ReportedAt report.Result.Outcome

        let node = JsonNode.Parse(text).AsObject()
        let optional (value: string option) = value |> Option.map str |> Option.toObj
        node["verifiedCommit"] <- optional report.Result.VerifiedCommit
        node["metadataCommit"] <- optional report.Result.MetadataCommit

        match report.Result.Outcome with
        | CompletionOutcome.Completed(_, completion)
        | CompletionOutcome.Replayed(_, completion) -> node["receiptDigests"] <- strings completion.EvidenceDigests
        | _ -> ()

        let timings = JsonObject()

        for step in [ "claim"; "admission"; "receipts"; "dokimos"; "metadataCommit" ] do
            timings[step] <-
                report.Timings
                |> List.filter (fst >> (=) step)
                |> List.map snd
                |> function
                    | [] -> null
                    | values -> JsonValue.Create(List.sum values) :> JsonNode

        node["timings"] <- timings
        node["admissionCache"] <- optional report.AdmissionCache
        render node, code

    let identity (outcome: Result<CompletionIdentityReport, string * string>) =
        match outcome with
        | Error(code, detail) -> refusal code detail
        | Ok report ->
            let consistent = report.SourcePaths.IsEmpty

            render (
                objectOf
                    [ "outcome", str (if consistent then "consistent" else "violated")
                      "code", (if consistent then null else str CompletionIdentity.ViolatedCode)
                      "workItemId", str report.WorkItemId
                      "verifiedCommit", str report.VerifiedCommit
                      "head", str report.Head
                      "changedPaths", strings report.ChangedPaths
                      "sourcePaths", strings report.SourcePaths
                      "clean", JsonValue.Create report.Clean ]
            ),
            (if consistent then 0 else 3)

    let migration (outcome: Result<FileWorkClaimStore.Migration list, string>) =
        match outcome with
        | Error reason -> failed reason
        | Ok items ->
            let array = JsonArray()

            for item in items do
                array.Add(
                    objectOf
                        [ "workItemId", str item.WorkItemId
                          "outcome", str item.Outcome
                          "revisions", JsonValue.Create item.Revisions
                          "validationFiles", JsonValue.Create item.ValidationFiles ]
                    :> JsonNode
                )

            let conflicts = items |> List.exists (fun item -> item.Outcome = "conflict")
            render (objectOf [ "outcome", str (if conflicts then "conflict" else "migrated"); "claims", array ]), (if conflicts then 3 else 0)

    /// `verifier run`: the key a host pins and each receipt's status.
    let verifierRun (outcome: Result<VerifierKey * ValidationPolicy * VerifierResponse, string>) (out: string) =
        match outcome with
        | Error reason -> failed reason
        | Ok(_, _, VerifierResponse.Refused(code, detail)) -> refusal code detail
        | Ok(key, policy, VerifierResponse.Signed(receipts, diagnostics)) ->
            let array = JsonArray()

            for receipt in receipts do
                array.Add(
                    objectOf
                        [ "evidenceId", str receipt.Payload.EvidenceId
                          "status", str (CheckStatus.code receipt.Payload.Status)
                          "digest", str (VerificationReceipt.identity receipt.Envelope.Payload)
                          "durationMs", JsonValue.Create receipt.Payload.DurationMs ]
                    :> JsonNode
                )

            render (
                objectOf
                    [ "outcome", str "signed"
                      "keyId", str key.KeyId
                      "publicKey", str key.PublicKey
                      "policyDigest", str policy.Digest
                      "out", str out
                      "receipts", array
                      "diagnostics", strings diagnostics ]
            ),
            0
