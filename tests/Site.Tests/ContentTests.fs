/// Content contracts for the public page: the copy the specification fixes
/// verbatim, the narrative order, and the claims the page must never make.
module Site.Tests.ContentTests

open System
open System.Text.RegularExpressions
open Praxis.Site
open Site.Tests.Fixture

let private text (html: string) =
    let withoutRaw = Regex.Replace(html, @"<(script|style)[\s\S]*?</\1>", " ")
    let withoutTags = Regex.Replace(withoutRaw, "<[^>]+>", " ").Replace("&amp;", "&")
    Regex.Replace(Regex.Replace(withoutTags, "&#39;|&rsquo;", "'"), @"\s+", " ")

let private groups (pattern: string) (group: int) (input: string) =
    Regex.Matches(input, pattern) |> Seq.map (fun found -> found.Groups[group].Value) |> List.ofSeq

let private upTo (marker: string) (input: string) =
    let index = input.IndexOf(marker, StringComparison.Ordinal)
    if index < 0 then input.Substring(0, max 0 (input.Length - 1)) else input.Substring(0, index)

let private from (marker: string) (input: string) =
    let index = input.IndexOf(marker, StringComparison.Ordinal)
    if index < 0 then input.Substring(max 0 (input.Length - 1)) else input.Substring index

