namespace Ros.Domain.Provenance

/// A record's accumulated provenance, as read from any Praxis store.
type AttributedRecord =
    { Kind: RecordKind
      RecordId: string
      Path: string
      Provenance: Provenance
      /// Lineage: the records this one was derived from (`derived_from`).
      DerivedFrom: string list }

type ActorTotals =
    { Kind: string
      Id: string
      Providers: string list
      Models: string list
      Executions: string list
      /// (record kind, operation) -> count.
      Operations: Map<string * string, int>
      RecordsCreated: int
      RecordsModified: int }

type ProvenanceSummary =
    { Records: int
      AttributedRecords: int
      UnattributedRecords: int
      Actors: ActorTotals list
      HumanCorrectionsOfAgentWork: string list
      AgentToAgentRevisions: string list
      HumanApprovedAgentWork: string list
      DerivedRecords: (string * string list) list
      /// Records with the most contributions, most-contributed first.
      Hotspots: (string * int) list }

/// Read-only aggregation over provenance. Praxis does not own analytics;
/// this projection only proves the recorded model answers the questions
/// (who created, who modified, who corrected whom) without inference.
[<RequireQualifiedAccess>]
module ProvenanceSummary =
    let private addCount key (map: Map<string * string, int>) =
        map |> Map.change key (fun current -> Some((current |> Option.defaultValue 0) + 1))

    let summarize (records: AttributedRecord list) =
        let contributions =
            records
            |> List.collect (fun record ->
                record.Provenance.Contributions |> List.map (fun contribution -> record, contribution))

        let actors =
            contributions
            |> List.groupBy (fun (_, contribution) -> Actor.stableKey contribution.Actor)
            |> List.map (fun ((kind, id), entries) ->
                let actorsOf = entries |> List.map (fun (_, contribution) -> contribution.Actor)
                let knownValues selector = actorsOf |> List.choose (selector >> Attribute.toOption) |> List.distinct |> List.sort

                let recordsWith operation =
                    entries
                    |> List.filter (fun (_, contribution) -> contribution.Operation = operation)
                    |> List.map (fun (record, _) -> record.Path, record.RecordId)
                    |> List.distinct
                    |> List.length

                { Kind = kind
                  Id = id
                  Providers = knownValues _.Provider
                  Models = knownValues _.Model
                  Executions = knownValues _.ExecutionId
                  Operations =
                    entries
                    |> List.fold
                        (fun totals (record, contribution) ->
                            addCount (RecordKind.code record.Kind, Operation.code contribution.Operation) totals)
                        Map.empty
                  RecordsCreated = recordsWith Operation.Created
                  RecordsModified = recordsWith Operation.Modified })
            |> List.sortBy (fun totals -> totals.Kind, totals.Id)

        let where predicate =
            records
            |> List.filter (fun record -> predicate (Provenance.involvement record.Provenance))
            |> List.map _.RecordId
            |> List.sort

        let attributed = records |> List.filter (fun record -> not record.Provenance.Contributions.IsEmpty)

        { Records = records.Length
          AttributedRecords = attributed.Length
          UnattributedRecords = records.Length - attributed.Length
          Actors = actors
          HumanCorrectionsOfAgentWork = where _.HumanCorrectedAgentWork
          AgentToAgentRevisions = where _.AgentRevisedOtherAgentWork
          HumanApprovedAgentWork = where (fun facts -> facts.CreatorKind = Some ActorKind.Agent && facts.HumanApproved)
          DerivedRecords =
            records
            |> List.filter (fun record -> not record.DerivedFrom.IsEmpty)
            |> List.map (fun record -> record.RecordId, record.DerivedFrom)
            |> List.sortBy fst
          Hotspots =
            attributed
            |> List.map (fun record -> record.RecordId, record.Provenance.Contributions.Length)
            |> List.filter (fun (_, count) -> count > 1)
            |> List.sortBy (fun (id, count) -> -count, id) }
