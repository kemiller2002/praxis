namespace Praxis.Infrastructure.Pacing

open System
open Praxis.Application.Pacing
open Praxis.Domain.Pacing

/// Explicit, versioned provider mapping rules (PRX-QUAL-005). Every
/// provider-specific decision that used to be an inline heuristic lives in
/// one table here: Codex quota-bucket selection, Claude window and scope
/// mapping, and the model families each adapter recognizes. Bump the rules
/// version whenever a table changes; it is reported by `pacing status`.
[<RequireQualifiedAccess>]
module PacingAdapterRules =
    [<Literal>]
    let CodexRulesVersion = "codex-rules/1"

    [<Literal>]
    let ClaudeRulesVersion = "claude-rules/1"

    let private family name =
        match ModelFamily.tryCreate name with
        | Some value -> value
        | None -> invalidArg (nameof name) $"'{name}' is not a model family"

    /// Codex: models whose identifier carries the `spark` token use the
    /// `codex_bengalfox` bucket; every other model, and an unknown model,
    /// uses `codex`.
    let codexBucketRules = [ "spark", QuotaBucket "codex_bengalfox" ]

    let codexDefaultBucket = QuotaBucket "codex"

    let codexBucket (model: string option) =
        let tokens = model |> Option.map ModelFamily.tokens |> Option.defaultValue Set.empty

        codexBucketRules
        |> List.tryPick (fun (token, bucket) -> if tokens.Contains token then Some bucket else None)
        |> Option.defaultValue codexDefaultBucket

    /// Codex window slots this adapter understands; both are always expected.
    let codexSlots = [ "primary"; "secondary" ]

    /// Claude model families the rules recognize without having seen a scoped
    /// window for them. Families named by scoped windows are recognized too.
    let claudeModelFamilies = [ family "opus"; family "sonnet"; family "haiku" ]

    let private session = TimeSpan.FromMinutes 300.0
    let private week = TimeSpan.FromMinutes 10080.0

    /// Claude top-level usage windows: key, scope and duration. `five_hour`
    /// and `seven_day` are always expected; the scoped ones only when present.
    let claudeTopLevel =
        [ "five_hour", QuotaScope.Global, session, true
          "seven_day", QuotaScope.Global, week, true
          "seven_day_opus", QuotaScope.Model(family "opus"), week, false
          "seven_day_sonnet", QuotaScope.Model(family "sonnet"), week, false
          "seven_day_oauth_apps", QuotaScope.Surface "oauth_apps", week, false ]

    let claudeExpected =
        claudeTopLevel |> List.choose (fun (key, _, _, expected) -> if expected then Some key else None)

    /// Maps a Claude scope display name to one model family: the single
    /// recognized family among its tokens, or the name itself when it is a
    /// single token. Anything else is unsupported, never guessed.
    let claudeFamily (displayName: string) =
        let tokens = ModelFamily.tokens displayName

        match claudeModelFamilies |> List.filter (fun known -> tokens.Contains(ModelFamily.value known)) with
        | [ known ] -> Some known
        | [] -> ModelFamily.tryCreate displayName
        | _ -> None

    [<RequireQualifiedAccess>]
    type ClaudeLimit =
        | Session
        | WeeklyAll
        | WeeklyModel of displayName: string
        | WeeklySurface of name: string
        | Unsupported of reason: string

    /// Claude `limits[]` rows: `session` and `weekly_all` are global; any
    /// `weekly*` kind with a model or surface scope is scoped weekly.
    let claudeLimit (kind: string) (model: string option) (surface: string option) =
        let weekly = kind.StartsWith("weekly", StringComparison.Ordinal)

        match kind, model, surface with
        | "session", None, None -> ClaudeLimit.Session
        | "weekly_all", None, None -> ClaudeLimit.WeeklyAll
        | _, Some name, _ when weekly -> ClaudeLimit.WeeklyModel name
        | _, None, Some name when weekly -> ClaudeLimit.WeeklySurface name
        | _ -> ClaudeLimit.Unsupported $"limit kind '{kind}' with this scope is not supported by {ClaudeRulesVersion}"

    let codexInfo (bucket: QuotaBucket) : PacingAdapterInfo =
        { AdapterId = "codex-app-server"
          RulesVersion = CodexRulesVersion
          Capabilities = [ "rate-limits-by-limit-id"; "primary-secondary-windows"; "quota-bucket-selection" ]
          ModelFamilies = []
          Bucket = Some bucket }

    let claudeInfo: PacingAdapterInfo =
        { AdapterId = "claude-oauth-usage"
          RulesVersion = ClaudeRulesVersion
          Capabilities = [ "global-windows"; "model-scoped-weekly"; "surface-scoped-weekly"; "transcript-model-resolution" ]
          ModelFamilies = claudeModelFamilies
          Bucket = None }

    let info (provider: ProviderId) (model: string option) =
        match provider with
        | ProviderId.Codex -> codexInfo (codexBucket model)
        | ProviderId.Claude -> claudeInfo
