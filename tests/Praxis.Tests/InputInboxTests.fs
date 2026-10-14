namespace Praxis.Tests

open System
open System.IO
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work

/// The input-document lifecycle (DER-09, DER-10; PRAXIS-MISC-01): pure
/// rules, crash/recovery at every write of every operation through an
/// in-memory port that fails on demand, and the CLI end to end on a real
/// Git repository.
[<RequireQualifiedAccess>]
module InputInboxTests =
    let private actor =
        { Kind = ActorKind.Human
          Id = "tester"
          Provider = None
          Model = None
          Runtime = None }

    let private source path content =
        { Path = path
          FileName = Path.GetFileName(path: string)
          Sha256 = content
          Size = int64 (String.length content) }

    /// An in-memory inbox whose content strings are their own digests. Every
    /// mutation counts as one step; `failAt` makes that step fail before it
    /// takes effect, which is what a crash between two writes looks like.
    type private Memory() =
        member val Inbox: Map<string, string> = Map.empty with get, set
        member val Claims: Map<ClaimLocation * string, InputClaim * string option> = Map.empty with get, set
        member val Committed: Set<string> = Set.empty with get, set
        member val Existing: Set<string> = Set.empty with get, set
        member val Steps = 0 with get, set
        member val FailAt: int option = None with get, set

        member this.Step(effect: unit -> unit) =
            this.Steps <- this.Steps + 1

            if this.FailAt = Some this.Steps then Error "simulated crash"
            else
                effect ()
                Ok()

        /// Every place the input content is held intact.
        member this.Holdings(content: string) =
            (this.Inbox |> Map.toList |> List.filter (fun (_, value) -> value = content) |> List.map fst)
            @ (this.Claims |> Map.toList |> List.choose (fun ((location, id), (_, copy)) -> if copy = Some content then Some $"{ClaimLocation.code location}/{id}" else None))

    let private port (memory: Memory) : InputInboxPort =
        let claimCopy location (claim: InputClaim) =
            memory.Claims |> Map.tryFind (location, claim.ClaimId) |> Option.bind snd

        { Now = fun () -> "2026-10-06T00:00:00.000Z"
          ReadPending = fun path -> Ok(memory.Inbox |> Map.tryFind path |> Option.map (source path))
          ListPending = fun () -> Ok(memory.Inbox |> Map.toList |> List.map (fun (path, content) -> source path content))
          ListClaims = fun () -> Ok(memory.Claims |> Map.toList |> List.map (fun ((location, _), (claim, _)) -> location, claim))
          WriteClaim =
            fun location claim ->
                memory.Step(fun () ->
                    let copy = claimCopy location claim
                    memory.Claims <- memory.Claims.Add((location, claim.ClaimId), (claim, copy)))
          MoveClaim =
            fun from target claimId ->
                memory.Step(fun () ->
                    let entry = memory.Claims[(from, claimId)]
                    memory.Claims <- memory.Claims.Remove((from, claimId)).Add((target, claimId), entry))
          RemoveClaim = fun location claimId -> memory.Step(fun () -> memory.Claims <- memory.Claims.Remove((location, claimId)))
          InboxDigest = fun source -> Ok(memory.Inbox |> Map.tryFind source.Path)
          ClaimedCopyDigest = fun location claim -> Ok(claimCopy location claim)
          CopyIntoClaim =
            fun claim ->
                memory.Step(fun () ->
                    let record, _ = memory.Claims[(ClaimLocation.Processing, claim.ClaimId)]
                    memory.Claims <- memory.Claims.Add((ClaimLocation.Processing, claim.ClaimId), (record, Some memory.Inbox[claim.Source.Path])))
          CopyBackToInbox =
            fun claim ->
                memory.Step(fun () ->
                    let content = claimCopy ClaimLocation.Processing claim |> Option.get
                    memory.Inbox <- memory.Inbox.Add(claim.Source.Path, content))
          RemoveInboxSource = fun claim -> memory.Step(fun () -> memory.Inbox <- memory.Inbox.Remove claim.Source.Path)
          ObserveDerivations =
            fun claim ->
                claim.Derivations
                |> List.map (fun derivation ->
                    let target = DerivationTarget.value derivation.Target

                    { Derivation = derivation
                      Exists = memory.Existing.Contains target
                      Committed = memory.Committed.Contains target
                      LineageNamesClaim = None })
                |> Ok }

    let private path = ".praxis/inbox/documents/notes.md"
    let private content = "content-digest"

    let private memoryWithInput () =
        let memory = Memory()
        memory.Inbox <- Map.ofList [ path, content ]
        memory

    let private claimedMemory () =
        let memory = memoryWithInput ()
        let claim, _ = InputInboxOperations.claim (port memory) path actor None |> Result.defaultWith (fun error -> failwith (InputInboxError.message error))
        memory, claim

    /// Runs `operation` with a crash at step `k`, checks the input was never
    /// lost, then recovers and retries with a healthy port.
    let private crashAt (memory: Memory) (k: int) (operation: InputInboxPort -> Result<'a, InputInboxError>) =
        memory.Steps <- 0
        memory.FailAt <- Some k
        let crashed = operation (port memory)
        Assert.isTrue (not (memory.Holdings content).IsEmpty) $"crash at step {k}: the input is held nowhere"
        memory.FailAt <- None
        memory.Steps <- 0
        InputInboxOperations.recover (port memory) |> Result.defaultWith (fun error -> failwith (InputInboxError.message error)) |> ignore

        match crashed with
        | Ok value -> Ok value
        | Error _ -> operation (port memory)

    let private steps (operation: Memory -> unit) =
        let memory = memoryWithInput ()
        operation memory
        memory.Steps

    let tests =
        [ { Name = "input inbox: a transfer copies before it removes and refuses anything that is not the recorded input"
            Run =
              fun () ->
                  let step source target =
                      InputInbox.nextTransferStep { Expected = "a"; SourceDigest = source; TargetDigest = target }

                  Assert.equal TransferStep.CopyThenRemoveSource (step (Some "a") None)
                  Assert.equal TransferStep.RemoveSource (step (Some "a") (Some "a"))
                  Assert.equal TransferStep.Finished (step None (Some "a"))
                  Assert.isTrue (step None None).IsUnsafe "lost input is unsafe"
                  Assert.isTrue (step (Some "b") None).IsUnsafe "changed source is unsafe"
                  Assert.isTrue (step (Some "a") (Some "b")).IsUnsafe "different target is unsafe" }

          { Name = "input inbox: claim IDs are deterministic per path and content"
            Run =
              fun () ->
                  let id = InputInbox.claimId path content
                  Assert.equal id (InputInbox.claimId path content)
                  Assert.isTrue (id <> InputInbox.claimId path "other") "content changes the ID"
                  Assert.isTrue (id <> InputInbox.claimId "input-documents/notes.md" content) "path changes the ID"
                  Assert.isTrue (InputInbox.isClaimId id) id }

          { Name = "input inbox: claiming is write-ahead and recovers from a crash at every step without losing the input"
            Run =
              fun () ->
                  let total = steps (fun memory -> InputInboxOperations.claim (port memory) path actor None |> ignore)
                  Assert.equal 4 total

                  for k in 1..total do
                      let memory = memoryWithInput ()
                      let claim, _ = crashAt memory k (fun port -> InputInboxOperations.claim port path actor None) |> Result.defaultWith (fun error -> failwith $"retry after crash at step {k}: {InputInboxError.message error}")
                      Assert.equal InputClaimState.Claimed claim.State
                      Assert.equal [ $"processing/{claim.ClaimId}" ] (memory.Holdings content)
                      Assert.equal 1 memory.Claims.Count }

          { Name = "input inbox: a repeated claim returns the existing claim and creates nothing"
            Run =
              fun () ->
                  let memory, claim = claimedMemory ()
                  let again, created = InputInboxOperations.claim (port memory) path actor None |> Result.defaultWith (fun error -> failwith (InputInboxError.message error))
                  Assert.equal false created
                  Assert.equal claim.ClaimId again.ClaimId
                  Assert.equal 1 memory.Claims.Count }

          { Name = "input inbox: derivations are recorded with provenance and are idempotent"
            Run =
              fun () ->
                  let memory, claim = claimedMemory ()
                  let derive () = InputInboxOperations.derive (port memory) claim.ClaimId DerivationKind.Requirement (DerivationTarget.Artifact "RQ-X-1") "the rule" (Some "## Scope") actor (Some "EXE-1")
                  let first, changed = derive () |> Result.defaultWith (fun error -> failwith (InputInboxError.message error))
                  let _, again = derive () |> Result.defaultWith (fun error -> failwith (InputInboxError.message error))
                  Assert.equal true changed
                  Assert.equal false again
                  let derivation = List.exactlyOne first.Derivations
                  Assert.equal (Some "EXE-1") derivation.ExecutionId
                  Assert.equal actor derivation.Actor
                  Assert.equal (Some "## Scope") derivation.Locator }

          { Name = "input inbox: completion is refused until every derived change is durably reconciled"
            Run =
              fun () ->
                  let memory, claim = claimedMemory ()
                  let target = "requirements/NEW.md"
                  InputInboxOperations.derive (port memory) claim.ClaimId DerivationKind.Requirement (DerivationTarget.RepositoryPath target) "new rule" None actor None |> ignore

                  let refusal () =
                      match InputInboxOperations.complete (port memory) claim.ClaimId None with
                      | Error(InputInboxError.NotReconciled problems) -> problems
                      | other -> failwith $"expected a refusal, got {other}"

                  Assert.isTrue ((refusal ()) |> List.exists (fun problem -> problem.Contains "does not exist")) "missing"
                  memory.Existing <- Set.singleton target
                  Assert.isTrue ((refusal ()) |> List.exists (fun problem -> problem.Contains "not committed")) "uncommitted"
                  Assert.equal [ $"processing/{claim.ClaimId}" ] (memory.Holdings content)
                  memory.Committed <- Set.singleton target
                  let completed = InputInboxOperations.complete (port memory) claim.ClaimId None |> Result.defaultWith (fun error -> failwith (InputInboxError.message error))
                  Assert.equal InputClaimState.Completed completed.State
                  Assert.equal [ $"processed/{claim.ClaimId}" ] (memory.Holdings content) }

          { Name = "input inbox: completing with nothing derived needs a stated reason, and an artifact must name the input in its lineage"
            Run =
              fun () ->
                  let memory, claim = claimedMemory ()

                  match InputInboxOperations.complete (port memory) claim.ClaimId None with
                  | Error(InputInboxError.NotReconciled [ problem ]) -> Assert.isTrue (problem.Contains "--no-derivations") problem
                  | other -> failwith $"expected a refusal, got {other}"

                  let derivation =
                      { Kind = DerivationKind.Decision
                        Target = DerivationTarget.Artifact "DF-X-1"
                        Summary = "s"
                        Locator = None
                        RecordedAt = "t"
                        Actor = actor
                        ExecutionId = None }

                  let problems =
                      InputInbox.completionProblems
                          { claim with Derivations = [ derivation ] }
                          [ { Derivation = derivation; Exists = true; Committed = true; LineageNamesClaim = Some false } ]
                          None

                  Assert.isTrue (problems |> List.exists (fun problem -> problem.Contains "derived_from")) (String.concat "; " problems)
                  let completed = InputInboxOperations.complete (port memory) claim.ClaimId (Some "meeting notes with no actionable content") |> Result.defaultWith (fun error -> failwith (InputInboxError.message error))
                  Assert.equal (Some "meeting notes with no actionable content") completed.NoDerivationReason }

          { Name = "input inbox: complete, release and reject each recover from a crash at every step without losing the input"
            Run =
              fun () ->
                  let check name (operation: string -> InputInboxPort -> Result<InputClaim, InputInboxError>) (prepare: Memory -> InputClaim -> unit) (expected: string -> string) =
                      let probe, probeClaim = claimedMemory ()
                      prepare probe probeClaim
                      probe.Steps <- 0
                      operation probeClaim.ClaimId (port probe) |> ignore
                      let total = probe.Steps
                      Assert.isTrue (total >= 2) $"{name} takes {total} steps"

                      for k in 1..total do
                          let memory, claim = claimedMemory ()
                          prepare memory claim
                          match crashAt memory k (operation claim.ClaimId) with
                          | Ok _ -> ()
                          // A release that recovery finished leaves no claim to release again.
                          | Error(InputInboxError.UnknownClaim _) when name = "release" -> ()
                          | Error error -> failwith $"{name}: retry after crash at step {k}: {InputInboxError.message error}"
                          Assert.equal [ expected claim.ClaimId ] (memory.Holdings content)

                  let committedDerivation (memory: Memory) (claim: InputClaim) =
                      memory.Existing <- Set.singleton "docs/x.md"
                      memory.Committed <- Set.singleton "docs/x.md"
                      InputInboxOperations.derive (port memory) claim.ClaimId DerivationKind.Reference (DerivationTarget.RepositoryPath "docs/x.md") "x" None actor None |> ignore

                  check "complete" (fun id port -> InputInboxOperations.complete port id None) committedDerivation (fun id -> $"processed/{id}")
                  check "release" (fun id port -> InputInboxOperations.release port id "not mine") (fun _ _ -> ()) (fun _ -> path)
                  check "reject" (fun id port -> InputInboxOperations.reject port id "spam" actor None) (fun _ _ -> ()) (fun id -> $"rejected/{id}") }

          { Name = "input inbox: a release never overwrites a different file at the inbox path"
            Run =
              fun () ->
                  let memory, claim = claimedMemory ()
                  memory.Inbox <- memory.Inbox.Add(path, "someone else's file")

                  match InputInboxOperations.release (port memory) claim.ClaimId "back" with
                  | Error(InputInboxError.ReleaseTargetOccupied _) -> ()
                  | other -> failwith $"expected a refusal, got {other}"

                  Assert.equal [ $"processing/{claim.ClaimId}" ] (memory.Holdings content) }

          { Name = "input inbox: the claim record round-trips through praxis.inbox-claim/1"
            Run =
              fun () ->
                  let _, claim = claimedMemory ()

                  let claim =
                      { claim with
                          Derivations =
                            [ { Kind = DerivationKind.OpenQuestion
                                Target = DerivationTarget.RepositoryPath "docs/q.md"
                                Summary = "who owns it?"
                                Locator = Some "line 4"
                                RecordedAt = "2026-10-06T00:00:00.000Z"
                                Actor = actor
                                ExecutionId = Some "EXE-1" } ] }

                  Assert.equal (Ok claim) (InputInboxJson.parse (InputInboxJson.render claim)) }

          { Name = "input inbox CLI: claim, derive, complete after commit, with list and recover on a Git repository"
            Run =
              fun () ->
                  let root = CliHarness.temporaryDirectory "praxis-input-inbox"

                  try
                      CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
                      CliHarness.git root [ "config"; "user.email"; "t@example.com" ] |> ignore
                      CliHarness.git root [ "config"; "user.name"; "T" ] |> ignore
                      CliHarness.write root ".praxis/inbox/documents/brief.md" "# Brief\n\nThe system must log every change.\n"
                      CliHarness.write root ".praxis/inbox/documents/README.md" "inbox"
                      CliHarness.commitAll root "inputs"
                      let identity = [ "--actor-kind"; "human"; "--actor"; "tester" ]

                      let listed = CliHarness.rosOk root [ "inbox"; "list" ]
                      Assert.isTrue (listed.Out.Contains ".praxis/inbox/documents/brief.md") listed.Out

                      let claimed = CliHarness.rosOk root ([ "inbox"; "claim"; ".praxis/inbox/documents/brief.md"; "--json" ] @ identity)
                      let claimedDocument = CliHarness.json claimed.Out
                      let claimId = claimedDocument["claimId"].GetValue<string>()
                      Assert.isTrue (not (File.Exists(Path.Combine(root, ".praxis/inbox/documents/brief.md")))) "the inbox copy moved"
                      Assert.isTrue (File.Exists(Path.Combine(root, $".praxis/processing/{claimId}/source/brief.md"))) "the claimed copy exists"
                      let again = CliHarness.rosOk root ([ "inbox"; "claim"; ".praxis/inbox/documents/brief.md" ] @ identity)
                      Assert.isTrue (again.Out.Contains $"already claimed: {claimId}") again.Out

                      CliHarness.write root "requirements/LOGGING.md" "Every change MUST be logged.\n"
                      CliHarness.rosOk root ([ "inbox"; "derive"; claimId; "--kind"; "requirement"; "--path"; "requirements/LOGGING.md"; "--summary"; "log every change"; "--locator"; "line 3" ] @ identity) |> ignore
                      let early = CliHarness.ros root [ "inbox"; "complete"; claimId ]
                      Assert.equal 2 early.Exit
                      Assert.isTrue (early.Err.Contains "derivations-not-reconciled") early.Err
                      Assert.isTrue (Directory.Exists(Path.Combine(root, $".praxis/processing/{claimId}"))) "still processing"

                      CliHarness.commitAll root "derived requirement"
                      CliHarness.rosOk root [ "inbox"; "complete"; claimId ] |> ignore
                      Assert.isTrue (File.Exists(Path.Combine(root, $".praxis/processed/{claimId}/source/brief.md"))) "archived with its source"
                      let record = CliHarness.read root $".praxis/processed/{claimId}/claim.json"
                      Assert.isTrue (record.Contains "\"state\": \"completed\"" && record.Contains "requirements/LOGGING.md") record

                      // A crash after the write-ahead record and the copy, before the inbox file was removed.
                      CliHarness.write root ".praxis/inbox/documents/second.md" "second"
                      let second = CliHarness.rosOk root ([ "inbox"; "claim"; ".praxis/inbox/documents/second.md"; "--json" ] @ identity)
                      let secondDocument = CliHarness.json second.Out
                      let secondId = secondDocument["claimId"].GetValue<string>()
                      let recordPath = $".praxis/processing/{secondId}/claim.json"
                      CliHarness.write root recordPath ((CliHarness.read root recordPath).Replace("\"state\": \"claimed\"", "\"state\": \"claiming\""))
                      CliHarness.write root ".praxis/inbox/documents/second.md" "second"
                      let recovered = CliHarness.rosOk root [ "inbox"; "recover" ]
                      Assert.isTrue (recovered.Out.Contains $"recovered {secondId}: finished claim") recovered.Out
                      Assert.isTrue (not (File.Exists(Path.Combine(root, ".praxis/inbox/documents/second.md")))) "the duplicate inbox copy was removed"
                      Assert.equal "second" (CliHarness.read root $".praxis/processing/{secondId}/source/second.md")

                      CliHarness.rosOk root [ "inbox"; "release"; secondId; "--reason"; "wrong repository" ] |> ignore
                      Assert.equal "second" (CliHarness.read root ".praxis/inbox/documents/second.md")
                      CliHarness.rosOk root ([ "inbox"; "reject"; ".praxis/inbox/documents/second.md"; "--reason"; "duplicate" ] @ identity) |> ignore
                      Assert.isTrue (File.Exists(Path.Combine(root, $".praxis/rejected/documents/{secondId}/source/second.md"))) "rejected with its source"

                      let final = CliHarness.rosOk root [ "inbox"; "list"; "--json" ]
                      let document = CliHarness.json final.Out
                      Assert.equal 0 (document["pending"].AsArray().Count)
                      Assert.equal 2 (document["claims"].AsArray().Count)
                  finally
                      CliHarness.removeDirectory root } ]
