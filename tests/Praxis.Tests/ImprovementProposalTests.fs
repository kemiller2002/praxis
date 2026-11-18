namespace Praxis.Tests

open System
open Praxis.Domain.Autonomy
open Praxis.Domain.Learning
open Praxis.Domain.Provenance

/// PRX-LEARN-001: governed self-improvement. Each case is a way a learning
/// loop can go wrong: accepting a lesson that only memorises its own
/// failure, regressing earlier successes, or an agent approving a change
/// to the governance that constrains it.
[<RequireQualifiedAccess>]
module ImprovementProposalTests =
    let private t name run = { Name = $"governed learning: {name}"; Run = run }
    let private at = DateTimeOffset(2026, 11, 18, 18, 0, 0, TimeSpan.Zero)

    let private actor kind id =
        { Kind = kind; Id = id; Provider = None; Model = None; Runtime = None }

    let private agent = actor ActorKind.Agent "anthropic/claude-code"
    let private otherAgent = actor ActorKind.Agent "openai/codex"
    let private reviewer = actor ActorKind.Human "owner"

    // The worked case comes from EXP-AE-001 round 2: the EDF-derived protocol
    // ranked fixing the shared predicate as "premature", patched only the
    // reported function, and claimed completion while a second caller
    // (the nightly conflicts report) stayed broken.
    let private proposal target =
        { Id = "IMP-0001"
          Version = 1
          Failures =
            [ { Source = FailureSource.FailedVerification
                Summary = "EDF run patched overlaps() locally; conflicts() kept the closed-interval bug"
                EvidenceRefs = [ "research/2026-autonomous-engineering/autobuild-comparison/experiments/clarity-edf/raw/round2/graded-round2.json#D-T4" ]
                ObservedAt = at } ]
          RootCause = RootCause.ContradictoryInstruction
          CorrectiveAction = "Before ranking a shared-origin fix as premature, enumerate every caller of the defective origin and test each one"
          Target = target
          ExpectedImprovement = "No completion claim while another caller of a fixed defect still fails"
          EstimatedCost = 2m
          RollbackPlan = "Revert the prompt to EDF protocol D v1"
          ProposedBy = agent
          DerivedFrom = [ "EXP-AE-001-R2" ] }

    let private case id cause passed =
        { CaseId = id; RootCause = cause; Passed = passed; EvidenceRef = $"runs/{id}" }

    let private sound =
        { Counterfactual = [ case "D-T4" RootCause.ContradictoryInstruction true ]
          HeldOut = [ case "shared-helper-2" RootCause.ContradictoryInstruction true ]
          Regression = [ case "D-T1" RootCause.MissingKnowledge true; case "D-T6" RootCause.MissingKnowledge true ]
          EvaluatedAt = at }

    let private prompt = proposal (ImprovementTarget.Prompt "edf-protocol-d")
    let private governance = proposal (ImprovementTarget.Governance ProtectedClass.VerificationPolicy)

    let private codes (state: Result<ProposalState, ProposalFinding list>) =
        match state with
        | Ok(ProposalState.Rejected reasons) -> reasons |> List.map (fun reason -> reason.Split(':').[0]) |> List.distinct
        | Error findings -> findings |> List.map _.Code |> List.distinct
        | Ok other -> failwith $"expected a refusal, got {other}"

    let tests =
        [ t "a complete proposal that passes counterfactual, held-out and regression cases is evaluated" (fun () ->
              match ImprovementProposal.evaluate prompt ProposalState.Proposed sound with
              | Ok(ProposalState.Evaluated _) -> ()
              | other -> failwith $"{other}")

          t "passing only the original failure is memorisation, not a generalisable rule" (fun () ->
              let memorised = { sound with HeldOut = [] }
              Assert.equal [ ImprovementProposal.GeneralizationUnprovenCode ] (codes (ImprovementProposal.evaluate prompt ProposalState.Proposed memorised)))

          t "a held-out case that is the original case, or of another root cause, does not count" (fun () ->
              let reused =
                  { sound with
                      HeldOut = [ case "D-T4" RootCause.ContradictoryInstruction true; case "x" RootCause.ToolDefect true ] }

              Assert.equal
                  [ ImprovementProposal.MemorizedCode; ImprovementProposal.GeneralizationUnprovenCode ]
                  (codes (ImprovementProposal.evaluate prompt ProposalState.Proposed reused)))

          t "a regression in a previously successful case rejects the proposal, and so does skipping regression" (fun () ->
              let regressed = { sound with Regression = [ case "D-T1" RootCause.MissingKnowledge false ] }
              Assert.equal [ ImprovementProposal.RegressionCode ] (codes (ImprovementProposal.evaluate prompt ProposalState.Proposed regressed))
              Assert.equal [ ImprovementProposal.RegressionCode ] (codes (ImprovementProposal.evaluate prompt ProposalState.Proposed { sound with Regression = [] })))

          t "a counterfactual that still fails, or is missing, rejects the proposal" (fun () ->
              let stillFails = { sound with Counterfactual = [ case "D-T4" RootCause.ContradictoryInstruction false ] }
              Assert.equal [ ImprovementProposal.CounterfactualFailedCode ] (codes (ImprovementProposal.evaluate prompt ProposalState.Proposed stillFails)))

          t "a proposal without evidence, action, rollback or proposer is incomplete" (fun () ->
              let hollow =
                  { prompt with
                      Failures = [ { prompt.Failures.Head with EvidenceRefs = [ " " ] } ]
                      CorrectiveAction = ""
                      RollbackPlan = ""
                      ProposedBy = actor ActorKind.Agent "" }

              Assert.equal 4 (ImprovementProposal.validate hollow).Length
              Assert.equal [ ImprovementProposal.IncompleteCode ] (codes (ImprovementProposal.evaluate hollow ProposalState.Proposed sound)))

          t "an agent can never approve a change to protected governance, even another agent's" (fun () ->
              let evaluated = ProposalState.Evaluated sound
              Assert.equal [ ImprovementProposal.HumanApprovalRequiredCode ] (codes (ImprovementProposal.approve governance evaluated otherAgent at))

              Assert.equal
                  [ ImprovementProposal.SelfApprovalCode; ImprovementProposal.HumanApprovalRequiredCode ]
                  (codes (ImprovementProposal.approve governance evaluated agent at))

              match ImprovementProposal.approve governance evaluated reviewer at with
              | Ok(ProposalState.Approved(_, approver, _)) -> Assert.equal reviewer approver
              | other -> failwith $"{other}")

          t "a human proposer cannot approve their own governance change either" (fun () ->
              let byOwner = { governance with ProposedBy = reviewer }
              Assert.equal [ ImprovementProposal.SelfApprovalCode ] (codes (ImprovementProposal.approve byOwner (ProposalState.Evaluated sound) reviewer at)))

          t "an unprotected prompt change may be approved by a different agent but never by its proposer" (fun () ->
              let evaluated = ProposalState.Evaluated sound
              Assert.equal [ ImprovementProposal.SelfApprovalCode ] (codes (ImprovementProposal.approve prompt evaluated agent at))

              match ImprovementProposal.approve prompt evaluated otherAgent at with
              | Ok(ProposalState.Approved _) -> ()
              | other -> failwith $"{other}")

          t "only an evaluated proposal can be approved, only an approved one applied, only an applied one rolled back" (fun () ->
              Assert.equal [ ImprovementProposal.IllegalTransitionCode ] (codes (ImprovementProposal.approve prompt ProposalState.Proposed reviewer at))
              Assert.equal [ ImprovementProposal.IllegalTransitionCode ] (codes (ImprovementProposal.apply (ProposalState.Evaluated sound) "v2" at))
              Assert.equal [ ImprovementProposal.IllegalTransitionCode ] (codes (ImprovementProposal.rollBack ProposalState.Proposed "bad" at))
              Assert.equal [ ImprovementProposal.IllegalTransitionCode ] (codes (ImprovementProposal.evaluate prompt (ProposalState.Evaluated sound) sound)))

          t "the full lifecycle records the approver, the applied version and the rollback" (fun () ->
              let lifecycle =
                  ImprovementProposal.evaluate governance ProposalState.Proposed sound
                  |> Result.bind (fun state -> ImprovementProposal.approve governance state reviewer at)
                  |> Result.bind (fun state -> ImprovementProposal.apply state "verification-policy@2" at)

              match lifecycle with
              | Ok(ProposalState.Applied(_, approver, version, _) as applied) ->
                  Assert.equal reviewer approver
                  Assert.equal "verification-policy@2" version

                  match ImprovementProposal.rollBack applied "held-out failure rate rose" at with
                  | Ok(ProposalState.RolledBack(reason, _)) -> Assert.equal "held-out failure rate rose" reason
                  | other -> failwith $"{other}"
              | other -> failwith $"{other}")

          t "applying needs a version and rolling back needs a reason" (fun () ->
              let approved = ProposalState.Approved(sound, reviewer, at)
              Assert.equal [ ImprovementProposal.IncompleteCode ] (codes (ImprovementProposal.apply approved " " at))
              let applied = ProposalState.Applied(sound, reviewer, "v2", at)
              Assert.equal [ ImprovementProposal.IncompleteCode ] (codes (ImprovementProposal.rollBack applied "" at))) ]
