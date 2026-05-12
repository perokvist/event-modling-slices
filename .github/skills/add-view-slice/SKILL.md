---
name: add-view-slice
description: >-
  Scaffold a View Slice: update the module's When(Event) handler, create a
  projection that builds a read model from events, implement the Query handler,
  and write Given-Then tests. Use when asked to add a read model, projection,
  view, or query for an existing event.
license: MIT
compatibility: '.NET 10+. Requires dotnet CLI. Project follows dapr-lab module layout.'
metadata:
  version: "1.0"
---

# Add View Slice

Scaffold a complete **View Slice** following the event sourcing pattern used in this codebase.

A View Slice implements the pattern: **Event(s) → View** with a Given-Then specification.

> For background theory on view slices and how AI should assist, see
> [Event Modeling AI Skills for Slices](../../../docs/research/event-modeling-ai-skills-for-slices.md).

## Scope Boundary

This skill implements **only** the View Slice: `Event(s) → View`.

| Slice type | Pattern | Skill |
|---|---|---|
| Command (State Change) | `Trigger → Command → Event(s)` | `add-command-slice` |
| **View (this skill)** | `Event(s) → View` | `add-view-slice` |
| Automation | `Event(s) → View → Trigger → Command → Event(s)` | *(future)* |

A View Slice is always paired with one or more Command Slices — it consumes events
produced by commands and materialises them into a queryable read model. It **cannot
reject** events; it only transforms them into state.

## Prerequisites

Before starting, gather these inputs from the user:

| Input | Example |
|---|---|
| **Module name** | `Order` |
| **Event(s) to react to** | `OrderCreated`, `OrderCancelled` |
| **View (read model) name** | `OrderView` |
| **View fields** | `Guid OrderId, string CustomerName, string Status` |
| **Query record name** | `GetOrderQuery` |
| **Query parameter** | `Guid Id` |
| **API route** | `GET /orders/{id}` |
| **State transitions** | `OrderCreated → Status = "Open"`, `OrderCancelled → Status = "Cancelled"` |

## Coding Conventions

Follow the project [coding standard](../../../docs/style/coding-standard.md):

- **Private fields:** camelCase, no underscore prefix
- **Public members:** PascalCase
- **Local variables and parameters:** camelCase
- **Indentation:** 4 spaces
- **Line length:** prefer ≤ 120 characters
- **Files:** one top-level type per file; filename matches the primary public type

## Step-by-Step Procedure

### Step 1 — Define the Read Model (View record)

The read model is a `ReadModel`-derived record that represents the projection state.

**File:** `src/{App}/Modules/{Module}/Get{View}/{View}.cs`

```csharp
namespace {App}.Modules.{Module}.Get{View};

public record {View}(
    Guid {Module}Id,
    {view fields with no defaults — this is a fully-materialised snapshot}) : ReadModel();
```

Key points:
- Inherits from `ReadModel` (framework base type from `Modules/Model.cs`)
- Fields do **not** need defaults — the projection always builds a complete snapshot
- Name matches what is returned by the query endpoint

### Step 2 — Define the Query record

**File:** `src/{App}/Modules/{Module}/Get{View}/{View}Query.cs`

```csharp
namespace {App}.Modules.{Module}.Get{View};

public record {View}Query(Guid Id) : Query<{View}>();
```

- Inherits from `Query<TView>` (generic base type from `Modules/Model.cs`)
- `Id` is the aggregate identifier used to look up the projected state
- The query is passed to `IModule.Query<T>()` via the module

### Step 3 — Create the Projection handler

The projection is a pure function (or a class with injected storage) that maps
events to a read model. In this codebase, projections are implemented in the module
using an in-memory or persisted store injected via the module constructor.

**File:** `src/{App}/Modules/{Module}/Get{View}/{View}Projection.cs`

```csharp
namespace {App}.Modules.{Module}.Get{View};

public class {View}Projection
{
    private readonly Dictionary<Guid, {View}> store = [];

    public void Apply({EventName1} e)
        => store[e.{Module}Id] = new {View}(
            {Module}Id: e.{Module}Id,
            {map event fields to view fields});

    // Add an Apply overload for each event type this projection handles:
    // public void Apply({EventName2} e) => store[e.{Module}Id] = store[e.{Module}Id] with { ... };

    public {View}? Get(Guid id)
        => store.GetValueOrDefault(id);
}
```

Key points:
- One `Apply` overload per event type; each is a pure state transition
- The projection is stateless between events — all state is in `store`
- `Get(Guid id)` returns `null` if the aggregate has never been seen

### Step 4 — Wire up `IModule.When`

Update the module's `When` method to dispatch incoming events to the projection:

**File:** `src/{App}/Modules/{Module}/{Module}Module.cs`

```csharp
public Task When(Event @event)
{
    switch (@event)
    {
        case {EventName1} e: projection.Apply(e); break;
        // case {EventName2} e: projection.Apply(e); break;
    }
    return Task.CompletedTask;
}
```

Inject the projection via constructor:

```csharp
public class {Module}Module(IEventStore store, Func<IntegrationEvent, Task> pub, {View}Projection projection) : IModule
```

Register the projection as a singleton (or scoped) in DI:

```csharp
builder.Services.AddSingleton<{View}Projection>();
```

### Step 5 — Implement `IModule.Query<T>`

Update the module's `Query` method to serve the projected view:

**File:** `src/{App}/Modules/{Module}/{Module}Module.cs`

