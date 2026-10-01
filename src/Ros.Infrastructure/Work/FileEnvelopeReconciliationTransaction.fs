namespace Ros.Infrastructure.Work

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Ros.Domain.Work

type EnvelopeTransactionWrite =
    { Path: string
      Content: string }

type EnvelopeTransactionPlan =
    { TransactionId: string
      EnvelopePath: string
      Writes: EnvelopeTransactionWrite list }

type RecoveredEnvelopeTransaction =
    { TransactionId: string
      EnvelopePath: string
      CheckpointTag: string
      Commit: string }

/// A durable, replayable reconciliation boundary. Canonical writes and the
/// applied receipt are journaled before mutation. A transaction is complete
/// only after those exact paths are committed and a deterministic checkpoint
/// tag resolves to that commit. A killed process can therefore resume without
/// dispatching any work transition twice.
[<RequireQualifiedAccess>]
module FileEnvelopeReconciliationTransaction =
    let private schemaVersion = "1.0.0"

    let private hashBytes (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    let private hashText (content: string) = hashBytes (Encoding.UTF8.GetBytes content)

    let private readHash (path: string) =
        if File.Exists path then File.ReadAllBytes path |> hashBytes |> Some else None

    let private writeAtomic (path: string) (content: string) =
        let directory = Path.GetDirectoryName path
        Directory.CreateDirectory directory |> ignore
        let temporary = Path.Combine(directory, $".{Path.GetFileName path}.{Guid.NewGuid():N}.tmp")
        File.WriteAllText(temporary, content, UTF8Encoding(false))
        File.Move(temporary, path, true)

    let private safeId (value: string) = EnvelopeReconciliationReceipt.safeTransactionId value

    let private journalPath (root: string) (transactionId: string) =
        Path.Combine(root, ".praxis", "processing", $"reconcile-{safeId transactionId}.json")

    let private insideRoot (root: string) (path: string) =
        let fullRoot = (Path.GetFullPath root).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
        let fullPath = Path.GetFullPath path
        fullPath.StartsWith(fullRoot, StringComparison.Ordinal)

    let private normalizedRelativePath (root: string) (path: string) =
        Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, path))).Replace('\\', '/')

    let private pathTraversesLink (root: string) (relativePath: string) =
        let parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
        let mutable current = Path.GetFullPath root
        parts
        |> Array.exists (fun part ->
            current <- Path.Combine(current, part)
            (File.Exists current || Directory.Exists current)
            && (File.GetAttributes(current) &&& FileAttributes.ReparsePoint) = FileAttributes.ReparsePoint)

    let private validWritePath (root: string) (path: string) =
        let relative = normalizedRelativePath root path
        insideRoot root (Path.Combine(root, relative))
        && not (pathTraversesLink root relative)
        && (relative = ".ros/context/current.json"
            || relative = ".ros/events/events.jsonl"
            || relative = ".ros/work/queue.json"
            || relative = ".ros/work/queue.md"
            || (relative.StartsWith(".ros/telemetry/executions/", StringComparison.Ordinal) && relative.EndsWith(".json", StringComparison.Ordinal))
            || (relative.StartsWith(".praxis/reconciled/", StringComparison.Ordinal) && relative.EndsWith(".json", StringComparison.Ordinal)))

    type private ProcessResult =
        { ExitCode: int
          Output: string
          Error: string }

    let private git root arguments =
        let info = ProcessStartInfo()
        info.FileName <- "git"
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.ArgumentList.Add "-C"
        info.ArgumentList.Add root
        arguments |> List.iter info.ArgumentList.Add
        use child = new Process(StartInfo = info)
        if not (child.Start()) then Error "git process did not start"
        else
            let output = child.StandardOutput.ReadToEndAsync()
            let error = child.StandardError.ReadToEndAsync()
            child.WaitForExit()
            Ok { ExitCode = child.ExitCode; Output = output.Result.Trim(); Error = error.Result.Trim() }

    let private gitSuccess root operation arguments =
        match git root arguments with
        | Error message -> Error $"{operation}: {message}"
        | Ok result when result.ExitCode <> 0 ->
            let detail = if result.Error.Length > 0 then result.Error else $"git exited with code {result.ExitCode}"
            Error $"{operation}: {detail}"
        | Ok result -> Ok result.Output

    let checkpointExists root transactionId =
        let tag = EnvelopeReconciliationReceipt.checkpointTag transactionId
        match git root [ "rev-parse"; "--verify"; "--quiet"; $"refs/tags/{tag}^{{commit}}" ] with
        | Ok result -> result.ExitCode = 0
        | Error _ -> false

    let checkpointAppliedHash root transactionId =
        let tag = EnvelopeReconciliationReceipt.checkpointTag transactionId
        let receipt = $".praxis/reconciled/{EnvelopeReconciliationReceipt.safeTransactionId transactionId}.json"
        match git root [ "show"; $"{tag}:{receipt}" ] with
        | Ok result when result.ExitCode = 0 ->
            try
                use document = JsonDocument.Parse result.Output
                let root = document.RootElement
                match root.TryGetProperty "transactionId", root.TryGetProperty "checkpointTag", root.TryGetProperty "status" with
                | (true, tx), (true, checkpoint), (true, status)
                    when tx.ValueKind = JsonValueKind.String
                         && checkpoint.ValueKind = JsonValueKind.String
                         && status.ValueKind = JsonValueKind.String ->
                    if tx.GetString() = transactionId && checkpoint.GetString() = tag && status.GetString() = "applied" then
                        match root.TryGetProperty "envelopeHash" with
                        | true, envelopeHash when envelopeHash.ValueKind = JsonValueKind.String -> Some(envelopeHash.GetString())
                        | _ -> None
                    else None
                | _ -> None
            with _ -> None
        | _ -> None

    let checkpointContainsAppliedReceipt root transactionId =
        checkpointAppliedHash root transactionId |> Option.isSome

    type private JournalWrite =
        { Path: string
          BeforeSha256: string option
          AfterSha256: string
          Content: string }

    type private Journal =
        { TransactionId: string
          EnvelopePath: string
          EnvelopeSha256: string option
          CheckpointTag: string
          Writes: JournalWrite list }

    let private serializeJournal (journal: Journal) =
        JsonSerializer.Serialize(
            {| schemaVersion = schemaVersion
               transactionId = journal.TransactionId
               envelopePath = journal.EnvelopePath
               envelopeSha256 = journal.EnvelopeSha256 |> Option.toObj
               checkpointTag = journal.CheckpointTag
               writes =
                journal.Writes
                |> List.map (fun write ->
                    {| path = write.Path
                       beforeSha256 = write.BeforeSha256 |> Option.toObj
                       afterSha256 = write.AfterSha256
                       content = write.Content |}) |},
            JsonSerializerOptions(WriteIndented = true)) + "\n"

    let private parseJournal path =
        try
            use document = JsonDocument.Parse(File.ReadAllText path)
            let root = document.RootElement
            let requiredString (name: string) (element: JsonElement) =
                match element.TryGetProperty name with
                | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                | _ -> None
            let optionalString (name: string) (element: JsonElement) =
                match element.TryGetProperty name with
                | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                | true, value when value.ValueKind = JsonValueKind.Null -> None
                | _ -> None
            let writes =
                match root.TryGetProperty "writes" with
                | true, values when values.ValueKind = JsonValueKind.Array ->
                    values.EnumerateArray()
                    |> Seq.choose (fun value ->
                        match requiredString "path" value, requiredString "afterSha256" value, requiredString "content" value with
                        | Some target, Some after, Some content ->
                            Some { Path = target; BeforeSha256 = optionalString "beforeSha256" value; AfterSha256 = after; Content = content }
                        | _ -> None)
                    |> Seq.toList
                | _ -> []
            match requiredString "schemaVersion" root, requiredString "transactionId" root, requiredString "envelopePath" root, requiredString "checkpointTag" root with
            | Some version, Some transactionId, Some envelopePath, Some checkpointTag
                when version = schemaVersion
                     && not writes.IsEmpty
                     && checkpointTag = EnvelopeReconciliationReceipt.checkpointTag transactionId
                     && (writes |> List.map _.Path |> List.distinct).Length = writes.Length ->
                Ok
                    { TransactionId = transactionId
                      EnvelopePath = envelopePath
                      EnvelopeSha256 = optionalString "envelopeSha256" root
                      CheckpointTag = checkpointTag
                      Writes = writes }
            | _ -> Error "unsupported or malformed reconciliation transaction journal"
        with error -> Error error.Message

    let prepare root (plan: EnvelopeTransactionPlan) =
        try
            let fullRoot = Path.GetFullPath root
            let path = journalPath fullRoot plan.TransactionId
            if pathTraversesLink fullRoot ".praxis/processing" then
                Error "reconciliation journal path traverses a symbolic link"
            elif File.Exists path then Ok path
            elif plan.Writes.IsEmpty then Error "reconciliation transaction has no canonical writes"
            elif plan.Writes |> List.exists (fun write -> not (validWritePath fullRoot write.Path)) then
                Error "reconciliation transaction contains a path outside the canonical allowlist"
            else
                let writes =
                    plan.Writes
                    |> List.map (fun write ->
                        let relative = normalizedRelativePath fullRoot write.Path
                        { Path = relative
                          BeforeSha256 = readHash (Path.Combine(fullRoot, relative))
                          AfterSha256 = hashText write.Content
                          Content = write.Content })
                let envelopePath = Path.GetFullPath plan.EnvelopePath
                let journal =
                    { TransactionId = plan.TransactionId
                      EnvelopePath = envelopePath
                      EnvelopeSha256 = readHash envelopePath
                      CheckpointTag = EnvelopeReconciliationReceipt.checkpointTag plan.TransactionId
                      Writes = writes }
                writeAtomic path (serializeJournal journal)
                Ok path
        with error -> Error error.Message

    let private findCommittedTransaction root transactionId =
        match git root [ "log"; "-1"; "HEAD"; "--fixed-strings"; $"--grep=Praxis-Reconciliation: {transactionId}"; "--format=%H" ] with
        | Ok result when result.ExitCode = 0 && result.Output.Length > 0 -> Some result.Output
        | _ -> None

    let private commitHasPath root commit relativePath =
        match git root [ "ls-tree"; "--name-only"; commit; "--"; relativePath ] with
        | Ok result -> result.ExitCode = 0 && result.Output = relativePath
        | Error _ -> false

    let private commitMatchesWorkingState root commit writtenPaths =
        let writtenPathsMatch =
            writtenPaths
            |> List.forall (fun path ->
                commitHasPath root commit path
                && match git root [ "diff"; "--quiet"; commit; "--"; path ] with
                   | Ok result -> result.ExitCode = 0
                   | Error _ -> false)
        writtenPathsMatch

    let private trackedPath root relativePath =
        match git root [ "ls-files"; "--error-unmatch"; "--"; relativePath ] with
        | Ok result -> result.ExitCode = 0
        | Error _ -> false

    let private tagCommit root tag commit =
        match git root [ "rev-parse"; "--verify"; "--quiet"; $"refs/tags/{tag}^{{commit}}" ] with
        | Ok existing when existing.ExitCode = 0 && existing.Output = commit -> Ok()
        | Ok existing when existing.ExitCode = 0 -> Error $"checkpoint tag '{tag}' already points to a different commit"
        | _ -> gitSuccess root "create reconciliation checkpoint" [ "tag"; tag; commit ] |> Result.map ignore

    let private recoverJournal root journalPath =
        match parseJournal journalPath with
        | Error message -> Error message
        | Ok journal ->
            let envelopeHash = readHash journal.EnvelopePath
            let envelopeConflict =
                match journal.EnvelopeSha256, envelopeHash with
                | Some expected, Some actual -> expected <> actual
                | None, Some _ -> true
                | _ -> false
            let invalidWrite =
                journal.Writes
                |> List.tryFind (fun write ->
                    let current = readHash (Path.Combine(root, write.Path))
                    not (validWritePath root write.Path)
                    || hashText write.Content <> write.AfterSha256
                    || (current <> write.BeforeSha256 && current <> Some write.AfterSha256))
            match invalidWrite with
            | Some write -> Error $"reconciliation target changed after preparation: {write.Path}"
            | None when envelopeConflict -> Error "accepted envelope changed while reconciliation was in progress"
            | None ->
                try
                    for write in journal.Writes do
                        let target = Path.Combine(root, write.Path)
                        if readHash target <> Some write.AfterSha256 then writeAtomic target write.Content

                    let writtenPaths = journal.Writes |> List.map _.Path

                    let commitResult =
                        match findCommittedTransaction root journal.TransactionId with
                        | Some commit when commitMatchesWorkingState root commit writtenPaths -> Ok commit
                        | Some _
                        | None ->
                            match gitSuccess root "stage reconciled canonical state" ([ "add"; "--" ] @ writtenPaths) with
                            | Error message -> Error message
                            | Ok _ ->
                                let subject = $"reconcile({journal.TransactionId}): apply fallback envelope"
                                match gitSuccess root "commit reconciled canonical state" ([ "commit"; "--only"; "-m"; subject; "-m"; $"Praxis-Reconciliation: {journal.TransactionId}"; "--" ] @ writtenPaths) with
                                | Error message -> Error message
                                | Ok _ -> gitSuccess root "read reconciliation commit" [ "rev-parse"; "HEAD" ]

                    match commitResult with
                    | Error message -> Error message
                    | Ok commit ->
                        match tagCommit root journal.CheckpointTag commit with
                        | Error message -> Error message
                        | Ok() ->
                            // The checkpoint is the durability boundary. Only
                            // after it exists may accepted input be removed.
                            let localEnvelope = insideRoot root journal.EnvelopePath
                            let localEnvelopeRelative =
                                if localEnvelope then Some(normalizedRelativePath root journal.EnvelopePath) else None
                            let trackedEnvelope = localEnvelopeRelative |> Option.exists (trackedPath root)
                            let envelopeResult =
                                match journal.EnvelopeSha256, readHash journal.EnvelopePath with
                                | Some expected, Some actual when expected <> actual ->
                                    Error "accepted envelope changed while reconciliation was in progress"
                                | _, Some _ ->
                                    File.Delete journal.EnvelopePath
                                    match trackedEnvelope, localEnvelopeRelative with
                                    | true, Some relative ->
                                        match gitSuccess root "stage accepted envelope removal" [ "add"; "--"; relative ] with
                                        | Error message -> Error message
                                        | Ok _ ->
                                            gitSuccess root "commit accepted envelope removal" [ "commit"; "--only"; "-m"; $"reconcile({journal.TransactionId}): remove accepted envelope"; "--"; relative ]
                                            |> Result.map ignore
                                    | _ -> Ok()
                                | _ -> Ok()
                            match envelopeResult with
                            | Error message -> Error message
                            | Ok() ->
                                File.Delete journalPath
                                Ok
                                    { TransactionId = journal.TransactionId
                                      EnvelopePath = journal.EnvelopePath
                                      CheckpointTag = journal.CheckpointTag
                                      Commit = commit }
                with error -> Error error.Message

    let recoverPending root =
        try
            let fullRoot = Path.GetFullPath root
            let directory = Path.Combine(fullRoot, ".praxis", "processing")
            if pathTraversesLink fullRoot ".praxis/processing" then
                Error "reconciliation journal path traverses a symbolic link"
            elif not (Directory.Exists directory) then Ok []
            else
                let journals = Directory.EnumerateFiles(directory, "reconcile-*.json") |> Seq.sort |> Seq.toList
                let tracked =
                    journals
                    |> List.tryFind (fun path -> trackedPath fullRoot (normalizedRelativePath fullRoot path))
                match tracked with
                | Some path -> Error $"tracked reconciliation journal is untrusted: {normalizedRelativePath fullRoot path}"
                | None ->
                    ((Ok []), journals)
                    ||> List.fold (fun state path ->
                        match state with
                        | Error message -> Error message
                        | Ok recovered -> recoverJournal fullRoot path |> Result.map (fun value -> recovered @ [ value ]))
        with error -> Error error.Message

    let apply root plan =
        match prepare root plan with
        | Error message -> Error message
        | Ok _ ->
            recoverPending root
            |> Result.bind (fun recovered ->
                recovered
                |> List.tryFind (fun value -> value.TransactionId = plan.TransactionId)
                |> Option.map Ok
                |> Option.defaultValue (Error "reconciliation transaction did not produce a checkpoint"))
