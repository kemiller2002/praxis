namespace Ros.Domain.Work

open System
open System.Security.Cryptography
open System.Text

/// One durable record a state fingerprint covers: its repository-relative,
/// '/'-separated path and the lower-case hex SHA-256 of its bytes as stored.
type StateRecord = { Path: string; ContentHash: string }

/// The repository, commit and durable Praxis state a control-plane response
/// was derived from (PRX-CTL-004, PRX-CTL-008). `Fingerprint` is `Error`
/// with the reason when the records could not be read.
type StateIdentity =
    { Repository: string
      Commit: string option
      Branch: string option
      Fingerprint: Result<string, string> }

/// A read attributed to the identity observed around it. `Stable` is false
/// when the state kept changing while it was read.
type StateAttributed<'T> =
    { Identity: StateIdentity
      Stable: bool
      Value: 'T }

[<RequireQualifiedAccess>]
module StateIdentity =
    let private hex (bytes: byte array) = Convert.ToHexString(bytes).ToLowerInvariant()

    let contentHash (bytes: byte array) = SHA256.HashData bytes |> hex

    /// The fingerprint of a set of records: `sha256:` over each record's
    /// `path NUL hash LF`, in ordinal path order, so it depends only on the
    /// records' paths and bytes, never on the order they were enumerated in.
    let fingerprint (records: StateRecord list) : string =
        records
        |> List.sortWith (fun left right -> String.CompareOrdinal(left.Path, right.Path))
        |> List.map (fun record -> $"{record.Path}\u0000{record.ContentHash}\n")
        |> String.concat ""
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> hex
        |> sprintf "sha256:%s"

    /// Whether `.ros/...`-relative `path` is a fingerprint input: every file
    /// under `.ros/` except transient lock files (`.ros/locks/`, gitignored).
    let coversStatePath (path: string) =
        let normalized = path.Replace('\\', '/')
        normalized.StartsWith(".ros/", StringComparison.Ordinal) && not (normalized.StartsWith(".ros/locks/", StringComparison.Ordinal))

    let fingerprintText (identity: StateIdentity) =
        match identity.Fingerprint with
        | Ok value -> value
        | Error _ -> "unavailable"

    /// Runs `read` between two identity observations and attributes it to
    /// the identity when both agree. A write that lands mid-read makes them
    /// differ; the read is then repeated, at most `attempts` times in all,
    /// and the last read is returned unstable with the identity seen after it.
    let stable (identify: unit -> StateIdentity) (read: unit -> 'T) (attempts: int) : StateAttributed<'T> =
        let rec attempt remaining =
            let before = identify ()
            let value = read ()
            let after = identify ()

            match before = after && after.Fingerprint |> Result.isOk, remaining > 1 with
            | true, _ -> { Identity = after; Stable = true; Value = value }
            | false, true when before <> after -> attempt (remaining - 1)
            | false, _ -> { Identity = after; Stable = false; Value = value }

        attempt (max 1 attempts)
