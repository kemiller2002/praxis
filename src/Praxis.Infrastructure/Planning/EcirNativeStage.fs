namespace Praxis.Infrastructure.Planning

open System
open System.Diagnostics
open System.IO
open System.Text.Json

/// Keeps the staged native effects alive until the host accepts or refuses
/// their write set. Disposal discards the stage; it never touches the repo.
type EcirNativeStageResult internal (directory: string, writes: EcirDispatchWrite list) =
    member _.Writes = writes
    interface IDisposable with
        member _.Dispose() = if Directory.Exists directory then Directory.Delete(directory, true)

[<RequireQualifiedAccess>]
module EcirNativeStage =
    let private git (root: string) (arguments: string list) =
        let info = ProcessStartInfo("git")
        info.WorkingDirectory <- root
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        arguments |> List.iter info.ArgumentList.Add
        use child = new Process(StartInfo = info)
        if not (child.Start()) then Error "cannot start Git for ECIR staging"
        else
            let output = child.StandardOutput.ReadToEndAsync()
            let diagnostics = child.StandardError.ReadToEndAsync()
            if not (child.WaitForExit 30000) then
                child.Kill(true)
                Error "ECIR staging Git operation timed out"
            elif child.ExitCode <> 0 then Error("ECIR staging Git refused: " + diagnostics.Result.Trim())
            else Ok(output.Result.Trim())

    let private files (root: string) =
        let rec walk directory =
            Directory.EnumerateFileSystemEntries directory
            |> Seq.collect (fun path ->
                let relative = Path.GetRelativePath(root, path).Replace('\\', '/')
                if relative = ".git" || relative = ".ros/locks" then Seq.empty
                elif FileInfo(path).LinkTarget <> null || DirectoryInfo(path).LinkTarget <> null then
                    invalidOp("ECIR staging refuses symbolic links: " + relative)
                elif Directory.Exists path then walk path
                else Seq.singleton(relative, File.ReadAllBytes path))
        walk root |> Map.ofSeq

    /// The caller holds work-protocol then work-groups locks. Invoke the
    /// EXISTING native work-begin and group-record effects in the isolated root;
    /// no alternative member lifecycle is implemented here. Host authority is
    /// revalidated after this returns and before its writes can be committed.
    let prepare
        (root: string)
        (memberId: string)
        (evidence: EcirDispatchEvidence)
        (beginMember: string -> Result<unit, string>)
        (recordGroup: string -> string -> Result<unit, string>) =
        let directory = Path.Combine(Path.GetTempPath(), "praxis-ecir-native-" + Guid.NewGuid().ToString("N"))
        let cleanup () = if Directory.Exists directory then Directory.Delete(directory, true)
        try
            let before = files root
            if before |> Map.exists (fun path _ -> path.StartsWith(".ros/transactions/", StringComparison.Ordinal)) then
                Error "ECIR staging requires all existing repository transactions recovered"
            else
                // An independent Git directory preserves HEAD/branch;
                // native telemetry observes the same actual source worktree.
                git root [ "clone"; "--shared"; "--no-checkout"; "--"; Path.GetFullPath root; directory ]
                |> Result.bind (fun _ ->
                    git directory [ "reset"; "--mixed"; "HEAD" ]
                    |> Result.bind (fun _ ->
                        git root [ "remote"; "get-url"; "origin" ]
                        |> Result.bind (fun origin ->
                            git directory [ "remote"; "set-url"; "origin"; origin ])))
                |> Result.bind (fun _ ->
                    for KeyValue(relative, bytes) in before do
                        let target = Path.Combine(directory, relative)
                        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                        File.WriteAllBytes(target, bytes)
                        if not (OperatingSystem.IsWindows()) then File.SetUnixFileMode(target, File.GetUnixFileMode(Path.Combine(root, relative)))
                    beginMember directory
                    |> Result.bind (fun () ->
                        let execution = FileGroupExecution.memberExecution directory memberId
                        match execution with
                        | None -> Error "native ECIR member begin did not produce a telemetry execution"
                        | Some executionId ->
                            recordGroup directory executionId
                            |> Result.bind (fun () ->
                                let receiptPath = ".ros/work/ecir-dispatches/" + executionId + ".json"
                                let target = Path.Combine(directory, receiptPath)
                                Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                                File.WriteAllText(target, JsonSerializer.Serialize
                                    {| schemaVersion = "ecir.dispatch-evidence/1"; memberId = memberId; executionId = executionId
                                       groupId = evidence.Approval.GroupId; cohortId = evidence.Approval.CohortId
                                       policyRevision = evidence.PolicyRevision; verifiedAt = evidence.VerifiedAt
                                       approver = evidence.Approval.Approver; keyId = evidence.Approval.KeyId
                                       sourceCommit = evidence.Approval.SourceCommit; manifestDigest = evidence.Approval.ManifestDigest
                                       blueprintDigest = evidence.Approval.BlueprintDigest
                                       requirementKeys = evidence.Approval.RequirementKeys; decisionIds = evidence.Approval.DecisionIds
                                       expiresAt = evidence.Approval.ExpiresAt
                                       validatorSha256 = evidence.Validator.ValidatedByExecutableSha256 |})
                                let after = files directory
                                let deleted = before |> Map.exists (fun path _ -> not (after.ContainsKey path))
                                let changed = after |> Map.toList |> List.filter (fun (path, bytes) -> (before |> Map.tryFind path) <> Some bytes)
                                if deleted || changed |> List.exists (fun (path, _) -> not (path.StartsWith(".ros/", StringComparison.Ordinal))) then
                                    Error "native ECIR staging changed source files or deleted state"
                                elif not (before = files root) then Error "repository changed while native ECIR effects were staged"
                                else
                                    let writes =
                                        changed |> List.map (fun (path, bytes) ->
                                            { Path = path
                                              BeforeSha256 = before |> Map.tryFind path |> Option.map EcirDispatchTransaction.hash
                                              Content = Text.Encoding.UTF8.GetString bytes })
                                    Ok(new EcirNativeStageResult(directory, writes)))))
                |> fun result ->
                    if Result.isError result then cleanup()
                    result
        with e ->
            cleanup()
            Error("native ECIR staging refused: " + e.Message)