```csharp
public ValueTask<T?> Query<T>(Query<T> query) where T : ReadModel?
    => query switch
    {
        {View}Query q => new ValueTask<T?>((T?)(object?)projection.Get(q.Id)),
        _ => throw new NotImplementedException()
    };
```

### Step 6 — Create the GET endpoint

**File:** `src/{App}/Modules/{Module}/Get{View}/{View}Handler.cs`

```csharp
using Microsoft.AspNetCore.Mvc;

namespace {App}.Modules.{Module}.Get{View};

public static class {View}Handler
{
    public static void MapEndpoints(RouteGroupBuilder app)
        => app.MapGet("/{id}",
            async (Guid id, {Module}Module m, LinkGenerator linkGenerator, HttpContext http) =>
                await m.Query(new {View}Query(id)) switch
                {
                    null => Results.NotFound(),
                    var v => Results.Ok(new ViewResponse<{View}>(
                        View: v,
                        Links:
                        [
                            new(
                                Href: linkGenerator.GetUriByName(http, nameof({View}Query), values: new { id })!,
                                Rel: "self",
                                Method: "GET")
                        ]))
                })
           .WithName(nameof({View}Query));
}
```

### Step 7 — Write the Given-Then projection test

The projection test is the **specification** of the view slice.
It directly maps to a row in the event model: seed events, then assert the view.

**File:** `test/{App}.Tests/Modules/{Module}/Get{View}/{View}ProjectionTests.cs`

```csharp
using {App}.Modules.{Module};
using {App}.Modules.{Module}.Get{View};

namespace {App}.Tests.Modules.{Module}.Get{View};

public class {View}ProjectionTests
{
    [Fact]
    public void {EventName1}_produces_{View}()
    {
        // Given — events that establish the read model
        var id = Guid.NewGuid();
        var projection = new {View}Projection();

        // When — apply the event(s)
        projection.Apply(new {EventName1}(id, {event args}));

        // Then — view reflects the event
        var view = projection.Get(id);
        Assert.NotNull(view);
        Assert.Equal(id, view.{Module}Id);
        // Assert additional fields...
    }

    [Fact]
    public void Returns_null_for_unknown_id()
    {
        var projection = new {View}Projection();
        Assert.Null(projection.Get(Guid.NewGuid()));
    }
}
```

### Step 8 — Write the API query test (optional)

**File:** `test/{App}.Tests/Modules/{Module}/Get{View}/{View}ApiTests.cs`

```csharp
using System.Net.Http.Json;
using {App}.Modules.{Module};
using {App}.Modules.{Module}.Get{View};

namespace {App}.Tests.Modules.{Module}.Get{View};

public class {View}ApiTests(Fixture fixture) : IClassFixture<Fixture>
{
    [Fact]
    public Task Get_{View}_returns_view_after_event() =>
        fixture
        .WithHistory(new {Module}Decider(), new {EventName1}(Guid.Parse("..."), {event args}))
        .Test(async client =>
        {
            var id = Guid.Parse("...");
            var response = await client.GetAsync($"/{route}/{id}");
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<ViewResponse<{View}>>();
            Assert.NotNull(body?.View);
        });
}
```

## Files Checklist

For a new view slice in an **existing module**, create and update these files:

| # | Action | File | Purpose |
|---|---|---|---|
| 1 | Create | `Modules/{Module}/Get{View}/{View}.cs` | Read model record |
| 2 | Create | `Modules/{Module}/Get{View}/{View}Query.cs` | Query record |
| 3 | Create | `Modules/{Module}/Get{View}/{View}Projection.cs` | Projection (event → state) |
| 4 | Update | `Modules/{Module}/{Module}Module.cs` | Wire `When` and `Query<T>` |
| 5 | Create | `Modules/{Module}/Get{View}/{View}Handler.cs` | GET endpoint |
| 6 | Update | `Program.cs` or DI registration | Register projection in DI |
| 7 | Create | `Tests/Modules/{Module}/Get{View}/{View}ProjectionTests.cs` | Given-Then unit tests |
| 8 | Create | `Tests/Modules/{Module}/Get{View}/{View}ApiTests.cs` | API query tests |

## Anti-Patterns

| ❌ Don't | ✅ Do Instead |
|---|---|
| Implement `When` in the command slice | Leave `When` as `NotImplementedException`; add it via a view slice |
| Share mutable projection state across requests | Inject projection as a singleton; use `Dictionary<Guid, View>` |
| Reject events in `When` | Views are passive — `When` never throws for handled events |
| Emit commands from `When` | That is an Automation slice — keep view slices side-effect free |
| Return stale data from `Query` without `When` being wired | Always wire `When` before implementing `Query` |
| Hard-code IDs in projection tests | Use `Guid.NewGuid()` per test; only use fixed IDs in API tests that seed history |

## See Also (Pattern Guides)

- [`docs/style/records.md`](../../../docs/style/records.md) — Record naming and hierarchy
- [`docs/style/endpoints.md`](../../../docs/style/endpoints.md) — Endpoint mapping and response patterns
- [`docs/research/event-modeling-ai-skills-for-slices.md`](../../../docs/research/event-modeling-ai-skills-for-slices.md) — Background on all three slice types

## Example Files

Self-contained annotated examples using a generic "Order" domain.

- [ExampleProjection.cs](examples/ExampleProjection.cs) — Load when generating a projection class or `When` dispatch
- [ExampleViewTests.cs](examples/ExampleViewTests.cs) — Load when generating Given-Then projection tests or API query tests
