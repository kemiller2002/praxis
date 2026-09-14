namespace Ros.Infrastructure.Lifecycle

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Ros.Domain.Lifecycle

/// One payload file, rendered and ready to write.
type PayloadFile =
    { Entry: PayloadEntry
      Content: byte array }

/// The scaffold this package installs, plus the metadata `init` records about
/// it. Loaded from the package's own `starter/<profile>/manifest.json`, so the
/// package -- not the caller, and never the Node launcher -- decides what an
/// installation consists of.
type Payload =
    { Profile: string
      PackageName: string
      PackageVersion: string
      /// The version a scaffolded project should pin. Differs from
      /// PackageVersion only for a main-branch snapshot install, which has no
      /// GitHub Release of its own to fetch a binary from.
      TargetVersion: string
      ProjectName: string
      Files: PayloadFile list }

[<RequireQualifiedAccess>]
module Payload =
    let private jsonOptions = JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip)

    let private readJson (path: string) =
        JsonDocument.Parse(File.ReadAllText path, jsonOptions)

    let private stringProperty (element: JsonElement) name =
        match element.TryGetProperty(name: string) with
        | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | _ -> None

    let private boolProperty (element: JsonElement) name =
        match element.TryGetProperty(name: string) with
        | true, value when value.ValueKind = JsonValueKind.True -> true
        | _ -> false

    let sha256Hex (content: byte array) =
        content |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    /// Refuses any destination that escapes the repository root, so a
    /// hand-edited starter manifest can never write outside the target.
    let resolveWithin (root: string) (relative: string) : Result<string, string> =
        let rootFull = Path.GetFullPath root
        let destination = Path.GetFullPath(Path.Combine(rootFull, relative))

        let prefix =
            if rootFull.EndsWith(string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
                rootFull
            else
                rootFull + string Path.DirectorySeparatorChar

        if destination = rootFull || destination.StartsWith(prefix, StringComparison.Ordinal) then
            Ok destination
        else
            Error $"unsafe path escapes the repository root: {relative}"

    /// `slugify` in `lib/bootstrap.mjs`, reproduced so an F#-initialized
    /// repository and a `ros-bootstrap`-initialized one agree byte for byte.
    let slugify (value: string) =
        let normalized = value.Normalize(NormalizationForm.FormKD).ToLowerInvariant()
        let collapsed = Regex.Replace(normalized, "[^a-z0-9]+", "-")
        let trimmed = Regex.Replace(collapsed, "^-+|-+$", "")
        if trimmed.Length = 0 then "project" else trimmed

    /// `deriveProjectName` in `lib/bootstrap.mjs`: the display name a target
    /// folder implies when `--project` is omitted.
    let deriveProjectName (target: string) : Result<string, string> =
        let basename =
            Regex.Replace(Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath target)), "\\.git$", "", RegexOptions.IgnoreCase)

        let words =
            Regex.Replace(basename, "([a-z0-9])([A-Z])", "$1 $2")
            |> fun value -> Regex.Replace(value, "[-_]+", " ")
            |> _.Trim()
            |> fun value -> Regex.Split(value, "\\s+")
            |> Array.filter (fun word -> word.Length > 0)

        if words.Length = 0 then
            Error "could not derive a project name from the target; provide --project"
        else
            words
            |> Array.map (fun word ->
                if word = word.ToUpperInvariant() && Regex.IsMatch(word, "[A-Z]") then
                    word
                else
                    string (Char.ToUpperInvariant word[0]) + word.Substring(1).ToLowerInvariant())
            |> String.concat " "
            |> Ok

    let private render (content: string) (variables: Map<string, string>) : Result<string, string> =
        let mutable failure = None

        let rendered =
            Regex.Replace(
                content,
                "\\{\\{([A-Z_]+)\\}\\}",
                fun (m: Match) ->
                    let key = m.Groups[1].Value

                    match Map.tryFind key variables with
                    | Some value -> value
                    | None ->
                        if failure.IsNone then
                            failure <- Some $"unknown template variable {m.Value}"

                        m.Value
            )

        match failure with
        | Some message -> Error message
        | None -> Ok rendered

    /// Ownership comes from the starter manifest's own `ownership` field when
    /// it declares one. Otherwise it is derived from the fields that already
    /// existed before ownership was modelled, so that a manifest entry never
    /// silently lands without a classification:
    ///   policy "preserve-existing" -> user-owned (the repository's file)
    ///   template true              -> shared     (seeded, then edited)
    ///   otherwise                  -> tool-owned
    let private ownershipOf (entry: JsonElement) =
        match stringProperty entry "ownership" |> Option.bind Ownership.parse with
        | Some ownership -> Ok ownership
        | None ->
            match stringProperty entry "ownership" with
            | Some unknown -> Error $"unknown ownership '{unknown}' in the starter manifest"
            | None ->
                if stringProperty entry "policy" = Some "preserve-existing" then Ok Ownership.UserOwned
                elif boolProperty entry "template" then Ok Ownership.Shared
                else Ok Ownership.ToolOwned

    let availableProfiles (packageRoot: string) =
        let starter = Path.Combine(packageRoot, "starter")

        if Directory.Exists starter then
            Directory.GetDirectories starter
            |> Array.filter (fun directory -> File.Exists(Path.Combine(directory, "manifest.json")))
            |> Array.map Path.GetFileName
            |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> List.ofArray
        else
            []

    let private looksLikePackageRoot (candidate: string) =
        File.Exists(Path.Combine(candidate, "package.json"))
        && File.Exists(Path.Combine(candidate, "starter", "greenfield", "manifest.json"))

    /// Where this package's own scaffold lives. An explicit `--package-root`
    /// wins; otherwise the environment variable the npm launcher sets; then a
    /// walk up from the executable (a source checkout, or a cached binary
    /// sitting inside the package); then a walk up from the working
    /// directory. Returning None is not an error -- `status`, `verify` and
    /// `doctor` all work without a payload.
    let locate (explicit: string option) : string option =
        let candidates =
            [ match explicit with
              | Some value -> yield Path.GetFullPath value
              | None -> ()

              match Environment.GetEnvironmentVariable "ROS_PACKAGE_ROOT" with
              | null
              | "" -> ()
              | value -> yield Path.GetFullPath value ]

        let rec walkUp (directory: string) =
            if String.IsNullOrEmpty directory then
                None
            elif looksLikePackageRoot directory then
                Some directory
            else
                match Path.GetDirectoryName directory with
                | null -> None
                | parent when parent = directory -> None
                | parent -> walkUp parent

        match candidates |> List.tryFind looksLikePackageRoot with
        | Some found -> Some found
        | None ->
            match walkUp (Path.GetFullPath AppContext.BaseDirectory) with
            | Some found -> Some found
            | None -> walkUp (Path.GetFullPath(Directory.GetCurrentDirectory()))

    let private readPackageMetadata (packageRoot: string) =
        use document = readJson (Path.Combine(packageRoot, "package.json"))
        let root = document.RootElement
        stringProperty root "name" |> Option.defaultValue "", stringProperty root "version" |> Option.defaultValue "0.0.0"

    /// `publish.yml` bundles `lib/stable-ros-version.json` into every
    /// published tarball, naming the newest stable release. A snapshot
    /// install must scaffold that version rather than its own, since a
    /// snapshot has no GitHub Release and therefore no runnable binary.
    let private readTargetVersion (packageRoot: string) (packageVersion: string) =
        let overridePath = Path.Combine(packageRoot, "lib", "stable-ros-version.json")

        if File.Exists overridePath then
            use document = readJson overridePath
            stringProperty document.RootElement "version" |> Option.defaultValue packageVersion
        else
            packageVersion

    /// Read, render and hash the whole scaffold for one profile. Nothing is
    /// written; the result is the desired state the planner compares against.
    let load (packageRoot: string) (profile: string) (projectName: string) : Result<Payload, string> =
        let manifestPath = Path.Combine(packageRoot, "starter", profile, "manifest.json")

        if not (File.Exists manifestPath) then
            let available = availableProfiles packageRoot

            let names = String.concat ", " available
            Error $"unsupported profile '{profile}'; available profiles: {names}"
        else
            let packageName, packageVersion = readPackageMetadata packageRoot
            let targetVersion = readTargetVersion packageRoot packageVersion

            let variables =
                Map.ofList
                    [ "PROJECT_NAME", projectName
                      "PROJECT_SLUG", slugify projectName
                      "CREATED_DATE", DateTime.UtcNow.ToString("yyyy-MM-dd")
                      "ROS_VERSION", targetVersion ]

            use document = readJson manifestPath

            let entries =
                document.RootElement.GetProperty("files").EnumerateArray()
                |> Seq.map (fun entry ->
                    let source = stringProperty entry "source" |> Option.defaultValue ""
                    let destination = stringProperty entry "destination" |> Option.defaultValue ""

                    if source = "" || destination = "" then
                        Error "starter manifest entry is missing 'source' or 'destination'"
                    else
                        match resolveWithin packageRoot source with
                        | Error message -> Error message
                        | Ok sourcePath ->
                            if not (File.Exists sourcePath) then
                                Error $"starter manifest references a missing source file: {source}"
                            else
                                match ownershipOf entry with
                                | Error message -> Error message
                                | Ok ownership ->
                                    let raw = File.ReadAllBytes sourcePath

                                    let contentResult =
                                        if boolProperty entry "template" then
                                            render (Encoding.UTF8.GetString raw) variables
                                            |> Result.map Encoding.UTF8.GetBytes
                                        else
                                            Ok raw

                                    contentResult
                                    |> Result.map (fun content ->
                                        { Entry =
                                            { Path = destination
                                              Ownership = ownership
                                              Sha256 = sha256Hex content
                                              Executable = boolProperty entry "executable"
                                              Integration = stringProperty entry "integration" }
                                          Content = content }))
                |> List.ofSeq

            match entries |> List.tryPick (function Error message -> Some message | Ok _ -> None) with
            | Some message -> Error message
            | None ->
                Ok
                    { Profile = profile
                      PackageName = packageName
                      PackageVersion = packageVersion
                      TargetVersion = targetVersion
                      ProjectName = projectName
                      Files = entries |> List.choose (function Ok file -> Some file | Error _ -> None) }
