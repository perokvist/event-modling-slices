# dapr-lab

A research and exploration project for **event sourcing on top of Dapr**. The lab investigates two main themes:

1. **Dynamic Consistency Boundaries (DCB)** — a pattern for loosening rigid aggregate boundaries in event-sourced systems by expressing per-command consistency scopes.
2. **Dapr's outbox pattern** — transactional event publishing using Dapr's built-in outbox support, including its current limitations.

> **Status:** Active research / lab. Not intended for production use.

---

## Tech Stack

| | |
|---|---|
| Language | C# / .NET 10 |
| Dapr SDK | `Dapr.Client` + `Dapr.AspNetCore` v1.17.6 |
| Dapr runtime | daprd v1.17.3+ |
| Testing | xUnit 2.9.3 + Testcontainers 4.11.0 |
| State stores | In-memory, Redis, Azure Cosmos DB |
| Pub/sub | In-memory, Redis |

---

## Project Layout

```
dapr-lab/
├── src/
│   └── DaprEventStore/          # Core event store library
├── test/
│   └── DaprEventStore.IntegrationTests/  # Integration tests (xUnit + Testcontainers)
├── docs/
│   ├── style/                   # Code guidelines and patterns
│   │   ├── README.md            # Entry point for all guides
│   │   ├── records.md           # Command/Event/State records
│   │   ├── decider.md           # Decider pattern and logic
│   │   └── endpoints.md         # ASP.NET integration
│   └── research/
│       └── dcb-dynamic-consistency-boundaries.md
└── issues/                      # Documented Dapr runtime bugs
    ├── dapr-outbox-false-suppression.md
    ├── dapr-multiprojection-panic.md
    └── dapr-inmemory-outbox-deadlock.md
```

---

## Code Guidelines

For implementing event-sourced command slices, see **[docs/style/](docs/style/)**:

- **[records.md](docs/style/records.md)** — Record structures (State, Command, Event) and naming conventions
- **[decider.md](docs/style/decider.md)** — Decider pattern, decision logic, guard streams, and event routing
- **[endpoints.md](docs/style/endpoints.md)** — ASP.NET feature mapping and HTTP integration

Start with **[docs/style/README.md](docs/style/README.md)** for an overview.

---

## Event Store Features

### Stream-Based Persistence

Events are stored in named streams. Each stream has a `StreamHead` that tracks the current version. All writes are append-only.

```csharp
await eventStore.AppendToStreamAsync("order-42", expectedVersion: 0, events);
var stream = await eventStore.LoadEventStreamAsync("order-42");
```

**Optimistic concurrency** is enforced via Dapr ETags. The `expectedVersion` check happens atomically inside a Dapr state transaction.

---

### Dynamic Consistency Boundaries (DCB)

DCB replaces fixed aggregate boundaries with per-command consistency scopes. Instead of locking a single aggregate stream, a command declares which streams it needs to read to make a decision — and all observed versions become the concurrency guard for the write.

```csharp
// Read multiple streams and capture their versions
var (events, condition) = await eventStore.ReadStreamsForDecisionAsync(
    "seat-A1", "seat-A2", "booking-xyz");

// Append to target stream; all read streams are guarded
await eventStore.AppendToStreamAsync("booking-xyz", condition, newEvents);
```

Under the hood, guard streams are written as no-op entries in the same Dapr transaction using their captured ETags. If any guarded stream was modified concurrently, the transaction fails with a conflict.

**Partitioning requirement:** DCB requires all involved streams to reside in the same Dapr partition (`PartitionAllStream`) so they can participate in a single atomic transaction.

See [`docs/research/dcb-dynamic-consistency-boundaries.md`](docs/research/dcb-dynamic-consistency-boundaries.md) for a deep dive.

---

### Outbox Pattern

The outbox pattern ensures events are published to pub/sub **atomically with the state write** — no dual-write problem.

