# Research: Should Our DCB Design Support Writing to More Than One Stream in a Transaction?

**Short answer: Yes — and the infrastructure already supports it. Only the API needs extending.**

---

## Executive Summary

The canonical DCB pattern writes to a single global log, so "multi-stream writes" is not even a concept there — the log _is_ the stream. In our stream-based Dapr adaptation, the natural analog to the global log is the **Dapr partition**: because `PartitionAllStream` places all streams in one Dapr partition, a single `ExecuteStateTransactionAsync` call is already atomic across any number of streams in that partition. Our current `AppendToStreamAsync(string streamName, DcbAppendCondition, ...)` API artificially limits writes to one named stream, but the underlying `ToStateTransactionsDcb` machinery already builds a `List<StateTransactionRequest>` that could include event entries for multiple target streams. A `TODO` comment in `ApplicationService.cs` even explicitly marks the multi-stream loop as needing to be "in single transaction." Extending to multi-stream atomic DCB appends is therefore a low-risk API extension, not an infrastructure change.

---

## Why the Question Arises: Global Log vs. Named Streams

### The Original DCB Model Has No "Streams" Problem

In `bwaidelich/dcb-event-store`, every event goes into one global append log regardless of what tags it carries[^1]. When a command like `SubscribeStudentToCourse` decides to emit `StudentWasSubscribed`, the event is appended to the global log once. Consumers reconstruct views for course C1 or student S2 by filtering on tags at read time. There is no routing decision to make.

```typescript
// bwaidelich/dcb-event-store — EventSourcedApi.ts
await eventPublisher.publish(
    new StudentWasSubscribedEvent({ courseId, studentId }),
    appendCondition   // ← ONE publish, to ONE log
)
```

"Writing to multiple streams in one transaction" is therefore a non-concept in the canonical model — because the global log is the only destination[^2].

### In Stream-Based Stores, the Routing Question Emerges

When you map DCB to a store with **named streams** (EventStoreDB, our Dapr KV-backed store), a structural question appears: if a command produces events that are semantically meaningful in multiple contexts (e.g., `StudentWasSubscribed` belongs both to `course-C1` history and `student-S2` history), which stream does it go into?

The standard answer in the prior research was: pick one. The DCB research document chose `course-{id}` as the target stream in the enrollment example[^3]:

```
append StudentWasSubscribed → stream "Course-C1" at expectedVersion 5
                               (Student-S2 is checked but not written)
```

But this is a **convention**, not a constraint. Nothing in the DCB concurrency model _requires_ the write to go to only one stream. It just has to be **atomic** — the whole point of DCB is that the guard condition and the write happen without interference.

---

## How Our Design Already Supports Multi-Stream Writes

### The Partition Is the Atomicity Boundary

Our `PartitionAllStream` places all streams under the same Dapr partition key. Dapr's `ExecuteStateTransactionAsync` is atomic within a partition[^4]:

```csharp
// Extensions.cs lines 21–23
public static DaprEventStore PartitionAllStream(this DaprEventStore daprEventStore, string allStream = "all")
    => daprEventStore.PartitionCustom(allStream);
```

This means any set of keys that share the partition key can be updated atomically in a single transaction — including the head keys and event keys of **multiple named streams**. The partition is, in effect, the same role as the global log's single sequence: the unit of atomicity.

### `ToStateTransactionsDcb` Already Builds a List

The method that constructs the transaction payload returns a `List<StateTransactionRequest>` that today contains:

1. The target stream head update
2. Guard stream no-op writes (head value unchanged, etag used for conflict detection)
3. Event entries for the **one** target stream

```csharp
// Extensions.cs lines 138–159
public static StateTransactionRequest[] ToStateTransactionsDcb(
    this VersionedEvent[] events,
    string streamName,           // ← today: one target
    string streamHeadKey,
    StreamHead streamHead,
    string headEtag,
    Dictionary<string, string> meta,
    IEnumerable<(string HeadKey, StreamHead Head, string Etag, ...)> guardEntries,
    JsonSerializerOptions serializerOptions)
{
    var result = new List<StateTransactionRequest>
    {
        streamHead.CreateStreamHeadStateRequest(streamHeadKey, headEtag, meta)   // head of target
    };
    foreach (var (headKey, head, etag, guardMeta) in guardEntries)
        result.Add(head.CreateStreamHeadStateRequest(headKey, etag, guardMeta)); // guard no-ops

    result.AddRange(events.CreateEventStateRequests(streamName, meta, serializerOptions)); // events → target
    return result.ToArray();
}
```

