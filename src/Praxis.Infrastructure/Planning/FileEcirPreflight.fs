namespace Praxis.Infrastructure.Planning

open System
open System.Text.Json
open Praxis.Infrastructure.Git

/// Host-pinned and committed ECIR inputs. The digest and commit must be
/// supplied by a trusted orchestration/release authority, not taken from the
/// blueprint, its authors, or a work group's natural-language shared context.
type CommittedEcirReference =
    { Commit: string
      ManifestPath: string
      BlueprintPath: string
      SourceManifestDigest: string }

/// Immutable bytes as read from the SAME verified commit. No execution
/// authority follows from this observation, even if the source IDs match.
type CommittedEcirDocuments =
    { Commit: string
      Manifest: string
      Blueprint: string
      SourceManifestDigest: string }

[<RequireQualifiedAccess>]
module FileEcirPreflight =
    let private sha40 (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length = 40
        && (value |> Seq.forall Uri.IsHexDigit)

    let private sha256 (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length = 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && (value.Substring(7)
            |> Seq.forall (fun character ->
                (character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f')))

    let private safePath (path: string) =
        not (String.IsNullOrWhiteSpace path)
        && not (IO.Path.IsPathRooted path)
        && not (path.Contains(':') || path.Contains('\\'))
        && path.EndsWith(".json", StringComparison.Ordinal)
        && (path.Split('/')
            |> Array.forall (fun segment ->
                segment.Length > 0 && segment <> "." && segment <> ".."))

    let private stringProperty (name: string) (json: string) =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement
            let mutable property = Unchecked.defaultof<JsonElement>
            if root.ValueKind = JsonValueKind.Object
               && root.TryGetProperty(name, &property)
               && property.ValueKind = JsonValueKind.String then
                property.GetString() |> Option.ofObj
            else None
        with :? JsonException -> None

    /// Strict commit identity and source pinning. An editable working-tree
    /// file, tag, branch, short object hash or fabricated blueprint-reported
    /// digest cannot satisfy this evidence reader.
    let readCommitted (root: string) (reference: CommittedEcirReference) : Result<CommittedEcirDocuments, string> =
        if not (sha40 reference.Commit) then
            Error "ECIR requires an exact full 40-digit Git commit"
        elif not (sha256 reference.SourceManifestDigest) then
            Error "ECIR source digest must be a lowercase SHA-256"
        elif not (safePath reference.ManifestPath && safePath reference.BlueprintPath) then
            Error "ECIR source and blueprint paths must be safe repository-relative JSON files"
        elif reference.ManifestPath = reference.BlueprintPath then
            Error "ECIR source manifest and blueprint must be separate committed artifacts"
        else
            match ProcessGitRepository.readLines root [ "cat-file"; "-t"; reference.Commit ] with
            | Ok [ "commit" ] ->
                let committed path =
                    match ProcessGitRepository.readLines root [ "show"; reference.Commit + ":" + path ] with
                    | Ok lines -> Ok(String.concat "\n" lines)
                    | Error problem -> Error("ECIR pinned Git document unavailable: " + problem.Message)

                match committed reference.ManifestPath, committed reference.BlueprintPath with
                | Ok manifest, Ok blueprint ->
                    if stringProperty "digest" manifest <> Some reference.SourceManifestDigest then
                        Error "ECIR committed manifest digest differs from externally pinned source authority"
                    elif stringProperty "sourceManifestDigest" blueprint <> Some reference.SourceManifestDigest then
                        Error "ECIR blueprint is not bound to the externally pinned source manifest"
                    elif stringProperty "schemaVersion" blueprint <> Some "ecir/1" then
                        Error "ECIR committed blueprint uses an unsupported wire version"
                    else
                        Ok {
                            Commit = reference.Commit
                            Manifest = manifest
                            Blueprint = blueprint
                            SourceManifestDigest = reference.SourceManifestDigest }
                | Error problem, _
                | _, Error problem -> Error problem
            | Ok _ -> Error "ECIR source revision is not a commit"
            | Error problem -> Error("ECIR source commit is unavailable: " + problem.Message)
