module Ros.ProjectAdministration.Tests.Program

[<EntryPoint>]
let main _ =
    OrganizationTests.tests
    @ ExternalActivityStateTests.tests
    @ OutboundDeliveryStateTests.tests
    @ OutboundIntegrationTests.tests
    |> TestRunner.run
