namespace Praxis.Tests

open Praxis.Domain.Lifecycle

[<RequireQualifiedAccess>]
module ConsumerLinksTests =
    let private context: ConsumerLinks.Context =
        { RepositoryUrl = "https://github.com/kemiller2002/praxis"
          Tag = "v9.8.7"
          Installed =
            Map.ofList
                [ "AGENTS.md", "AGENTS.md"
                  "docs/work-protocol.md", "docs/work-protocol.md"
                  "docs/00-governance/README.md", "docs/00-governance/README.md"
                  "starter/greenfield/HANDOFF.md", "HANDOFF.md" ]
          RepositoryLocal = [ ".ros" ] }

    let private rewrite source destination markdown =
        ConsumerLinks.rewrite context source destination markdown

    let tests =
        [ { Name = "consumer links: a link to a file every install carries stays relative to the installed copy"
            Run =
              fun () ->
                  Assert.equal
                      "See [protocol](work-protocol.md#start) and [governance](00-governance/)."
                      (rewrite
                          "docs/remote-protocol.md"
                          "docs/remote-protocol.md"
                          "See [protocol](work-protocol.md#start) and [governance](00-governance/).") }
          { Name = "consumer links: a link to a file only Praxis has is pinned to the release tag"
            Run =
              fun () ->
                  Assert.equal
                      "[cli](https://github.com/kemiller2002/praxis/blob/v9.8.7/docs/cli.md#exit-codes)"
                      (rewrite "AGENTS.md" "AGENTS.md" "[cli](docs/cli.md#exit-codes)")

                  Assert.equal
                      "[DF](https://github.com/kemiller2002/praxis/blob/v9.8.7/research/decisions/DF-1.md)"
                      (rewrite "docs/remote-protocol.md" "docs/remote-protocol.md" "[DF](../research/decisions/DF-1.md)") }
          { Name = "consumer links: a source link is retargeted at the file's installed destination"
            Run =
              fun () ->
                  Assert.equal
                      "[handoff](../HANDOFF.md)"
                      (rewrite "docs/work-protocol.md" "docs/work-protocol.md" "[handoff](../starter/greenfield/HANDOFF.md)") }
          { Name = "consumer links: a template link already written against its destination is kept"
            Run =
              fun () ->
                  Assert.equal "[agents](AGENTS.md)" (rewrite "starter/greenfield/README.md" "README.md" "[agents](AGENTS.md)") }
          { Name = "consumer links: repository-local Praxis state names the consumer's own copy"
            Run =
              fun () ->
                  Assert.equal
                      "[queue](../.ros/work/queue.md)"
                      (rewrite "docs/work-backlog-guide.md" "docs/work-backlog-guide.md" "[queue](../.ros/work/queue.md)") }
          { Name = "consumer links: external links, anchors and fenced code blocks are untouched, line endings kept"
            Run =
              fun () ->
                  let markdown =
                      "[web](https://example.com/x.md) [here](#top)\r\n```\r\n[code](docs/cli.md)\r\n```\r\n[mail](mailto:a@b.c)\n"

                  Assert.equal markdown (rewrite "AGENTS.md" "AGENTS.md" markdown) } ]
