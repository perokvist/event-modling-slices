# Research: Did Our DCB Research Find That DCB Only Writes to a Single Stream?

**Short answer: Yes — explicitly and unambiguously.**

The prior research document (`docs/research/dcb-dynamic-consistency-boundaries.md`) was written precisely to answer this question, and it concludes clearly that DCB writes to one stream, not many.

---

## Executive Summary

Our DCB research document explicitly states — in its Executive Summary, in a dedicated "Write Phase" section, and in a summary table — that **DCB only writes to a single stream**. The multi-stream aspect of DCB is entirely about the *concurrency check* (reading N streams and asserting their versions haven't changed), not about writing events to multiple streams. This is confirmed both by the reference implementations studied (TypeScript and Java) and by our own Dapr-backed implementation in `src/DaprEventStore/EventStore.cs`.

---

## What the Research Document Says

### 1. Executive Summary of the Research Doc

The opening paragraph of `docs/research/dcb-dynamic-consistency-boundaries.md` states[^1]:

> "DCB (Dynamic Consistency Boundary) **replaces static aggregate boundaries with per-command consistency boundaries** defined dynamically by the query used to build decision state. When implemented on a stream-based event store, **DCB changes the concurrency CHECK to span multiple streams, but the WRITE remains a single append to one logical destination**. DCB does not atomically append to multiple streams; that is explicitly outside its design."

This is the document's own first sentence — framing single-stream writes as a core, settled fact.

### 2. The Dedicated "Write Phase" Section

The research document has a section titled "DCB on Top of Streams" with three sub-phases. The "Write Phase" states[^2]:

> "You append **one event to one stream**. That event gets version N+1 on its stream. The other streams in the condition are checked but NOT written to."
>
> ```
> append StudentWasSubscribed → stream "Course-C1" at expectedVersion 5
>                                (Student-S2 is checked but not written)
> ```
>
> **"This is the direct answer to the question: DCB on streams checks concurrency across multiple streams but writes to only one."**

That last line is a verbatim quote from the doc — it was written as a deliberate direct answer to this exact question.

### 3. The Summary Table

The research doc's Summary Table includes a row that explicitly addresses multi-stream writes[^3]:

| Aspect | Traditional Aggregate | DCB |
|--------|----------------------|-----|
| Write destination | One stream | **Still one stream (or global log)** |
| Multi-stream atomic write? | No | **No — not part of DCB** |

---

## What the Reference Implementations Confirm

### TypeScript: bwaidelich/dcb-event-store

In the in-memory reference implementation, the `append` method — even when given an `AppendCondition` that queries across many tags — does a single push to the global log[^4]:

```typescript
// MemoryEventStore.ts
if (appendCondition !== "Any") {
    const { query, maxSequenceNumber } = appendCondition
    // ... check for conflicting events across the multi-tag query ...
    if (matchingEvents.length > 0)
        throw new Error("Expected Version fail: New events matching appendCondition found.")
}

this.events.push(...eventEnvelopes)  // ← ONE write, to ONE global log
```

Even in the `subscribeStudentToCourse` example — which reads across what would be two streams (course events + student events) — the eventual write is ONE event[^5]:

```typescript
await eventPublisher.publish(
    new StudentWasSubscribedEvent({ courseId, studentId }),
    appendCondition   // spans courseId AND studentId events, but only ONE event is published
)
```

### Java: gnschenker/dcb-starter

The `SliceConcurrencyGuard` reads across multiple event types and tags (across "course stream" + "student stream"), hashes all sequence numbers for a fingerprint, then appends exactly one record[^6]:

```java
// SliceConcurrencyGuard.java
List<EventRecord> events = fetcher.apply(slice);   // multi-tag read
String fingerprint = EventSliceFingerprint.compute(events);
// ... build state, decide ...
appender.accept(record);  // ← ONE event appended
```

The event is stored with `stream_id = studentId + "-" + courseId` — a composite target stream, but still a single stream[^7].

---

## How Our Dapr Implementation Confirms This

Our own `EventStore.cs` implements DCB append (`AppendToStreamAsync` with a `DcbAppendCondition`). The code makes the single-stream write explicit[^8]:

```csharp
// EventStore.cs lines 133–183
public async Task<long> AppendToStreamAsync(
    string streamName,           // ← ONE target stream
    DcbAppendCondition condition,
    params EventData[] events)
{
    // Re-read each guard stream head to get a fresh etag — for conflict detection only:
    foreach (var (observedName, expectedVersion) in condition.ObservedStreams)
    {
        if (observedName == streamName) continue; // target handled separately
        var (guardHead, guardEtag) = await client.GetStateAndETagAsync<StreamHead>(...);
        // version mismatch → throw concurrency exception (no write occurs)
        guardEntries.Add((guardHeadKey, guardHead, guardEtag, guardMeta));
    }

    // Single transaction:
    await client.ExecuteStateTransactionAsync(
        storeName: StoreName,
        operations: versionedEvents.ToStateTransactionsDcb(
            streamName,    // ← events go into THIS stream only
            streamHeadKey,
            newHead, headEtag, meta,
            guardEntries,  // ← guard streams: no-op writes with fresh etags (detect conflict; add NO events)
            ...));
}
```

The `ToStateTransactionsDcb` helper in `Extensions.cs` makes the distinction explicit in code and a doc comment[^9]:

```csharp
// Extensions.cs lines 138–159
/// <summary>
/// Builds the StateTransactionRequest list for a DCB append:
/// the target stream head write, one guard no-op entry per observed stream, then all event entries.
/// Guard entries write the same head value back with a fresh etag so Dapr rejects the transaction
/// if any guard stream was modified by a concurrent writer between the fresh re-read and commit.
/// </summary>
public static StateTransactionRequest[] ToStateTransactionsDcb(...)
{
    var result = new List<StateTransactionRequest>
    {
        streamHead.CreateStreamHeadStateRequest(streamHeadKey, headEtag, meta)  // target head
    };
    foreach (var (headKey, head, etag, guardMeta) in guardEntries)
        result.Add(head.CreateStreamHeadStateRequest(headKey, etag, guardMeta)); // guard: no-op, same value back

    result.AddRange(events.CreateEventStateRequests(streamName, ...));  // events → target stream only
    return result.ToArray();
}
```

Guard streams participate in the transaction **only to enforce the etag conflict check** — they receive a write of their *current* head value unchanged. No new events are written to them.

---

## Why "Single-Stream Write" Is by Design (in the Canonical Model)

The research document explains the rationale clearly. DCB is about scoping the **consistency guarantee**, not about denormalizing events into multiple streams simultaneously. The key insight is:

> "The concurrency guarantee is based on the *highest sequence number observed across any event matching the query*, not on a single stream's version."

Writing to one stream is sufficient because:
1. The event captures the fact that happened (e.g. `StudentWasSubscribed`).
2. The `AppendCondition` ensures no relevant precondition changed between read and write.
3. Readers who care about both course-centric and student-centric views reconstruct them from the global (or partition-scoped) event log by filtering — not by reading separate per-entity streams.

> **See also:** [`dcb-multi-stream-write-extension.md`](dcb-multi-stream-write-extension.md) for research on whether our Dapr design can and should extend beyond single-stream writes.

---

## Confidence Assessment

**High confidence — no inference required.**

All three sources of evidence are direct and unambiguous:
1. The research document was written to answer this question and contains an explicit sentence: *"This is the direct answer to the question: DCB on streams checks concurrency across multiple streams but writes to only one."*
2. The reference implementation source code (TypeScript in-memory store, Java `SliceConcurrencyGuard`) confirms single-append behavior.
3. Our own `EventStore.cs` implementation mirrors exactly this pattern in code, with guard-stream no-ops and a single target stream for events.

---

## Footnotes

[^1]: `docs/research/dcb-dynamic-consistency-boundaries.md` lines 7–9 — Executive Summary paragraph

[^2]: `docs/research/dcb-dynamic-consistency-boundaries.md` lines 178–185 — Write Phase section

[^3]: `docs/research/dcb-dynamic-consistency-boundaries.md` lines 270–278 — Summary Table

[^4]: [`bwaidelich/dcb-event-store — MemoryEventStore.ts:63–80`](https://github.com/bwaidelich/dcb-event-store/blob/8e27ef1de1da2eb4519c4cff0d0467d9f858a602/eventStore/memoryEventStore/MemoryEventStore.ts) — append guard then `this.events.push(...eventEnvelopes)`

[^5]: [`bwaidelich/dcb-event-store — EventSourcedApi.ts:72–90`](https://github.com/bwaidelich/dcb-event-store/blob/8e27ef1de1da2eb4519c4cff0d0467d9f858a602/courseManager/eventSourced/EventSourcedApi.ts) — `subscribeStudentToCourse` publishes one `StudentWasSubscribedEvent`

[^6]: [`gnschenker/dcb-starter — SliceConcurrencyGuard.java`](https://github.com/gnschenker/dcb-starter/blob/5e8dab1bc3a2b7dcc00a5bfa6bd71fc76e19b74d/src/main/java/com/example/eventsourcing/core/store/SliceConcurrencyGuard.java) — double-fetch fingerprint + single `appender.accept(record)`

[^7]: [`gnschenker/dcb-starter — EnrollStudentSliceFactory.java`](https://github.com/gnschenker/dcb-starter/blob/5e8dab1bc3a2b7dcc00a5bfa6bd71fc76e19b74d/src/main/java/com/example/university/features/enrollstudent/slice/EnrollStudentSliceFactory.java) — slice with both `courseId` and `studentId` criteria; writes to composite stream `studentId + "-" + courseId`

[^8]: `src/DaprEventStore/EventStore.cs` lines 133–183 — `AppendToStreamAsync(string streamName, DcbAppendCondition condition, ...)` — single `ExecuteStateTransactionAsync` with `streamName` as the only event target

[^9]: `src/DaprEventStore/Extensions.cs` lines 133–159 — `ToStateTransactionsDcb` doc comment and implementation: guard entries use `CreateStreamHeadStateRequest` (head-only, no events), events go to target stream only via `CreateEventStateRequests(streamName, ...)`
