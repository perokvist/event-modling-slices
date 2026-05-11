# Research: src\SampleApp\ApplicationService.cs — impact of DCB and Decider changes

Date: 2026-04-22T19:17:58+02:00

## Executive Summary

This report inspects `C:\Users\per.okvist\Documents\GitHub\dapr-lab\src\SampleApp\ApplicationService.cs` and evaluates how its public extension method signatures and the `Decider` interaction should evolve to support DCB-style workflows and the event-store enhancements already present in `src\DaprEventStore\EventStore.cs`.

Key findings:
- Current Execute extension overloads cover: state-store with outbox, single-stream event sourcing, and an incomplete multi-stream prototype[^1].
- The event store implements DCB primitives: ReadStreamsForDecisionAsync, DcbAppendCondition, and atomic AppendToStreamsAsync requiring partitionKey agreement[^2].
- To properly adopt DCB semantics, ApplicationService should change signatures to carry: explicit decision-read → append flow (read result + append-condition), cancellation tokens, optional partition keys, and richer event envelopes (metadata such as OccuredAt and stream target) surfaced to the Decider[^1][^2].

## Current code summary (what exists now)

- ApplicationService exposes three Execute patterns:
  1. State-store-based Execute that uses `dapr.Execute(stateStoreName, key, default, func)` and SaveWithOutboxProjection[^1:8-18, 21-34].
  2. Event-store single-stream Execute which constructs a DaprEventStore, reads stream metadata & history, computes current state, asks Decider.Decide(command, state) and then appends events with AppendToStreamAsync(streamName, version, eventData)[^1:39-66].
  3. A multi-stream sketch that obtains stream metadata for multiple streams, merges histories by OccuredAt, and attempts per-stream appends (TODO single transaction) — currently implemented as sequential AppendToStreamAsync calls (not atomic)[^1:68-101].

Citations:
- ApplicationService single-stream flow (build state, decide, append): `src\SampleApp\ApplicationService.cs:39-66`[^1].
- ApplicationService multi-stream prototype (collect streamInfo, merge, per-stream append): `src\SampleApp\ApplicationService.cs:68-101`[^1].

## What the EventStore already offers (relevant features)

- ReadStreamsForDecisionAsync returns a merged, ordered list of VersionedEvent plus an opaque DcbAppendCondition capturing observed stream versions for multi-stream decisioning[^2:50-75][^2:298-302].
- AppendToStreamAsync has a DCB-aware overload that accepts a DcbAppendCondition and performs full validation and an atomic state transaction that includes guard no-op writes[^2:139-195].
- AppendToStreamsAsync implements atomic multi-stream append across streams that share the same partitionKey in metadata; it returns a mapping of stream name → new version[^2:198-269].

Citations:
- ReadStreamsForDecisionAsync: `src\DaprEventStore\EventStore.cs:50-75`[^2].
- DcbAppendCondition: `src\DaprEventStore\EventStore.cs:298-302`[^2].
- DCB append overload: `src\DaprEventStore\EventStore.cs:139-195`[^2].
- Multi-stream atomic append: `src\DaprEventStore\EventStore.cs:205-269`[^2].

## Design implications for ApplicationService and Decider

Goals when adopting DCB semantics:
- Separate "decision-time read" from "append-time assert+write" using the token returned by ReadStreamsForDecisionAsync (DcbAppendCondition).
- Ensure decisions are made over a merged timeline and the append validates no guard-streams changed before committing.
- Keep the Decider pure: same Decide/Evolve shape but accept richer input (merged events with Position/OccuredAt) or alternatively keep Decider untouched but change the ApplicationService plumbing.

Two feasible approaches (differing in invasive changes):

