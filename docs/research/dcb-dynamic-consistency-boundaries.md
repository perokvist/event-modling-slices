# Dynamic Consistency Boundaries (DCB) — Append Model on Stream-Based Stores

**Research question:** *If DCB is implemented on top of streams (like our Dapr EventStore), does it allow appending to multiple streams, or does it only check concurrency across multiple streams?*

---

## Executive Summary

DCB (Dynamic Consistency Boundary, coined by Sara Pellegrini) **replaces static aggregate boundaries with per-command consistency boundaries** defined dynamically by the query used to build decision state. The key design insight is: the concurrency guarantee is based on the *highest sequence number observed across any event matching the query*, not on a single stream's version. When implemented on a stream-based event store, **DCB changes the concurrency CHECK to span multiple streams, but the WRITE remains a single append to one logical destination**. DCB does not atomically append to multiple streams; that is explicitly outside its design. The pattern is a re-scoping of the *read-decide-append* loop, not a multi-stream write protocol.

---

## Background: The Problem DCB Solves

Traditional aggregate-based event sourcing has a fixed consistency boundary: one aggregate = one stream. This creates two failure modes:

1. **Over-bounded aggregates** — a single aggregate owns too many events and becomes a bottleneck, causing unnecessary contention (e.g. locking an entire `Course` just to enroll one student).
2. **Cross-aggregate consistency** — enforcing an invariant that spans two aggregates (e.g. "a student may not enroll in more than 5 courses") requires a third aggregate, a saga, or a process manager.

DCB solves both by letting the *command handler* declare exactly which past events it needs to observe to make its decision, and then asserting "nothing in that set of events changed between my read and my write".[^1]

---

## The Core Model

### Events Are Tagged, Not Streamed

In the native DCB model, events are stored in a **global append log**. Each event carries a set of **tags** (key-value metadata) instead of belonging to a named stream:

```typescript
// from bwaidelich/dcb-event-store — EventStore.ts
export type Tags = Record<string, string | string[]>

export interface EsEvent {
    type: string
    tags: Tags   // e.g. { courseId: "C1", studentId: "S2" }
    data: unknown
}
```

A "stream" in the traditional sense is replaced by tag-based filtering. The event `StudentWasSubscribed { courseId: "C1", studentId: "S2" }` simultaneously belongs to the "course C1" virtual stream and the "student S2" virtual stream — you decide what it belongs to at read time, not write time.[^2]

### The Append Condition

The `append` signature is the key to understanding DCB concurrency:

```typescript
// bwaidelich/dcb-event-store — EventStore.ts
export type AppendCondition = {
    query: EsQuery         // the same query used for the read
    maxSequenceNumber: SequenceNumber  // the highest seq# seen during the read
}

export interface EventStore {
    append: (
        events: EsEvent | EsEvent[],
        condition: AppendCondition | AnyCondition
    ) => Promise<EsEventEnvelope[]>
    read: (query: EsQuery, options?: EsReadOptions) => AsyncGenerator<EsEventEnvelope>
}
```

The append contract is: **"The last event matching this query has sequence number ≤ `maxSequenceNumber`. If a newer matching event exists at the time of append, reject it."**[^3]

This is structurally identical to an optimistic concurrency check on a single stream's version number — but the version here is the maximum position across ALL events that matched the multi-tag query.

### The Read-Decide-Append Loop

The `reconstitute` function assembles the decision state and the append condition in a single pass:

```typescript
// bwaidelich/dcb-event-store — reconstitute.ts
export async function reconstitute<T extends EventHandlers>(
    eventStore: EventStore,
    eventHandlers: T
): Promise<{ state: EventHandlerStates<T>; appendCondition: AppendCondition }> {
    const query: EsQuery = {
        criteria: R.values(R.map(
            proj => ({ tags: proj.tagFilter, eventTypes: R.keys(proj.when) as string[] }),
            eventHandlers
        ))
    }

    let maxSequenceNumber = SequenceNumber.zero()
    for await (const eventEnvelope of eventStore.read(query)) {
        // ... apply each handler ...
        if (sequenceNumber > maxSequenceNumber) maxSequenceNumber = sequenceNumber
    }

    return { state: states, appendCondition: { query, maxSequenceNumber } }
}
```

