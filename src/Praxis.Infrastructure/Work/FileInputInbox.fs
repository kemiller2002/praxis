namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Artifacts
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Artifacts
open Praxis.Infrastructure.Git

/// The input-document lifecycle on disk (DER-08..10):
///
/// - pending: `.praxis/inbox/documents/<file>` (or legacy `input-documents/<file>`);
/// - claimed: `.praxis/processing/<claim-id>/{claim.json, source/<file>}`;
/// - completed: `.praxis/processed/<claim-id>/...`;
/// - rejected: `.praxis/rejected/documents/<claim-id>/...`.
///
/// Files are written to a temporary name and renamed; claim directories move
/// by rename. Everything is plain files, so an agent without a Praxis
/// runtime can still see what is pending and what is claimed.
[<RequireQualifiedAccess>]
module FileInputInbox =
    let inboxDirectories = [ InputDocuments.canonicalRelative; InputDocuments.legacyRelative ]

    let locationRelative location =
        match location with
        | ClaimLocation.Processing -> Path.Combine(".praxis", "processing")
        | ClaimLocation.Processed -> Path.Combine(".praxis", "processed")
        | ClaimLocation.Rejected -> Path.Combine(".praxis", "rejected", "documents")

    let private normalize (path: string) = path.Replace('\\', '/')

    let private catching (operation: string) (action: unit -> 'a) : Result<'a, string> =
        try
            Ok(action ())
        with
        | :? IOException
        | :? UnauthorizedAccessException as error -> Error $"{operation}: {error.Message}"

    let private digestFile (path: string) =
        use stream = File.OpenRead path
        Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

    let private digestIfPresent (path: string) =
        catching $"reading {path}" (fun () -> if File.Exists path then Some(digestFile path) else None)

    let private writeAtomic (file: string) (write: string -> unit) =
        let directory = Path.GetDirectoryName file
        Directory.CreateDirectory directory |> ignore
        let temporary = Path.Combine(directory, $".{Path.GetFileName file}.{Guid.NewGuid():N}.tmp")

        try
            write temporary
            File.Move(temporary, file, true)
        finally
            if File.Exists temporary then File.Delete temporary

    let private isInboxFile (file: string) =
        let name = Path.GetFileName file
        not (String.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase)) && not (name.StartsWith ".")

    let create (root: string) : InputInboxPort =
        let root = Path.GetFullPath root
        let full (relative: string) = Path.GetFullPath(Path.Combine(root, relative))
        let claimDirectory location claimId = Path.Combine(full (locationRelative location), claimId)
        let claimFile location claimId = Path.Combine(claimDirectory location claimId, "claim.json")
        let copyPath location (claim: InputClaim) = Path.Combine(claimDirectory location claim.ClaimId, "source", claim.Source.FileName)

        let sourceOf (file: string) =
            let info = FileInfo file
            { Path = normalize (Path.GetRelativePath(root, file))
              FileName = info.Name
              Sha256 = digestFile file
              Size = info.Length }

        let readPending (path: string) =
            catching "reading the inbox" (fun () ->
                let file = full path

                let inInbox =
                    inboxDirectories
                    |> List.exists (fun directory -> String.Equals(Path.GetDirectoryName file, full directory, StringComparison.Ordinal))

                if inInbox && File.Exists file && isInboxFile file then Some(sourceOf file) else None)

        let listPending () =
            catching "reading the inbox" (fun () ->
                inboxDirectories
                |> List.collect (fun directory ->
                    let path = full directory
                    if Directory.Exists path then Directory.GetFiles path |> Array.filter isInboxFile |> Array.map sourceOf |> List.ofArray
                    else [])
                |> List.sortWith (fun left right -> String.CompareOrdinal(left.Path, right.Path)))

        let listClaims () =
            [ ClaimLocation.Processing; ClaimLocation.Processed; ClaimLocation.Rejected ]
            |> List.map (fun location ->
                let directory = full (locationRelative location)

                if not (Directory.Exists directory) then Ok []
                else
                    Directory.GetDirectories directory
                    |> Array.sort
                    |> Array.filter (fun claim -> InputInbox.isClaimId (Path.GetFileName claim))
                    |> Array.map (fun claim ->
                        let file = Path.Combine(claim, "claim.json")

                        if not (File.Exists file) then
                            Error $"{normalize (Path.GetRelativePath(root, claim))} has no claim.json; inspect it before retrying"
                        else
                            catching $"reading {file}" (fun () -> File.ReadAllText file)
                            |> Result.bind InputInboxJson.parse
                            |> Result.mapError (fun message -> $"{normalize (Path.GetRelativePath(root, file))}: {message}")
                            |> Result.map (fun parsed -> location, parsed))
                    |> List.ofArray
                    |> List.fold (fun state item -> Result.bind (fun items -> item |> Result.map (fun value -> items @ [ value ])) state) (Ok []))
            |> List.fold (fun state item -> Result.bind (fun items -> item |> Result.map (fun value -> items @ value)) state) (Ok [])

        let writeClaim location (claim: InputClaim) =
            catching "writing the claim record" (fun () ->
                writeAtomic (claimFile location claim.ClaimId) (fun temporary ->
                    File.WriteAllText(temporary, InputInboxJson.render claim, UTF8Encoding false)))

        let moveClaim from target claimId =
            catching "moving the claim" (fun () ->
                let source = claimDirectory from claimId
                let destination = claimDirectory target claimId
                Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
                Directory.Move(source, destination))

        let removeClaim location claimId =
            catching "removing the claim" (fun () ->
                let directory = claimDirectory location claimId
                if Directory.Exists directory then Directory.Delete(directory, true))

        let copyInto (source: string) (target: string) =
            writeAtomic target (fun temporary -> File.Copy(source, temporary, true))

        let observeDerivations (claim: InputClaim) =
            let committed (relative: string) =
                match ProcessGitRepository.readLines root [ "ls-files"; "--error-unmatch"; "--"; relative ] with
                | Error _ -> Ok false
                | Ok _ ->
                    match ProcessGitRepository.readLines root [ "status"; "--porcelain"; "--"; relative ] with
                    | Ok lines -> Ok lines.IsEmpty
                    | Error failure -> Error $"git status {relative}: {failure.Message}"

            let documents =
                if claim.Derivations |> List.exists (fun derivation -> derivation.Target.IsArtifact) then
                    match (FileArtifactRepository.create root).Load() with
                    | Ok loaded -> Ok loaded.Documents
                    | Error failure -> Error failure.Message
                else Ok []

            documents
            |> Result.bind (fun documents ->
                claim.Derivations
                |> List.map (fun derivation ->
                    match derivation.Target with
                    | DerivationTarget.Artifact id ->
                        match documents |> List.filter (fun document -> ArtifactDocument.identifier document = id) with
                        | [ document ] ->
                            committed document.RelativePath
                            |> Result.map (fun isCommitted ->
                                { Derivation = derivation
                                  Exists = true
                                  Committed = isCommitted
                                  LineageNamesClaim = Some(Lineage.sources document |> List.contains claim.ClaimId) })
                        | _ -> Ok { Derivation = derivation; Exists = false; Committed = false; LineageNamesClaim = None }
                    | DerivationTarget.RepositoryPath path ->
                        if File.Exists(full path) || Directory.Exists(full path) then
                            committed (normalize path)
                            |> Result.map (fun isCommitted -> { Derivation = derivation; Exists = true; Committed = isCommitted; LineageNamesClaim = None })
                        else Ok { Derivation = derivation; Exists = false; Committed = false; LineageNamesClaim = None })
                |> List.fold (fun state item -> Result.bind (fun items -> item |> Result.map (fun value -> items @ [ value ])) state) (Ok []))

        { Now = fun () -> DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Globalization.CultureInfo.InvariantCulture)
          ReadPending = readPending
          ListPending = listPending
          ListClaims = listClaims
          WriteClaim = writeClaim
          MoveClaim = moveClaim
          RemoveClaim = removeClaim
          InboxDigest = fun source -> digestIfPresent (full source.Path)
          ClaimedCopyDigest = fun location claim -> digestIfPresent (copyPath location claim)
          CopyIntoClaim =
            fun claim ->
                catching "copying the input into its claim" (fun () ->
                    copyInto (full claim.Source.Path) (copyPath ClaimLocation.Processing claim))
          CopyBackToInbox =
            fun claim ->
                catching "copying the input back to the inbox" (fun () ->
                    copyInto (copyPath ClaimLocation.Processing claim) (full claim.Source.Path))
          RemoveInboxSource =
            fun claim -> catching "removing the inbox copy" (fun () -> File.Delete(full claim.Source.Path))
          ObserveDerivations = observeDerivations }
