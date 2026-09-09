namespace Ros.Domain.Work

/// Mirrors production's `workFindings` (`tools/ros_cli.mjs`): when
/// attribution enforcement is configured, every meaningful change not
/// already covered by the repository's first-begin baseline must be
/// attributed to a recorded event or to an active/blocked work item.
type WorkAttributionRequest =
    { Enforce: bool
      ObservedGitPaths: string list
      PathFilterConfig: PathFilterConfig
      BaselineDirtyPaths: string list
      AttributedPaths: Set<string>
      HasActiveOrBlockedWork: bool }

type WorkAttributionFinding =
    { Path: string
      Field: string
      Message: string }

[<RequireQualifiedAccess>]
module WorkAttribution =
    let findings (request: WorkAttributionRequest) =
        if not request.Enforce then
            []
        else
            request.ObservedGitPaths
            |> PathFilter.meaningfulPaths request.PathFilterConfig
            |> List.filter (fun path -> not (List.contains path request.BaselineDirtyPaths))
            |> List.filter (fun path ->
                not (request.AttributedPaths.Contains path) && not request.HasActiveOrBlockedWork)
            |> List.map (fun path ->
                { Path = path
                  Field = "work_items"
                  Message = "meaningful change has no active or completed work-item attribution" })
