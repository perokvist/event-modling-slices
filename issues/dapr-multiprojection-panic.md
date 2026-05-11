# Bug: Multiple outbox.projection:true entries with different keys cause daprd runtime panic

**Component**: `dapr/dapr` (runtime)  
**Version**: v1.17.3  
**Severity**: High (daprd crashes — affects all pub/sub backends)

---

## Description

When an `ExecuteStateTransaction` call to an outbox-configured state store contains **two or more entries with `outbox.projection: true` and different keys**, daprd panics at runtime with a slice bounds out of range error. The entire sidecar process crashes, affecting all subsequent requests until it is restarted.

---

## Panic Output

```
panic: runtime error: slice bounds out of range [N:M] with N > M
goroutine ... [running]:
github.com/dapr/dapr/pkg/runtime/pubsub.(*outbox).PublishInternal(...)
```

*(Exact stack trace varies by daprd version)*

---

## Reproduction

```csharp
await client.ExecuteStateTransactionAsync("statestore-outbox",
[
    // First key: regular + projection
    new("key1", regularData1, StateOperationType.Upsert),
    new("key1", projectionData1, StateOperationType.Upsert,
        metadata: new Dictionary<string, string> { { "outbox.projection", "true" } }),
    // Second key: regular + projection — TRIGGERS PANIC
    new("key2", regularData2, StateOperationType.Upsert),
    new("key2", projectionData2, StateOperationType.Upsert,
        metadata: new Dictionary<string, string> { { "outbox.projection", "true" } }),
]);
// daprd crashes
```

---

## Expected Behaviour

Multiple projections with different keys should each be published as separate pub/sub messages. The Dapr docs describe projections as a mechanism to control the published payload; it is reasonable to expect one published message per `true` projection key.

---

## Root Cause

In `pkg/runtime/pubsub/outbox.go`, `PublishInternal`, the code extracts all `true` projections into a `projections` map and then **removes** them from the operations slice. The removal logic likely has an off-by-one error or incorrect index tracking when multiple consecutive projection entries are removed, resulting in a slice bounds panic.

The single-projection case (`[K1(regular), K1(true)]`) works correctly; the panic only occurs with 2+ distinct-key projections in one transaction.

---

## Impact

This bug prevents the following patterns:
- Publishing one message per event with custom payload (e.g. stripped of internal metadata)
- Publishing different event types to the same topic with individual control over payload shape
- Any use case requiring more than one distinct `true` projection per transaction

As a workaround, consumers must either accept the raw stored value (no projection) or use a single shared projection key — neither of which is always appropriate.

---

## Related Issues

- **outbox.projection:false does not suppress** (`issues/dapr-outbox-false-suppression.md`): The combination of fixing both bugs would enable a clean N-event outbox pattern: `[e1(false), e2(false), e1(true), e2(true)]` — each event stored but only the projection value published per event.
- **In-memory pub/sub deadlock** (`issues/dapr-inmemory-outbox-deadlock.md`): Even after fixing the panic, multi-key outbox transactions may deadlock with in-memory pub/sub.
