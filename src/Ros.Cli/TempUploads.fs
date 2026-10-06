namespace Ros.Cli

open System
open System.IO

/// Uploaded bytes on their way to the CLI: written to a private directory
/// under the system temporary path (never inside a repository) for the
/// duration of one command, so the CLI's own `work attach` copies them into
/// the repository. The one file-system write the HTTP hosts make themselves.
[<RequireQualifiedAccess>]
module TempUploads =
    /// Runs `action` with `uploads` written as `(path, display name)`; the
    /// directory is always removed afterwards.
    let withFiles (prefix: string) (uploads: Upload list) (action: (string * string) list -> 'T) : 'T =
        let directory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}")
        Directory.CreateDirectory directory |> ignore

        try
            let files =
                uploads
                |> List.mapi (fun index upload ->
                    let path = Path.Combine(directory, $"upload-{index}")
                    File.WriteAllBytes(path, upload.Data)
                    path, upload.Name)

            action files
        finally
            try
                Directory.Delete(directory, true)
            with _ ->
                ()
