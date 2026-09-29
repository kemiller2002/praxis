namespace Ros.Tests

open System
open System.IO

[<RequireQualifiedAccess>]
module WorkflowPolicyTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "package.json"))
           && Directory.Exists(Path.Combine(directory.FullName, ".github", "workflows")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let private root () =
        repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))

    let private read path =
        File.ReadAllText(Path.Combine(root (), path.Replace('/', Path.DirectorySeparatorChar)))

    let private contains value text message =
        Assert.isTrue (text.Contains(value, StringComparison.Ordinal)) message

    let private notContains value text message =
        Assert.isTrue (not (text.Contains(value, StringComparison.Ordinal))) message

    let tests =
        [ { Name = "repository validation debounces by ref before expensive jobs"
            Run =
              fun () ->
                  let workflow = read ".github/workflows/ros-validation.yml"
                  contains "branches-ignore:" workflow "validation must exclude control-plane inbox branches"
                  contains "praxis-inbox/**" workflow "validation must exclude praxis-inbox branches"
                  contains "group: ros-validation-" workflow "validation must have a per-ref concurrency group"
                  contains "cancel-in-progress: true" workflow "new pushes must cancel superseded validation"
                  contains "  debounce:" workflow "multi-job validation must use one debounce gate"
                  contains "run: sleep 600" workflow "debounce quiet period must be ten minutes"
                  contains "  validate:\n    needs: debounce" workflow "validation must wait for the debounce gate"
                  contains "  packaged-lifecycle:\n    needs: debounce" workflow "packaged lifecycle must wait for the debounce gate" }

          { Name = "greenfield validation inherits CI batching"
            Run =
              fun () ->
                  let workflow = read "starter/greenfield/.github/workflows/ros-validation.yml"
                  contains "praxis-inbox/**" workflow "starter validation must exclude Praxis inbox branches"
                  contains "group: ros-validation-" workflow "starter validation must cancel superseded runs"
                  contains "cancel-in-progress: true" workflow "starter validation must cancel superseded runs"
                  contains "run: sleep 600" workflow "starter validation must keep the ten-minute quiet period" }

          { Name = "commit-driven site workflows debounce but manual deploy remains immediate"
            Run =
              fun () ->
                  let site = read ".github/workflows/site.yml"
                  let deploy = read ".github/workflows/deploy-pages.yml"
                  contains "group: public-site-${{ github.ref }}" site "site checks must cancel older runs for the same ref"
                  contains "run: sleep 600" site "site checks must use the ten-minute quiet period"
                  contains "workflow_dispatch:" deploy "Pages deployment must retain manual dispatch"
                  contains "if: github.event_name == 'push'\n        run: sleep 600" deploy "only push-driven Pages deployment should wait" }

          { Name = "native release only debounces pull-request verification"
            Run =
              fun () ->
                  let workflow = read ".github/workflows/native-release.yml"
                  contains "if: github.event_name == 'pull_request'\n        run: sleep 600" workflow "native release PR checks must debounce"
                  contains "cancel-in-progress: ${{ github.event_name == 'pull_request' }}" workflow "only PR native runs may be superseded"
                  contains "workflow_dispatch: {}" workflow "manual native release must remain available" }

          { Name = "Praxis command and explicit release workflows stay immediate"
            Run =
              fun () ->
                  [ ".github/workflows/praxis-remote.yml"
                    ".github/workflows/praxis-remote-inbox.yml"
                    ".github/workflows/release.yml"
                    ".github/workflows/ros-fs-assets.yml"
                    ".github/workflows/foundations-verify.yml" ]
                  |> List.iter (fun path ->
                      notContains "sleep 600" (read path) $"{path} must not inherit commit debounce") }

          { Name = "agent guidance keeps incremental commits but defers remote CI observation"
            Run =
              fun () ->
                  let startup = read "AGENTS.md"
                  let manual = read "docs/00-governance/Agent-Operating-Manual.md"
                  contains "do not wait for remote CI after every push" startup "startup guidance must tell agents to keep working after pushes"
                  contains "implementation boundary by default" startup "startup guidance must bias remote CI checks to the end"
                  contains "Remote CI/build status is end-biased" manual "operating manual must define end-biased CI observation"
                  contains "docs/ci-batching.md" startup "startup guidance must link the batching policy" } ]