The structure is already a list. Adding event entries for a second or third target stream is as simple as calling `CreateEventStateRequests` again with a different `streamName` and appending to the same list[^5].

### The TODO in ApplicationService.cs Confirms the Intent

`ApplicationService.cs` already has a multi-stream loop that writes to several streams based on which event type belongs to which stream — but it does so with sequential, non-atomic individual calls:

```csharp
// ApplicationService.cs lines 104–113
var foo = streamMeta.SelectMany(s => events
    .Where(x => x.GetType().IsAssignableTo(s.streamInfo.EventType))
    .Select((x, i) => (meta: s, EventData.Create(eventName: x.GetType().Name, data: x)))
    .ToArray());

foreach (var f in foo)
{
    //TODO this in single transaction       ← explicit acknowledgement of the gap
    await eventStore.AppendToStreamAsync(f.meta.streamInfo.StreamName, f.meta.Item2.version, f.Item2);
}
```

This TODO is a direct acknowledgement that multi-stream writes should be in one transaction but are not yet[^6]. The obstacle is entirely in the API shape, not the infrastructure.

---

## What the Multi-Stream DCB Append Would Look Like

### Proposed API Extension

The existing single-stream DCB overload:

```csharp
// EventStore.cs line 133
public async Task<long> AppendToStreamAsync(
    string streamName,
    DcbAppendCondition condition,
    params EventData[] events)
```

Could be complemented by a multi-stream variant:

```csharp
// Proposed new overload
public async Task<IReadOnlyDictionary<string, long>> AppendToStreamsAsync(
    IReadOnlyDictionary<string, EventData[]> streamsAndEvents,
    DcbAppendCondition condition)
```

### What the Implementation Would Do

The logic mirrors the current single-stream DCB append, generalized to N target streams:

```
1. For each stream in condition.ObservedStreams:
     if it is a TARGET stream: re-read head → get fresh etag + validate version
     if it is a GUARD-ONLY stream: re-read head → get fresh etag + validate version + mark as guard

2. For each TARGET stream:
     compute new head version = currentVersion + events.Length
     build VersionedEvent list

3. Build one StateTransactionRequest list:
     - New head write for each TARGET stream (with fresh etag)
     - No-op head write for each GUARD-ONLY stream (same value back, with fresh etag)
     - Event key entries for each TARGET stream

4. Execute ONE ExecuteStateTransactionAsync (single partition → atomic)

5. Return { streamName → newVersion } map
```

### Diagram

```
ReadStreamsForDecisionAsync(course, student, enrollment)
        │
        ▼
DcbAppendCondition
  { course-C1 → v5, student-S2 → v3, enrollment-C1-S2 → v0 }
        │
        ▼
AppendToStreamsAsync(
  { "course-C1"        → [ CourseSeatReserved ],
    "enrollment-C1-S2" → [ StudentWasSubscribed ] },
  condition
)
        │
        ▼ re-read all stream heads for fresh etags
        │
        ▼ ExecuteStateTransactionAsync (ONE call, ONE partition)
  ┌──────────────────────────────────────────────────────┐
  │  UPSERT course-C1|head         v6  (etag A) ← target  │
  │  UPSERT enrollment-C1-S2|head  v1  (etag C) ← target  │
  │  UPSERT student-S2|head        v3  (etag B) ← guard   │
  │  UPSERT course-C1|6            CourseSeatReserved      │
  │  UPSERT enrollment-C1-S2|1     StudentWasSubscribed    │
  └──────────────────────────────────────────────────────┘
        │
        ▼ If etag B changed (student-S2 was modified) → Dapr rejects entire transaction
```

### Comparison Table

| Aspect | Single-stream DCB (current) | Multi-stream DCB (proposed) |
|--------|----------------------------|------------------------------|
| Target streams | 1 | N |
| Guard streams | N-1 observed streams | Observed streams minus all targets |
| Transaction operations | head + guards + events | heads (all targets) + guards + events (all targets) |
| Atomicity boundary | Same Dapr partition | Same Dapr partition (no change) |
| `PartitionAllStream` required | Yes | Yes (same requirement) |
| Infrastructure changes | None | None |
| API changes | None | New overload needed |

---

## Why This Is Semantically Sound

### It Matches the Global-Log Semantics

In the canonical DCB global log, `StudentWasSubscribed` is written once to the log. Consumers reconstruct the course view or the student view by filtering on tags — the event "belongs" to both views simultaneously without being duplicated.

In a stream-based store, writing `CourseSeatReserved` to `course-C1` AND `StudentWasSubscribed` to `enrollment-C1-S2` in the same atomic transaction is the faithful equivalent. Both events exist atomically — there is no moment where one exists and the other doesn't. Consumers reading either stream see a consistent picture.

