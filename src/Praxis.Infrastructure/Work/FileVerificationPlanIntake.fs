namespace Praxis.Infrastructure.Work

open System
open System.IO
open Praxis.Application.Work
open Praxis.Domain.Work

/// File edge of verification-plan admission: reads the claim directory's
/// copy of the input (`.praxis/processing/<claim-id>/source/<file>`) and
/// writes admitted files by temporary name and rename.
[<RequireQualifiedAccess>]
module FileVerificationPlanIntake =
    let private catching (operation: string) (action: unit -> 'a) : Result<'a, string> =
        try
            Ok(action ())
        with
        | :? IOException
        | :? UnauthorizedAccessException as error -> Error $"{operation}: {error.Message}"

    let create (root: string) : VerificationPlanPort =
        let root = Path.GetFullPath root
        let full (relative: string) = Path.GetFullPath(Path.Combine(root, relative))

        { ReadClaimedCopy =
            fun claim ->
                let path = Path.Combine(full (FileInputInbox.locationRelative ClaimLocation.Processing), claim.ClaimId, "source", claim.Source.FileName)
                catching $"reading the claimed copy of {claim.ClaimId}" (fun () -> File.ReadAllBytes path)
          WriteFile =
            fun relative bytes ->
                catching $"writing {relative}" (fun () ->
                    let file = full relative
                    let directory = Path.GetDirectoryName file
                    Directory.CreateDirectory directory |> ignore
                    let temporary = Path.Combine(directory, $".{Path.GetFileName file}.{Guid.NewGuid():N}.tmp")

                    try
                        File.WriteAllBytes(temporary, bytes)
                        File.Move(temporary, file, true)
                    finally
                        if File.Exists temporary then File.Delete temporary) }
