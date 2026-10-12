namespace Praxis.Infrastructure.Work

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Praxis.Contracts.Work
open Praxis.Domain.Work

/// A replaceable model backend. `Command` runs an installed agent CLI in the
/// worker checkout (for example a Codex, Claude Code or other agent command
/// the operator has installed and authenticated). `Ollama` asks a local
/// Ollama server for complete file contents. Neither is trusted: the bridge
/// only observes what changed, and the controller re-observes everything.
[<RequireQualifiedAccess>]
type LocalBridgeBackend =
    | Command of executable: string * arguments: string list
    | Ollama of endpoint: Uri * model: string

type LocalBridgeSettings = { Git: string; Backend: LocalBridgeBackend; Worktree: string }

/// Worker-side adapter run by the controller as the worker process. Reads the
/// canonical packet on stdin, lets the backend change files, commits only
/// allowed paths, and writes the canonical result on stdout. Its result is a
/// claim; the controller independently observes commits, paths and evidence.
[<RequireQualifiedAccess>]
module LocalWorkerBridge =
    let private excerptLimit = 16384

    let private log (message: string) = eprintfn "praxis worker bridge: %s" message

    let private bounded (text: string) limit = if text.Length > limit then text.Substring(0, limit) + "\n[truncated]" else text

    let private exec (executable: string) (arguments: string list) (cwd: string) (environment: (string * string) list) (stdin: string option) (timeout: TimeSpan) =
        let info = ProcessStartInfo(executable, UseShellExecute = false, WorkingDirectory = cwd,
                                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                                    StandardOutputEncoding = UTF8Encoding(false), StandardErrorEncoding = UTF8Encoding(false))
        arguments |> List.iter info.ArgumentList.Add
        environment |> List.iter (fun (key, value) -> info.Environment[key] <- value)
        try
            use child = Process.Start info
            let output = child.StandardOutput.ReadToEndAsync()
            let error = child.StandardError.ReadToEndAsync()
            match stdin with
            | Some text -> child.StandardInput.Write text
            | None -> ()
            child.StandardInput.Close()
            if not (child.WaitForExit timeout) then
                child.Kill true
                child.WaitForExit()
                Error(sprintf "%s timed out" (Path.GetFileName executable))
            else Ok(child.ExitCode, output.Result, error.Result)
        with error -> Error(sprintf "%s could not run: %s" (Path.GetFileName executable) error.Message)

    let private git settings arguments =
        exec settings.Git ([ "-c"; "core.hooksPath=" + Path.Combine(Path.GetTempPath(), "praxis-no-hooks-" + Guid.NewGuid().ToString("N")) ] @ arguments)
            settings.Worktree [] None (TimeSpan.FromMinutes 2.)
        |> Result.bind (fun (code, output, error) -> if code = 0 then Ok output else Error(sprintf "git %s failed: %s" (List.head arguments) (bounded error 512)))

    let private sha (text: string) = "sha256:" + (UTF8Encoding(false).GetBytes text |> SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()

    let private inside (settings: LocalBridgeSettings) (relative: string) =
        let full = Path.GetFullPath(Path.Combine(settings.Worktree, relative))
        let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath settings.Worktree) + string Path.DirectorySeparatorChar
        if LocalAgentHandoff.isResourcePath relative && full.StartsWith(root, StringComparison.Ordinal) && LocalHostJournalFiles.noLinks full then Some full else None

    let private readExcerpt settings (relative: string) =
        match inside settings relative with
        | Some path when File.Exists path -> Some(bounded (File.ReadAllText path) excerptLimit)
        | _ -> None

    /// The prompt carries the assignment, the referenced requirement text and
    /// the allowed files. It grants nothing: validators decide acceptance.
    let prompt settings (packet: LocalWorkerPacket) jsonFiles =
        let buffer = StringBuilder()
        let line (text: string) = buffer.AppendLine text |> ignore
        line "You are a delegated worker in a Praxis local coordinator/worker run."
        line (sprintf "Work only in the current directory, a Git checkout of commit %s." packet.SourceCommit)
        line (sprintf "Dispatch %s, role %s, worker %s." packet.DispatchId (Praxis.Domain.Execution.ExecutionRole.toWire packet.Role) packet.WorkerId)
        line ""
        line "Requirements you must satisfy:"
        for item in packet.Members do
            for key in item.RequirementKeys do
                line (sprintf "- %s: %s" item.WorkItemId key)
                let document = key.Split('#').[0]
                match readExcerpt settings document with
                | Some text -> line (sprintf "  Text of %s:\n%s" document text)
                | None -> ()
        line ""
        line "You may create or modify only these files (exact paths):"
        for path in packet.AllowedPaths do
            match readExcerpt settings path with
            | Some text -> line (sprintf "- %s (current content follows)\n%s" path text)
            | None -> line (sprintf "- %s (does not exist yet)" path)
        line ""
        line "Rules: change no other file; do not run git commit; do not run praxis or ros commands;"
        line "do not claim that checks passed. Independent validators build, test and check the result."
        if jsonFiles then
            line ""
            line "Reply with only a JSON object: {\"files\":[{\"path\":\"<allowed path>\",\"content\":\"<complete new file content>\"}]}"
        buffer.ToString()

    let private ollama settings (endpoint: Uri) model (packet: LocalWorkerPacket) (timeout: TimeSpan) =
        try
            use client = new HttpClient(Timeout = timeout)
            let request = JsonObject()
            request["model"] <- JsonValue.Create(model: string)
            request["prompt"] <- JsonValue.Create(prompt settings packet true)
            request["stream"] <- JsonValue.Create false
            request["format"] <- JsonValue.Create "json"
            use content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json")
            let target = Uri(endpoint, "/api/generate")
            use response = client.PostAsync(target, content).GetAwaiter().GetResult()
            let body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if not response.IsSuccessStatusCode then Error(sprintf "Ollama returned %d: %s" (int response.StatusCode) (bounded body 512))
            else
                let envelope = JsonNode.Parse body
                let reply = envelope["response"].GetValue<string>()
                let proposal = JsonNode.Parse reply
                let files = proposal["files"] :?> JsonArray
                files |> Seq.fold (fun acc file ->
                    acc |> Result.bind (fun () ->
                        let path = file["path"].GetValue<string>()
                        let text = file["content"].GetValue<string>()
                        match inside settings path with
                        | Some full when List.contains path packet.AllowedPaths ->
                            Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
                            File.WriteAllText(full, text, UTF8Encoding(false))
                            Ok()
                        | _ -> Error("model proposed a path outside the delegation: " + path))) (Ok())
        with error -> Error("Ollama backend failed: " + error.Message)

    let private backend settings (packet: LocalWorkerPacket) (timeout: TimeSpan) =
        match settings.Backend with
        | LocalBridgeBackend.Ollama(endpoint, model) -> ollama settings endpoint model packet timeout
        | LocalBridgeBackend.Command(executable, arguments) ->
            let text = prompt settings packet false
            let expanded = arguments |> List.map (fun value -> value.Replace("{prompt}", text).Replace("{worktree}", settings.Worktree))
            let stdin = if arguments |> List.exists (fun value -> value.Contains "{prompt}") then None else Some text
            exec executable expanded settings.Worktree [] stdin timeout
            |> Result.bind (fun (code, output, error) ->
                log (bounded (output + error) 2048)
                if code = 0 then Ok() else Error(sprintf "agent command exited %d" code))

    /// Changed, untracked and deleted paths in the worker checkout.
    let private changes settings =
        git settings [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=all"; "--no-renames" ]
        |> Result.map (fun output ->
            output.Split('\u0000', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun entry -> entry.Substring 3) |> Array.toList |> List.distinct |> List.sort)

    let private result (packet: LocalWorkerPacket) outcome output (paths: string list) evidence : LocalWorkerResult =
        { SchemaVersion = LocalAgentHandoff.ResultSchema; DispatchId = packet.DispatchId; AttemptId = packet.AttemptId
          ChildExecutionId = packet.ChildExecutionId; WorkerId = packet.WorkerId
          PacketDigest = LocalAgentHandoff.packetDigest packet; OutputCommit = output; ChangedPaths = paths
          Members = packet.Members |> List.map (fun item ->
              { WorkItemId = item.WorkItemId; ExecutionId = item.ExecutionId; ChangedPaths = paths
                Outcome = outcome; Evidence = evidence |> List.filter (fun claim -> packet.Acceptance |> List.exists (fun a -> a.ObligationId = claim.ObligationId && a.WorkItemId = item.WorkItemId)) }) }

    /// Run one assignment. Multi-member packets are refused (blocked) because
    /// this bridge cannot attribute one backend's edits to separate members.
    let run (settings: LocalBridgeSettings) (packetJson: string) =
        match LocalAgentHandoffJson.readPacket packetJson with
        | Error error -> Error("invalid packet: " + string error)
        | Ok packet ->
            let source = packet.SourceCommit
            let unchanged outcome = result packet outcome source [] []
            if packet.Members.Length <> 1 then
                log "multi-member packets need a bridge that can attribute changes per member"
                Ok(unchanged LocalWorkerOutcome.Blocked)
            else
                let timeout = TimeSpan.FromSeconds(float (max 10 (packet.TimeoutSeconds - 30)))
                match backend settings packet timeout with
                | Error error ->
                    log error
                    Ok(unchanged LocalWorkerOutcome.Blocked)
                | Ok() ->
                    changes settings |> Result.bind (fun paths ->
                        if paths.IsEmpty then
                            log "the backend changed no files"
                            Ok(unchanged LocalWorkerOutcome.Failed)
                        elif paths |> List.exists (fun path -> not (List.contains path packet.AllowedPaths)) then
                            log ("the backend changed files outside the delegation: " + String.concat ", " (paths |> List.filter (fun path -> not (List.contains path packet.AllowedPaths))))
                            Ok(unchanged LocalWorkerOutcome.Failed)
                        else
                            git settings ([ "add"; "--" ] @ paths) |> Result.bind (fun _ ->
                                git settings [ "-c"; "user.name=" + packet.WorkerId; "-c"; "user.email=" + packet.WorkerId + "@workers.praxis.invalid"
                                               "commit"; "--no-verify"; "-q"; "-m"; sprintf "Worker output for %s (%s)" packet.DispatchId packet.WorkerId ]
                                |> Result.bind (fun _ -> git settings [ "rev-parse"; "HEAD" ])
                                |> Result.bind (fun head ->
                                    let output = head.Trim()
                                    // The evidence artifact for each obligation is the
                                    // member's first changed file; validators judge it.
                                    let artifact = paths |> List.find (fun path -> paths |> List.forall (fun other -> String.CompareOrdinal(path, other) <= 0))
                                    let present = git settings [ "cat-file"; "-t"; output + ":" + artifact ] |> Result.map (fun kind -> kind.Trim() = "blob")
                                    match present with
                                    | Ok true ->
                                        git settings [ "cat-file"; "blob"; output + ":" + artifact ] |> Result.map (fun content ->
                                            let digest = sha content
                                            let evidence = packet.Acceptance |> List.map (fun obligation ->
                                                { ObligationId = obligation.ObligationId; ArtifactPath = artifact; Digest = digest })
                                            result packet LocalWorkerOutcome.Submitted output paths evidence)
                                    | _ ->
                                        log "the only changes were deletions; no artifact can carry acceptance evidence"
                                        Ok(result packet LocalWorkerOutcome.Failed output paths []))))
