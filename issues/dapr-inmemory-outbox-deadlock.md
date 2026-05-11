# Bug: Outbox transactions with multiple distinct keys deadlock with in-memory pub/sub

**Component**: `dapr/dapr` (runtime) / `dapr/components-contrib` (pubsub.in-memory)  
**Version**: v1.17.3  
**Severity**: High (blocks integration testing without external infrastructure)

---

## Description

When a state store is configured with `outboxPublishPubsub` + `outboxPublishTopic` and the configured pub/sub component is `pubsub.in-memory`, any `ExecuteStateTransaction` call containing **two or more entries with distinct keys** causes daprd to deadlock. The calling application hangs indefinitely and no messages are published to the external topic.

Transactions with a single distinct key (or same-key pairs used for projections) work correctly.

---

## Root Cause

The Dapr outbox implementation (`pkg/runtime/pubsub/outbox.go`, `PublishInternal`) operates as follows:

1. For each non-projection `SetRequest` entry in the transaction, it calls `o.publisher.Publish(ctx, internalTopic, ...)` to write to the **internal** outbox topic.
2. A background subscriber of the internal outbox topic waits for the transaction marker (`outbox-{uuid}`) to appear in the state store, then publishes to the external user topic.
3. Only after `PublishInternal` returns does daprd commit the state transaction (writing the markers and data).

With `pubsub.in-memory`, `Publish` is **synchronous** — it invokes the subscriber callback inline before returning. This creates a chicken-and-egg deadlock:

- `Publish` (entry 1) → subscriber wakes → tries to read `outbox-{uuid1}` from state → **not there yet** (transaction not committed) → retries for up to 10 s → times out → returns
- `Publish` (entry 2) → same sequence
- After all `Publish` calls return, state finally commits — but all subscriber callbacks have already timed out and given up

For **same-key pairs** `[K(regular), K(outbox.projection:true)]`, only one `Publish` call is made for the pair. Due to in-memory pub/sub timing, the marker is read successfully. (The exact mechanism here may involve timing differences or async goroutine scheduling.)

---

## Reproduction

```yaml
# statestore-outbox.yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: statestore-outbox
spec:
  type: state.in-memory
  version: v1
  metadata:
    - name: outboxPublishPubsub
      value: pubsub
    - name: outboxPublishTopic
      value: events
```

```csharp
// This hangs — two distinct keys in one outbox transaction
await client.ExecuteStateTransactionAsync("statestore-outbox",
[
    new("key1", Encoding.UTF8.GetBytes("{\"a\":1}"), StateOperationType.Upsert),
    new("key2", Encoding.UTF8.GetBytes("{\"b\":2}"), StateOperationType.Upsert),
]);
// No message is ever delivered to the "events" topic
```

```csharp
// This works — single distinct key
await client.ExecuteStateTransactionAsync("statestore-outbox",
[
    new("key1", Encoding.UTF8.GetBytes("{\"a\":1}"), StateOperationType.Upsert),
]);
// Message delivered correctly
```

---

## Expected Behaviour

Multi-key outbox transactions should work with in-memory pub/sub the same way they work with Redis Streams or RabbitMQ.

---

## Impact

- Prevents integration testing of outbox-based event sourcing patterns without requiring external infrastructure (Redis, RabbitMQ)
- Forces use of the split-transaction workaround (two separate `ExecuteStateTransactionAsync` calls) to avoid the deadlock

---

## Suggested Fix

Options:
1. Make `pubsub.in-memory`'s `Publish` always deliver asynchronously (via goroutine), decoupling the subscriber callback from the `Publish` call duration
2. Change the outbox implementation to commit the state transaction (including markers) **before** calling `PublishInternal`, so subscriber callbacks can read the markers immediately
3. Document the limitation clearly in the in-memory pub/sub component docs

Option 2 may have broader correctness implications (at-least-once vs exactly-once). Option 1 (async delivery) is the least invasive fix.
