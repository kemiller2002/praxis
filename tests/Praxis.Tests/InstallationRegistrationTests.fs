namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Cli
open Praxis.Domain.Installation
open Praxis.Domain.Provenance

/// `praxis installation ...` against a stub receiving system. The stub
/// stands in for Project Administration's `administration` executable: it
/// records the request it was given and answers with a typed result, so
/// these tests prove the client contract without knowing the provider's
/// storage.
[<RequireQualifiedAccess>]
module InstallationRegistrationTests =
    let private walk (node: JsonNode) (keys: string list) =
        keys |> List.fold (fun (n: JsonNode) (k: string) -> if k.StartsWith "#" then n.AsArray().[int (k.Substring 1)] else n.[k]) node

    let private textAt node keys = (walk node keys).GetValue<string>()
    let private boolAt node keys = (walk node keys).GetValue<bool>()

    let private agent: Actor =
        { Kind = ActorKind.Agent; Id = "anthropic/claude-code"; Provider = Some "anthropic"; Model = None; Runtime = Some "claude-code" }

    let private repository () =
        let dir = GitFixture.temporaryDirectory "install"
        GitFixture.git dir [ "init"; "-q"; "-b"; "main" ] |> ignore
        GitFixture.git dir [ "remote"; "add"; "origin"; "git@github.com:echelon-foundry/example.git" ] |> ignore
        dir

    /// A stub provider that saves the request and answers with `status`.
    let private configure (root: string) (status: string) (required: bool) =
        let store = Path.Combine(root, "admin-store")
        Directory.CreateDirectory store |> ignore
        let stub = Path.Combine(root, "stub-admin.sh")

        File.WriteAllText(
            stub,
            "#!/bin/sh\n# args: installation VERB --store DIR --request - --json\nstore=\"$4\"\ncat > \"$store/request-$2.json\"\n"
            + $"printf '%%s' '{{\"schema\":\"echelon.installation.result/v1\",\"status\":\"{status}\",\"eventId\":\"IE-1\",\"operation\":\"installed\"}}'\n"
        )

        Directory.CreateDirectory(Path.Combine(root, ".echelon")) |> ignore
        let requiredText = if required then "true" else "false"

        File.WriteAllText(
            Path.Combine(root, ".echelon", "administration.json"),
            $$"""{ "schema": "echelon.administration/v1", "provider": "project-administration", "required": {{requiredText}},
                   "environment": { "id": "ws-primary" },
                   "transport": { "kind": "local", "command": ["/bin/sh", "{{stub}}"], "store": "../admin-store" } }"""
        )

        store

    let private capture (f: unit -> int) =
        let previous = Console.Out
        use sink = new StringWriter()
        Console.SetOut sink

        try
            let code = f ()
            code, sink.ToString()
        finally
            Console.SetOut previous

    let private register root args =
        let code, out = capture (fun () -> InstallationCommands.run root agent ("register" :: args @ [ "--json" ]))
        code, JsonNode.Parse out

    let private inDirectory (dir: string) (f: unit -> 'a) =
        let previous = Directory.GetCurrentDirectory()
        Directory.SetCurrentDirectory dir

        try
            f ()
        finally
            Directory.SetCurrentDirectory previous

    let tests =
        [ { Name = "installation: repository target is inferred from the Git remote"
            Run =
              fun () ->
                  let root = repository ()
                  let store = configure root "recorded" false
                  let code, result = inDirectory root (fun () -> register root [ "--system"; "praxis"; "--version"; "3.6.0" ])
                  Assert.equal 0 code
                  Assert.equal "recorded" (textAt result [ "outcome" ])
                  let sent = JsonNode.Parse(File.ReadAllText(Path.Combine(store, "request-register.json")))
                  Assert.equal "repository" (textAt sent [ "target"; "kind" ])
                  Assert.equal "echelon-foundry/example" (textAt sent [ "target"; "id" ])
                  Assert.equal "installation.register" (textAt sent [ "capability" ]) }
          { Name = "installation: explicit environment target uses the configured logical id, never the hostname"
            Run =
              fun () ->
                  let root = repository ()
                  let store = configure root "recorded" false
                  let code, _ = inDirectory root (fun () -> register root [ "--system"; "ordo"; "--version"; "1.4.0"; "--target-kind"; "environment" ])
                  Assert.equal 0 code
                  let text = File.ReadAllText(Path.Combine(store, "request-register.json"))
                  let sent = JsonNode.Parse text
                  Assert.equal "ws-primary" (textAt sent [ "target"; "id" ])
                  Assert.isTrue (not (text.Contains Environment.MachineName)) "hostname must not leak"
                  Assert.isTrue (not (text.Contains Environment.UserName) || Environment.UserName.Length < 3) "username must not leak"
                  Assert.isTrue (not (text.Contains(Environment.GetFolderPath Environment.SpecialFolder.UserProfile))) "home directory must not leak"
                  Assert.isTrue (not (text.Contains root)) "local paths must not leak" }
          { Name = "installation: an environment target without an explicit id is refused"
            Run =
              fun () ->
                  let root = repository ()
                  let code, result = register root [ "--system"; "ordo"; "--version"; "1.4.0"; "--target-kind"; "environment" ]
                  Assert.equal 2 code
                  Assert.equal "invalid" (textAt result [ "outcome" ]) }
          { Name = "installation: malformed system and version are refused before sending"
            Run =
              fun () ->
                  let root = repository ()
                  configure root "recorded" false |> ignore
                  Assert.equal 2 (fst (register root [ "--system"; "Praxis"; "--version"; "3.6.0" ]))
                  Assert.equal 2 (fst (register root [ "--system"; "praxis"; "--version"; "3.6" ])) }
          { Name = "installation: unavailable Project Administration is optional unless required"
            Run =
              fun () ->
                  let root = repository ()
                  let code, result = register root [ "--system"; "praxis"; "--version"; "3.6.0" ]
                  Assert.equal 0 code
                  Assert.equal "unavailable" (textAt result [ "outcome" ])
                  Assert.equal 6 (fst (register root [ "--system"; "praxis"; "--version"; "3.6.0"; "--require" ]))

                  let store = configure root "recorded" true
                  Directory.Delete(store, true)
                  Assert.equal 6 (fst (register root [ "--system"; "praxis"; "--version"; "3.6.0" ])) }
          { Name = "installation: artifact, digest and execution provenance are sent as given"
            Run =
              fun () ->
                  let root = repository ()
                  let store = configure root "recorded" false

                  inDirectory root (fun () ->
                      register
                          root
                          [ "--system"; "ordo"; "--version"; "1.4.0"; "--distribution"; "github-release"; "--release"; "v1.4.0"
                            "--artifact"; "ordo-linux-x64.tar.gz"; "--digest"; "sha256:26389b98"; "--execution"; "EXE-1"; "--work-item"; "WI-7"
                            "--evidence"; "receipt=.conditor/ledger.json#install-ordo" ])
                  |> ignore

                  let sent = JsonNode.Parse(File.ReadAllText(Path.Combine(store, "request-register.json")))
                  Assert.equal "sha256:26389b98" (textAt sent [ "source"; "digest" ])
                  Assert.equal "EXE-1" (textAt sent [ "execution"; "id" ])
                  Assert.equal "anthropic/claude-code" (textAt sent [ "actor"; "id" ])
                  Assert.equal "receipt" (textAt sent [ "evidence"; "#0"; "kind" ]) }
          { Name = "installation: a retry at the same instant reuses the operation id"
            Run =
              fun () ->
                  let root = repository ()
                  let store = configure root "replayed" false
                  let args = [ "--system"; "praxis"; "--version"; "3.6.0"; "--occurred-at"; "2026-09-29T12:00:00Z" ]
                  inDirectory root (fun () -> register root args) |> ignore
                  let first = textAt (JsonNode.Parse(File.ReadAllText(Path.Combine(store, "request-register.json")))) [ "operationId" ]
                  let code, result = inDirectory root (fun () -> register root args)
                  let second = textAt (JsonNode.Parse(File.ReadAllText(Path.Combine(store, "request-register.json")))) [ "operationId" ]
                  Assert.equal first second
                  Assert.equal 0 code
                  Assert.equal "replayed" (textAt result [ "outcome" ]) }
          { Name = "installation: provider refusal is surfaced with its own exit code"
            Run =
              fun () ->
                  let root = repository ()
                  configure root "refused" false |> ignore
                  Assert.equal 3 (fst (inDirectory root (fun () -> register root [ "--system"; "praxis"; "--version"; "3.6.0" ]))) }
          { Name = "installation: remote URL forms map to a stable repository identity"
            Run =
              fun () ->
                  Assert.equal (Some "owner/repo") (Target.repositoryFromRemote "https://github.com/owner/repo.git")
                  Assert.equal (Some "owner/repo") (Target.repositoryFromRemote "git@github.com:owner/repo.git")
                  Assert.equal (Some "owner/repo") (Target.repositoryFromRemote "http://127.0.0.1:1234/git/owner/repo")
                  Assert.equal None (Target.repositoryFromRemote "/home/someone/repo") } ]
