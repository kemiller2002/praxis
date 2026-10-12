namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Xml
open System.Xml.Linq
open Praxis.Application.Work
open Praxis.Domain.Work

/// Concrete portable validation transport. The controller must provision a
/// fresh candidate checkout and protect tool/input/evidence roots from workers.
/// This does not claim OS containment or enable autonomous acceptance.
[<RequireQualifiedAccess>]
module FileLocalValidator =
    let private digest (bytes: byte array) = "sha256:" + (SHA256.HashData bytes |> Convert.ToHexString).ToLowerInvariant()
    let private framed values =
        let buffer = StringBuilder()
        for value: string in values do buffer.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value).Append('|') |> ignore
        Encoding.UTF8.GetBytes(buffer.ToString()) |> digest

    let private fileDigest (path: string) =
        if not (Path.IsPathFullyQualified path && LocalHostJournalFiles.noLinks path) then invalidOp "validator input path is relative or linked"
        use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        "sha256:" + (SHA256.HashData input |> Convert.ToHexString).ToLowerInvariant()

    let private inputs (request: LocalValidatorRequest) =
        if String.IsNullOrWhiteSpace request.ValidatorId || request.ValidatorId.Length > 1024
           || request.ValidatorId <> request.ValidatorId.Trim() || (request.ValidatorId |> Seq.exists Char.IsControl)
           || not (LocalAgentHandoff.isCommit request.CandidateCommit) || request.Dependencies.Count > 256
           || request.Process.MaxOutputBytes > 1048576 then
            invalidOp "invalid validator identity, candidate or input closure"
        if fileDigest request.GitExecutable <> request.GitExecutableDigest || fileDigest request.Process.Executable <> request.Process.ExecutableDigest then
            invalidOp "validator tool pin changed"
        for KeyValue(path, expected) in request.Dependencies do
            if not (LocalAgentHandoff.isDigest expected) || fileDigest path <> expected then invalidOp "validator dependency pin changed"
        if not (LocalHostJournalFiles.noLinks request.Process.WorkingDirectory) then invalidOp "validator checkout is linked"
        framed ([ "praxis.local-validator-inputs/1"; request.ValidatorId; request.GitExecutable; request.GitExecutableDigest
                  request.Process.Executable; request.Process.ExecutableDigest; request.Process.WorkingDirectory
                  string request.Process.TimeoutMilliseconds; string request.Process.MaxOutputBytes; request.Process.Input
                  request.TrxPath |> Option.defaultValue ""; string request.Process.Arguments.Length ]
                @ request.Process.Arguments @ [ string request.Process.Environment.Count ]
                @ (request.Process.Environment |> Map.toList |> List.collect (fun (key, value) -> [ key; value ]))
                @ [ string request.Dependencies.Count ]
                @ (request.Dependencies |> Map.toList |> List.collect (fun (path, value) -> [ path; value ])))

    let private run spec cancellation =
        LocalWorkerProcess.runControllerTool spec cancellation

    let private git request arguments cancellation = task {
        let spec =
            { request.Process with Executable = request.GitExecutable; ExecutableDigest = request.GitExecutableDigest
                                   Arguments = [ "--no-optional-locks"; "-c"; "core.fsmonitor=false" ] @ arguments
                                   Environment = Map.empty; Input = ""; TimeoutMilliseconds = min 10000 request.Process.TimeoutMilliseconds
                                   MaxOutputBytes = 1048576 }
        let! observed = run spec cancellation
        match observed.Outcome with
        | LocalWorkerProcessOutcome.Exited 0 when observed.RootExitObserved && observed.ProcessStarted = Some true -> return Ok observed.StandardOutput
        | _ -> return Error(sprintf "validator Git observation unavailable: %A" observed.Outcome) }

    let private candidate request cancellation = task {
        let! identity = git request [ "rev-parse"; "--verify"; "HEAD^{commit}" ] cancellation
        match identity with
        | Error error -> return Error error
        | Ok head when head.TrimEnd('\r', '\n') <> request.CandidateCommit -> return Error "validator checkout is not the exact candidate"
        | Ok _ ->
            let! status = git request [ "status"; "--porcelain=v1"; "--untracked-files=all" ] cancellation
            match status with
            | Error error -> return Error error
            | Ok value when value <> "" -> return Error "validator checkout has uncommitted inputs"
            | Ok _ ->
                let! tree = git request [ "ls-tree"; "-r"; "-z"; "--full-tree"; request.CandidateCommit ] cancellation
                return tree |> Result.bind (fun value ->
                    let entries = value.Split('\u0000', StringSplitOptions.RemoveEmptyEntries)
                    if entries |> Array.exists (fun entry -> not (entry.StartsWith("100644 blob ", StringComparison.Ordinal) || entry.StartsWith("100755 blob ", StringComparison.Ordinal))) then
                        Error "validator candidate has unsupported linked or submodule inputs"
                    else Ok(framed [ "praxis.local-validator-candidate/1"; request.CandidateCommit; value ])) }

    /// Only a fresh host-selected TRX artifact counts tests, never stdout prose.
    /// DTDs, oversized documents, partial execution and inconsistent totals refuse.
    let private readTrxSnapshot (path: string) =
        try
            if not (Path.IsPathFullyQualified path && LocalHostJournalFiles.noLinks path) then invalidOp "relative or linked test result"
            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            if stream.Length > 16777216L then invalidOp "oversized test result"
            let bytes = Array.zeroCreate<byte> (int stream.Length)
            stream.ReadExactly bytes
            if stream.ReadByte() <> -1 then invalidOp "test result grew during capture"
            let settings = XmlReaderSettings(DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16777216L)
            use snapshot = new MemoryStream(bytes, false)
            use reader = XmlReader.Create(snapshot, settings)
            let document = XDocument.Load reader
            let ns = XNamespace.Get "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
            if document.Root.Name <> ns + "TestRun" then invalidOp "unexpected test result schema"
            let summaries = document.Root.Elements(ns + "ResultSummary") |> Seq.toList
            let summary = match summaries with [ value ] -> value | _ -> invalidOp "missing or duplicate test summary"
            if summary.Attribute(XName.Get "outcome").Value <> "Completed" then invalidOp "test run did not complete successfully"
            let counters = match summary.Elements(ns + "Counters") |> Seq.toList with [ value ] -> value | _ -> invalidOp "missing or duplicate test counters"
            let count name =
                match Int32.TryParse(counters.Attribute(XName.Get name).Value, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
                | true, value when value >= 0 -> value
                | _ -> invalidOp "invalid test counter"
            let total, executed, passed = count "total", count "executed", count "passed"
            if total <= 0 || total <> executed || passed <> executed || count "failed" <> 0 || count "error" <> 0 || count "notExecuted" <> 0 then
                invalidOp "test result has zero, failed, skipped or inconsistent execution"
            for name in [ "aborted"; "timeout"; "inconclusive"; "notRunnable"; "passedButRunAborted"; "disconnected"; "warning" ] do
                if not (isNull (counters.Attribute(XName.Get name))) && count name <> 0 then invalidOp "test result has a nonpassing counter"
            let results = document.Root.Descendants(ns + "UnitTestResult") |> Seq.toList
            let ids = results |> List.map (fun result -> result.Attribute(XName.Get "executionId").Value)
            if results.Length <> executed || (Set.ofList ids).Count <> executed
               || ids |> List.exists String.IsNullOrWhiteSpace
               || results |> List.exists (fun result -> result.Attribute(XName.Get "outcome").Value <> "Passed") then
                invalidOp "test results do not match executed passing counters"
            Ok(executed, digest bytes)
        with error -> Error("validator test artifact refused: " + error.GetType().Name)

    let readTrx path = readTrxSnapshot path |> Result.map fst

    /// Read-only controller observation, so signed/checklist validator pins can
    /// be compared before execution rather than learned from a claimed pass.
    let observe request cancellation = task {
        try
            let inputDigest = inputs request
            let! observed = candidate request cancellation
            return observed |> Result.map (fun manifest ->
                { LocalValidatorInputs.ValidatorId = request.ValidatorId
                  CandidateCommit = request.CandidateCommit; CandidateManifestDigest = manifest
                  ValidatorInputDigest = inputDigest })
        with error -> return Error("validator input observation refused: " + error.GetType().Name) }

    let execute (request: LocalValidatorRequest) (cancellation: CancellationToken) = task {
        try
            let evidenceRoot = LocalHostJournalFiles.rootOutside request.Process.WorkingDirectory request.EvidenceRoot
            match request.TrxPath with
            | Some path ->
                let parent = Path.GetDirectoryName path
                LocalHostJournalFiles.rootOutside request.Process.WorkingDirectory parent |> ignore
                if not (Path.IsPathFullyQualified path && LocalHostJournalFiles.noLinks path) || File.Exists path || Directory.Exists path then
                    invalidOp "test artifact must be a fresh non-link host path"
            | None -> ()
            let inputDigest = inputs request
            let! before = candidate request cancellation
            match before with
            | Error error -> return Error error
            | Ok manifest ->
                let started = DateTimeOffset.UtcNow
                let! observation = run request.Process cancellation
                let! after = candidate request cancellation
                let currentInputs = inputs request
                let tests = request.TrxPath |> Option.map readTrxSnapshot
                let verdict =
                    if after <> Ok manifest || currentInputs <> inputDigest then Error "validator candidate or input closure changed during execution"
                    else
                        match observation.Outcome with
                        | LocalWorkerProcessOutcome.Exited 0 when observation.RootExitObserved && observation.ProcessStarted = Some true ->
                            match tests with Some(Error error) -> Error error | _ -> Ok()
                        | _ -> Error(sprintf "validator process did not pass: %A" observation.Outcome)
                let finished = DateTimeOffset.UtcNow
                // Preserve actual output and outcome once, independently of worker stdout.
                let evidenceRecord =
                    {| schemaVersion = "praxis.local-validator-evidence/1"
                       validatorId = request.ValidatorId; candidateCommit = request.CandidateCommit
                       candidateManifestDigest = manifest; validatorInputDigest = inputDigest
                       startedAt = started; finishedAt = finished
                       outcome = sprintf "%A" observation.Outcome
                       observationKind = "transient-controller-tool-handle"
                       standardOutput = observation.StandardOutput; standardError = observation.StandardError
                       testsExecuted = tests |> Option.bind (function Ok(count, _) -> Some count | Error _ -> None)
                       testArtifactDigest = tests |> Option.bind (function Ok(_, hash) -> Some hash | Error _ -> None)
                       passed = Result.isOk verdict
                       refusal = verdict |> function Ok() -> None | Error error -> Some error |}
                let evidence = JsonSerializer.Serialize evidenceRecord
                let path = Path.Combine(evidenceRoot, Guid.NewGuid().ToString("N") + ".json")
                let bytes = Encoding.UTF8.GetBytes evidence
                if bytes.Length > 8388608 then invalidOp "validator evidence exceeds archive bound"
                if not (LocalHostJournalFiles.noLinks path) then invalidOp "validator evidence path is linked"
                do
                    use output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    output.Write bytes
                    output.Flush(true)
                let evidenceDigest = digest bytes
                if fileDigest path <> evidenceDigest then invalidOp "validator evidence differs after durable write"
                return Ok { ValidatorId = request.ValidatorId; CandidateCommit = request.CandidateCommit
                            CandidateManifestDigest = manifest; ValidatorInputDigest = inputDigest
                            StartedAt = started; FinishedAt = finished
                            TestsExecuted = tests |> Option.bind (function Ok(count, _) -> Some count | Error _ -> None)
                            Verdict = verdict; EvidencePath = path; EvidenceDigest = evidenceDigest }
        with error -> return Error("validator host operation refused: " + error.GetType().Name) }
