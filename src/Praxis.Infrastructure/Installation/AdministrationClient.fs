namespace Praxis.Infrastructure.Installation

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Praxis.Contracts.Installation
open Praxis.Domain.Installation

/// Resolves the configured Project Administration integration and calls it
/// through its typed capability boundary. It never reads or writes Project
/// Administration's files: the local transport runs the provider's own
/// `administration` executable against a store the provider owns; the GitHub
/// transport dispatches the provider's workflow.
[<RequireQualifiedAccess>]
module AdministrationClient =
    /// Where the configuration may live, in precedence order: an explicit
    /// path, the repository's `.echelon/administration.json`, then
    /// `ECHELON_ADMINISTRATION_CONFIG` (for workstation-scoped setups).
    let configCandidates (root: string) (explicitPath: string option) =
        [ yield! explicitPath |> Option.toList
          yield Path.Combine(root, ".echelon", "administration.json")
          match Environment.GetEnvironmentVariable "ECHELON_ADMINISTRATION_CONFIG" with
          | null
          | "" -> ()
          | path -> yield path ]

    /// `Ok None` when nothing is configured: an optional integration that is
    /// simply absent.
    let loadConfig (root: string) (explicitPath: string option) : Result<AdministrationConfig option, string> =
        match configCandidates root explicitPath |> List.tryFind File.Exists, explicitPath with
        | None, Some path -> Error $"administration configuration {path} does not exist"
        | None, None -> Ok None
        | Some path, _ ->
            let baseDirectory = Path.GetDirectoryName(Path.GetFullPath path) |> Option.ofObj |> Option.defaultValue root
            InstallationJson.readConfig baseDirectory (File.ReadAllText path) |> Result.map Some

    let private runProcess (file: string) (arguments: string list) (stdin: string option) =
        let info = ProcessStartInfo(file, arguments)
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.RedirectStandardInput <- stdin.IsSome
        info.UseShellExecute <- false

        try
            use p = Process.Start info

            stdin
            |> Option.iter (fun text ->
                p.StandardInput.Write text
                p.StandardInput.Close())

            let out = p.StandardOutput.ReadToEnd()
            let err = p.StandardError.ReadToEnd()
            p.WaitForExit()
            Ok(p.ExitCode, out, err)
        with
        | :? Win32Exception as ex -> Error $"cannot start '{file}': {ex.Message}"
        | ex -> Error ex.Message

    let private parse (text: string) : JsonNode =
        try
            JsonNode.Parse text
        with _ ->
            null

    /// Send one request through the configured transport.
    let send (config: AdministrationConfig) (catalog: string option) (request: RegistrationRequest) : RegistrationOutcome * JsonNode =
        let document = (InstallationJson.request request).ToJsonString InstallationJson.options
        let verb = request.Capability.Replace("installation.", "")

        match config.Transport with
        | Transport.LocalExecutable(command, store) ->
            if not (Directory.Exists store) then
                RegistrationOutcome.Unavailable $"Project Administration store {store} does not exist", null
            else
                let catalogArgs = catalog |> Option.orElse config.Catalog |> Option.map (fun c -> [ "--catalog"; c ]) |> Option.defaultValue []

                let arguments =
                    (command |> List.tail) @ [ "installation"; verb; "--store"; store; "--request"; "-"; "--json" ] @ catalogArgs

                match runProcess (List.head command) arguments (Some document) with
                | Error reason -> RegistrationOutcome.Unavailable reason, null
                | Ok(_, out, err) ->
                    match InstallationJson.readResult out with
                    | Ok outcome -> outcome, parse out
                    | Error reason -> RegistrationOutcome.Unavailable $"{reason}; stderr: {err.Trim()}", null
        | Transport.GitHubWorkflow(repository, workflow, reference) ->
            match runProcess "gh" [ "workflow"; "run"; workflow; "-R"; repository; "--ref"; reference; "-f"; "request=" + document ] None with
            | Error reason -> RegistrationOutcome.Unavailable $"GitHub CLI unavailable: {reason}", null
            | Ok(0, _, _) ->
                RegistrationOutcome.Submitted $"dispatched {workflow} on {repository}@{reference}; the provider records the event asynchronously", null
            | Ok(code, _, err) -> RegistrationOutcome.Unavailable $"gh workflow run exited {code}: {err.Trim()}", null

    /// Run a query through the local transport. Asynchronous transports do
    /// not answer queries synchronously; that is reported, not faked.
    let query (config: AdministrationConfig) (arguments: string list) : Result<string, string> =
        match config.Transport with
        | Transport.LocalExecutable(command, store) ->
            match runProcess (List.head command) ((command |> List.tail) @ [ "installation" ] @ arguments @ [ "--store"; store ]) None with
            | Ok(0, out, _) -> Ok out
            | Ok(code, out, err) -> Error $"administration exited {code}: {out.Trim()} {err.Trim()}"
            | Error reason -> Error reason
        | Transport.GitHubWorkflow(repository, _, _) ->
            Error $"the github-workflow transport to {repository} is write-only; query a Project Administration checkout with a local transport"