1) Minimal-change (ApplicationService transforms only)
- Use EventStore.ReadStreamsForDecisionAsync to read the merged event history and obtain a DcbAppendCondition, then pass only the merged event list (or reconstruct state) to Decider.Decide. After Decide, call AppendToStreamAsync(targetStream, condition, events) to atomically validate guards and append.
- Signature changes suggested:
  - Add an overload: Execute<TState>(this DaprClient dapr, string eventStateStoreName, string targetStreamName, Command command, Decider decider)
    - Internals: read merged history via ReadStreamsForDecisionAsync(streamNames...), derive current state, decide, then call AppendToStreamAsync(targetStreamName, condition, eventData[]). Include CancellationToken optional param.
- Pros: Decider interface unchanged; minimal code churn. Leverages existing DCB API[^2:50-75][^2:139-195].

2) Decider-aware/explicit multi-stream (more invasive)
- Change Decider to handle multi-stream decisioning: have Decider.Decide accept a timeline of merged events (VersionedEvent[] or IReadOnlyList<VersionedEvent>) or accept both the decision-read token + merged events, and return events annotated with target stream(s) where they should be persisted.
- New Decider API shape examples:
  - `Event[] Decide(Command cmd, IReadOnlyList<VersionedEvent> history)` — slightly more info to the decider.
  - `IEnumerable<(string StreamName, Event e)> DecideMulti(Command cmd, IReadOnlyList<VersionedEvent> history)` — explicit mapping of events to streams.
- ApplicationService then calls EventStore.AppendToStreamsAsync with grouped per-stream EventData[] and ExpectedVersion(s) (or use ReadStreamsForDecisionAsync to produce a DcbAppendCondition for guards) and submit one atomic transaction[^2:205-269].
- Pros: Decider expresses where each event belongs; cleaner multi-stream atomics and supports heterogenous event routing.
- Cons: Changes Decider interface and all implementers/tests; more refactor but semantically explicit.

## Concrete recommended signature changes

Recommendation A — Minimal, non-breaking, DCB-friendly
- Add helper overloads on ApplicationService that use EventStore.ReadStreamsForDecisionAsync and the DCB append API.
- Example (pseudo-signature):

```csharp
public static async Task ExecuteWithDcb<TState>(
    this DaprClient dapr,
    string eventStateStoreName,
    string targetStreamName,
    Command command,
    Decider decider,
    CancellationToken cancellationToken = default)
{
    var eventStore = new DaprEventStore.DaprEventStore(dapr) { StoreName = eventStateStoreName }.PartitionAllStream();

    var (events, condition) = await eventStore.ReadStreamsForDecisionAsync(targetStreamName /* or other guards */);
    var currentState = await events.Select(x => x.EventAs<Event>()).AggregateAsync(decider.InitialState, decider.Evolve);

    var domainEvents = decider.Decide(command, currentState);
    var eventData = domainEvents.Select(e => EventData.Create(e.GetType().Name, e)).ToArray();

    await eventStore.AppendToStreamAsync(targetStreamName, condition, eventData);
}
```

Caveats/Notes:
- Use PartitionAllStream() to ensure all guard streams and the target share partition, required by ReadStreamsForDecisionAsync/AppendToStreamsAsync behavior[^2:42-49][^2:137-138].
- This keeps Decider untouched, but requires ApplicationService to orchestrate the Read→Decide→Append flow and pass the DcbAppendCondition to the DaprEventStore append that enforces version checks[^2:50-75][^2:139-195].

Recommendation B — Decider emits stream-routing (explicit multi-stream)
- Change Decider.Decide to return a richer type that includes stream destination and optionally expected version per stream:

```csharp
public record RoutedEvent(string StreamName, object EventPayload);

public interface IDecider
{
    TState InitialState { get; }
    TState Evolve(TState state, VersionedEvent e);
    IEnumerable<RoutedEvent> Decide(Command cmd, TState state);
}
```

- Then ApplicationService can group RoutedEvents by StreamName and call `eventStore.AppendToStreamsAsync` with per-stream ExpectedVersion checks (or use ReadStreamsForDecisionAsync to produce a DcbAppendCondition for guards) and submit one atomic transaction[^2:205-269].
- This enables the EventStore to atomically append events across streams if they share the same partitionKey.

