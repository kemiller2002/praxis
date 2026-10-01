namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Praxis.Domain.Provenance
open Praxis.Domain.Remote
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Remote
open Praxis.Infrastructure.Work

/// Identity roles for remote execution (PRAXIS-REMOTE-02,
/// `PRX-REMOTE-005..007`): the requester's asserted actor, the executor's
/// observed facts, and the transport principal stay distinct; a runner
/// never becomes the actor of an agent's request; unknown stays unknown;
/// credentials in the runner's environment never reach the command.
[<RequireQualifiedAccess>]
module RemoteIdentityTests =
    /// A GitHub-hosted runner's environment, including the kinds of
    /// credentials and host identity markers that must never leak.
    let private runnerEnvironment =
        Map.ofList
            [ "PATH", "/usr/bin:/bin"
              "HOME", "/home/runner"
              "LANG", "C.UTF-8"
              "GITHUB_ACTIONS", "true"
              "GITHUB_RUN_ID", "9001"
              "GITHUB_RUN_ATTEMPT", "2"
              "GITHUB_WORKFLOW_REF", "octo/repo/.github/workflows/praxis-remote.yml@refs/heads/main"
              "GITHUB_REPOSITORY", "octo/repo"
              "GITHUB_ACTOR", "octocat"
              "GITHUB_TRIGGERING_ACTOR", "hubot"
              "RUNNER_ENVIRONMENT", "github-hosted"
              "GITHUB_TOKEN", "ghs_" + String('x', 36)
              "ACTIONS_ID_TOKEN_REQUEST_TOKEN", "secret-oidc"
              "ANTHROPIC_API_KEY", "sk-ant-" + String('y', 30)
              "OPENAI_API_KEY", "sk-" + String('z', 30)
              "CLAUDE_CODE_SESSION_ID", "runner-side-session"
              "ROS_ACTOR", "a-previous-agent"
              "ROS_ACTOR_KIND", "agent"
              "GIT_DIR", "/elsewhere/.git" ]

    let private runnerVariable name = runnerEnvironment |> Map.tryFind name

    let private executor =
        GitHubActionsExecutor.observe runnerVariable "3.4.0" |> Option.defaultWith (fun () -> failwith "expected a GitHub executor")

    let private agent: RequestActor =
        { Actor =
            { Kind = ActorKind.Agent
              Id = "example/cloud-agent"
              Provider = Some "example"
              Model = Some "unknown"
              Runtime = Some "cloud-agent" }
          SessionId = Some "agent-session-1" }

    /// Runs `body` with the process environment's identity-relevant
    /// variables replaced by `environment`, restoring them afterwards. The
    /// test process itself may be running under Claude Code or GitHub
    /// Actions, which is exactly the leakage these tests must rule out.
    let private withProcessEnvironment (environment: Map<string, string>) (body: unit -> unit) =
        let names =
            RemoteIdentity.hostIdentityVariables @ RemoteIdentity.executorVariables @ [ RemoteIdentity.AssuranceVariable ]

        let saved = names |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)

        try
            names |> List.iter (fun name -> Environment.SetEnvironmentVariable(name, environment |> Map.tryFind name |> Option.toObj))
            body ()
        finally
            saved |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

    let private createExecution () =
        let root = Path.Combine(Path.GetTempPath(), $"ros-remote-identity-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            let request: FileTelemetryExecutionRepository.CreateExecutionRequest =
                { WorkItemId = "WI-0100"
                  WorkType = "task"
                  Classifications = []
                  ClassificationRationale = None
                  ExecutionId = None
                  IdentityOverrides = IdentityInputs.empty }

            match FileTelemetryExecutionRepository.createExecution root request with
            | Ok(Some executionId) ->
                JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json"))) :?> JsonObject
            | Ok None -> failwith "expected an execution"
            | Error message -> failwith message
        finally
            Directory.Delete(root, true)

    let private text (node: JsonNode) =
        match node with
        | null -> None
        | value -> Some(value.GetValue<string>())

    let tests =
        [ { Name = "remote identity: the child environment drops every credential and host identity marker"
            Run =
              fun () ->
                  let child = RemoteIdentity.childEnvironment runnerEnvironment (Some agent) executor

                  [ "GITHUB_TOKEN"; "ACTIONS_ID_TOKEN_REQUEST_TOKEN"; "ANTHROPIC_API_KEY"; "OPENAI_API_KEY"; "GITHUB_ACTIONS"; "GITHUB_RUN_ID"; "CLAUDE_CODE_SESSION_ID"; "GIT_DIR" ]
                  |> List.iter (fun name -> Assert.isTrue (not (child.ContainsKey name)) $"{name} must not reach the child")

                  Assert.equal (Some "/usr/bin:/bin") (child.TryFind "PATH")
                  Assert.equal (Some "/home/runner") (child.TryFind "HOME")

                  child
                  |> Map.iter (fun name value ->
                      Assert.isTrue (not (SecretMaterial.looksLikeSecret value)) $"{name} carries credential material") }

          { Name = "remote identity: the asserted actor replaces anything a previous agent left in the environment"
            Run =
              fun () ->
                  let child = RemoteIdentity.childEnvironment runnerEnvironment (Some agent) executor
                  Assert.equal (Some "example/cloud-agent") (child.TryFind "ROS_ACTOR")
                  Assert.equal (Some "agent") (child.TryFind "ROS_ACTOR_KIND")
                  Assert.equal (Some "example") (child.TryFind "ROS_TELEMETRY_PROVIDER")
                  Assert.equal (Some "cloud-agent") (child.TryFind "ROS_TELEMETRY_RUNTIME")
                  Assert.equal (Some "agent-session-1") (child.TryFind "ROS_TELEMETRY_SESSION_ID")
                  Assert.equal None (child.TryFind "ROS_TELEMETRY_MODEL")
                  Assert.equal None (child.TryFind "ROS_TELEMETRY_RUN_ID")
                  Assert.equal (Some RemoteIdentity.AssertedByRequest) (child.TryFind RemoteIdentity.AssuranceVariable) }

          { Name = "remote identity: an absent actor is explicitly unknown, never the runner"
            Run =
              fun () ->
                  let child = RemoteIdentity.childEnvironment runnerEnvironment None executor
                  Assert.equal (Some "unknown") (child.TryFind "ROS_ACTOR_KIND")
                  Assert.equal (Some "unknown") (child.TryFind "ROS_ACTOR")
                  Assert.equal (Some "unknown") (child.TryFind "ROS_TELEMETRY_PROVIDER")
                  Assert.equal (Some "unknown") (child.TryFind "ROS_TELEMETRY_RUNTIME") }

          { Name = "remote identity: executor facts are observed from the adapter and round-trip through the child"
            Run =
              fun () ->
                  Assert.equal "github-actions" executor.Kind
                  Assert.equal (Some "9001") executor.RunId
                  Assert.equal (Some "2") executor.RunAttempt
                  Assert.equal (Some "github:hubot") executor.Principal
                  Assert.equal None (GitHubActionsExecutor.observe (fun _ -> None) "3.4.0")

                  let child = RemoteIdentity.childEnvironment runnerEnvironment (Some agent) executor
                  Assert.equal (Some executor) (RemoteIdentity.readExecutor child.TryFind)
                  Assert.equal None (RemoteIdentity.readExecutor (fun _ -> None)) }

          { Name = "remote identity: a runner executing an agent's request records the agent as actor and the runner as executor"
            Run =
              fun () ->
                  let child = RemoteIdentity.childEnvironment runnerEnvironment (Some agent) executor

                  withProcessEnvironment child (fun () ->
                      let record = createExecution ()
                      let identity = record["identity"] :?> JsonObject
                      Assert.equal (Some "example") (text identity["provider"])
                      Assert.equal (Some "cloud-agent") (text identity["runtime"])
                      Assert.equal (Some "agent") (text identity["actorKind"])
                      Assert.equal (Some "example/cloud-agent") (text identity["agentId"])
                      Assert.equal (Some "agent-session-1") (text identity["sessionId"])
                      // The runner's run ID belongs to the executor, not to the agent.
                      Assert.equal None (text identity["runId"])
                      Assert.equal (Some RemoteIdentity.AssertedByRequest) (text identity["assurance"])

                      let recordedExecutor = record["executor"] :?> JsonObject
                      Assert.equal (Some "github-actions") (text recordedExecutor["kind"])
                      Assert.equal (Some "9001") (text recordedExecutor["runId"])
                      Assert.equal (Some "github:hubot") (text recordedExecutor["principal"])
                      Assert.equal (Some "3.4.0") (text recordedExecutor["praxisVersion"])
                      Assert.equal (Some RemoteIdentity.ObservedByExecutor) (text recordedExecutor["assurance"])

                      let source = ((record["provenance"] :?> JsonObject)["sources"] :?> JsonArray)[0] :?> JsonObject
                      Assert.isTrue (text source["mechanism"] <> Some "whitelisted-github-actions-environment") "runner detection must not decide identity") }

          { Name = "remote identity: an unidentified requester stays unknown even on a GitHub runner"
            Run =
              fun () ->
                  let child = RemoteIdentity.childEnvironment runnerEnvironment None executor

                  withProcessEnvironment child (fun () ->
                      let record = createExecution ()
                      let identity = record["identity"] :?> JsonObject
                      Assert.equal (Some "unknown") (text identity["provider"])
                      Assert.equal (Some "unknown") (text identity["runtime"])
                      Assert.equal (Some "unknown") (text identity["actorKind"])
                      Assert.isTrue (text identity["provider"] <> Some "github") "the runner must not become the actor") }

          { Name = "remote identity: a local execution keeps its historical shape, with no executor or assurance field"
            Run =
              fun () ->
                  let local = Map.ofList [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "someone" ]

                  withProcessEnvironment local (fun () ->
                      let record = createExecution ()
                      Assert.isTrue (not (record.ContainsKey "executor")) "no executor block locally"
                      Assert.isTrue (not ((record["identity"] :?> JsonObject).ContainsKey "assurance")) "absence means self-reported"
                      Assert.equal RemoteIdentity.SelfReported (RemoteIdentity.assurance (fun _ -> None))
                      Assert.equal RemoteIdentity.SelfReported (RemoteIdentity.assurance (fun _ -> Some "forged-value"))) }

          { Name = "remote identity: the scrubbed variables are exactly the ones identity discovery reads"
            Run =
              fun () ->
                  let source =
                      File.ReadAllText(
                          Path.Combine(CliPort.repositoryRoot.Value, "src", "Praxis.Infrastructure", "Work", "FileTelemetryExecutionRepository.fs")
                      )

                  let start = source.IndexOf "environmentIdentityInputs () : IdentityInputs ="
                  let finish = source.IndexOf("OllamaHost = variable", start)
                  let block = source.Substring(start, finish - start + 40)

                  let read =
                      Regex.Matches(block, "variable \"([A-Z_]+)\"")
                      |> Seq.map (fun matched -> matched.Groups[1].Value)
                      |> Set.ofSeq

                  Assert.equal (Set.ofList RemoteIdentity.hostIdentityVariables) read }

          { Name = "remote identity: no credential-bearing name is on the operational allow-list"
            Run =
              fun () ->
                  RemoteIdentity.operationalVariables
                  |> Set.iter (fun name ->
                      Assert.isTrue
                          (not (Regex.IsMatch(name, "TOKEN|KEY|SECRET|PASSWORD|CREDENTIAL|AUTH")))
                          $"{name} looks credential-bearing") } ]
