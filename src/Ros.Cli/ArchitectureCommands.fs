/// `ros architecture check`: enforces the repository's declared
/// implementation-language policy (`ros.json` `implementationPolicy`,
/// DF-ROS-2026-A041). The same findings join the unified `validate`.
module Ros.Cli.ArchitectureCommands

open System
open Ros.Contracts.Cli
open Ros.Domain.Architecture
open Ros.Domain.Artifacts
open Ros.Infrastructure.Architecture

let usage = "architecture check [--json]"

let private toArtifactFinding (finding: ImplementationPolicyFinding) : ArtifactFinding =
    { Path = finding.Path
      Field = finding.Field
      Message = finding.Message }

/// Findings in the shared validation shape, for the unified `validate`.
let findings (root: string) : Result<ArtifactFinding list, string> =
    FileImplementationPolicyRepository.findings root
    |> Result.map (List.map toArtifactFinding)

let private render (finding: ArtifactFinding) =
    let location =
        if String.IsNullOrEmpty finding.Field then finding.Path else $"{finding.Path}:{finding.Field}"

    $"{location}: {finding.Message}"

let private check (root: string) (asJson: bool) =
    match FileImplementationPolicyRepository.readPolicy root, findings root with
    | Error message, _
    | _, Error message ->
        eprintfn "ERROR %s" message
        1
    | Ok policy, Ok all when asJson ->
        printf "%s" (FindingContract.renderJson all)
        ignore policy
        if all.IsEmpty then 0 else 1
    | Ok policy, Ok [] ->
        if policy.ProhibitNodeArtifacts then
            printfn "architecture check passed: no repository-owned Node/JavaScript/TypeScript artifacts"
        else
            printfn "architecture check: ros.json declares no implementationPolicy; nothing is enforced"

        0
    | Ok _, Ok all ->
        all |> List.iter (fun finding -> eprintfn "ERROR %s\n  REPAIR %s" (render finding) (FindingContract.repair finding))
        eprintfn "architecture check failed with %d violation(s)" all.Length
        1

let run (root: string) (arguments: string list) =
    match arguments with
    | [ "check" ] -> check root false
    | [ "check"; "--json" ] -> check root true
    | _ ->
        eprintfn "ERROR usage: ros %s" usage
        2
