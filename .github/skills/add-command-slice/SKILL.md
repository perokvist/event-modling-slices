---
name: add-command-slice
description: >-
  Scaffold a new command slice with Command record, Decider logic, ASP.NET
  endpoint, and Given-When-Then tests following the event sourcing Decider
  pattern. Use when asked to add a new command, feature, or slice.
license: MIT
compatibility: '.NET 10+. Requires dotnet CLI. Project follows dapr-lab module layout.'
metadata:
  version: "1.0"
---

# Add Command Slice

Scaffold a complete **Command Slice** following the event sourcing Decider pattern used in this codebase.

A Command Slice implements the pattern: **Trigger → Command → Event(s)** with a Given-When-Then specification.

> For background theory on command slices and how AI should assist, see
> [Event Modeling AI Skills for Slices](../../../docs/research/event-modeling-ai-skills-for-slices.md).

## Prerequisites

Before starting, gather these inputs from the user:

| Input | Example |
|---|---|
| **Module name** | `Order` |
| **Command name** | `CreateOrder` |
| **Command parameters** | `string CustomerName, string Product, int Quantity` |
| **Event name** | `OrderCreated` |
| **Event fields** | `Guid OrderId, string CustomerName, string Product, int Quantity` |
| **State record name** | `OrderState` |
| **State fields** | `Guid OrderId, string CustomerName, bool IsCreated` |
| **Guard condition** | `!state.IsCreated` (prevents duplicate creation) |
| **API route** | `/orders` |
| **HTTP method** | `POST` |

## Coding Conventions

Follow the project [coding standard](../../../docs/style/coding-standard.md):

- **Private fields:** camelCase, no underscore prefix (`streamHead`, not `_streamHead`)
- **Public members:** PascalCase
- **Local variables and parameters:** camelCase
- **Constants:** PascalCase
- **Indentation:** 4 spaces
- **Line length:** prefer ≤ 120 characters
- **Files:** one top-level type per file; filename matches the primary public type

## Step-by-Step Procedure

### Step 1 — Create the Module base types (if the module is new)

If the module does not already exist, create the module-level base types.

**File:** `src/{App}/Modules/{Module}/Model.cs`

```csharp
namespace {App}.Modules.{Module};

public record {Module}Event(Guid Id) : DomainEvent(Id);
public record {Module}Command(Guid Id) : Command(Id);
```

These base records extend the framework types from `Modules/Model.cs` and provide a module-scoped event/command hierarchy.

> See example: [ExampleCommand.cs](examples/ExampleCommand.cs) — shows the base types along with a concrete command.

### Step 2 — Create the Command record

**File:** `src/{App}/Modules/{Module}/{CommandName}/{CommandName}Command.cs`

```csharp
namespace {App}.Modules.{Module}.{CommandName};

public record {CommandName}Command(
    Guid Id, {parameters}) : {Module}Command(Id);
```

- The command inherits from `{Module}Command` which inherits from `Command`.
- `Id` is always `Guid` — it identifies the aggregate/stream.
- The `[property: JsonIgnore]` on `Id` is inherited from the base `Command` record.

> See example: [ExampleCommand.cs](examples/ExampleCommand.cs)

### Step 3 — Create the Event record

**File:** `src/{App}/Modules/{Module}/{CommandName}/{EventName}.cs`

```csharp
namespace {App}.Modules.{Module}.{CommandName};

public record {EventName}(Guid {Module}Id, {fields}) : {Module}Event({Module}Id);
```

- The event inherits from `{Module}Event` which inherits from `DomainEvent`.
- The first parameter is the aggregate ID (e.g., `OrderId`).

### Step 4 — Create the State record

**File:** `src/{App}/Modules/{Module}/{CommandName}/{StateName}.cs`

```csharp
namespace {App}.Modules.{Module}.{CommandName};

public record {StateName}(
    Guid {Module}Id = default,
    {state fields with defaults}) : State({Module}Id);
```

- The state inherits from `State`.
- All fields must have default values so `new {StateName}()` works as the initial state.

### Step 5 — Create or update the Decider

**File:** `src/{App}/Modules/{Module}/{Module}Decider.cs`