## API ergonomics & practical recommendations

1. CancellationToken: Add CancellationToken parameters to all Execute methods and any long-running IO call paths. Current methods lack tokens[^1:8-18][^1:21-34][^1:39-66].
2. Explicit partition intent: Either accept a partitionKey parameter or enforce PartitionAllStream/PartitionPerStream earlier and document expectations. EventStore's multi-stream atomic append requires a shared partitionKey in meta[^2:210-220].
3. Event envelope and OccuredAt: EventStore sets OccuredAt when persisting if default; decider might benefit from observing OccuredAt on read (already present on VersionedEvent)[^2:98-100][^2:236-244]. Consider exposing VersionedEvent to Decider.Evolve to allow time-sensitive logic[^2:71-72][^2:291].
4. Preserve backward compatibility: Prefer adding new overloads instead of changing Decider signature immediately; then migrate callers incrementally.

## Migration plan (concrete steps)

1. Add new ApplicationService helper overload(s) implementing Recommendation A. Unit test them.
   - Implement ReadStreamsForDecisionAsync-based flow and atomic AppendToStreamAsync with DcbAppendCondition.
   - Add tests using existing EventStore integration fixtures, gated with TEST_REDIS where needed.
2. Add integration test demonstrating atomic multi-stream append using AppendToStreamsAsync and a Decider that routes events to multiple streams.
3. If adopting Recommendation B, change Decider interface in a new major/minor API version and migrate implementers.
4. Update docs (README & docs/research) describing DCB guarantees and PartitionAllStream requirement for multi-stream atomics[^2:136-138][^2:211-221].

## Tests to add

- Read→Decide→Append with DcbAppendCondition should succeed if no concurrent update and fail with DBConcurrencyException when a guard is updated between read and append[^2:156-171].
- Multi-stream AppendToStreamsAsync scenario where both streams share partitionKey should commit atomically; verify versions mapping returned and that partial visibility doesn't occur[^2:205-269].
- Negative test: Attempt AppendToStreamsAsync where streams have different partitionKey meta should throw and not perform writes[^2:218-221].

## Risks and trade-offs

- Changing Decider signature is invasive and will touch all consumers; prefer adding overloads first.
- Using global regex refactors (earlier attempted remove-underscores) can break syntax; use Roslyn for symbol-aware renames if applying widespread breaking changes.
- Dapr transaction semantics require same partition — this is a real operational constraint (document and enforce) — otherwise atomicity is impossible[^2:137-138][^2:211-221].

## Confidence assessment

- High confidence about current ApplicationService code shape and EventStore capabilities (directly observed in code)[^1][^2].
- Medium confidence about the project's intended DCB semantics — inferred from EventStore methods ReadStreamsForDecisionAsync and DcbAppendCondition naming and comments[^2:50-75][^2:298-302].
- Lower confidence about organizational constraints and how broadly Decider interface can be changed (requires policy decision); recommendations assume incremental migration.

## Actionable checklist (short)

- [ ] Add ExecuteWithDcb helper that uses ReadStreamsForDecisionAsync + AppendToStreamAsync(condition)
- [ ] Add CancellationToken parameters to Execute overloads
- [ ] Add integration tests for DCB guard failure and multi-stream atomic append
- [ ] Document PartitionAllStream and partitionKey requirement in README/docs
- [ ] If needed, plan Decider API migration (define IDecider v2 and adapter)

## Footnotes

[^1]: `C:\Users\per.okvist\Documents\GitHub\dapr-lab\src\SampleApp\ApplicationService.cs:6-18, 21-34, 39-66, 68-101` (current Execute overloads and multi-stream prototype).

[^2]: `C:\Users\per.okvist\Documents\GitHub\dapr-lab\src\DaprEventStore\EventStore.cs:50-75, 139-195, 205-269, 298-302, 210-221` (ReadStreamsForDecisionAsync, DCB append overload, AppendToStreamsAsync, DcbAppendCondition, partitionKey checks).
