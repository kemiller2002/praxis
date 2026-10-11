namespace Praxis.Domain.Work

open System
open System.Security.Cryptography
open System.Text

/// Host archive of untrusted stdout, not approval or accepted result evidence.
type LocalWorkerSubmission =
    { SchemaVersion: string; RepositoryIdentity: string; DispatchId: string; AttemptId: string
      PacketDigest: string; ProcessIdentity: string; CapturedAt: DateTimeOffset
      ExitCode: int; Payload: string; PayloadDigest: string }
[<RequireQualifiedAccess>]
type LocalSubmissionSaveOutcome = Created | Existing

[<RequireQualifiedAccess>]
module LocalWorkerSubmission =
    [<Literal>]
    let Schema = "praxis.local-worker-submission/1"
    let payloadBytes (payload: string) =
        try
            if isNull payload then Error "missing submission payload"
            elif UTF8Encoding(false, true).GetByteCount payload > 65536 then Error "oversized submission payload"
            else Ok(UTF8Encoding(false, true).GetBytes payload)
        with :? EncoderFallbackException -> Error "submission payload is not valid Unicode"
    let digest bytes = "sha256:" + (SHA256.HashData(bytes: byte array) |> Convert.ToHexString).ToLowerInvariant()
    let problems submission =
        let clean (value: string) =
            not (String.IsNullOrWhiteSpace value) && value.Length <= 1024 && value = value.Trim()
            && not (value |> Seq.exists Char.IsControl)
        [ if submission.SchemaVersion <> Schema then yield "local-submission-schema"
          if not ([ submission.RepositoryIdentity; submission.DispatchId; submission.AttemptId; submission.ProcessIdentity ] |> List.forall clean) then yield "local-submission-identity"
          if submission.CapturedAt.Offset <> TimeSpan.Zero then yield "local-submission-time"
          if not (LocalAgentHandoff.isDigest submission.PacketDigest && LocalAgentHandoff.isDigest submission.PayloadDigest) then yield "local-submission-digest"
          match payloadBytes submission.Payload with
          | Error error -> yield error
          | Ok bytes when digest bytes <> submission.PayloadDigest -> yield "local-submission-payload-changed"
          | Ok _ -> () ]
    let validate journal submission =
        let packet = journal.Reservation.Packet
        let errors =
            [ yield! problems submission
              match LocalDispatchJournal.phase journal with
              | Ok(LocalDispatchPhase.Exited identity) when identity = submission.ProcessIdentity -> ()
              | _ -> yield "local-submission-process-not-reconciled"
              if submission.RepositoryIdentity <> packet.RepositoryIdentity || submission.DispatchId <> packet.DispatchId
                 || submission.AttemptId <> packet.AttemptId || submission.PacketDigest <> LocalAgentHandoff.packetDigest packet then yield "local-submission-assignment-changed"
              match List.tryLast journal.Events with
              | Some event when submission.CapturedAt >= event.At -> ()
              | _ -> yield "local-submission-capture-before-exit"
              match payloadBytes submission.Payload with
              | Ok bytes when bytes.Length <= packet.MaxOutputBytes -> ()
              | _ -> yield "local-submission-delegated-output-budget" ]
        if errors.IsEmpty then Ok() else Error(String.concat "; " errors)
    /// Duplicate archival preserves the first capture time. Exact stdout bytes,
    /// process incarnation and assignment cannot be replaced on retry.
    let sameContent left right = { left with CapturedAt = right.CapturedAt } = right