```csharp
using {App}.Modules.{Module}.{CommandName};

namespace {App}.Modules.{Module};

public record {Module}Decider() : Decider<Command, Event, {StateName}>
    (InitialState: new {StateName}(),
        Decide: (command, state) => command switch
        {
            {CommandName}Command c when {guard condition} => [new {EventName}(c.Id, {event args from c})],
            _ => []
        },
        Evolve: (state, @event) => @event switch
        {
            {EventName} e => state with { {state mutations from e} },
            _ => state
        },
        IsTerminal: state => false,
        SelectGuardStreams: (cmd, _) => [$"{Module}-{cmd.Id}"],
        RouteEvent: evt => evt switch
        {
            {Module}Event e => [($"{Module}-{e.Id}", e)],
            _ => []
        }
    );
```

Key points:
- `Decide` validates the guard condition and returns an array of events, or an empty array to reject.
- `Evolve` applies events to state using `with` expressions.
- `SelectGuardStreams` determines which streams to read for DCB (Dynamic Consistency Boundary).
- `RouteEvent` determines which stream each event is appended to.
- When adding a new command to an **existing** Decider, add a new pattern match arm to both `Decide` and `Evolve`.

> See example: [ExampleDecider.cs](examples/ExampleDecider.cs)

### Step 6 — Create the ASP.NET Endpoint

**File:** `src/{App}/Modules/{Module}/{CommandName}/{CommandName}Feature.cs`

```csharp
using Microsoft.AspNetCore.Mvc;

namespace {App}.Modules.{Module}.{CommandName};

public static class {CommandName}Feature
{
    public static void MapEndpoints(RouteGroupBuilder app)
     => app.MapPost("/{route}",
             async ({Module}Module m, [FromBody] {CommandName}Command cmd) =>
             {
                 var id = Guid.NewGuid();
                 return await m.Dispatch(
                     command: cmd with { Id = id },
                     onSuccess: () => Results.Created($"/{route}/{id}", null));
             });
}
```

- The module is injected via DI (`{Module}Module m`).
- `Dispatch` is an extension method from `ModuleExtensions` that calls the module's `Dispatch` and converts the `Result` to an HTTP result.
- Use `Results.CreatedAtRoute(...)` if a corresponding GET query exists.

> See example: [ExampleFeature.cs](examples/ExampleFeature.cs)

### Step 7 — Register the command in the Module

**File:** `src/{App}/Modules/{Module}/{Module}Module.cs`

Add the new command to the `Dispatch` method's switch expression:

```csharp
public Task<Result> Dispatch(Command command)
    => command switch
    {
        {CommandName}Command cmd => store.Execute(cmd, new {Module}Decider()).ToResultAsync(),
        _ => throw new NotImplementedException()
    };
```

If the module doesn't exist yet, create the full module class implementing `IModule`:

```csharp
using DaprEventStore;

namespace {App}.Modules.{Module};

public class {Module}Module(IEventStore store, Func<IntegrationEvent, Task> pub) : IModule
{
    public Task<Result> Dispatch(Command command)
        => command switch
        {
            {CommandName}Command cmd => store.Execute(cmd, new {Module}Decider()).ToResultAsync(),
            _ => throw new NotImplementedException()
        };

    public ValueTask<T?> Query<T>(Query<T> query) where T : ReadModel?
        => throw new NotImplementedException();

    public Task When(Event @event)
        => throw new NotImplementedException();
}
```

### Step 8 — Wire up the endpoint

Register the feature endpoint in the application's route mapping (e.g., in `Program.cs` or a module registration method):

```csharp
var group = app.MapGroup("/{route}").WithTags("{Module}");
{CommandName}Feature.MapEndpoints(group);
```

### Step 9 — Write the Given-When-Then state change test

**File:** `test/{App}.Tests/Modules/{Module}/{CommandName}/StateChangeTests.cs`

```csharp
using {App}.Modules.{Module};
using {App}.Modules.{Module}.{CommandName};

namespace {App}.Tests.Modules.{Module}.{CommandName};

public class StateChangeTests
{
    [Fact]
    public void {CommandName}_produces_expected_event()
    {
        // Given — build state from prior events
        var id = Guid.NewGuid();
        {Module}Event[] history = [];

        var decider = new {Module}Decider();
        var state = history.Aggregate(decider.InitialState, decider.Evolve);

        // When — execute the command
        var events = decider.Decide(
            new {CommandName}Command(id, {command args}), state);

        // Assert — verify the resulting events
        Assert.Collection(events, e =>
        {
            var ev = Assert.IsType<{EventName}>(e);
            Assert.Equal(id, ev.{Module}Id);
            // Assert additional fields...
        });
    }

    [Fact]
    public void {CommandName}_is_rejected_when_guard_fails()
    {
        // Given — set up state where guard condition fails
        var id = Guid.NewGuid();
        {Module}Event[] history = [new {EventName}(id, {event args})];

        var decider = new {Module}Decider();
        var state = history.Aggregate(decider.InitialState, decider.Evolve);

        // When
        var events = decider.Decide(
            new {CommandName}Command(id, {command args}), state);

        // Then — no events produced (command rejected)
        Assert.Empty(events);
    }
}
```

