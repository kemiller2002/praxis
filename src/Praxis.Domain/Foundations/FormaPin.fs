namespace Praxis.Domain.Foundations

open System
open System.Text.RegularExpressions

/// A Forma release pinned by its immutable GitHub release tarball
/// (SAF-FORMA-1, SAF-DEP-1). Praxis owns no npm, so the pin is a lock file
/// naming the version, the release URL and the tarball's sha256.
type FormaLock =
    { Version: string
      Url: string
      Sha256: string }

[<RequireQualifiedAccess>]
type FormaLockError =
    | Missing of field: string
    | InvalidVersion of string
    | UrlNotImmutableRelease of url: string * version: string
    | InvalidDigest of string
    | DigestMismatch of expected: string * actual: string

/// Pure: parse and check a Forma lock; the caller reads the files.
[<RequireQualifiedAccess>]
module FormaPin =
    let private semver = Regex(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled)
    let private sha = Regex(@"^[0-9a-f]{64}$", RegexOptions.Compiled)

    let describe error =
        match error with
        | FormaLockError.Missing field -> $"forma.lock has no '{field}' line"
        | FormaLockError.InvalidVersion v -> $"forma.lock version '{v}' is not an exact release version"
        | FormaLockError.UrlNotImmutableRelease(url, v) -> $"forma.lock url '{url}' is not the v{v} GitHub release tarball"
        | FormaLockError.InvalidDigest d -> $"forma.lock sha256 '{d}' is not a sha256 digest"
        | FormaLockError.DigestMismatch(expected, actual) -> $"Forma tarball sha256 {actual} does not match the lock ({expected})"

    /// The stylesheet path a page links: versioned, so a pin change is a new URL.
    let stylesheetPath (lock: FormaLock) = $"/forma/{lock.Version}/all.css"

    let parse (text: string) : Result<FormaLock, FormaLockError> =
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
            values |> Map.tryFind name |> Option.map Ok |> Option.defaultValue (Error(FormaLockError.Missing name))

        field "forma"
        |> Result.bind (fun version -> if semver.IsMatch version then Ok version else Error(FormaLockError.InvalidVersion version))
        |> Result.bind (fun version ->
            field "url"
            |> Result.bind (fun url ->
                let expected =
                    $"https://github.com/kemiller2002/forma/releases/download/v{version}/echelon-foundry-design-system-{version}.tgz"

                if url = expected then Ok url else Error(FormaLockError.UrlNotImmutableRelease(url, version)))
            |> Result.bind (fun url ->
                field "sha256"
                |> Result.bind (fun digest ->
                    if sha.IsMatch digest then
                        Ok { Version = version; Url = url; Sha256 = digest }
                    else
                        Error(FormaLockError.InvalidDigest digest))))

    /// The tarball is the pinned artifact only when its digest is the lock's.
    let verify (lock: FormaLock) (actualSha256: string) =
        if String.Equals(lock.Sha256, actualSha256, StringComparison.OrdinalIgnoreCase) then Ok lock
        else Error(FormaLockError.DigestMismatch(lock.Sha256, actualSha256))
