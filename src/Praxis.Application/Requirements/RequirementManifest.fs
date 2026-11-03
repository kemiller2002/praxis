namespace Praxis.Application.Requirements

open System.Text
open Praxis.Contracts.Requirements
open Praxis.Domain.Requirements

/// Orchestrates `praxis requirements manifest compile|check` (REQ-RVM-001):
/// reads inputs through a port, decodes them and runs the pure compiler.
/// No filesystem here; Infrastructure supplies the port.
[<RequireQualifiedAccess>]
module RequirementManifest =
    type Inputs =
        { ReadFile: string -> Result<byte array, string> }

    type CompileRequest =
        { Catalog: string
          Mappings: string option
          SourceCommit: string
          /// A previously compiled manifest: every ID it names must be kept or retired.
          Previous: string option }

    type CompileOutcome =
        | Compiled of manifest: string * findings: CatalogFinding list
        | Rejected of catalogId: string * findings: CatalogFinding list

    /// Why a compiled manifest does not match the catalog it claims to compile.
    type Staleness =
        /// The manifest's own content no longer matches its contentHash.
        | Tampered of recorded: string * recomputed: string
        /// The catalog bytes changed since the manifest was compiled.
        | CatalogChanged of recorded: string * actual: string
        /// Same catalog hash, but recompiling gives different content.
        | ContentDiffers
        /// The catalog no longer compiles at all.
        | CatalogInvalid of CatalogFinding list

    type CheckOutcome =
        | Current of catalogId: string * contentHash: string
        | Stale of Staleness list

    let private read (inputs: Inputs) (path: string) =
        inputs.ReadFile path
        |> Result.mapError (fun reason -> $"could not read '{path}': {reason}")

    let private text (bytes: byte array) = Encoding.UTF8.GetString bytes

    /// LF-normalised bytes, so a checkout's line-ending policy never changes the hash.
    let private normalisedHash (bytes: byte array) =
        (text bytes).Replace("\r\n", "\n") |> Encoding.UTF8.GetBytes |> RequirementManifestJson.sha256Hex

    let private decodeAt (path: string) decode (bytes: byte array) =
        decode (text bytes) |> Result.mapError (fun reason -> $"'{path}': {reason}")

    let private mappingsOf inputs (path: string option) =
        match path with
        | None -> Ok []
        | Some path -> read inputs path |> Result.bind (decodeAt path RequirementManifestJson.decodeMappings)

    let private compileWith inputs (catalogPath: string) (mappingsPath: string option) (commit: string) =
        read inputs catalogPath
        |> Result.bind (fun bytes ->
            decodeAt catalogPath RequirementManifestJson.decodeCatalog bytes
            |> Result.bind (fun catalog ->
                mappingsOf inputs mappingsPath
                |> Result.map (fun mappings -> catalog, normalisedHash bytes, RequirementCatalog.compile catalog (normalisedHash bytes) commit mappings)))

    let compile (inputs: Inputs) (request: CompileRequest) : Result<CompileOutcome, string> =
        let previousIds =
            match request.Previous with
            | None -> Ok []
            | Some path ->
                read inputs path
                |> Result.bind (decodeAt path RequirementManifestJson.decodeManifestSummary)
                |> Result.map _.RequirementIds

        previousIds
        |> Result.bind (fun previous ->
            compileWith inputs request.Catalog request.Mappings request.SourceCommit
            |> Result.map (fun (catalog, _, compiled) ->
                let removed = RequirementCatalog.removedWithoutRetirement previous catalog

                match compiled with
                | Ok(manifest, findings) when removed.IsEmpty -> Compiled(RequirementManifestJson.renderManifest manifest, findings)
                | Ok(_, findings) -> Rejected(catalog.CatalogId, removed @ findings)
                | Error findings -> Rejected(catalog.CatalogId, removed @ findings)))

    /// Is the manifest at `manifestPath` exactly what the catalog compiles to today?
    let check (inputs: Inputs) (catalogPath: string) (manifestPath: string) (mappingsPath: string option) : Result<CheckOutcome, string> =
        read inputs manifestPath
        |> Result.bind (fun manifestBytes ->
            decodeAt manifestPath RequirementManifestJson.decodeManifestSummary manifestBytes
            |> Result.bind (fun summary ->
                compileWith inputs catalogPath mappingsPath summary.SourceCommit
                |> Result.map (fun (_, catalogHash, compiled) ->
                    let tampered =
                        if summary.ContentHash = summary.RecomputedHash then [] else [ Tampered(summary.ContentHash, summary.RecomputedHash) ]

                    let changed =
                        if summary.CatalogSha256 = catalogHash then [] else [ CatalogChanged(summary.CatalogSha256, catalogHash) ]

                    let content =
                        match compiled with
                        | Error findings -> [ CatalogInvalid findings ]
                        | Ok(manifest, _) ->
                            if RequirementManifestJson.renderManifest manifest = (text manifestBytes).Replace("\r\n", "\n") then []
                            else [ ContentDiffers ]

                    match tampered @ changed @ content with
                    | [] -> Current(summary.CatalogId, summary.ContentHash)
                    | problems -> Stale problems)))
