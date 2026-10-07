namespace Ros.Domain.Work

open System.Text.RegularExpressions

/// Mirrors production's `workConfig(root).meaningful`/`.ignored` defaults
/// (`tools/ros_cli.mjs`): a path is meaningful when it matches at least one
/// meaningful pattern and no ignored pattern.
type PathFilterConfig =
    { MeaningfulPatterns: string list
      IgnoredPatterns: string list }

[<RequireQualifiedAccess>]
module PathFilterConfig =
    let defaultConfig =
        { MeaningfulPatterns = [ "**" ]
          IgnoredPatterns =
            [ ".git/**"
              ".ros/context/**"
              ".ros/events/**"
              ".ros/work/**"
              ".ros/telemetry/**"
              ".ros/locks/**" ] }

/// Mirrors production `globMatch`/`meaningfulPaths` in `tools/ros_cli.mjs`
/// exactly: escape only the same regex metacharacter set Node's manual
/// escape does (never a generic regex-escape, which would also escape `*`
/// itself and other characters Node leaves literal, such as `?`), then turn
/// a literal `**` into `.*` and a remaining literal `*` into `[^/]*`,
/// anchored on the whole value.
[<RequireQualifiedAccess>]
module PathFilter =
    let private regexMetacharacters =
        set [ '.'; '+'; '^'; '$'; '{'; '}'; '('; ')'; '|'; '['; ']'; '\\' ]

    let private escapeMetacharacters (pattern: string) =
        pattern
        |> Seq.collect (fun character ->
            if regexMetacharacters.Contains character then
                [| '\\'; character |]
            else
                [| character |])
        |> Seq.toArray
        |> System.String

    /// A code point guaranteed not to appear in an ordinary glob pattern, so
    /// a literal character already in the pattern is never mistaken for the
    /// `**` marker (mirrors Node's own NUL-character placeholder choice).
    let private doubleStarPlaceholder = "\u0000"

    let private toRegexPattern (pattern: string) =
        let escaped = escapeMetacharacters pattern
        let withDoubleStarPlaceholder = escaped.Replace("**", doubleStarPlaceholder)
        let withSingleStarExpanded = withDoubleStarPlaceholder.Replace("*", "[^/]*")
        let withDoubleStarExpanded = withSingleStarExpanded.Replace(doubleStarPlaceholder, ".*")
        $"^{withDoubleStarExpanded}$"

    let globMatch (pattern: string) (value: string) =
        Regex.IsMatch(value, toRegexPattern pattern)

    /// The lifecycle CLI's own bookkeeping is never a meaningful change,
    /// whatever a repository's `ignoredPaths` says. A repository's ros.json
    /// replaces the default ignore list wholesale, so repositories installed
    /// before `.echelon/` existed have no rule for it; without this invariant
    /// the first `ros init`/`ros upgrade` in such a repository would make its
    /// next `validate` demand work-item attribution for the tool's own
    /// installation manifest.
    let private alwaysIgnoredPatterns = [ ".echelon/**" ]

    let isMeaningful (config: PathFilterConfig) (path: string) =
        (config.MeaningfulPatterns |> List.exists (fun pattern -> globMatch pattern path))
        && not (config.IgnoredPatterns |> List.exists (fun pattern -> globMatch pattern path))
        && not (alwaysIgnoredPatterns |> List.exists (fun pattern -> globMatch pattern path))

    let meaningfulPaths (config: PathFilterConfig) (paths: string list) =
        paths |> List.filter (isMeaningful config)
