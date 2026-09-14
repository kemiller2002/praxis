module Ros.Host.Tests.Program

[<EntryPoint>]
let main _ = ActivityIngestionTests.tests |> TestRunner.run
