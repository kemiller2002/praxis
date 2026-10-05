namespace Praxis.Cli

open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// CLI adapter for completion readiness (PRX-QUAL-023): runs the gate and
/// renders its outcome. Policy, decoding and decisions live below the CLI.
[<RequireQualifiedAccess>]
module QualityEvidenceCommands =
    /// Exit code for a refused completion: the documented "verification
    /// failed" contract.
    let verificationFailed = 3

    /// `Ok records` (empty when the repository has not opted in) to write on
    /// the completion event and item, or `Error exitCode` after reporting a
    /// refusal: the readiness record on stdout and one line per blocking
    /// facet on stderr.
    let completionGate root (ids: string list) (provided: WorkEvidence list) : Result<Map<string, JsonObject>, int> =
        match FileCompletionReadiness.evaluate root ids provided with
        | CompletionGateOutcome.NotApplicable -> Ok Map.empty
        | CompletionGateOutcome.Ready _ as outcome -> Ok(FileCompletionReadiness.extensions outcome)
        | CompletionGateOutcome.PolicyInvalid reason ->
            eprintfn "ERROR workProtocol.qualityEvidence in ros.json is invalid; completion cannot be verified: %s" reason
            Error verificationFailed
        | CompletionGateOutcome.Refused items ->
            printfn "%s" (FileCompletionReadiness.refusalDocument items)

            for item in items |> List.filter (CompletionReadiness.isReady >> not) do
                for reason in CompletionReadiness.blockingReasons item do
                    eprintfn "ERROR completion readiness refused for '%s': %s" item.WorkItemId reason

            Error verificationFailed
