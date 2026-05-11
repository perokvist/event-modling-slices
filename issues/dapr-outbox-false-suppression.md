# Bug: outbox.projection:false does not suppress auto-publication without a matching projection

**Component**: `dapr/dapr` (runtime)  
**Version**: v1.17.3  
**Severity**: Medium (undocumented behaviour diverges from expected semantics)

---

## Description

The Dapr documentation and naming convention for `outbox.projection: false` implies that setting it on a state transaction entry will **suppress** that entry from being auto-published to the configured pub/sub topic. In practice, `outbox.projection: false` only suppresses the auto-published **value** when a matching `outbox.projection: true` entry exists for the same key. Without a matching `true` entry, a `false`-annotated entry is published identically to an entry with no projection metadata.

---

## Observed vs Expected Behaviour

| Scenario | Expected | Actual |
|---|---|---|
| `[K1(false)]` alone | K1 NOT published | K1 IS published |
| `[K1(false), K1(true)]` | K1's `true` value published, not `false` value | ✅ Correct |
| `[K1(false), K2(false), K1(true)]` | K1's `true` value published; K2 NOT published | K1 AND K2 both published |

---

## Root Cause

In `pkg/runtime/pubsub/outbox.go`, `PublishInternal`:

1. All entries with `outbox.projection: true` are extracted into a `projections` map, then **removed** from the operations list.
2. The remaining operations are iterated. For each `SetRequest`, the code calls `o.publisher.Publish(...)`. There is **no check** for `outbox.projection: false` in this loop — all remaining entries are published unconditionally.
3. The `false` flag only affects which **value** is published: if a projection exists for that key, the projection's value is used instead of the entry's own value.

Without a matching `true` projection, `false` has no effect on whether the entry is published.

---

## Reproduction

```csharp
// K2(false) is still published — no matching projection
await client.ExecuteStateTransactionAsync("statestore-outbox",
[
    new("K1", data1, StateOperationType.Upsert,
        metadata: new Dictionary<string, string> { { "outbox.projection", "false" } }),
    new("K2", data2, StateOperationType.Upsert,
        metadata: new Dictionary<string, string> { { "outbox.projection", "false" } }),
    new("K1", projectionData, StateOperationType.Upsert,
        metadata: new Dictionary<string, string> { { "outbox.projection", "true" } })
]);
// Both K1 (as projectionData) AND K2 (as data2) are published — K2 publication is unexpected
```

---

## Impact

This prevents a natural single-transaction design for event sourcing outbox patterns where:
- Multiple event entries should be stored but NOT published individually
- Only one "notification" projection per transaction should be published

Because `false` doesn't suppress, any design that includes non-projected entries in an outbox-configured store transaction will publish those entries unexpectedly.

---

## Suggested Fix

In `PublishInternal`'s iteration loop, skip any `SetRequest` that has `outbox.projection: false` AND no matching projection in the `projections` map:

```go
// Skip entries explicitly marked as non-projection without a matching true projection
if projection, hasProjection := projections[sr.Key]; !hasProjection {
    if isProjectionFalse(sr.Metadata) {
        continue // explicitly suppressed, no matching projection
    }
}
```

This would make `false` behave as documented: "stored, not published".