The `appendCondition.query` captures all the criteria from all the handlers. The `appendCondition.maxSequenceNumber` is the watermark. Any new event matching ANY of those criteria after this moment would fail the append check.[^4]

### Real Example: Subscribe Student to Course

```typescript
// bwaidelich/dcb-event-store — EventSourcedApi.ts
subscribeStudentToCourse: async ({ courseId, studentId }) => {
    const { state, appendCondition } = await reconstitute(eventStore, {
        courseExists: CourseExists(courseId),          // reads CourseWasRegistered with tag courseId
        courseCapacity: CourseCapacity(courseId),      // reads capacity events with tag courseId
        studentAlreadySubscribed: StudentAlreadySubscribed({ courseId, studentId }),
        studentSubscriptions: StudentSubscriptions(studentId) // reads sub events with tag studentId
    })

    // enforce invariants using state ...

    await eventPublisher.publish(
        new StudentWasSubscribedEvent({ courseId, studentId }),
        appendCondition   // condition spans events tagged with courseId AND events tagged with studentId
    )
}
```

The `appendCondition` now spans what would traditionally be TWO separate aggregates: `Course-C1` stream and `Student-S2` stream. But the **write** is still a single event appended to the global log. No multi-stream atomic write occurs.[^5]

---

## What Happens in the Append Check

In the in-memory implementation, the check is explicit:

```typescript
// bwaidelich/dcb-event-store — MemoryEventStore.ts
if (appendCondition !== "Any") {
    const { query, maxSequenceNumber } = appendCondition

    const matchingEvents = (query?.criteria ?? []).flatMap(criterion =>
        this.events.filter(
            event =>
                !isSeqOutOfRange(event.sequenceNumber, maxSequenceNumber.plus(1), false) &&
                matchesCriterion(criterion, event)
        )
    )

    if (matchingEvents.length > 0)
        throw new Error("Expected Version fail: New events matching appendCondition found.")
}

this.events.push(...eventEnvelopes)  // single atomic push to the global log
```

The query is re-evaluated against events after `maxSequenceNumber`. If any new event matches — even one from a completely different "conceptual stream" — the append fails. This is the DCB concurrency guarantee: **the entire decision boundary becomes stale if any relevant fact changed**.[^6]

---

## DCB on Top of Streams (Not a Global Log)

When DCB is adapted to a stream-based event store (EventStoreDB, our Dapr KV-backed store), the model changes slightly:

### Read Phase

Instead of a tag query against a global log, you must read from **multiple named streams** and collect their latest positions:

```
streams to read = union of streams implied by the query criteria
               = [ "Course-C1", "Student-S2" ]
maxPosition    = max(position in Course-C1, position in Student-S2)
```

### Concurrency Check

The `AppendCondition` encodes per-stream expected versions:

```
appendCondition = {
    "Course-C1"  → expectedVersion = 5,
    "Student-S2" → expectedVersion = 3
}
```

If `Course-C1` is at version 6 at commit time → concurrency conflict → retry.

### Write Phase

You append **one event to one stream**. That event gets version N+1 on its stream. The other streams in the condition are checked but NOT written to.

```
append StudentWasSubscribed → stream "Course-C1" at expectedVersion 5
                               (Student-S2 is checked but not written)
```

**This is the direct answer to the question: DCB on streams checks concurrency across multiple streams but writes to only one.**

### The Java/Postgres Variant (gnschenker/dcb-starter)

The `SliceConcurrencyGuard` shows this pattern directly:

```java
// gnschenker/dcb-starter — SliceConcurrencyGuard.java
public EventRecord runWithGuard(EventSlice slice, ...) {
    List<EventRecord> events = fetcher.apply(slice);          // read across the slice
    String fingerprint = EventSliceFingerprint.compute(events); // hash of all seq numbers

    Object state = stateBuilder.apply(events);
    EventRecord record = eventBuilder.apply(state);           // single event to append

    List<EventRecord> verifyEvents = fetcher.apply(slice);    // re-read
    String verifyFingerprint = EventSliceFingerprint.compute(verifyEvents);
    if (!fingerprint.equals(verifyFingerprint)) {
        // retry — something changed across the slice
    }

    appender.accept(record);  // append ONE event
    return record;
}
```