| Mode | Status | Description |
|------|--------|-------------|
| **AutoPublish** | ✅ Working | Each stored entry is auto-published individually by Dapr's built-in outbox |
| **EventsArray** | ⚠️ Blocked | Publish all events as a single array payload (blocked by [Dapr bug #1](#1-outboxprojection-false-does-not-suppress-publication)) |
| **PerEvent** | ⚠️ Blocked | Publish each event with a custom projection payload (blocked by [Dapr bug #2](#2-multiple-projections-with-different-keys-crash-daprd)) |

The outbox is configured via Dapr component metadata (`outboxPublishPubsub`, `outboxPublishTopic`) and controlled per transaction entry using `outbox.projection` metadata.

---

### Query API

Cross-stream event queries are supported via Dapr's state store Query API:

```csharp
var results = await eventStore.QueryEventsAsync(
    filter: e => e.StreamName.StartsWith("order-"),
    pageSize: 50);
```

**Requires a query-capable state store backend.** Compatible backends:

- Redis (with RedisSearch + RedisJSON modules)
- MongoDB
- PostgreSQL (via Dapr component)

Sorting is performed client-side (by `OccurredAt`) because not all backends support server-side ordering.

---

### Partition Strategies

| Strategy | Description | Use Case |
|----------|-------------|----------|
| `PartitionPerStream()` | Each stream gets its own Dapr partition key | Independent streams; best scalability |
| `PartitionAllStream()` | All streams share one partition key | Required for DCB multi-stream transactions |
| `PartitionCustom(key)` | Caller-supplied partition key | Advanced grouping scenarios |

---

## Running the Tests

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Dapr CLI](https://docs.dapr.io/getting-started/install-dapr-cli/) with `daprd` on PATH
- [Docker](https://www.docker.com/) (for Testcontainers — Redis and Cosmos DB emulator)

### Run all tests

```bash
dotnet test
```

### Test fixture overview

| Fixture | Infrastructure | Used by |
|---------|---------------|---------|
| `DaprSidecarFixture` | In-memory state + pub/sub | Most unit-style integration tests |
| `DaprSidecarRedisFixture` | Redis (Testcontainers) | Outbox tests requiring real pub/sub |
| `DaprSidecarCosmosFixture` | Cosmos DB emulator (Testcontainers) | Query API tests |

> **Note:** Tests using `EventStoreOutboxTests` are skipped by default due to the in-memory pub/sub deadlock issue described below. Use `EventStoreOutboxRedisTests` for outbox validation.

---

## Known Limitations

### Query API requires a compatible backend

The Dapr Query API is only available on Redis (RedisSearch/RedisJSON), MongoDB, and PostgreSQL backends. In-memory and most other state stores do not support it.

### DCB writes to a single target stream

DCB reads from multiple streams to build a consistency boundary, but the actual event write always targets **one** stream. True multi-stream atomic appends (writing events to multiple streams in one transaction) are not part of the DCB pattern as implemented here.

### ApplicationService is incomplete

`ApplicationService.cs` is a higher-level abstraction over the event store but has several unfinished areas:

- Stream naming is hardcoded to `{TypeName}-{Id}` and is not externally configurable.
- `GetStreamMetaData` has a known issue and is not fully functional.
- Multi-stream appends via `ApplicationService` are not yet transactional.

### In-memory pub/sub cannot be used for outbox integration tests

The in-memory pub/sub component is **synchronous** — subscriber callbacks are invoked inline during `Publish`, before the state transaction has committed. This causes multi-key outbox transactions to deadlock. Use Redis-backed pub/sub for any test that exercises the outbox.

---

## Dapr Issues

Three Dapr runtime bugs (all verified against v1.17.3) are documented in [`issues/`](issues/). They directly constrain what outbox modes are achievable today.

---

### 1. `outbox.projection: false` Does Not Suppress Publication

**File:** [`issues/dapr-outbox-false-suppression.md`](issues/dapr-outbox-false-suppression.md)  
**Severity:** Medium  
**Dapr version:** v1.17.3

#### What should happen

A state transaction entry tagged with `outbox.projection: false` should be stored but **not** published to pub/sub.

#### What actually happens

If no matching `outbox.projection: true` entry exists for the same key, the entry is published anyway using its raw stored value — ignoring the `false` flag.

#### Impact

Blocks the **EventsArray** outbox mode, which needs to store N individual event entries without publishing them, then publish one combined projection. Without reliable suppression, all raw entries are published individually instead.

#### Workaround

Either add a matching `projection: true` entry for every suppressed key (doubles write volume), or avoid the EventsArray pattern.

---

### 2. Multiple Projections with Different Keys Crash daprd

**File:** [`issues/dapr-multiprojection-panic.md`](issues/dapr-multiprojection-panic.md)  
**Severity:** High — sidecar crash  
**Dapr version:** v1.17.3

#### What should happen

A state transaction with 2+ entries that each carry `outbox.projection: true` (with different keys) should publish a custom payload for each entry.

#### What actually happens

The daprd process **panics** with:

```
panic: runtime error: slice bounds out of range [:2] with length 1
```

The sidecar crashes and must be restarted.

#### Root cause

An off-by-one error in the projection removal loop. When multiple projection entries are present and share no common key, the slice index goes out of bounds.

#### Impact

Blocks the **PerEvent** outbox mode, which requires one custom projection payload per event. Any transaction with 2+ projection entries with distinct keys will crash the sidecar.

#### Workaround

Use a single shared projection key (publishes one combined payload) or fall back to AutoPublish mode (no custom projection).

---

### 3. In-Memory Pub/Sub Deadlocks on Multi-Key Outbox Transactions

**File:** [`issues/dapr-inmemory-outbox-deadlock.md`](issues/dapr-inmemory-outbox-deadlock.md)  
**Severity:** High — hangs indefinitely  
**Dapr version:** v1.17.3

#### What should happen

After a multi-key state transaction completes, Dapr's outbox should publish events to the configured pub/sub component and subscribers should receive them.

#### What actually happens

The sidecar hangs. Multi-key transactions (minimum 2 keys: one `StreamHead` + one event) never complete when using the in-memory pub/sub component.

#### Root cause

The in-memory pub/sub `Publish` call is **synchronous**: it invokes the subscriber callback inline, before the state transaction has been committed. The subscriber tries to read an outbox marker key that does not yet exist (transaction is still open), hits a timeout, and blocks. The transaction never commits. Deadlock.

#### Impact

Makes it impossible to run outbox integration tests without external infrastructure. Single-event appends (which produce at least 2 state keys) are affected.

#### Workaround

Use a real pub/sub backend — Redis is the recommended choice and is wired up in `DaprSidecarRedisFixture`. See `EventStoreOutboxRedisTests` for working outbox tests.

---

## Further Reading

- [Dynamic Consistency Boundaries — research notes](docs/research/dcb-dynamic-consistency-boundaries.md)
- [Dapr state management docs](https://docs.dapr.io/developing-applications/building-blocks/state-management/)
- [Dapr outbox pattern docs](https://docs.dapr.io/developing-applications/building-blocks/state-management/howto-outbox/)
- [DCB reference implementation (TypeScript)](https://github.com/bwaidelich/dcb-event-store)
