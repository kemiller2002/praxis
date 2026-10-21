namespace Praxis.Infrastructure.Work

open System
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work

/// The `complete-verified` half of `work finish`: its own report, or the
/// invalid-arguments message it would exit 2 with.
type FinishCompletion = Result<DerivedCompletionReport, string>

/// REL-TOKEN-003 composition of `praxis work finish`: the same
/// `WorkVerification.run` that `work verify` runs and the same
/// `FileDerivedCompletion.run` that `work complete-verified` runs, in that
/// order, through `WorkFinish.run`.
[<RequireQualifiedAccess>]
module FileWorkFinish =
    /// `--authority` or `PRAXIS_COMPLETION_AUTHORITY` is set, so the
    /// completion step can run (checked before verification starts).
    let authorityConfigured (arguments: CompletionArguments) =
        arguments.AuthorityPath.IsSome
        || not (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable FileDerivedCompletion.AuthorityVariable))

    let run
        (root: string)
        (choice: ClaimStoreChoice)
        (actor: Actor option)
        (verifyPorts: WorkVerifyPorts)
        (verifier: CompletionVerifier option)
        (arguments: CompletionArguments)
        : FinishOutcome<FinishCompletion> =
        WorkFinish.run
            (verifyPorts.Claims.Read arguments.WorkItemId)
            (fun () -> WorkVerification.run verifyPorts arguments.WorkItemId arguments.Token)
            (fun () -> FileDerivedCompletion.run root choice actor verifier arguments)