The fingerprint (SHA-256 of all sequence numbers in the result set) acts as the multi-stream version token. Any change to any event in the slice — across any stream — invalidates it and forces a retry.[^7]

The `EventSlice` query definition for enrolling a student spans four event types across what would be two streams:

```java
// gnschenker/dcb-starter — EnrollStudentSliceFactory.java
return EventSliceBuilder.slice("enrollment-" + studentId + "-" + courseId)
        .include("CoursePublished")        .where("courseId").eq(courseId)
        .include("CourseCapacityChanged")  .where("courseId").eq(courseId)
        .include("StudentEnrolled")        .where("courseId").eq(courseId)
        .include("StudentEnrolled")        .where("studentId").eq(studentId)
        .include("StudentUnenrolled")      .where("courseId").eq(courseId)
        .include("StudentUnenrolled")      .where("studentId").eq(studentId)
        .build();
```

This reads from what would be the "course stream" and the "student stream" simultaneously. The resulting single event is appended with `stream_id = studentId + "-" + courseId` — a composite key, not two separate streams.[^8]

---

## Adapting Our Dapr EventStore to DCB

Our current `DaprEventStore` is strictly single-stream:

```csharp
// src/DaprEventStore/EventStore.cs
public async Task<long> AppendToStreamAsync(
    string streamName,
    Action<StreamHead> concurrencyGuard,
    params EventData[] events)
{
    var (head, headetag) = await client.GetStateAndETagAsync<StreamHead>(
        StoreName, Naming.StreamHead(streamName), ...);
    concurrencyGuard(head);   // checks ONLY this stream's version
    ...
    await client.EventsTransactionAsync(StoreName, streamName, ...);
}
```

To support DCB, the required changes are:

| Current | DCB-compatible |
|---------|---------------|
| `concurrencyGuard: Action<StreamHead>` — checks one stream's version | Multi-stream read returning `maxPosition` across N streams |
| Append condition = `expectedVersion` on one stream | Append condition = `{ [streamName]: expectedVersion }` map for N streams |
| `EventsTransactionAsync` writes to one store | Same — still writes to one store, but checks N streams atomically |
| `MetaProvider(streamName)` — one partition key | Multiple partition keys, one per queried stream |

The Dapr state store's `ExecuteStateTransactionAsync` with etags gives us per-key optimistic concurrency, which is sufficient for checking multiple stream heads as long as they share a partition (or we use a global lock).

**Option A — Per-stream etag check before append**
Read the head of each involved stream, collect their etags, check no etag changed between read and write, then append to the primary stream. This is a "manual" DCB using sequential reads + optimistic write.

**Option B — Store all events in a single partition**
Use `PartitionAllStream` (already exists) to put all events in one partition with a single global sequence number — then the DCB append condition is `maxSeqNumber` exactly as in the native implementation.

---

## Summary Table

| Aspect | Traditional Aggregate | DCB |
|--------|----------------------|-----|
| Read boundary | One stream (one aggregate) | Query across N streams or tags |
| Concurrency token | `expectedVersion` of one stream | `maxSequenceNumber` across all matching events |
| Concurrency scope | Single stream | Any event matching the query |
| Write destination | One stream | **Still one stream (or global log)** |
| Multi-stream atomic write? | No | **No — not part of DCB** |
| Implementation complexity | Low | Medium — requires multi-stream read and cross-stream seq# tracking |

---

## Key Repositories

