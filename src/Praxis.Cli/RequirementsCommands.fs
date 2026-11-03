namespace Praxis.Cli

open Praxis.Application.Requirements
open Praxis.Contracts.Requirements
open Praxis.Infrastructure.Requirements

/// `praxis requirements preservation verify|sections` (REQ-PRES-001): the
/// deterministic requirements-preservation gate. The CLI parses and renders;
/// the gate lives in Domain, decoding in Contracts, reading in Infrastructure.
/// Exit codes: 0 pass, 3 fail or blocked, 2 invalid arguments or unreadable
/// input, so a gate can branch on the code and read `verdict` for detail.
[<RequireQualifiedAccess>]
module RequirementsCommands =
    let usage =
        "requirements preservation verify --baseline FILE --disposition FILE --derived DIR --source DIR [--approvals FILE --approvals-sha256 HEX] [--trusted-approver ID]... [--output FILE] [--json] | requirements preservation sections --source DIR --document PATH [--pattern REGEX] [--json] | requirements manifest compile --catalog FILE --source-commit SHA [--mappings FILE] [--previous MANIFEST] [--output FILE] [--findings FILE] | requirements manifest check --catalog FILE --manifest FILE [--mappings FILE]"

    let rec private option (name: string) (arguments: string list) =
        match arguments with
        | flag :: value :: _ when flag = name -> Some value
        | _ :: rest -> option name rest
        | [] -> None

    /// Every value of a repeatable option, in order.
    let rec private options (name: string) (arguments: string list) =
        match arguments with
        | flag :: value :: rest when flag = name -> value :: options name rest
        | _ :: rest -> options name rest
        | [] -> []

    let private invalid (message: string) =
        eprintfn "ERROR %s" message
        2

    let private verify (arguments: string list) =
        match option "--baseline" arguments, option "--disposition" arguments, option "--derived" arguments, option "--source" arguments with
        | Some baseline, Some disposition, Some derived, Some source ->
            let request: PreservationGate.Request =
                { Baseline = baseline
                  Disposition = disposition
                  Approvals = option "--approvals" arguments
                  ApprovalsSha256 = option "--approvals-sha256" arguments
                  TrustedApprovers = options "--trusted-approver" arguments
                  Derived = derived
                  Source = source }

            match PreservationGate.verify (FilePreservationInputs.create ()) request with
            | Error reason -> invalid reason
            | Ok outcome ->
                let json = PreservationJson.renderReport outcome.Inputs outcome.Report

                let written =
                    match option "--output" arguments with
                    | None -> Ok()
                    | Some path -> FilePreservationInputs.writeEvidence path json |> Result.mapError (fun reason -> $"could not write --output '{path}': {reason}")

                match written with
                | Error reason ->
                    eprintfn "ERROR %s" reason
                    1
                | Ok() ->
                    if List.contains "--json" arguments then printf "%s" json
                    else printfn "%s" (PreservationJson.renderText outcome.Inputs outcome.Report)

                    PreservationJson.exitCodeOf outcome.Report.Verdict
        | _ -> invalid "requirements preservation verify requires --baseline FILE, --disposition FILE, --derived DIR and --source DIR"

    let private sections (arguments: string list) =
        match option "--source" arguments, option "--document" arguments with
        | Some source, Some document ->
            let pattern = option "--pattern" arguments

            match PreservationGate.sections (FilePreservationInputs.create ()) source document pattern with
            | Error reason -> invalid reason
            | Ok(sha, entries) ->
                if List.contains "--json" arguments then
                    printf "%s" (PreservationJson.renderSections document entries sha)
                else
                    printfn "%s sha256 %s" document sha

                    for entry in entries do
                        let id = entry.Id |> Option.defaultValue "-"
                        printfn "  %s %s %s" entry.Sha256 id entry.Heading

                0
        | _ -> invalid "requirements preservation sections requires --source DIR and --document PATH"

    let private writeOutput (path: string option) (content: string) =
        match path with
        | None -> Ok()
        | Some path -> FilePreservationInputs.writeEvidence path content |> Result.mapError (fun reason -> $"could not write '{path}': {reason}")

    let private manifestInputs () : RequirementManifest.Inputs =
        { ReadFile = (FilePreservationInputs.create ()).ReadFile }

    /// `requirements manifest compile` (REQ-RVM-001). Exit 0 compiled, 3 the
    /// catalog is invalid (no manifest is written), 2 bad arguments or unreadable input.
    let private manifestCompile (arguments: string list) =
        match option "--catalog" arguments, option "--source-commit" arguments with
        | Some catalog, Some commit ->
            let request: RequirementManifest.CompileRequest =
                { Catalog = catalog
                  Mappings = option "--mappings" arguments
                  SourceCommit = commit
                  Previous = option "--previous" arguments }

            match RequirementManifest.compile (manifestInputs ()) request with
            | Error reason -> invalid reason
            | Ok(RequirementManifest.Rejected(catalogId, findings)) ->
                match writeOutput (option "--findings" arguments) (RequirementManifestJson.renderFindings catalogId findings) with
                | Error reason -> invalid reason
                | Ok() ->
                    eprintfn "%s" (RequirementManifestJson.renderFindingsText findings)
                    eprintfn "REJECTED catalog %s: %d error(s); no manifest written" catalogId (findings |> List.filter (fun f -> f.Severity = Praxis.Domain.Requirements.CatalogError) |> List.length)
                    3
            | Ok(RequirementManifest.Compiled(manifest, findings)) ->
                match writeOutput (option "--output" arguments) manifest with
                | Error reason -> invalid reason
                | Ok() ->
                    if not findings.IsEmpty then eprintfn "%s" (RequirementManifestJson.renderFindingsText findings)
                    if (option "--output" arguments).IsNone then printf "%s" manifest
                    0
        | _ -> invalid "requirements manifest compile requires --catalog FILE and --source-commit SHA"

    /// `requirements manifest check`: exit 0 when the manifest is exactly what
    /// the catalog compiles to, 3 when it is stale or tampered with.
    let private manifestCheck (arguments: string list) =
        match option "--catalog" arguments, option "--manifest" arguments with
        | Some catalog, Some manifest ->
            match RequirementManifest.check (manifestInputs ()) catalog manifest (option "--mappings" arguments) with
            | Error reason -> invalid reason
            | Ok(RequirementManifest.Current(catalogId, hash)) ->
                printfn "CURRENT %s contentHash %s" catalogId hash
                0
            | Ok(RequirementManifest.Stale problems) ->
                for problem in problems do
                    match problem with
                    | RequirementManifest.Tampered(recorded, recomputed) -> eprintfn "STALE tampered: contentHash %s but content hashes to %s" recorded recomputed
                    | RequirementManifest.CatalogChanged(recorded, actual) -> eprintfn "STALE catalog-changed: manifest compiled catalog %s, catalog is now %s" recorded actual
                    | RequirementManifest.ContentDiffers -> eprintfn "STALE content-differs: recompiling the catalog gives a different manifest"
                    | RequirementManifest.CatalogInvalid findings -> eprintfn "STALE catalog-invalid:\n%s" (RequirementManifestJson.renderFindingsText findings)

                3
        | _ -> invalid "requirements manifest check requires --catalog FILE and --manifest FILE"

    let run (arguments: string list) =
        match arguments with
        | "preservation" :: "verify" :: rest -> verify rest
        | "preservation" :: "sections" :: rest -> sections rest
        | "manifest" :: "compile" :: rest -> manifestCompile rest
        | "manifest" :: "check" :: rest -> manifestCheck rest
        | _ -> invalid $"usage: praxis {usage}"