/// `work finish` output: one compact line (plus a bounded failure excerpt)
/// by default, or with `--json` one document holding the full `work verify`
/// and `complete-verified` documents. Exit codes are those of the step
/// that ended the run: 0 verified or replayed, 3 refused or not passed,
/// 2 invalid arguments, 1 storage failure.
[<RequireQualifiedAccess>]
module WorkFinishOutput =
    let private short (sha: string) = if sha.Length > 12 then sha.Substring(0, 12) else sha

    let private node (text: string) = JsonNode.Parse text

    let private str (value: string) : JsonNode = JsonValue.Create value

    let private strings (values: string list) : JsonNode =
        JsonArray(values |> List.map str |> Array.ofList) :> JsonNode

    let private render (value: JsonNode) =
        value.ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))

    let private verifyCodes (outcome: WorkVerifyOutcome) =
        match outcome with
        | WorkVerifyOutcome.Refused finding -> [ finding.Code ]
        | _ -> []

    let private completionCodes (completion: FinishCompletion) =
        match completion |> Result.map _.Result.Outcome with
        | Ok(CompletionOutcome.Rejected(findings, _)) -> findings |> List.map _.Code |> List.distinct
        | _ -> []

    let private exitOf (completion: FinishCompletion) =
        match completion with
        | Error _ -> 2
        | Ok report -> snd (VerificationJson.completion report)

    /// The exit code, from the step that ended the run.
    let exitCode (outcome: FinishOutcome<FinishCompletion>) =
        match outcome with
        | FinishOutcome.Stopped verify -> snd (VerificationJson.verify verify)
        | FinishOutcome.Finished(_, completion)
        | FinishOutcome.Replayed completion -> exitOf completion

    let private excerptNode (excerpt: FailureExcerpt) : JsonNode =
        JsonObject(
            dict
                [ "evidenceId", str excerpt.EvidenceId
                  "validation", str excerpt.Validation
                  "status", str (CheckStatus.code excerpt.Status)
                  "exitCode", (excerpt.ExitCode |> Option.map (fun code -> JsonValue.Create code :> JsonNode) |> Option.toObj)
                  "outputPath", str excerpt.OutputPath
                  "firstLine", JsonValue.Create excerpt.FirstLine :> JsonNode
                  "totalLines", JsonValue.Create excerpt.TotalLines :> JsonNode
                  "lines", strings excerpt.Lines ]
        )
        :> JsonNode

    let private completionNode (completion: FinishCompletion) : JsonNode =
        match completion with
        | Ok report -> node (fst (VerificationJson.completion report))
        | Error message -> JsonObject(dict [ "outcome", str "invalid"; "detail", str message ]) :> JsonNode

    let private completionOutcome (completion: FinishCompletion) =
        match completion with
        | Error _ -> "invalid"
        | Ok report ->
            let document = node (fst (VerificationJson.completion report))
            document["outcome"].GetValue<string>()

    /// `--json`: the full structured result.
    let json (workItemId: string) (maxLines: int) (outcome: FinishOutcome<FinishCompletion>) =
        let verifyNode verify = node (fst (VerificationJson.verify verify))

        let verifyOutcome verify =
            let document = verifyNode verify
            document["outcome"].GetValue<string>()

        let outcomeName, step, verify, completion, codes, excerpts =
            match outcome with
            | FinishOutcome.Stopped verify ->
                verifyOutcome verify, "verify", verifyNode verify, null, verifyCodes verify, WorkFinish.excerpts maxLines verify
            | FinishOutcome.Finished(verify, completion) ->
                completionOutcome completion, "complete-verified", verifyNode verify, completionNode completion, completionCodes completion, []
            | FinishOutcome.Replayed completion -> completionOutcome completion, "complete-verified", null, completionNode completion, completionCodes completion, []

        let document = JsonObject()
        document["outcome"] <- str outcomeName
        document["workItemId"] <- str workItemId
        document["step"] <- str step
        document["codes"] <- strings codes
        document["verify"] <- verify
        document["completion"] <- completion
        document["excerpts"] <- JsonArray(excerpts |> List.map excerptNode |> Array.ofList)
        render document, exitCode outcome

    let private findingLines (findings: CompletionFinding list) =
        findings |> List.truncate 10 |> List.map (fun finding -> $"  {finding.Code}: {finding.Detail}")

    let private completionText (workItemId: string) (completion: FinishCompletion) =
        match completion with
        | Error message -> [ $"{workItemId} invalid arguments at complete-verified: {message}" ]
        | Ok report ->
            match report.Result.Outcome with
            | CompletionOutcome.Completed(_, verified)
            | CompletionOutcome.Replayed(_, verified) ->
                let verdict =
                    match report.Result.Outcome with
                    | CompletionOutcome.Replayed _ -> "replayed"
                    | _ -> "verified"

                let metadata = report.Result.MetadataCommit |> Option.map short |> Option.defaultValue "none"
                [ $"{workItemId} {verdict} ({CompletionAttestation.code verified.Attestation}) commit {short verified.Commit} metadata {metadata}" ]
            | CompletionOutcome.Rejected(findings, _) ->
                let codes = findings |> List.map _.Code |> List.distinct |> String.concat ", "
                $"{workItemId} rejected at complete-verified: {codes}" :: findingLines findings
            | CompletionOutcome.Failed reason -> [ $"{workItemId} failed at complete-verified: {reason}" ]

    /// Default output: one line on success; on failure the codes and, for a
    /// not-passed verification, a bounded excerpt per failing receipt.
    let text (workItemId: string) (maxLines: int) (outcome: FinishOutcome<FinishCompletion>) =
        let lines =
            match outcome with
            | FinishOutcome.Stopped(WorkVerifyOutcome.Refused finding) -> [ $"{workItemId} refused at verify: {finding.Code}: {finding.Detail}" ]
            | FinishOutcome.Stopped(WorkVerifyOutcome.Failed reason) -> [ $"{workItemId} failed at verify: {reason}" ]
            | FinishOutcome.Stopped(WorkVerifyOutcome.Verified(request, receipts, _) as verify) ->
                let statuses =
                    receipts
                    |> List.map (fun receipt ->
                        let exit = receipt.Payload.ExitCode |> Option.map (sprintf " (exit %d)") |> Option.defaultValue ""
                        $"{receipt.Payload.EvidenceId} {CheckStatus.code receipt.Payload.Status}{exit}")
                    |> function
                        | [] -> "no receipts"
                        | items -> String.concat ", " items

                let header = $"{workItemId} not-passed at verify (commit {short request.Commit}): {statuses}; nothing was completed"

                let excerpts =
                    WorkFinish.excerpts maxLines verify
                    |> List.collect (fun excerpt ->
                        let range =
                            if excerpt.TotalLines = 0 then "no output"
                            else $"{excerpt.Lines.Length} of {excerpt.TotalLines} lines from line {excerpt.FirstLine}"

                        $"--- {excerpt.EvidenceId} {excerpt.Validation} output, {range} (full: {excerpt.OutputPath}) ---" :: excerpt.Lines)

                header :: excerpts
            | FinishOutcome.Finished(_, completion)
            | FinishOutcome.Replayed completion -> completionText workItemId completion

        String.concat "\n" lines, exitCode outcome
