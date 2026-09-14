namespace Ros.ProjectAdministration.Tests

open EchelonFoundry.Ros.Integration
open Ros.ProjectAdministration

[<RequireQualifiedAccess>]
module OrganizationTests =
    let private org = { Id = OrganizationId.create "ORG-ECHELON"; Name = "Echelon Foundry" }
    let private project = { Id = ProjectId.create "PROJ-ROS"; OrganizationId = org.Id; Name = "ROS" }
    let private repo = { RepositoryId = RepositoryId.create "REPO-ROS-CORE"; ProjectId = project.Id }

    let tests =
        [ { Name = "a project cannot be added under an unregistered organization"
            Run =
              fun () ->
                  let error = ProjectAdministrationState.addProject project ProjectAdministrationState.empty |> Assert.isError
                  Assert.isTrue (error.Contains "ORG-ECHELON") "expected the error to name the missing organization" }

          { Name = "a project registers once its organization exists, and not twice"
            Run =
              fun () ->
                  let state = ProjectAdministrationState.empty |> ProjectAdministrationState.addOrganization org |> Assert.isOk

                  let withProject = ProjectAdministrationState.addProject project state |> Assert.isOk
                  Assert.equal 1 withProject.Projects.Length

                  ProjectAdministrationState.addProject project withProject |> Assert.isError |> ignore }

          { Name = "a repository cannot be assigned to an unregistered project"
            Run =
              fun () ->
                  let error = ProjectAdministrationState.assignRepository repo ProjectAdministrationState.empty |> Assert.isError
                  Assert.isTrue (error.Contains "PROJ-ROS") "expected the error to name the missing project" }

          { Name = "projectFor resolves a repository to its assigned project by immutable repository id"
            Run =
              fun () ->
                  let state =
                      ProjectAdministrationState.empty
                      |> ProjectAdministrationState.addOrganization org
                      |> Assert.isOk
                      |> ProjectAdministrationState.addProject project
                      |> Assert.isOk
                      |> ProjectAdministrationState.assignRepository repo
                      |> Assert.isOk

                  let resolved = ProjectAdministrationState.projectFor repo.RepositoryId state
                  Assert.equal (Some project) resolved }

          { Name = "reassigning a repository moves it rather than creating a second assignment"
            Run =
              fun () ->
                  let secondProject = { Id = ProjectId.create "PROJ-OTHER"; OrganizationId = org.Id; Name = "Other" }

                  let state =
                      ProjectAdministrationState.empty
                      |> ProjectAdministrationState.addOrganization org
                      |> Assert.isOk
                      |> ProjectAdministrationState.addProject project
                      |> Assert.isOk
                      |> ProjectAdministrationState.addProject secondProject
                      |> Assert.isOk
                      |> ProjectAdministrationState.assignRepository repo
                      |> Assert.isOk
                      |> ProjectAdministrationState.assignRepository { repo with ProjectId = secondProject.Id }
                      |> Assert.isOk

                  Assert.equal 1 state.RepositoryAssignments.Length
                  Assert.equal (Some secondProject) (ProjectAdministrationState.projectFor repo.RepositoryId state) }

          { Name = "organizationFor resolves a project back to its owning organization"
            Run =
              fun () ->
                  let state =
                      ProjectAdministrationState.empty
                      |> ProjectAdministrationState.addOrganization org
                      |> Assert.isOk
                      |> ProjectAdministrationState.addProject project
                      |> Assert.isOk

                  Assert.equal (Some org) (ProjectAdministrationState.organizationFor project.Id state) } ]
