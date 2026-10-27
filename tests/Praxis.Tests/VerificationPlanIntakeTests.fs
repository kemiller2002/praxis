namespace Praxis.Tests

open System
open System.IO
open System.Text
open Praxis.Domain.Verification
open Praxis.Domain.Work

/// Admission of a compiled verification plan through the input-document
/// lifecycle (`praxis inbox admit-plan`): identity recomputation against the
/// producer's canonical writer, refusal of altered or incomplete plans, and
/// the CLI end to end (claim -> admit-plan -> commit -> complete).
[<RequireQualifiedAccess>]
module VerificationPlanIntakeTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json"))
           && Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "verification-plan")) then
            directory.FullName
        elif isNull directory.Parent then failwith "Could not locate repository root"
        else repositoryRoot directory.Parent

    let private fixturePath () =
        Path.Combine(repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory())), "tests", "fixtures", "verification-plan", "plan.json")

    /// Digest computed for the fixture by an independent implementation of
    /// the producer's writer (Python `json.dumps(indent=2, ensure_ascii=True)`
    /// plus a newline), so the test is not Praxis agreeing with itself.
    let private fixtureDigest = "61029ee4580ea59aae08485dc2c92fecbd631a0a70a1430d76758db11a84bedb"

    let private text (node: System.Text.Json.Nodes.JsonNode) = node.GetValue<string>()

    let private bytes () = File.ReadAllBytes(fixturePath ())

    let private sourceOf (content: byte array) =
        { Path = ".praxis/inbox/documents/verification-plan.json"
          FileName = "verification-plan.json"
          Sha256 = VerificationPlanIntake.sha256 content
          Size = int64 content.Length }

    let private replace (oldText: string) (newText: string) (content: byte array) =
        let text = Encoding.UTF8.GetString content
        Assert.isTrue (text.Contains oldText) $"fixture lacks {oldText}"
        Encoding.UTF8.GetBytes(text.Replace(oldText, newText))

    let private refusedWith (fragment: string) (content: byte array) =
        match VerificationPlanIntake.problems (sourceOf content) content with
        | [], _ -> failwith $"expected refusal containing '{fragment}'"
        | problems, identity ->
            Assert.isTrue identity.IsNone "a refused plan has no identity"
            Assert.isTrue (problems |> List.exists (fun p -> p.Contains fragment)) $"expected '{fragment}' in {problems}"

    let tests: TestCase list =
        [ { Name = "verification plan: identity recomputes over the producer's canonical writer (non-ASCII, escapes, order)"
            Run =
              fun () ->
                  let content = bytes ()

                  match VerificationPlanIntake.problems (sourceOf content) content with
                  | [], Some identity ->
                      Assert.equal fixtureDigest identity.Digest
                      Assert.equal ("VP-" + fixtureDigest.Substring(0, 12)) identity.PlanId
                      Assert.equal 2 identity.Requirements
                      Assert.equal 3 identity.Obligations
                      Assert.equal 1 identity.MissingTestObligations
                      Assert.equal "kemiller2002/indy-init" identity.SourceRepository
                  | problems, _ -> failwith $"fixture refused: {problems}" }

          { Name = "verification plan: an altered plan, a wrong claim digest or a dropped requirement is refused"
            Run =
              fun () ->
                  bytes () |> replace "\"title\": \"API\"" "\"title\": \"APl\"" |> refusedWith "does not match the recomputed"
                  bytes () |> replace "\"requirements\": 2," "\"requirements\": 3," |> refusedWith "scope.requirements"
                  bytes () |> replace "VPO-0000000000000003" "VPO-0000000000000001" |> refusedWith "appears more than once"
                  bytes () |> replace "indy-init.verification-plan/1" "indy-init.verification-plan/2" |> refusedWith "schema is not"

                  let content = bytes ()
                  let tampered = { sourceOf content with Sha256 = String('0', 64) }

                  match VerificationPlanIntake.problems tampered content with
                  | [ problem ], None -> Assert.isTrue (problem.Contains "the claim recorded") problem
                  | other -> failwith $"unexpected {other}"

                  let notJson = Encoding.UTF8.GetBytes "{"
                  refusedWith "not JSON" notJson }

          { Name = "verification plan CLI: claim, admit-plan, commit, complete; handoff names Ordo's command and the digest"
            Run =
              fun () ->
                  let root = CliHarness.temporaryDirectory "praxis-verification-plan"

                  try
                      CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
                      CliHarness.git root [ "config"; "user.email"; "t@example.com" ] |> ignore
                      CliHarness.git root [ "config"; "user.name"; "T" ] |> ignore
                      let inboxFile = Path.Combine(root, ".praxis", "inbox", "documents", "verification-plan.json")
                      Directory.CreateDirectory(Path.GetDirectoryName inboxFile) |> ignore
                      File.Copy(fixturePath (), inboxFile)
                      CliHarness.commitAll root "plan arrives"
                      let identity = [ "--actor-kind"; "human"; "--actor"; "tester" ]

                      let claimed = CliHarness.rosOk root ([ "inbox"; "claim"; ".praxis/inbox/documents/verification-plan.json"; "--json" ] @ identity)
                      let claimId = text ((CliHarness.json claimed.Out)["claimId"])
                      CliHarness.commitAll root "claim"

                      let admitted = CliHarness.rosOk root ([ "inbox"; "admit-plan"; claimId; "--json" ] @ identity)
                      let handoff = CliHarness.json admitted.Out
                      let planId = "VP-" + fixtureDigest.Substring(0, 12)
                      Assert.equal "praxis.verification-plan-handoff/1" (text (handoff["schema"]))
                      Assert.equal fixtureDigest (text (handoff["plan"]["digest"]))
                      Assert.equal claimId (text (handoff["claimId"]))
                      Assert.equal claimId (text (handoff["derived_from"][0]))
                      Assert.equal "ordo" (text (handoff["ordering"]["owner"]))
                      Assert.isTrue ((text (handoff["ordering"]["command"])).Contains $"--expected-digest {fixtureDigest}") "ordering command pins the digest"

                      // The admitted plan is the claimed input, byte for byte.
                      let admittedPath = $"verification-plans/{planId}.verification-plan.json"
                      Assert.isTrue (File.ReadAllBytes(Path.Combine(root, admittedPath)) = bytes ()) "admitted bytes unchanged"
                      Assert.isTrue (File.Exists(Path.Combine(root, $"verification-plans/{planId}.handoff.json"))) "handoff written"

                      // Complete is refused until the admitted files are committed.
                      let early = CliHarness.ros root [ "inbox"; "complete"; claimId ]
                      Assert.equal 2 early.Exit
                      CliHarness.commitAll root "admit plan"
                      CliHarness.rosOk root [ "inbox"; "complete"; claimId ] |> ignore
                      let record = CliHarness.read root $".praxis/processed/{claimId}/claim.json"
                      Assert.isTrue (record.Contains admittedPath) "claim records the admitted plan as a derivation"
                  finally
                      CliHarness.removeDirectory root }

          { Name = "verification plan CLI: an altered plan is refused and nothing is admitted"
            Run =
              fun () ->
                  let root = CliHarness.temporaryDirectory "praxis-verification-plan-refused"

                  try
                      CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
                      CliHarness.git root [ "config"; "user.email"; "t@example.com" ] |> ignore
                      CliHarness.git root [ "config"; "user.name"; "T" ] |> ignore
                      CliHarness.write root ".praxis/inbox/documents/plan.json" (Encoding.UTF8.GetString(bytes () |> replace "\"title\": \"API\"" "\"title\": \"API!\""))
                      CliHarness.commitAll root "plan arrives"
                      let identity = [ "--actor-kind"; "human"; "--actor"; "tester" ]
                      let claimed = CliHarness.rosOk root ([ "inbox"; "claim"; ".praxis/inbox/documents/plan.json"; "--json" ] @ identity)
                      let claimId = text ((CliHarness.json claimed.Out)["claimId"])
                      let refused = CliHarness.ros root ([ "inbox"; "admit-plan"; claimId ] @ identity)
                      Assert.equal 2 refused.Exit
                      Assert.isTrue (not (Directory.Exists(Path.Combine(root, "verification-plans")))) "nothing admitted"
                  finally
                      CliHarness.removeDirectory root } ]
