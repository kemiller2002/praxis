module Ros.Integration.Compatibility.Tests.Program

[<EntryPoint>]
let main _ = CompatibilityTests.tests |> TestRunner.run
