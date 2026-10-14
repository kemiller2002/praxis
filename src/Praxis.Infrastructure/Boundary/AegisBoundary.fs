namespace Praxis.Infrastructure.Boundary

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Sockets
open System.Text.Json
open System.Text.RegularExpressions
open Aegis

/// The operational boundary an unexpected failure escaped from
/// (SAF-AEGIS-1). Expected Praxis outcomes (refused transitions, failed
/// verification, policy refusals) are typed results and exit codes and never
/// reach here (SAF-AEGIS-2).
[<RequireQualifiedAccess>]
type OperationalBoundary =
    | Git
    | Process
    | Filesystem
    | Network
    | RepositoryState
    | WebRequest
    | Unexpected

/// Praxis's single Aegis boundary: configuration, classification, redaction
/// and presentation. `aegis-boundaries.json` declares exactly `codes`
/// (FoundationsConformance tests assert it).
[<RequireQualifiedAccess>]
module AegisBoundary =
    [<Literal>]
    let Application = "Praxis"

    let code boundary =
        match boundary with
        | OperationalBoundary.Git -> "PRAXIS.GIT.FAILURE"
        | OperationalBoundary.Process -> "PRAXIS.PROCESS.FAILURE"
        | OperationalBoundary.Filesystem -> "PRAXIS.FILES.FAILURE"
        | OperationalBoundary.Network -> "PRAXIS.NETWORK.FAILURE"
        | OperationalBoundary.RepositoryState -> "PRAXIS.STATE.FAILURE"
        | OperationalBoundary.WebRequest -> "PRAXIS.WEB.FAILURE"
        | OperationalBoundary.Unexpected -> "PRAXIS.CLI.UNEXPECTED"

    let all =
        [ OperationalBoundary.Git
          OperationalBoundary.Process
          OperationalBoundary.Filesystem
          OperationalBoundary.Network
          OperationalBoundary.RepositoryState
          OperationalBoundary.WebRequest
          OperationalBoundary.Unexpected ]

    let codes = all |> List.map code

    /// Context keys that carry repository data or work content and must
    /// never reach a sink, whatever their declared classification
    /// (SAF-AEGIS-5). They extend Aegis's credential rules; they never
    /// replace them.
    let redactionRules: Redaction.Rule list =
        let rule name (needles: string list) =
            { Redaction.Rule.Name = name
              Redaction.Rule.AppliesTo =
                fun key ->
                    let k = key.ToLowerInvariant()
                    needles |> List.exists k.Contains }

        [ rule "praxis-repository-data" [ "remote"; "url"; "origin"; "repository" ]
          rule
              "praxis-work-content"
              [ "description"; "content"; "prompt"; "transcript"; "body"; "title"; "attachment"; "note"; "evidence" ] ]

    let private credentialPatterns =
        [ Regex(@"(?<=://)[^/\s:@]+(:[^/\s@]*)?@", RegexOptions.Compiled)
          Regex(@"\b(gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,})", RegexOptions.Compiled)
          Regex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.Compiled)
          Regex(@"(?i)\b(token|password|passwd|secret|api[_-]?key|authorization)(\s*[=:]\s*)[^\s;,&]+", RegexOptions.Compiled) ]

    /// Exception text reaches a sink as technical detail, so credentials in
    /// it (a token in a remote URL, a bearer header in an HTTP error) are
    /// removed before the fault is recorded.
    let scrub (text: string) =
        credentialPatterns
        |> List.fold (fun (current: string) (pattern: Regex) -> pattern.Replace(current, Redaction.Placeholder)) text

    let rec private scrubDetail (detail: ExceptionDetail) =
        { detail with
            Message = scrub detail.Message
            StackTrace = detail.StackTrace
            Inner = detail.Inner |> Option.map scrubDetail }

    let private scrubFault (fault: Fault) =
        { fault with
            TechnicalDetails = fault.TechnicalDetails |> Option.map scrub
            Cause =
                match fault.Cause with
                | Some(CausedByException detail) -> Some(CausedByException(scrubDetail detail))
                | other -> other }

    let rec private chain (ex: exn) =
        seq {
            yield ex

            match ex.InnerException with
            | null -> ()
            | inner -> yield! chain inner
        }

    /// The Praxis infrastructure area an exception was thrown from, read from
    /// its stack, so a failure is attributed to the adapter boundary that
    /// raised it rather than to the command that happened to be running.
    let private origin (ex: exn) =
        chain ex
        |> Seq.collect (fun e -> StackTrace(e, false).GetFrames() |> Seq.ofArray)
        |> Seq.choose (fun frame ->
            match frame.GetMethod() with
            | null -> None
            | m ->
                match m.DeclaringType with
                | null -> None
                | t -> t.Namespace |> Option.ofObj)
        |> Seq.tryFind (fun ns -> ns.StartsWith("Praxis.Infrastructure.", StringComparison.Ordinal))

    /// Classify an escaped exception into its boundary. `fallback` is the
    /// boundary of the host that caught it (the command line or a web
    /// request).
    let boundaryFrom (originNamespace: string option) (fallback: OperationalBoundary) (ex: exn) =
        let byType =
            chain ex
            |> Seq.tryPick (fun e ->
                match e with
                | :? HttpRequestException
                | :? SocketException
                | :? TimeoutException -> Some OperationalBoundary.Network
                | :? Win32Exception -> Some OperationalBoundary.Process
                | :? JsonException
                | :? InvalidDataException -> Some OperationalBoundary.RepositoryState
                | :? IOException
                | :? UnauthorizedAccessException -> Some OperationalBoundary.Filesystem
                | _ -> None)

        match originNamespace, byType with
        | Some ns, _ when ns.StartsWith("Praxis.Infrastructure.Git", StringComparison.Ordinal) -> OperationalBoundary.Git
        | _, Some boundary -> boundary
        | Some ns, None when ns.StartsWith("Praxis.Infrastructure.Remote", StringComparison.Ordinal) ->
            OperationalBoundary.Network
        | _ -> fallback

    let boundaryOf (fallback: OperationalBoundary) (ex: exn) = boundaryFrom (origin ex) fallback ex

    let private hint boundary =
        match boundary with
        | OperationalBoundary.Git -> "Git failed in a way Praxis did not anticipate. Check that git is installed and the repository is readable."
        | OperationalBoundary.Process -> "An external program could not be run. Check that it is installed and on PATH."
        | OperationalBoundary.Filesystem -> "A file or directory could not be read or written. Check that the path exists and is accessible."
        | OperationalBoundary.Network -> "A network request failed. Check connectivity and try again."
        | OperationalBoundary.RepositoryState -> "Repository state could not be read. Run './praxis validate' to locate the damaged record."
        | OperationalBoundary.WebRequest -> "The request could not be completed."
        | OperationalBoundary.Unexpected -> "The failure was not one Praxis anticipated."

    let private category boundary =
        match boundary with
        | OperationalBoundary.Network
        | OperationalBoundary.Git
        | OperationalBoundary.Process -> IntegrationFailure
        | OperationalBoundary.RepositoryState -> DataFailure
        | OperationalBoundary.Filesystem
        | OperationalBoundary.WebRequest -> InfrastructureFailure
        | OperationalBoundary.Unexpected -> UnknownFailure

    /// Classify, scrub and describe one escaped failure. `failure` is the
    /// caller's safe description of what did not happen.
    let classify (fallback: OperationalBoundary) (failure: string) (aegis: AegisConfig) (scope: Scope) (ex: exn) : Fault =
        let boundary = boundaryOf fallback ex

        Aegis.faultOf
            aegis
            scope
            (FaultCode(code boundary))
            (category boundary)
            FaultSeverity.Error
            OperationOnly
            (if boundary = OperationalBoundary.Unexpected then RequiresIntervention else Transient)
            (if boundary = OperationalBoundary.Unexpected then ManualIntervention else Continue)
            $"Praxis {failure}. {hint boundary}"
            ex
        |> scrubFault

    /// Aegis configured once per process with Praxis's redaction rules and
    /// validated before it is trusted. Delivery is awaited: a command-line
    /// process may exit as soon as the command returns.
    let configure (version: string option) (sinks: Sinks.Sink list) : Result<AegisConfig, string list> =
        let configured =
            { Aegis.configure Application version sinks with
                Rules = redactionRules @ Redaction.defaultRules
                Persistence = Blocking
                Fallback = fun message -> Console.Error.WriteLine message }

        match Bootstrap.validate None configured with
        | Ok valid -> Ok valid
        | Result.Error problems -> Result.Error(problems |> List.map (Bootstrap.describe >> snd))

    /// The scope of one operation. Only the command words are public; the
    /// repository root is sensitive.
    let scope (aegis: AegisConfig) (operation: string) (root: string option) =
        let context =
            [ yield "operation", Public operation
              match root with
              | Some path -> yield "root", Sensitive path
              | None -> () ]
            |> Map.ofList

        Aegis.scope aegis $"Praxis.{operation}" context

    /// Run one step at a boundary: its own result, or the recorded fault.
    /// Programming defects and caller cancellation are re-raised by Aegis.
    let capture (aegis: AegisConfig) (fallback: OperationalBoundary) (operation: string) (root: string option) (failure: string) (step: unit -> 'T) : Result<'T, Fault> =
        Aegis.capture aegis (scope aegis operation root) (classify fallback failure aegis) step

    /// The error line an operator sees: the safe message and a reference to
    /// the full fault record. Never the exception.
    let describe (fault: Fault) =
        $"{fault.UserMessage} Reference {Presentation.reference fault}"

    /// The public command words of an invocation (no values, no paths).
    let operationOf (arguments: string list) =
        arguments
        |> List.takeWhile (fun a -> not (a.StartsWith "-") && a |> Seq.forall (fun c -> Char.IsLetter c || c = '-'))
        |> List.truncate 3
        |> function
            | [] -> "cli"
            | words -> String.Join(".", words)
