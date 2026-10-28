namespace Praxis.Infrastructure.Autonomy

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Autonomy

/// The independent review's inputs, read from a local checkout with git: the
/// candidate's changes against its base, every added line, and the commit
/// messages and pull-request text a requirement trace is looked for in.
[<RequireQualifiedAccess>]
module LocalReview =
    [<Literal>]
    let Schema = "praxis.independent-review/1"

    let private git (root: string) (arguments: string list) : Result<string, string> =
        let info = ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root)
        arguments |> List.iter info.ArgumentList.Add
        use proc = Process.Start info
        let output = proc.StandardOutput.ReadToEnd()
        let error = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        if proc.ExitCode = 0 then Ok output else Error $"""git {String.Join(" ", arguments)}: {error.Trim()}"""

    let private lines (text: string) = text.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

    /// `git diff --name-status -M base...head`.
    let parseNameStatus (text: string) : ChangedPath list =
        lines text
        |> List.choose (fun line ->
            match line.TrimEnd('\r').Split('\t') |> Array.toList with
            | status :: previous :: path :: _ when status.StartsWith "R" -> Some { Path = path; Kind = ChangeKind.Renamed previous }
            | status :: path :: _ when status.StartsWith "A" -> Some { Path = path; Kind = ChangeKind.Added }
            | status :: path :: _ when status.StartsWith "D" -> Some { Path = path; Kind = ChangeKind.Removed }
            | _ :: path :: _ -> Some { Path = path; Kind = ChangeKind.Modified }
            | _ -> None)

    /// Added lines of a zero-context unified diff, with their file.
    let parseAddedLines (diff: string) : (string * string) list =
        diff.Split('\n')
        |> Array.fold
            (fun (file: string option, acc) (line: string) ->
                if line.StartsWith("+++ ", StringComparison.Ordinal) then
                    let target = line.Substring(4).Trim()
                    (if target = "/dev/null" then None else Some(if target.StartsWith "b/" then target.Substring 2 else target)), acc
                elif line.StartsWith("+", StringComparison.Ordinal) then
                    match file with
                    | Some f -> file, (f, line.Substring 1) :: acc
                    | None -> file, acc
                else file, acc)
            (None, [])
        |> snd
        |> List.rev

    /// Pull-request title and body from a GitHub event payload, when given.
    let eventText (path: string) =
        if String.IsNullOrWhiteSpace path || not (File.Exists path) then ""
        else
            match JsonNode.Parse(File.ReadAllText path)["pull_request"] with
            | null -> ""
            | pull -> [ "title"; "body" ] |> List.choose (fun n -> pull[n] |> Option.ofObj |> Option.map string) |> String.concat "\n"

    let inputs (root: string) (baseRev: string) (headRev: string) (extraText: string) (policyProtected: ProtectedPath list) (allowed: string list option) =
        let range = $"{baseRev}...{headRev}"
        git root [ "diff"; "--name-status"; "-M"; range ]
        |> Result.bind (fun nameStatus ->
            git root [ "diff"; "-U0"; "--no-color"; range ]
            |> Result.bind (fun diff ->
                git root [ "log"; "--format=%B"; $"{baseRev}..{headRev}" ]
                |> Result.bind (fun messages ->
                    git root [ "ls-tree"; "-r"; "--name-only"; baseRev ]
                    |> Result.map (fun tree ->
                        let tracked = lines tree |> List.map (fun l -> l.TrimEnd('\r'))
                        let workflows =
                            tracked
                            |> List.filter (fun p -> p.StartsWith(".github/workflows/", StringComparison.Ordinal))
                            |> List.choose (fun p -> match git root [ "show"; $"{baseRev}:{p}" ] with Ok t -> Some t | Error _ -> None)

                        { Changes = parseNameStatus nameStatus
                          AddedLines = parseAddedLines diff
                          TraceText = messages + "\n" + extraText
                          ProtectedPaths = policyProtected
                          CiReferencedPaths = Classification.ciReferencedPaths workflows tracked
                          AllowedPaths = allowed }))))

    let reportNode (baseSha: string) (headSha: string) (report: ReviewReport) (executor: (string * string) list) =
        let findings = JsonArray()
        report.Findings
        |> List.iter (fun f ->
            let node = JsonObject()
            node["severity"] <- JsonValue.Create(ReviewSeverity.toWire f.Severity)
            node["code"] <- JsonValue.Create f.Code
            node["path"] <- (match f.Path with Some p -> JsonValue.Create p :> JsonNode | None -> null)
            node["detail"] <- JsonValue.Create f.Detail
            findings.Add node)

        let node = JsonObject()
        node["schema"] <- JsonValue.Create Schema
        node["baseSha"] <- JsonValue.Create baseSha
        node["candidateSha"] <- JsonValue.Create headSha
        node["verdict"] <- JsonValue.Create(if report.Passed then "pass" else "fail")
        node["criticalDefects"] <- JsonValue.Create report.CriticalDefects
        node["findings"] <- findings
        let reviewer = JsonObject()
        executor |> List.iter (fun (k, v) -> reviewer[k] <- JsonValue.Create v)
        node["reviewer"] <- reviewer
        node

    let resolve (root: string) (rev: string) = git root [ "rev-parse"; "--verify"; rev + "^{commit}" ] |> Result.map (fun s -> s.Trim())