let tests =
    [ test "hero states the claim and the demand" (fun () ->
          let html = html ()
          let hero = section html "top"
          Assert.matches "Echelon</span> <span>Foundry" hero "foundry"
          Assert.matches "class=\"hero__mark\">Praxis<" hero "mark"

          Assert.matches
              @"<h1[^>]*>\s*<span class=""hero__claim"">You said it&rsquo;s done\.</span>\s*<span class=""hero__demand"">Prove it\.</span>\s*</h1>"
              hero
              "h1"

          Assert.isTrue
              ((text html)
                  .Contains(
                      "Praxis connects requirements, execution, agents, changes, tests, evidence, provenance, and cost into a verifiable engineering record."
                  ))
              "lede"

          Assert.isTrue ((text html).Contains("Trust is not an engineering control. Evidence is.")) "statement"
          Assert.matches ">See the evidence<" hero "primary action"
          Assert.matches @"href=""https://github\.com/kemiller2002/praxis""[^>]*>View on GitHub<" hero "GitHub action")

      test "the page never borrows from the film it was inspired by" (fun () ->
          let lower = (text (html ())).ToLowerInvariant()

          for phrase in
              [ "Pulp Fiction"; "Jules"; "Winnfield"; "Samuel L"; "Ezekiel"; "royale with cheese"; "say what again"; "motherf" ] do
              Assert.isFalse (lower.Contains(phrase.ToLowerInvariant())) phrase)

      test "the claim is complete without JavaScript" (fun () ->
          let html = html ()
          let claim = section html "verify"
          Assert.notMatches @"\bhidden\b" claim "no content is hidden in the static page"
          Assert.equal 8 (count "class=\"claim__step\"" claim)
          Assert.matches "class=\"claim__verdict\"" claim "verdict"
          Assert.matches "Real record" claim "real record"
          Assert.notMatches "<script" html "the page has no script at all")

      test "the claim needs no script hook and no script-only styles" (fun () ->
          let css = css ()
          Assert.notMatches @"data-claim\b" (html ()) "no data-claim hook"

          for selector in [ "is-enhanced"; "is-proven"; "claim__bar"; "claim__progress"; "claim__control"; "[hidden]" ] do
              Assert.isFalse (css.Contains(selector: string)) selector)

      test "the problem precedes the proposition, which precedes the verification" (fun () ->
          let html = html ()
          let order = [ "top"; "problem"; "proposition"; "verify" ] |> List.map (fun id -> html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal))
          Assert.equal (List.sort order) order
          Assert.isTrue ((text html).Contains("Agents can write code. Praxis makes their work accountable.")) "bridge")

      test "the execution chain runs from requirement to verified history in order" (fun () ->
          let names = groups @"<h3 class=""chain__name"">([^<]+)</h3>" 1 (section (html ()) "how")

          Assert.equal
              [ "Requirement"
                "Work item"
                "Execution"
                "Steps"
                "Changes"
                "Tests"
                "Evidence"
                "Reconciliation"
                "Verified history" ]
              names)

      test "every command the chain names exists in the CLI" (fun () ->
          let cli = read "src/Ros.Cli/Program.fs"

          let verbs =
              groups "<code>([^<]+)</code>" 1 (section (html ()) "how")
              |> List.collect (groups "work ([a-z-]+)" 1)

          Assert.isTrue (verbs.Length >= 5) "at least five verbs"

          for verb in verbs do
              Assert.isTrue (cli.Contains($"\"{verb}\"")) $"work {verb}")

      test "Git and Praxis are compared as complements, in an accessible table" (fun () ->
          let git = section (html ()) "git"
          Assert.matches @"Git knows what\. <span class=""accent"">Praxis knows why\.</span>" git "heading"
          Assert.matches "<caption" git "caption"
          Assert.equal 6 (count "<th scope=\"row\">" git)
          Assert.equal 3 (count "<th scope=\"col\">" git)
          let lower = git.ToLowerInvariant()

          for phrase in [ "Git is bad"; "broken"; "fails to"; "inadequate"; "outdated"; "legacy" ] do
              Assert.isFalse (lower.Contains(phrase.ToLowerInvariant())) phrase)

      test "agent accountability distinguishes every quality of knowledge" (fun () ->
          let agents = section (html ()) "agents"
          // Metric qualities in code are observed, derived and estimated
          // (TelemetryValidation.fs); unknown and unavailable are capability states.
          for quality in [ "Observed"; "Derived"; "Estimated"; "Unknown"; "Unavailable" ] do
              Assert.matches $">{quality}<" agents quality

          Assert.notMatches ">Declared<" agents "declared is not a metric quality"
          let validation = read "src/Ros.Domain/Telemetry/TelemetryValidation.fs"

          for quality in [ "observed"; "derived"; "estimated" ] do
              Assert.isTrue (validation.Contains($"\"{quality}\"")) quality

          Assert.matches "a declaration takes precedence" agents "declaration"
          Assert.matches "does not infer a model" agents "model"
          Assert.matches "Praxis does not compute cost" agents "cost")

      test "the runtimes the page says are detected are the ones the CLI detects" (fun () ->
          let identity = read "src/Ros.Domain/Telemetry/Identity.fs"
          let agents = section (html ()) "agents"

          for name, mechanism in
              [ "OpenAI Codex", "whitelisted-codex-environment"
                "Claude Code", "whitelisted-claude-environment"
                "Gemini CLI", "whitelisted-gemini-environment"
                "GitHub Copilot", "whitelisted-copilot-environment"
                "GitHub Actions", "whitelisted-github-actions-environment" ] do
              Assert.isTrue (agents.Contains(name: string)) name
              Assert.isTrue (identity.Contains(mechanism: string)) mechanism)

      test "the GH-84 handoff is rendered from real records only" (fun () ->
          let evidence = section (html ()) "evidence"
          Assert.equal 2 (count "data-record=\"real\"" evidence)
          Assert.notMatches "(?i)illustrative" evidence "no illustrative record"
          Assert.matches @"Praxis will not transfer identity merely because the work continued\." evidence "identity sentence"

          let gh84 =
              Evidence.readSnapshot root
              |> Json.get "executions"
              |> Json.items
              |> List.filter (fun execution -> Json.get "workItemId" execution = Some(JString "GH-84"))

          Assert.equal 2 gh84.Length

          Assert.equalMessage
              (Json.get "executionId" gh84[0])
              (Json.get "parentExecutionId" gh84[1])
              "second execution is the child of the first"

          for execution in gh84 do
              let id = Json.get "executionId" execution |> Json.toText
              Assert.isTrue (evidence.Contains($"data-evidence=\"{id}:identity.provider\"")) id)

      test "unattributed changes: the message shown is the one the CLI prints" (fun () ->
          let reconciliation = section (html ()) "reconciliation"
          let protocol = read "docs/work-protocol.md"
          let message = "meaningful change has no active or completed work-item attribution"
          Assert.isTrue (reconciliation.Contains message) "page"
          Assert.isTrue (protocol.Contains message) "protocol"

          for state in [ "Attributed"; "Reconciled"; "Unresolved" ] do
              Assert.matches $">{state}<" reconciliation state

          Assert.matches
              @"Manufactured attribution is worse than explicitly unresolved attribution\."
              reconciliation
              "principle")

      test "resilience separates what exists from what is only direction" (fun () ->
          let resilience = section (html ()) "resilience"
          Assert.matches @"The process survives the tool\." resilience "heading"
          Assert.matches ">Available now<" resilience "available"
          Assert.matches ">Architectural direction<" resilience "direction"
          let planned = from "availability__col--planned" resilience
          Assert.matches @"Not implemented\." planned "planned"

          Assert.notMatches
              "(?i)fallback|double-entry"
              (upTo "availability__col--planned" resilience)
              "fallback appears only under direction")

      test "the record locations the page names exist in this repository" (fun () ->
          let records = section (html ()) "records"

          for word in [ "Versionable"; "Inspectable"; "Portable"; "Attributable"; "Reviewable"; "Automatable" ] do
              Assert.matches $"<dt>{word}</dt>" records word

          for location in [ ".ros/work/queue.json"; ".ros/events/events.jsonl"; ".ros/telemetry/executions"; "research"; "registries" ] do
              Assert.isTrue (exists location) location)

      test "integrations listed as available are backed by CLI commands" (fun () ->
          let independence = section (html ()) "independence"
          let cli = read "src/Ros.Cli/Program.fs"
          Assert.matches ">Available now<" independence "available"
          Assert.matches ">Architectural direction<" independence "direction"

          for claim, command in [ "Ordo", "\"ordo\""; "adapter contract", "\"adapter\"" ] do
              Assert.isTrue (independence.Contains(claim: string)) claim
              Assert.isTrue (cli.Contains(command: string)) command

          let available = upTo "availability__col--planned" independence

          for name in [ "Aegis"; "Forma"; "Folio"; "Tutela"; "Vigila"; "Dokimos"; "Percepta"; "Chrona"; "Summa" ] do
              Assert.isFalse (available.Contains(name: string)) $"{name} is not an available integration")

      test "each principle links to the section that explains it" (fun () ->
          let html = html ()

          let items =
              Regex.Matches(section html "principles", @"<h3>([^<]+)</h3>[\s\S]*?href=""#([a-z-]+)""")
              |> Seq.map (fun found -> found.Groups[1].Value, found.Groups[2].Value)
              |> List.ofSeq

          Assert.equal
              [ "Done is a claim."
                "Every change has to answer for itself."
                "No anonymous work."
                "Trust is not an engineering control."
                "The process survives the tool." ]
              (items |> List.map fst)

          for _, target in items do
              Assert.isTrue (html.Contains($"id=\"{target}\"")) target)

      test "get started uses only commands and installers that exist, and names the ROS transition" (fun () ->
          let start = section (html ()) "get-started"

          for file in
              [ "scripts/install-native.sh"
                "scripts/install-native.ps1"
                "docs/work-protocol.md"
                "docs/cli.md"
                "docs/native-installation.md" ] do
              Assert.isTrue (start.Contains(file: string)) file
              Assert.isTrue (exists file) file

          Assert.matches "for command_name in praxis ros; do" (read "scripts/install-native.sh") "install-native.sh provides praxis and ros"
          Assert.matches @"foreach \(\$name in @\(""praxis"", ""ros""\)\)" (read "scripts/install-native.ps1") "install-native.ps1 provides praxis and ros"

          Assert.matches
              "install both <code>praxis</code> and <code>ros</code> commands"
              start
              "the page says the installers provide both commands"

          // release.json, not an npm manifest, is the name and version source.
          // npm publishing is retired (DF-ROS-2026-A044): the page may name the
          // retired package only to say so, and never offers an npm install.
          let release = Json.parse (read "release.json")
          Assert.isTrue (Json.truthy (Json.get "version" release)) "release.json has a version"
          Assert.notMatches @"\bnpx\b|\bnpm (install|i)\b" start "no npm install path"

          if start.Contains "repository-operating-system" then
              Assert.matches "is no longer published" start "the retired npm package is named only as retired"

          // With .NET 10, the same release installs as a global tool.
          let tool = read "src/Ros.Cli/Ros.Cli.fsproj"
          let packageId = Regex.Match(tool, "<PackageId>([^<]+)</PackageId>").Groups[1].Value
          Assert.matches "<ToolCommandName>praxis</ToolCommandName>" tool "the tool's command is praxis"
          Assert.isTrue (start.Contains $"dotnet tool install -g {packageId}") "the page installs the real .NET tool"
          Assert.matches "From ROS to Praxis" start "rename"
          Assert.notMatches "still provides only <code>ros</code>" start "no stale npm fact")

      test "primary navigation is the six specified destinations and needs no script" (fun () ->
          let html = html ()
          let nav = Regex.Match(html, @"<nav aria-label=""Primary"">([\s\S]*?)</nav>").Groups[1].Value

          let links =
              Regex.Matches(nav, @"<a href=""([^""]+)"">([^<]+)")
              |> Seq.map (fun found -> found.Groups[2].Value.Trim(), found.Groups[1].Value)
              |> List.ofSeq

          Assert.equal [ "What is Praxis"; "How it works"; "Evidence"; "Agents"; "Get started"; "GitHub" ] (links |> List.map fst)

          for _, href in links |> List.filter (fun (_, href) -> href.StartsWith("#", StringComparison.Ordinal)) do
              Assert.isTrue (html.Contains($"id=\"{href.Substring 1}\"")) href

          Assert.notMatches "<button" nav "no script-driven menu")

      test "the footer connects Praxis to Echelon Foundry and states the privacy facts" (fun () ->
          let html = html ()
          let footer = Regex.Match(html, @"<footer[\s\S]*?</footer>").Value
          Assert.matches @"Praxis is part of Echelon Foundry\." footer "Echelon Foundry"
          Assert.matches @"No analytics, no cookies, no tracking\." footer "privacy"
          Assert.notMatches "<script" html "no script at all")

      test "no broken entities or duplicated sentences" (fun () ->
          let html = html ()
          let text = text html

          Assert.notMatches
              "(?<!&)(rsquo|ldquo|rdquo|hellip|amp);"
              (Regex.Replace(html, "&(rsquo|ldquo|rdquo|hellip|amp);", ""))
              "entity without its ampersand"

          let sentences =
              Regex.Split(text, @"(?<=[.!?])\s+")
              |> Array.map (fun sentence -> sentence.Trim())
              |> Array.filter (fun sentence -> sentence.Split(' ').Length >= 8)
              |> List.ofArray

          let repeated =
              sentences
              |> List.indexed
              |> List.filter (fun (index, sentence) -> List.findIndex ((=) sentence) sentences <> index)
              |> List.map snd

          Assert.empty repeated
          Assert.notMatches @"[a-z][A-Z][a-z]+ CI\b|repositoryWhen" text "run-together words") ]
