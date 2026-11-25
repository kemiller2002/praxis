namespace Praxis.Domain.Foundations

open System
open System.Text.RegularExpressions

/// A presentation release pinned by its immutable GitHub release tarball
/// (SAF-FORMA-1, SAF-DEP-1). Praxis owns no npm, so the pin is a lock file
/// naming the version, the release URL and the tarball's sha256.
type ReleaseLock =
    { Version: string
      Url: string
      Sha256: string }

[<RequireQualifiedAccess>]
type ReleaseLockError =
    | Missing of field: string
    | InvalidVersion of string
    | UrlNotImmutableRelease of url: string * version: string
    | InvalidDigest of string
    | DigestMismatch of expected: string * actual: string

/// What a lock pins: the lock's key line, its file name (for messages) and the
/// one release URL a version may name.
type PinnedRelease =
    { Key: string
      LockFile: string
      ReleaseUrl: string -> string }

/// Pure: parse and check a release lock; the caller reads the files.
[<RequireQualifiedAccess>]
module ReleasePin =
    let private semver = Regex(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled)
    let private sha = Regex(@"^[0-9a-f]{64}$", RegexOptions.Compiled)

    let describe (release: PinnedRelease) error =
        match error with
        | ReleaseLockError.Missing field -> $"{release.LockFile} has no '{field}' line"
        | ReleaseLockError.InvalidVersion v -> $"{release.LockFile} version '{v}' is not an exact release version"
        | ReleaseLockError.UrlNotImmutableRelease(url, v) -> $"{release.LockFile} url '{url}' is not the v{v} GitHub release tarball"
        | ReleaseLockError.InvalidDigest d -> $"{release.LockFile} sha256 '{d}' is not a sha256 digest"
        | ReleaseLockError.DigestMismatch(expected, actual) -> $"{release.Key} tarball sha256 {actual} does not match the lock ({expected})"

    let parse (release: PinnedRelease) (text: string) : Result<ReleaseLock, ReleaseLockError> =
        let values =
            text.Split('\n')
            |> Array.map _.Trim()
            |> Array.filter (fun line -> line <> "" && not (line.StartsWith "#"))
            |> Array.choose (fun line ->
                match line.Split(' ', 2, StringSplitOptions.TrimEntries) with
                | [| key; value |] -> Some(key, value)
                | _ -> None)
            |> Map.ofArray

        let field name =
            values |> Map.tryFind name |> Option.map Ok |> Option.defaultValue (Error(ReleaseLockError.Missing name))

        field release.Key
        |> Result.bind (fun version -> if semver.IsMatch version then Ok version else Error(ReleaseLockError.InvalidVersion version))
        |> Result.bind (fun version ->
            field "url"
            |> Result.bind (fun url ->
                if url = release.ReleaseUrl version then Ok url else Error(ReleaseLockError.UrlNotImmutableRelease(url, version)))
            |> Result.bind (fun url ->
                field "sha256"
                |> Result.bind (fun digest ->
                    if sha.IsMatch digest then
                        Ok { Version = version; Url = url; Sha256 = digest }
                    else
                        Error(ReleaseLockError.InvalidDigest digest))))

    /// The tarball is the pinned artifact only when its digest is the lock's.
    let verify (lock: ReleaseLock) (actualSha256: string) =
        if String.Equals(lock.Sha256, actualSha256, StringComparison.OrdinalIgnoreCase) then Ok lock
        else Error(ReleaseLockError.DigestMismatch(lock.Sha256, actualSha256))

/// The Forma release pin (`vendor/forma/forma.lock`).
[<RequireQualifiedAccess>]
module FormaPin =
    let release =
        { Key = "forma"
          LockFile = "forma.lock"
          ReleaseUrl =
            fun version -> $"https://github.com/kemiller2002/forma/releases/download/v{version}/echelon-foundry-design-system-{version}.tgz" }

    let describe error = ReleasePin.describe release error
    let parse text = ReleasePin.parse release text
    let verify lock actualSha256 = ReleasePin.verify lock actualSha256

    /// The stylesheet path a page links: versioned, so a pin change is a new URL.
    let stylesheetPath (lock: ReleaseLock) = $"/forma/{lock.Version}/all.css"

    /// Source text that shows a repository uses Forma, for `foundations
    /// verify`. Forma 1.0 renamed the `ef-` namespace to `forma-`
    /// (FORMA-NS-001); the pre-1.0 markers still identify 0.x consumers.
    let usageMarkers =
        let elements = [ "button"; "input"; "field"; "card"; "dialog"; "alert"; "select"; "checkbox"; "toggle" ]

        [ "@echelon-foundry/design-system"; "design-system/all.css" ]
        @ ([ "ef"; "forma" ]
           |> List.collect (fun prefix -> $"class=\\\"{prefix}-" :: (elements |> List.map (fun element -> $"<{prefix}-{element}"))))

/// The Echelon theme pin (`vendor/echelon-theme/echelon-theme.lock`): the
/// Echelon brand for Forma 1.0, an optional layer over Forma (EDS-ADR-002).
[<RequireQualifiedAccess>]
module EchelonThemePin =
    let release =
        { Key = "echelon-theme"
          LockFile = "echelon-theme.lock"
          ReleaseUrl =
            fun version ->
                $"https://github.com/kemiller2002/echelon-design-system/releases/download/v{version}/echelon-foundry-echelon-theme-{version}.tgz" }

    let describe error = ReleasePin.describe release error
    let parse text = ReleasePin.parse release text
    let verify lock actualSha256 = ReleasePin.verify lock actualSha256

    /// The brand a page's root element names so the theme's scoped rules apply.
    [<Literal>]
    let Brand = "echelon"

    /// The theme's stylesheets, in the order they load after Forma's all.css.
    let stylesheets = [ "echelon.css"; "echelon-fonts.css"; "echelon-app.css" ]

    /// Where the theme's files are served: versioned, so a pin change is a new URL.
    /// `echelon-fonts.css` names its fonts relative to itself (`fonts/<file>`).
    let basePath (lock: ReleaseLock) = $"/echelon-theme/{lock.Version}"

    let stylesheetPaths (lock: ReleaseLock) =
        stylesheets |> List.map (fun name -> $"{basePath lock}/{name}")
