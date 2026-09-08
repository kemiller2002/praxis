namespace Ros.Application.Artifacts

open System
open Ros.Contracts.Artifacts
open Ros.Domain.Artifacts

[<RequireQualifiedAccess>]
type DependencyOutcome =
    | Failed
    | Indeterminate

type DependencyFailure =
    { Operation: string
      Path: string option
      Message: string
      Outcome: DependencyOutcome }

type ArtifactLoad =
    { Documents: ArtifactDocument list
      ParseFindings: ArtifactFinding list }

type ArtifactRepository =
    { Load: unit -> Result<ArtifactLoad, DependencyFailure>
      ReadRegistry: string -> Result<string option, DependencyFailure>
      WriteRegistry: string -> string -> Result<unit, DependencyFailure> }

[<RequireQualifiedAccess>]
type ValidationOutcome =
    | Completed of ArtifactFinding list
    | DependencyFailure of DependencyFailure

[<RequireQualifiedAccess>]
type RegistryCheckOutcome =
    | Completed of ArtifactFinding list
    | DependencyFailure of DependencyFailure

type RegistryChange =
    { Path: string
      Content: string }

[<RequireQualifiedAccess>]
type RegistryBuildOutcome =
    | Rejected of ArtifactFinding list
    | Completed of RegistryChange list
    | DependencyFailure of DependencyFailure
    | Incomplete of written: RegistryChange list * pending: RegistryChange list * failure: DependencyFailure

[<RequireQualifiedAccess>]
module ArtifactOperations =
    let private compareFindings (left: ArtifactFinding) (right: ArtifactFinding) =
        let comparePath = StringComparer.Ordinal.Compare(left.Path, right.Path)

        if comparePath <> 0 then
            comparePath
        else
            let compareField = StringComparer.Ordinal.Compare(left.Field, right.Field)
            if compareField <> 0 then compareField else StringComparer.Ordinal.Compare(left.Message, right.Message)

    let private renderedProjections documents =
        documents
        |> ArtifactProjection.plan
        |> List.map (fun projection ->
            { Path = projection.RegistryPath
              Content = RegistryJson.render projection.Documents })

    let private changedRegistries repository projections =
        let rec collect changed remaining =
            match remaining with
            | [] -> Ok(List.rev changed)
            | projection :: rest ->
                match repository.ReadRegistry projection.Path with
                | Error failure -> Error failure
                | Ok(Some current) when current = projection.Content -> collect changed rest
                | Ok _ -> collect (projection :: changed) rest

        collect [] projections

    let validate repository =
        match repository.Load() with
        | Error failure -> ValidationOutcome.DependencyFailure failure
        | Ok loaded ->
            ArtifactPolicy.validate loaded.ParseFindings loaded.Documents
            |> ValidationOutcome.Completed

    let checkRegistries repository =
        match repository.Load() with
        | Error failure -> RegistryCheckOutcome.DependencyFailure failure
        | Ok loaded ->
            let projections = renderedProjections loaded.Documents

            match changedRegistries repository projections with
            | Error failure -> RegistryCheckOutcome.DependencyFailure failure
            | Ok changes ->
                let stale =
                    changes
                    |> List.map (fun (change: RegistryChange) ->
                        ({ Path = change.Path
                           Field = ""
                           Message = "registry is stale; run 'ros registry build'" }: ArtifactFinding))

                loaded.ParseFindings @ stale
                |> List.sortWith compareFindings
                |> RegistryCheckOutcome.Completed

    let buildRegistries dryRun repository =
        match repository.Load() with
        | Error failure -> RegistryBuildOutcome.DependencyFailure failure
        | Ok loaded when not loaded.ParseFindings.IsEmpty ->
            loaded.ParseFindings
            |> List.sortWith compareFindings
            |> RegistryBuildOutcome.Rejected
        | Ok loaded ->
            let projections = renderedProjections loaded.Documents

            match changedRegistries repository projections with
            | Error failure -> RegistryBuildOutcome.DependencyFailure failure
            | Ok changes when dryRun -> RegistryBuildOutcome.Completed changes
            | Ok changes ->
                let rec write written remaining =
                    match remaining with
                    | [] -> RegistryBuildOutcome.Completed(List.rev written)
                    | change :: rest ->
                        match repository.WriteRegistry change.Path change.Content with
                        | Ok() -> write (change :: written) rest
                        | Error failure ->
                            RegistryBuildOutcome.Incomplete(List.rev written, change :: rest, failure)

                write [] changes
