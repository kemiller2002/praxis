namespace Praxis.Tests

open System
open System.Security.Cryptography
open System.Text.Json
open Praxis.Infrastructure.Planning

[<RequireQualifiedAccess>]
module EcirApprovalTests =
    let private t name run = { Name = "ECIR signed approval: " + name; Run = run }

    let private source = "sha256:" + String('a', 64)
    let private blueprint = "sha256:" + String('b', 64)
    let private commit = String('c', 40)
    let private now = DateTimeOffset(2026, 10, 9, 17, 0, 0, TimeSpan.Zero)
    let private scope =
        { GroupId = "GROUP-1"
          CohortId = "COHORT-1"
          SourceCommit = commit
          ManifestDigest = source
          BlueprintDigest = blueprint
          RequirementKeys = [ "docs/req.md#R-1"; "docs/req.md#R-2" ]
          DecisionIds = [ "DEC-2"; "DEC-1" ] }

    let private template =
        { SchemaVersion = EcirApprovals.SchemaVersion
          KeyId = "governance-v1"
          Approver = "owner"
          IssuedAt = "2026-10-09T16:00:00Z"
          ExpiresAt = "2026-10-10T15:00:00Z"
          Scope = scope
          Signature = "" }

    let private withApproval test =
        use secretKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let publicPem = secretKey.ExportSubjectPublicKeyInfoPem()
        let trusted = Map.ofList [ "governance-v1", { Approver = "owner"; PublicKeyPem = publicPem } ]
        let sign (candidate: EcirSignedApproval) =
            let bytes = secretKey.SignData(EcirApprovals.signingPayload candidate, HashAlgorithmName.SHA256)
            { candidate with Signature = Convert.ToBase64String bytes }
        test trusted sign

    let private isRejected trusted expected time receipt =
        Assert.isTrue
            (EcirApprovals.verify trusted expected time receipt |> Result.isError)
            "invalid approval was unexpectedly accepted"

    let private serialized (receipt: EcirSignedApproval) =
        let s = receipt.Scope
        JsonSerializer.Serialize(
            {| schemaVersion = receipt.SchemaVersion
               keyId = receipt.KeyId
               approver = receipt.Approver
               issuedAt = receipt.IssuedAt
               expiresAt = receipt.ExpiresAt
               scope =
                   {| groupId = s.GroupId
                      cohortId = s.CohortId
                      sourceCommit = s.SourceCommit
                      manifestDigest = s.ManifestDigest
                      blueprintDigest = s.BlueprintDigest
                      requirementKeys = s.RequirementKeys
                      decisionIds = s.DecisionIds |}
               signature = receipt.Signature |})

    let tests =
        [ t "ECDSA owner receipt authorizes only its exact, bounded scope" (fun () ->
              withApproval (fun trusted sign ->
                  let signed = sign template
                  let observed =
                      match EcirApprovals.verify trusted scope now signed with
                      | Ok evidence -> evidence
                      | Error reason -> failwith reason
                  Assert.equal scope.BlueprintDigest observed.BlueprintDigest
                  Assert.equal scope.SourceCommit observed.SourceCommit
                  Assert.equal 2 observed.RequirementKeys.Length
                  Assert.equal 2 observed.DecisionIds.Length
                  Assert.equal "owner" observed.Approver))

          t "tampering with digest, group, cohort, commit, requirement or decision invalidates receipt" (fun () ->
              withApproval (fun trusted sign ->
                  let signed = sign template
                  let variants =
                      [ { signed with Scope = { scope with BlueprintDigest = source } }
                        { signed with Scope = { scope with SourceCommit = String('d', 40) } }
                        { signed with Scope = { scope with GroupId = "OTHER" } }
                        { signed with Scope = { scope with CohortId = "OTHER" } }
                        { signed with Scope = { scope with RequirementKeys = [ "docs/req.md#R-1" ] } }
                        { signed with Scope = { scope with DecisionIds = [ "DEC-1" ] } }
                        { signed with Approver = "somebody-else" }
                        { signed with ExpiresAt = "2026-10-11T15:00:00Z" } ]
                  variants |> List.iter (isRejected trusted scope now)
                  isRejected trusted { scope with BlueprintDigest = source } now signed
                  isRejected trusted { scope with DecisionIds = [ "DEC-1" ] } now signed))

          t "unsigned, unknown signer, invalid public key, revoked or mismatched signer fails" (fun () ->
              withApproval (fun trusted sign ->
                  let signed = sign template
                  isRejected trusted scope now { signed with Signature = "" }
                  isRejected Map.empty scope now signed
                  isRejected trusted scope now { signed with KeyId = "unknown" }
                  isRejected (Map.ofList [ "governance-v1", { Approver = "other"; PublicKeyPem = trusted["governance-v1"].PublicKeyPem } ]) scope now signed
                  isRejected (Map.ofList [ "governance-v1", { Approver = "owner"; PublicKeyPem = "bad" } ]) scope now signed
                  let flipped = Convert.FromBase64String signed.Signature
                  flipped[0] <- flipped[0] ^^^ 0x01uy
                  isRejected trusted scope now { signed with Signature = Convert.ToBase64String flipped }))

          t "expiry, future time, excessive lifetime and malformed UTC are denied" (fun () ->
              withApproval (fun trusted sign ->
                  let signed = sign template
                  isRejected trusted scope (now.AddDays(2.)) signed
                  isRejected trusted scope (now.AddDays(-1.)) signed
                  isRejected trusted scope now (sign { template with ExpiresAt = "2026-10-11T16:00:00Z" })
                  isRejected trusted scope now (sign { template with IssuedAt = "2026-10-09T16:00:00+00:00" })
                  isRejected trusted scope now (sign { template with ExpiresAt = template.IssuedAt })))

          t "canonical signature binding is order-independent but rejects duplicate requirement IDs" (fun () ->
              withApproval (fun trusted sign ->
                  let signed = sign template
                  let reorderedScope =
                      { scope with
                          RequirementKeys = List.rev scope.RequirementKeys
                          DecisionIds = List.rev scope.DecisionIds }
                  let reordered = { signed with Scope = reorderedScope }
                  Assert.isTrue (EcirApprovals.verify trusted scope now reordered |> Result.isOk)
                      "reordering reference sets invalidated a valid signature"
                  isRejected trusted scope now
                      (sign { template with Scope = { scope with RequirementKeys = [ "docs/req.md#R-1"; "docs/req.md#R-1" ] } })))

          t "strict JSON decoder rejects extra, duplicate, missing and invalid fields" (fun () ->
              withApproval (fun trusted sign ->
                  let signed = sign template
                  let valid = serialized signed
                  let parsed =
                      match EcirApprovals.read valid with
                      | Ok value -> value
                      | Error problem -> failwith problem
                  Assert.isTrue (EcirApprovals.verify trusted scope now parsed |> Result.isOk)
                      "round-trip approval lost its signature"

                  let unknown = valid.Replace("\"signature\":", "\"unexpected\":1,\"signature\":")
                  let duplicated = valid.Replace("\"keyId\":", "\"keyId\":\"forged\",\"keyId\":")
                  let missing = valid.Replace("\"schemaVersion\":\"ecir.approval/1\",", "")
                  let malformed = valid.Replace("\"requirementKeys\":[", "\"requirementKeys\":{")
                  for value in [ unknown; duplicated; missing; malformed; "{invalid json}" ] do
                      Assert.isTrue (EcirApprovals.read value |> Result.isError) "unsafe receipt JSON was accepted"))

          t "a valid signature on a different 256-bit curve is denied" (fun () ->
              // This is a genuinely valid signature, not malformed key material.
              // Linux/OpenSSL supports secp256k1; no fallback to P-256 is allowed.
              use otherCurve = ECDsa.Create(ECCurve.CreateFromValue "1.3.132.0.10")
              Assert.equal 256 otherCurve.KeySize
              let trusted =
                  Map.ofList [ template.KeyId,
                               { Approver = template.Approver
                                 PublicKeyPem = otherCurve.ExportSubjectPublicKeyInfoPem() } ]
              let payload = EcirApprovals.signingPayload template
              let signature =
                  otherCurve.SignData(payload, HashAlgorithmName.SHA256,
                                      DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
              Assert.isTrue
                  (otherCurve.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                                         DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                  "alternate-curve fixture must have a valid signature"
              isRejected trusted scope now { template with Signature = Convert.ToBase64String signature })

          t "tampering fails cryptography even when the new scope matches host expectations" (fun () ->
              withApproval (fun trusted sign ->
                  let signed = sign template
                  let variants =
                      [ { scope with ManifestDigest = blueprint }
                        { scope with BlueprintDigest = source }
                        { scope with SourceCommit = String('d', 40) }
                        { scope with GroupId = "GROUP-2" }
                        { scope with CohortId = "COHORT-2" }
                        { scope with RequirementKeys = [ "docs/req.md#R-3" ] }
                        { scope with DecisionIds = [ "DEC-3" ] } ]
                  for changed in variants do
                      // Exact-scope checks pass; only signature binding can refuse.
                      isRejected trusted changed now { signed with Scope = changed }
                  isRejected trusted scope now { signed with IssuedAt = "2026-10-09T15:00:00Z" }
                  isRejected trusted scope now { signed with ExpiresAt = "2026-10-10T14:00:00Z" }))

          t "signature representation and exact time boundaries are enforced" (fun () ->
              withApproval (fun trusted sign ->
                  let signed = sign template
                  let issued = DateTimeOffset(2026, 10, 9, 16, 0, 0, TimeSpan.Zero)
                  let expires = DateTimeOffset(2026, 10, 10, 15, 0, 0, TimeSpan.Zero)
                  Assert.isTrue (EcirApprovals.verify trusted scope issued signed |> Result.isOk)
                      "approval must be valid at issuance"
                  isRejected trusted scope expires signed
                  isRejected trusted scope now { signed with Signature = "not-base64!" }
                  isRejected trusted scope now { signed with Signature = Convert.ToBase64String(Array.zeroCreate<byte> 63) }
                  use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
                  let derTrusted =
                      Map.ofList [ template.KeyId,
                                   { Approver = template.Approver
                                     PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() } ]
                  let der = key.SignData(EcirApprovals.signingPayload template, HashAlgorithmName.SHA256,
                                         DSASignatureFormat.Rfc3279DerSequence)
                  isRejected derTrusted scope now { template with Signature = Convert.ToBase64String der })) ]
