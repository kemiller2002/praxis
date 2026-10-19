namespace Praxis.Infrastructure.Work

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work

/// Composition of the `work validate-run` ports for one repository root
/// (PRAXIS-REL-005): Git HEAD and worktree observation, a non-shell child
/// process with a timeout, and result files under
/// `.ros/claims/<ID>/validations/<EVIDENCE-ID>/`.
[<RequireQualifiedAccess>]
module FileValidationRuns =
    /// Paths Praxis itself writes during work; they never make a run dirty.
    let private ignoredPrefixes = [ ".ros/"; ".praxis/" ]

    /// Tracked paths that differ from HEAD (staged or not), from
    /// `git status --porcelain=v1 -z --untracked-files=no`, outside `.ros/`
    /// and `.praxis/`.
    let dirtyPaths (root: string) () =
        match ClaimProcess.run "git" root [] None FileWorkClaims.gitTimeout [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=no" ] with
        | Ok result when result.ExitCode = 0 ->
            let rec entries (tokens: string list) =
                match tokens with
                | entry :: rest when entry.Length > 3 ->
                    let path = entry.Substring 3
                    // A rename or copy is followed by its source path.
                    let rest = if entry[0] = 'R' || entry[0] = 'C' then List.tail rest else rest
                    path :: entries rest
                | _ :: rest -> entries rest
                | [] -> []

            result.Stdout.Split('\000', StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
            |> entries
            |> List.filter (fun path -> not (ignoredPrefixes |> List.exists (fun prefix -> path.StartsWith(prefix, StringComparison.Ordinal))))
            |> Ok
        | Ok result -> Error $"cannot inspect the worktree: {result.Stderr.Trim()}"
        | Error _ -> Error "cannot inspect the worktree: git is unavailable"

    /// Runs the command (arguments as a list, never through a shell) in the
    /// repository root. Output is stdout followed by stderr.
    let runCommand (root: string) (command: string list) (timeout: TimeSpan) =
        match command with
        | [] -> CommandRun.NotStarted "no command"
        | executable :: arguments ->
            match ClaimProcess.run executable root [] None timeout arguments with
            | Ok result -> CommandRun.Exited(result.ExitCode, result.Stdout + result.Stderr)
            | Error(ClaimProcessFailure.TimedOut after) -> CommandRun.TimedOut after
            | Error(ClaimProcessFailure.NotStarted reason) -> CommandRun.NotStarted reason

    let directory (root: string) (workItemId: string) (evidenceId: string) =
        Path.Combine(FileWorkClaimStore.itemDirectory root workItemId, "validations", evidenceId)

    /// `<generation>-<revision[0..11]>-<observedAt>`: one file per run, so a
    /// later run never overwrites a result an event already recorded.
    let baseName (result: ValidationResult) =
        let revision = if result.Revision.Length > 12 then result.Revision.Substring(0, 12) else result.Revision
        let observed = result.ObservedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
        $"{result.Generation}-{revision}-{observed}"

    let private relative (root: string) (path: string) =
        Path.GetRelativePath(root, path).Replace('\\', '/')

    let private publish (path: string) (bytes: byte array) =
        let temporary = $"{path}.{Guid.NewGuid():N}.tmp"
        File.WriteAllBytes(temporary, bytes)
        File.Move(temporary, path, false)

    let record (root: string) (result: ValidationResult) (output: string) =
        if not (WorkItemId.isValid result.WorkItemId) then
            Error $"'{result.WorkItemId}' is not a work-item id"
        else
            try
                let folder = directory root result.WorkItemId result.EvidenceId
                Directory.CreateDirectory folder |> ignore
                let name = baseName result
                let outputPath = Path.Combine(folder, name + ".output.txt")
                let resultPath = Path.Combine(folder, name + ".json")
                let document = UTF8Encoding(false).GetBytes(ValidationResultJson.render result)
                publish outputPath (UTF8Encoding(false).GetBytes output)
                publish resultPath document

                Ok
                    { ResultPath = relative root resultPath
                      OutputPath = relative root outputPath
                      ResultDigest = CompletionGate.sha256 document }
            with error ->
                Error error.Message

    let ports (root: string) (choice: ClaimStoreChoice) (actor: Actor option) (execution: string option) : ValidationRunPorts =
        { Claims = FileWorkClaims.ports root choice actor execution
          ObserveHead = FileWorkClaims.observeHead root
          DirtyPaths = dirtyPaths root
          RunCommand = runCommand root
          Record = record root }

/// `praxis work validate-run` result document and exit code: 0 the run
/// passed, 3 it failed, was unavailable or was refused, 1 a storage or
/// event failure.
[<RequireQualifiedAccess>]
module ValidationRunOutcomeJson =
    let render (outcome: ValidationRunOutcome) =
        let node = JsonObject()
        let set (name: string) (value: JsonNode) = node[name] <- value

        let code =
            match outcome with
            | ValidationRunOutcome.Observed(result, recorded) ->
                set "outcome" (JsonValue.Create "observed")
                set "status" (JsonValue.Create(CheckStatus.code result.Status))
                set "resultPath" (JsonValue.Create recorded.ResultPath)
                set "resultDigest" (JsonValue.Create recorded.ResultDigest)
                set "outputPath" (JsonValue.Create recorded.OutputPath)
                set "result" (ValidationResultJson.node result)
                if result.Status = CheckStatus.Passed then 0 else 3
            | ValidationRunOutcome.Refused(finding, _) ->
                set "outcome" (JsonValue.Create "refused")
                set "code" (JsonValue.Create finding.Code)
                set "detail" (JsonValue.Create finding.Detail)
                3
            | ValidationRunOutcome.Failed reason ->
                set "outcome" (JsonValue.Create "failed")
                set "detail" (JsonValue.Create reason)
                1

        node.ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)), code
