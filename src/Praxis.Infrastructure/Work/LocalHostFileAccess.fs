namespace Praxis.Infrastructure.Work

open System
open System.IO

/// Injectable filesystem access policy. A name or successful callback is not
/// evidence of ownership, ACL correctness or isolation from worker identities.
type LocalHostFileAccess =
    { Name: string
      CheckDirectory: string -> Result<unit, string>
      CheckFile: string -> Result<unit, string>
      ConfigureCreate: FileStreamOptions -> unit }

[<RequireQualifiedAccess>]
module LocalHostFileAccess =
    let private mode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let internal directory path =
        if not (Directory.Exists path && LocalHostJournalFiles.noLinks path) then Error "host directory must exist without links"
        else Ok()
    let internal file path =
        if Directory.Exists path || not (LocalHostJournalFiles.noLinks path) then Error "host file path is a directory or linked"
        else Ok()

    let unixPrivate : LocalHostFileAccess =
        { Name = "unix-private-modes"
          CheckDirectory = fun path ->
              if OperatingSystem.IsWindows() then Error "Unix host file access is unavailable on Windows"
              else directory path |> Result.bind (fun () ->
                  if File.GetUnixFileMode path <> (mode ||| UnixFileMode.UserExecute) then Error "host directory must have mode 0700" else Ok())
          CheckFile = fun path ->
              if OperatingSystem.IsWindows() then Error "Unix host file access is unavailable on Windows"
              else file path |> Result.bind (fun () ->
                  if File.Exists path && File.GetUnixFileMode path <> mode then Error "host file must have mode 0600" else Ok())
          ConfigureCreate = fun options -> options.UnixCreateMode <- mode }

    /// Explicit portability/development profile. Checks path shape and links;
    /// does not inspect or enforce Unix modes, Windows ACLs or worker isolation.
    let portableDevelopment : LocalHostFileAccess =
        { Name = "portable-development-unqualified"
          CheckDirectory = directory; CheckFile = file; ConfigureCreate = fun _ -> () }
