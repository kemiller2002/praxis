namespace Ros.Domain.Remote

open Ros.Domain.Provenance

/// Identity roles for a remote request (PRAXIS-REMOTE-02, `PRX-REMOTE-005`
/// to `007`). The same command implementations run a remote request as a
/// local one, but in a child process whose environment is *derived*, not
/// inherited:
///
/// - every host variable identity discovery would read is dropped, so the
///   runner's own runtime markers (`GITHUB_ACTIONS`, `GITHUB_RUN_ID`, an
///   agent session variable on the runner, a stray `ROS_ACTOR`) can never
///   leak into the requester's identity;
/// - the requester's *asserted* actor becomes the explicit identity, with
///   `unknown` for anything it did not state, so host detection never fills
///   a gap with a guess;
/// - the executor's *observed* facts travel separately and are recorded as
///   the execution's `executor`, never as its actor;
/// - only an allow-list of operational variables survives, so tokens and
///   provider credentials in the runner's environment never reach the
///   command, its telemetry, or its diagnostics.
[<RequireQualifiedAccess>]
module RemoteIdentity =
    /// Every environment variable execution-identity discovery reads
    /// (`FileTelemetryExecutionRepository.environmentIdentityInputs`). A
    /// test keeps the two lists identical.
    let hostIdentityVariables =
        [ "ROS_TELEMETRY_PROVIDER"
          "ROS_TELEMETRY_RUNTIME"
          "ROS_TELEMETRY_MODEL"
          "ROS_TELEMETRY_MODEL_VERSION"
          "ROS_TELEMETRY_RUNTIME_VERSION"
          "ROS_TELEMETRY_SESSION_ID"
          "ROS_TELEMETRY_CONVERSATION_ID"
          "ROS_TELEMETRY_RUN_ID"
          "ROS_ACTOR"
          "ROS_ACTOR_KIND"
          "CODEX_SESSION_ID"
          "CODEX_THREAD_ID"
          "CLAUDE_CODE_SESSION_ID"
          "GEMINI_SESSION_ID"
          "COPILOT_SESSION_ID"
          "GITHUB_ACTIONS"
          "GITHUB_RUN_ID"
          "OLLAMA_HOST" ]

    /// Operational variables a child command legitimately needs (process
    /// lookup, home directory for Git configuration, locale, temporary
    /// files, the .NET single-file extraction directory). Everything else
    /// in the executor's environment, including every credential, is
    /// withheld.
    let operationalVariables =
        set
            [ "PATH"
              "HOME"
              "USER"
              "LOGNAME"
              "LANG"
              "LC_ALL"
              "LC_CTYPE"
              "TZ"
              "TMPDIR"
              "TEMP"
              "TMP"
              "SYSTEMROOT"
              "WINDIR"
              "COMSPEC"
              "PATHEXT"
              "USERPROFILE"
              "DOTNET_ROOT"
              "DOTNET_BUNDLE_EXTRACT_BASE_DIR"
              "DOTNET_CLI_HOME" ]

    [<Literal>]
    let AssuranceVariable = "PRAXIS_IDENTITY_ASSURANCE"

    /// The identity came from the remote request and is the requester's own
    /// claim; the executor did not and cannot verify it.
    [<Literal>]
    let AssertedByRequest = "asserted-by-request"

    /// The identity came from the local process environment (the historical
    /// behaviour): still self-reported, never authenticated.
    [<Literal>]
    let SelfReported = "self-reported"

    /// The executor recorded these facts from its own environment.
    [<Literal>]
    let ObservedByExecutor = "observed-by-executor"

    let executorVariables =
        [ "PRAXIS_EXECUTOR_KIND"
          "PRAXIS_EXECUTOR_RUN_ID"
          "PRAXIS_EXECUTOR_RUN_ATTEMPT"
          "PRAXIS_EXECUTOR_WORKFLOW_REF"
          "PRAXIS_EXECUTOR_REPOSITORY"
          "PRAXIS_EXECUTOR_HOST"
          "PRAXIS_EXECUTOR_PRINCIPAL"
          "PRAXIS_EXECUTOR_PRAXIS_VERSION" ]

    let private orUnknown (value: string option) =
        value |> Option.defaultValue Actor.UnknownValue

    /// The explicit identity assignments for the requester's asserted actor.
    /// An absent actor is `unknown` in every field -- never the runner.
    let actorAssignments (requestActor: RequestActor option) : (string * string) list =
        match requestActor with
        | None ->
            [ "ROS_ACTOR_KIND", ActorKind.code ActorKind.Unknown
              "ROS_ACTOR", Actor.UnknownValue
              "ROS_TELEMETRY_PROVIDER", Actor.UnknownValue
              "ROS_TELEMETRY_RUNTIME", Actor.UnknownValue ]
        | Some value ->
            [ yield "ROS_ACTOR_KIND", ActorKind.code value.Actor.Kind
              yield "ROS_ACTOR", value.Actor.Id
              // Explicit `unknown` (rather than an absent variable) stops
              // host runtime detection from ever filling the gap.
              yield "ROS_TELEMETRY_PROVIDER", orUnknown value.Actor.Provider
              yield "ROS_TELEMETRY_RUNTIME", orUnknown value.Actor.Runtime
              match value.Actor.Model with
              | Some model when model <> Actor.UnknownValue -> yield "ROS_TELEMETRY_MODEL", model
              | _ -> ()
              match value.SessionId with
              | Some session -> yield "ROS_TELEMETRY_SESSION_ID", session
              | None -> () ]

    let executorAssignments (facts: ExecutorFacts) : (string * string) list =
        [ Some("PRAXIS_EXECUTOR_KIND", facts.Kind)
          facts.RunId |> Option.map (fun value -> "PRAXIS_EXECUTOR_RUN_ID", value)
          facts.RunAttempt |> Option.map (fun value -> "PRAXIS_EXECUTOR_RUN_ATTEMPT", value)
          facts.WorkflowRef |> Option.map (fun value -> "PRAXIS_EXECUTOR_WORKFLOW_REF", value)
          facts.Repository |> Option.map (fun value -> "PRAXIS_EXECUTOR_REPOSITORY", value)
          facts.Host |> Option.map (fun value -> "PRAXIS_EXECUTOR_HOST", value)
          facts.Principal |> Option.map (fun value -> "PRAXIS_EXECUTOR_PRINCIPAL", value)
          Some("PRAXIS_EXECUTOR_PRAXIS_VERSION", facts.PraxisVersion) ]
        |> List.choose id

    /// The complete environment for the child command that executes a
    /// remote request: allow-listed operational variables from the parent,
    /// then the asserted actor, the observed executor, and the assurance
    /// marker. Nothing else from the parent survives.
    let childEnvironment (parent: Map<string, string>) (requestActor: RequestActor option) (facts: ExecutorFacts) : Map<string, string> =
        let operational =
            parent |> Map.filter (fun name _ -> operationalVariables.Contains(name.ToUpperInvariant()))

        actorAssignments requestActor @ executorAssignments facts @ [ AssuranceVariable, AssertedByRequest ]
        |> List.fold (fun environment (name, value) -> environment |> Map.add name value) operational

    /// Reads executor facts back from an environment (the child side of
    /// `childEnvironment`). `None` when the process is not executing a
    /// remote request, which is the local CLI's case.
    let readExecutor (variable: string -> string option) : ExecutorFacts option =
        match variable "PRAXIS_EXECUTOR_KIND", variable "PRAXIS_EXECUTOR_PRAXIS_VERSION" with
        | Some kind, Some version ->
            Some
                { Kind = kind
                  RunId = variable "PRAXIS_EXECUTOR_RUN_ID"
                  RunAttempt = variable "PRAXIS_EXECUTOR_RUN_ATTEMPT"
                  WorkflowRef = variable "PRAXIS_EXECUTOR_WORKFLOW_REF"
                  Repository = variable "PRAXIS_EXECUTOR_REPOSITORY"
                  Host = variable "PRAXIS_EXECUTOR_HOST"
                  Principal = variable "PRAXIS_EXECUTOR_PRINCIPAL"
                  PraxisVersion = version }
        | _ -> None

    /// How the execution's identity was obtained. Only the exact
    /// `asserted-by-request` marker changes the label; anything else is the
    /// historical self-reported case.
    let assurance (variable: string -> string option) =
        match variable AssuranceVariable with
        | Some AssertedByRequest -> AssertedByRequest
        | _ -> SelfReported
