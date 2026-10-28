namespace Praxis.Domain.Autonomy

open System
open System.Globalization
open System.Text

/// The exact bytes an owner signs to authorize a run. The framing is the one
/// `ecir.approval/1` uses (EcirApprovals.signingPayload): every field is
/// UTF-8 byte-length framed and set-valued fields are sorted ordinally with a
/// count, so no two distinct authorizations share a payload.
[<RequireQualifiedAccess>]
module Authorization =
    [<Literal>]
    let Schema = "praxis.run-authorization/1"

    let utc (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)

    let signingPayload (authorization: RunAuthorization) : byte array =
        let frame (value: string) (buffer: StringBuilder) =
            buffer.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value)

        let frameSet (values: string list) (buffer: StringBuilder) =
            let sorted = values |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
            sorted |> List.fold (fun b value -> frame value b) (frame (string sorted.Length) buffer)

        [ Schema
          authorization.RunId
          authorization.Owner
          authorization.KeyId
          utc authorization.IssuedAt
          utc authorization.ExpiresAt
          string authorization.PolicyVersion
          authorization.PolicyDigest ]
        |> List.fold (fun buffer value -> frame value buffer) (StringBuilder("praxis-run-authorization/1\n"))
        |> frameSet authorization.Repositories
        |> frameSet authorization.TargetBranches
        |> frameSet (authorization.PermittedOperations |> List.map Operation.toWire)
        |> frameSet (authorization.ProtectedOperations |> List.map ProtectedClass.toWire)
        |> fun buffer -> Encoding.UTF8.GetBytes(buffer.ToString())

/// `praxis.run-revocation/1`. A revocation only ever removes authority, so
/// any revocation found anywhere counts, whoever wrote it.
type RunRevocation =
    { RunId: string
      RevokedAt: DateTimeOffset
      Reason: string }