This is actually **more correct** than writing to one stream and projecting the other via a read model or eventual consistency — it avoids the "did the projection catch up yet?" problem.

### The DCB Concurrency Guarantee Is Unchanged

The `DcbAppendCondition` still guards all observed streams regardless of which ones are targets. If any observed stream changes between the read and the commit, Dapr's etag check rejects the entire transaction. This invariant is independent of how many streams are written to.

### The Constraint Doesn't Change

`PartitionAllStream` is already required for single-stream DCB. Multi-stream DCB is the same: all involved streams must share one partition. There is no new infrastructure requirement.

---

## Summary

| Question | Answer |
|----------|--------|
| Does canonical DCB write to multiple streams? | No — it writes to one global log (multi-stream is not a concept) |
| Does our Dapr partition support multi-stream atomic writes? | Yes — `ExecuteStateTransactionAsync` is atomic across all keys in one partition |
| Does the current API support it? | No — `AppendToStreamAsync` takes one `streamName` |
| Is the infrastructure already capable? | Yes — `ToStateTransactionsDcb` builds a list; adding more stream entries is trivial |
| Is the intent already acknowledged in the codebase? | Yes — `//TODO this in single transaction` in `ApplicationService.cs:112` |
| Is multi-stream DCB write semantically correct? | Yes — atomic append to N streams in one partition is the faithful stream-based analog of the global log |
| What is needed to implement it? | A new `AppendToStreamsAsync(Dictionary<string, EventData[]>, DcbAppendCondition)` overload and a generalized `ToStateTransactionsDcb` that accepts N target streams |

---

## Confidence Assessment

**High confidence:**
- The partition-level atomicity of Dapr's `ExecuteStateTransactionAsync` is documented Dapr behavior and confirmed by the existing guard-stream no-op mechanism, which already includes multiple keys from different streams in one transaction.
- The `TODO this in single transaction` comment in `ApplicationService.cs:112` is direct evidence that the developers identified this gap.
- The `ToStateTransactionsDcb` list-building pattern in `Extensions.cs:138–159` is confirmed from source and trivially extensible.

**Inferred (reasonable):**
- The proposed `AppendToStreamsAsync` API shape is a natural extension. The exact naming and return type are design choices, not fixed by existing code.
- That multi-stream write is "semantically equivalent to the global log" is an architectural interpretation — well-supported by the DCB pattern literature in `docs/research/dcb-dynamic-consistency-boundaries.md` but not an explicit statement in the existing codebase.

---

## Footnotes

[^1]: [`bwaidelich/dcb-event-store — EventSourcedApi.ts:72–90`](https://github.com/bwaidelich/dcb-event-store/blob/8e27ef1de1da2eb4519c4cff0d0467d9f858a602/courseManager/eventSourced/EventSourcedApi.ts) — `subscribeStudentToCourse` calls `eventPublisher.publish(new StudentWasSubscribedEvent(...), appendCondition)` — single publish call to global log

[^2]: [`bwaidelich/dcb-event-store — MemoryEventStore.ts:63–80`](https://github.com/bwaidelich/dcb-event-store/blob/8e27ef1de1da2eb4519c4cff0d0467d9f858a602/eventStore/memoryEventStore/MemoryEventStore.ts) — `this.events.push(...eventEnvelopes)` — one array is the whole store

[^3]: `docs/research/dcb-dynamic-consistency-boundaries.md` lines 178–185 — "Write Phase" section: "append StudentWasSubscribed → stream 'Course-C1' at expectedVersion 5 (Student-S2 is checked but not written)"

[^4]: `src/DaprEventStore/Extensions.cs` lines 21–23 — `PartitionAllStream` sets `partitionKey` to a single fixed value for all streams; [Dapr state management docs](https://docs.dapr.io/developing-applications/building-blocks/state-management/howto-stateful-service/) confirm transactions are atomic within a partition

[^5]: `src/DaprEventStore/Extensions.cs` lines 138–159 — `ToStateTransactionsDcb` — builds `List<StateTransactionRequest>` with guard entries and one `CreateEventStateRequests(streamName, ...)` call; adding a second target stream is `result.AddRange(moreEvents.CreateEventStateRequests(secondStreamName, ...))`

[^6]: `src/DaprEventStore/ApplicationService.cs` lines 104–113 — `foreach (var f in foo) { //TODO this in single transaction await eventStore.AppendToStreamAsync(...) }` — sequential non-atomic multi-stream writes with explicit TODO