| Repository | Language | Notes |
|---|---|---|
| [bwaidelich/dcb-event-store](https://github.com/bwaidelich/dcb-event-store) | TypeScript | Reference impl of Sara Pellegrini's DCB spec; in-memory + Postgres adapters |
| [gnschenker/dcb-starter](https://github.com/gnschenker/dcb-starter) | Java/Spring | "EventSlice DSL" variant; global Postgres table, fingerprint-based concurrency |

---

## Confidence Assessment

**High confidence:**
- The `append` signature and semantics in `bwaidelich/dcb-event-store` are directly from source code — no ambiguity.
- The `SliceConcurrencyGuard` and its single-append pattern in `gnschenker/dcb-starter` are verified from source.
- The `reconstitute` function building a multi-criteria `appendCondition` is directly read.

**Medium confidence:**
- The adaptation strategy to stream-based stores is inferred from the pattern's semantics. There are no widely adopted reference implementations on EventStoreDB/Marten that were findable on GitHub at this time. The Marten library has DCB-adjacent features ("single-stream aggregates" with version checks) but its multi-stream DCB support is separate work.
- "Appends to one stream" is the standard interpretation, but advanced usages could technically append to multiple streams separately (not atomically) after passing the DCB check — that would be a non-standard extension.

**Assumed:**
- Sara Pellegrini's original blog posts (sara.event-thinking.io, "Kill the Aggregate" series, 2023) are the conceptual origin — not directly fetchable but referenced by both repos.

---

## Footnotes

[^1]: [`bwaidelich/dcb-event-store` README](https://github.com/bwaidelich/dcb-event-store/blob/main/readme.md) — "Implementation of the Dynamic Consistency Boundary pattern... described by Sara Pellegrini"

[^2]: [`eventStore/EventStore.ts:1-20`](https://github.com/bwaidelich/dcb-event-store/blob/8e27ef1de1da2eb4519c4cff0d0467d9f858a602/eventStore/EventStore.ts) — `EsEvent` with `tags` field; `EsQueryCriterion` filters by `tags` + `eventTypes`

[^3]: [`eventStore/EventStore.ts:21-32`](https://github.com/bwaidelich/dcb-event-store/blob/8e27ef1de1da2eb4519c4cff0d0467d9f858a602/eventStore/EventStore.ts) — `AppendCondition = { query: EsQuery; maxSequenceNumber: SequenceNumber }`

[^4]: [`eventHandling/reconstitute/reconstitute.ts:1-46`](https://github.com/bwaidelich/dcb-event-store/blob/8e27ef1de1da2eb4519c4cff0d0467d9f858a602/eventHandling/reconstitute/reconstitute.ts) — `reconstitute` builds `query` from all handlers and tracks `maxSequenceNumber`

[^5]: [`courseManager/eventSourced/EventSourcedApi.ts:72-90`](https://github.com/bwaidelich/dcb-event-store/blob/8e27ef1de1da2eb4519c4cff0d0467d9f858a602/courseManager/eventSourced/EventSourcedApi.ts) — `subscribeStudentToCourse` spans 4 write-model handlers across course and student concerns, publishes ONE event

[^6]: [`eventStore/memoryEventStore/MemoryEventStore.ts:63-80`](https://github.com/bwaidelich/dcb-event-store/blob/8e27ef1de1da2eb4519c4cff0d0467d9f858a602/eventStore/memoryEventStore/MemoryEventStore.ts) — `append` checks events after `maxSequenceNumber`, throws on any match, then does `this.events.push(...eventEnvelopes)`

[^7]: [`src/main/java/com/example/eventsourcing/core/store/SliceConcurrencyGuard.java`](https://github.com/gnschenker/dcb-starter/blob/5e8dab1bc3a2b7dcc00a5bfa6bd71fc76e19b74d/src/main/java/com/example/eventsourcing/core/store/SliceConcurrencyGuard.java) — double-fetch fingerprint check, single `appender.accept(record)` write

[^8]: [`src/main/java/com/example/university/features/enrollstudent/slice/EnrollStudentSliceFactory.java`](https://github.com/gnschenker/dcb-starter/blob/5e8dab1bc3a2b7dcc00a5bfa6bd71fc76e19b74d/src/main/java/com/example/university/features/enrollstudent/slice/EnrollStudentSliceFactory.java) — slice spanning both `courseId` and `studentId` filters; [`EnrollStudentService.java`](https://github.com/gnschenker/dcb-starter/blob/5e8dab1bc3a2b7dcc00a5bfa6bd71fc76e19b74d/src/main/java/com/example/university/features/enrollstudent/app/EnrollStudentService.java) — `store::append` called with single `EventRecord` whose `stream_id = studentId + "-" + courseId`