The Given-When-Then test is the **specification** of the command slice. It directly maps from the event model.

> See example: [ExampleTests.cs](examples/ExampleTests.cs)

### Step 10 — Write the API integration test (optional)

**File:** `test/{App}.Tests/Modules/{Module}/{CommandName}/{CommandName}ApiTests.cs`

```csharp
using {App}.Modules.{Module};
using {App}.Modules.{Module}.{CommandName};
using System.Net.Http.Json;

namespace {App}.Tests.Modules.{Module}.{CommandName};

public class {CommandName}ApiTests(Fixture fixture) : IClassFixture<Fixture>
{
    [Fact]
    public Task {CommandName}_via_api() =>
        fixture
        .Test(async client =>
        {
            var response = await client.PostAsJsonAsync(
                "/{route}",
                new {CommandName}Command(Guid.NewGuid(), {command args}));

            response.EnsureSuccessStatusCode();
        });
}
```

## Files Checklist

For a new command slice in a **new module**, create these files:

| # | File | Purpose |
|---|---|---|
| 1 | `Modules/{Module}/Model.cs` | Base `{Module}Event` and `{Module}Command` records |
| 2 | `Modules/{Module}/{Cmd}/{Cmd}Command.cs` | Command record |
| 3 | `Modules/{Module}/{Cmd}/{EventName}.cs` | Event record |
| 4 | `Modules/{Module}/{Cmd}/{StateName}.cs` | State record |
| 5 | `Modules/{Module}/{Module}Decider.cs` | Decider with Decide + Evolve |
| 6 | `Modules/{Module}/{Cmd}/{Cmd}Feature.cs` | ASP.NET endpoint |
| 7 | `Modules/{Module}/{Module}Module.cs` | Module class (IModule) |
| 8 | `Tests/Modules/{Module}/{Cmd}/StateChangeTests.cs` | Given-When-Then tests |
| 9 | `Tests/Modules/{Module}/{Cmd}/{Cmd}ApiTests.cs` | API integration tests |

For a new command in an **existing module**, you typically only need files 2–4, 6, 8–9, plus updating the existing Decider (5) and Module (7).

## Anti-Patterns

| ❌ Don't | ✅ Do Instead |
|---|---|
| Mutable state (`{ get; set; }`) | Immutable `record` with `with` expressions |
| Side effects inside `Decide` | Pure function — return events, no I/O |
| Imperative event name (`BookRoom`) | Past-tense event name (`RoomBooked`) |
| State fields without defaults | All state fields have defaults (`= default`, `= false`, `= ""`) |
| Module base types missing | Always create `{Module}Event` and `{Module}Command` in `Model.cs` |
| Overwriting state by replacing fields not in the current event | Only mutate fields relevant to the current event in each `Evolve` arm |

## See Also (Pattern Guides)

Deep-dive documentation for each part of the slice — source of truth for advanced patterns, gotchas, and variations:

- [`docs/style/records.md`](../../../docs/style/records.md) — Record naming, validation, hierarchy, inheritance gotchas
- [`docs/style/decider.md`](../../../docs/style/decider.md) — Decide/Evolve logic, guard streams, DCB, idempotency, testing
- [`docs/style/endpoints.md`](../../../docs/style/endpoints.md) — Endpoint mapping, request binding, error handling, response patterns

## Example Files

Self-contained annotated examples using a generic "Order" domain. Load a file when the user needs to understand or verify that specific pattern — do not read all files eagerly.

- [ExampleCommand.cs](examples/ExampleCommand.cs) — Load when generating Command, Event, or State records, or when the user asks about record structure
- [ExampleDecider.cs](examples/ExampleDecider.cs) — Load when generating a Decider, or when the user asks about Decide/Evolve/guard streams/routing
- [ExampleFeature.cs](examples/ExampleFeature.cs) — Load when generating an ASP.NET endpoint, or when the user asks about endpoint mapping
- [ExampleTests.cs](examples/ExampleTests.cs) — Load when generating Given-When-Then tests, or when the user asks about test structure
