namespace Praxis.Infrastructure.Architecture

open System.IO
open System.Text.Json
open Praxis.Domain.Architecture
open Praxis.Infrastructure.Git

/// Reads `ros.json` `implementationPolicy` and the repository's own file
/// listing. Read-only. A missing file or missing object is the disabled
/// policy, so projects that never opted in are unaffected.
[<RequireQualifiedAccess>]
module FileImplementationPolicyRepository =
    let private stringProperty (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> ""

    let private parseException (element: JsonElement) : ImplementationException =
        { Path = stringProperty element "path"
          Decision = stringProperty element "decision" }

    let private parsePolicy (policy: JsonElement) : Result<ImplementationPolicy, string> =
        let prohibit =
            match policy.TryGetProperty "prohibitNodeArtifacts" with
            | true, value when value.ValueKind = JsonValueKind.True -> Ok true
            | true, value when value.ValueKind = JsonValueKind.False -> Ok false
            | false, _ -> Ok false
            | true, _ -> Error "ros.json implementationPolicy.prohibitNodeArtifacts must be true or false"

        let exceptions =
            match policy.TryGetProperty "exceptions" with
            | false, _ -> Ok []
            | true, value when value.ValueKind = JsonValueKind.Array ->
                value.EnumerateArray()
                |> Seq.map (fun entry ->
                    if entry.ValueKind = JsonValueKind.Object then
                        Ok(parseException entry)
                    else
                        Error "ros.json implementationPolicy.exceptions entries must be objects with path and decision")
                |> Seq.fold
                    (fun state entry ->
                        match state, entry with
                        | Ok items, Ok item -> Ok(item :: items)
                        | Error message, _
                        | _, Error message -> Error message)
                    (Ok [])
                |> Result.map List.rev
            | true, _ -> Error "ros.json implementationPolicy.exceptions must be an array"

        match prohibit, exceptions with
        | Ok prohibitNode, Ok items ->
            Ok
                { ProhibitNodeArtifacts = prohibitNode
                  Exceptions = items }
        | Error message, _
        | _, Error message -> Error message

    let readPolicy (root: string) : Result<ImplementationPolicy, string> =
        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            Ok ImplementationLanguagePolicy.disabled
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)

                match document.RootElement.TryGetProperty "implementationPolicy" with
                | true, value when value.ValueKind = JsonValueKind.Object -> parsePolicy value
                | true, _ -> Error "ros.json implementationPolicy must be an object"
                | false, _ -> Ok ImplementationLanguagePolicy.disabled
            with :? JsonException as error ->
                Error $"ros.json is not valid JSON: {error.Message}"

    let listFiles (root: string) : Result<string list, string> =
        ProcessGitRepository.listRepositoryFiles root
        |> Result.mapError (fun failure -> $"{failure.Operation} failed: {failure.Message}")

    /// The policy's findings for the repository at `root`; an unconfigured
    /// policy never lists files, so it needs no Git repository at all.
    let findings (root: string) : Result<ImplementationPolicyFinding list, string> =
        readPolicy root
        |> Result.bind (fun policy ->
            if policy.ProhibitNodeArtifacts then
                listFiles root |> Result.map (ImplementationLanguagePolicy.findings policy)
            else
                Ok(ImplementationLanguagePolicy.configurationFindings policy))
