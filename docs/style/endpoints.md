# ASP.NET Integration: Features and Endpoints

This guide covers mapping commands to HTTP endpoints and integrating with the application service layer. Each command slice typically has one or more **Features** that expose HTTP routes.

---

## 1. Feature Structure

A **Feature** is a static class that defines one or more endpoint routes for a command slice. It coordinates the flow: HTTP → Command → Decider → Persistence.

### Key Points

- One Feature per command slice (or per route grouping)
- Static class with static methods (no state)
- Use the `MapEndpoints(RouteGroupBuilder app)` pattern
- Leverage ASP.NET's minimal APIs and dependency injection

### Example Structure

```csharp
namespace SampleApp.Modules.Sample.StartGame;

public static class StartGameFeature
{
    public static void MapEndpoints(RouteGroupBuilder app)
        => app.MapPost("/",
            async (GameModule m, [FromBody] StartGameCommand cmd) =>
            {
                var id = Guid.NewGuid();
                return await m.Dispatch(
                    command: cmd with { Id = id },
                    onSuccess: () => Results.CreatedAtRoute(nameof(GetGameQuery), new { id }));
            });
}
```

### Endpoints Organization

```
Modules/
├── Sample/
│   ├── StartGame/
│   │   ├── StartGameCommand.cs
│   │   ├── GameStarted.cs
│   │   └── StartGameFeature.cs       ← HTTP endpoints
│   ├── GetGame/
│   │   ├── GetGameQuery.cs
│   │   ├── GameView.cs
│   │   └── GetGameFeature.cs         ← HTTP endpoints
│   └── GameModule.cs                 ← Coordinator
```

---

## 2. Mapping Endpoints

Endpoints are mapped in Program.cs using route groups:

```csharp
// Program.cs
var gameGroup = app.MapGroup("/api/games");

StartGameFeature.MapEndpoints(gameGroup);
GetGameFeature.MapEndpoints(gameGroup);
```

This groups all game-related endpoints under `/api/games` and passes the RouteGroupBuilder to each feature.

### Key Points

- Use `MapGroup()` to organize routes by domain/module
- Each feature registers its own routes; no manual route construction
- ASP.NET DI handles passing `GameModule` automatically
- Validation attributes on Command properties are enforced automatically

### Common Patterns

**Nested route groups:**

```csharp
var apiGroup = app.MapGroup("/api");
var gameGroup = apiGroup.MapGroup("/games");

StartGameFeature.MapEndpoints(gameGroup);
```

**Route prefixes:**

```csharp
public static void MapEndpoints(RouteGroupBuilder app)
    => app.MapPost("", ...)      // POST /api/games
         .MapGet("{id}", ...)     // GET /api/games/{id}
         .MapDelete("{id}", ...); // DELETE /api/games/{id}
```

---

## 3. Request Binding and Validation

Command records are bound from HTTP request bodies and automatically validated using data annotations.

### Key Points

- Use `[FromBody]` to bind the command from JSON request body
- Validation attributes (`[Required]`, `[StringLength]`, etc.) are enforced automatically by ASP.NET
- Custom validation can be added via the Validation helper
- Model binding errors return 400 Bad Request automatically

### Example: With Validation

```csharp
public record StartGameCommand(
    Guid Id,
    [Required]
    [StringLength(100, MinimumLength = 1)]
    string Name) : GameCommand(Id);

public static void MapEndpoints(RouteGroupBuilder app)
    => app.MapPost("/",
        async (GameModule m, [FromBody] StartGameCommand cmd) =>
        {
            // ASP.NET automatically validates cmd before this code runs
            var id = Guid.NewGuid();
            return await m.Dispatch(command: cmd with { Id = id }, ...);
        });
```

### Validation Flow

1. ASP.NET deserializes JSON → Command record
2. Data annotations are checked (`[Required]`, `[StringLength]`, etc.)
3. If validation fails → 400 Bad Request is returned automatically
4. If valid → Endpoint handler is invoked

### Custom Validation (Optional)

For complex validation that depends on state, use the Validation helper:

```csharp
using SampleApp.Modules;

public static void MapEndpoints(RouteGroupBuilder app)
    => app.MapPost("/",
        async (GameModule m, [FromBody] StartGameCommand cmd) =>
        {
            // Manual validation if needed
            var validationResult = cmd.Validate();
            if (!validationResult.IsSuccess)
                return Results.BadRequest(validationResult.Exception?.Message);
            
            return await m.Dispatch(command: cmd with { Id = Guid.NewGuid() }, ...);
        });
```

---

## 4. Dispatcher Pattern (ApplicationService Integration)

Commands are executed via the **Dispatcher**, which routes them to the Decider and persists events.

### Key Points

- The `GameModule` (or equivalent) is injected by ASP.NET DI
- `Dispatch()` method encapsulates the full command workflow
- The `onSuccess` callback handles success response generation
- Errors bubble up as exceptions; use middleware to handle them globally

### Example

```csharp
public static void MapEndpoints(RouteGroupBuilder app)
    => app.MapPost("/",
        async (GameModule m, [FromBody] StartGameCommand cmd) =>
        {
            var id = Guid.NewGuid();
            return await m.Dispatch(
                command: cmd with { Id = id },
                onSuccess: () => Results.CreatedAtRoute(
                    nameof(GetGameQuery), new { id }));
        });
```

### Flow

1. ASP.NET binds and validates the command
2. Module's `Dispatch()` method:
   - Invokes the Decider to generate events
   - Applies events to state
   - Persists events to the event store
3. `onSuccess` callback is invoked and returns HTTP response

---

## 5. Response Handling

Responses vary by command intent. Common patterns:

### CreatedAtRoute (201 Created)

Used when a new resource is created:

```csharp
return await m.Dispatch(
    command: cmd with { Id = id },
    onSuccess: () => Results.CreatedAtRoute(
        routeName: nameof(GetGameQuery),
        routeValues: new { id },
        value: new { id, name = cmd.Name }));
```

### Ok (200 OK)

Used for updates or queries:

```csharp
return await m.Dispatch(
    command: cmd,
    onSuccess: () => Results.Ok(new { message = "Game updated" }));
```

### NoContent (204 No Content)

Used for deletions or fire-and-forget:

```csharp
return await m.Dispatch(
    command: cmd,
    onSuccess: () => Results.NoContent());
```

### Accepted (202 Accepted)

Used for async operations:

```csharp
return await m.Dispatch(
    command: cmd,
    onSuccess: () => Results.Accepted(
        location: $"/api/games/{cmd.Id}/status",
        value: new { status = "processing" }));
```

---

## 6. Error Handling

Errors can occur at validation, decision, or persistence layers. Handle them appropriately:

### Validation Errors (400 Bad Request)

ASP.NET model binding handles validation attributes automatically:

```csharp
// If Name is missing or exceeds 100 chars, ASP.NET returns 400
[Required]
[StringLength(100)]
string Name
```

### Decision Rejections (Empty Event List)

When the Decider returns `[]` (rejection), the command is not persisted. Your endpoint should return a meaningful response:

```csharp
var events = decider.Decide(command, state);

if (events.Length == 0)
    return Results.BadRequest("Game is already started");

// Persist events...
```

### Persistence Errors (500 Internal Server Error)

Exception handling can be done globally via middleware:

```csharp
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        
        var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
        var exception = exceptionHandlerPathFeature?.Error;
        
        await context.Response.WriteAsJsonAsync(new { error = exception?.Message });
    });
});
```

### Concurrency Errors (409 Conflict)

Dynamic Consistency Boundaries (DCB) may fail if a guarded stream was modified concurrently:

```csharp
try
{
    await eventStore.AppendToStreamAsync(streamName, condition, events);
}
catch (ConcurrencyException ex)
{
    return Results.Conflict(new { error = "Resource was modified concurrently" });
}
```

---

## 7. Common Endpoint Patterns

### Pattern: Command with Generated ID

```csharp
public static void MapEndpoints(RouteGroupBuilder app)
    => app.MapPost("/",
        async (GameModule m, [FromBody] StartGameCommand cmd) =>
        {
            var id = Guid.NewGuid();
            return await m.Dispatch(
                command: cmd with { Id = id },
                onSuccess: () => Results.CreatedAtRoute(nameof(GetGameQuery), new { id }));
        });
```

### Pattern: Command with Client-Provided ID

```csharp
public static void MapEndpoints(RouteGroupBuilder app)
    => app.MapPost("/{id:guid}",
        async (GameModule m, [FromRoute] Guid id, [FromBody] RenameGameCommand cmd) =>
        {
            return await m.Dispatch(
                command: cmd with { Id = id },
                onSuccess: () => Results.Ok(new { id, name = cmd.Name }));
        });
```

### Pattern: Query (Read-Only)

Queries don't use the Decider; they fetch state or projections directly:

```csharp
public static void MapEndpoints(RouteGroupBuilder app)
    => app.MapGet("/{id:guid}",
        async (GameModule m, [FromRoute] Guid id) =>
        {
            var game = await m.GetGameViewAsync(id);
            return game != null ? Results.Ok(game) : Results.NotFound();
        });
```

### Pattern: Bulk Command with Validation

```csharp
public static void MapEndpoints(RouteGroupBuilder app)
    => app.MapPost("/bulk",
        async (GameModule m, [FromBody] List<StartGameCommand> commands) =>
        {
            var results = new List<Guid>();
            
            foreach (var cmd in commands)
            {
                var id = Guid.NewGuid();
                await m.Dispatch(
                    command: cmd with { Id = id },
                    onSuccess: () => { results.Add(id); return Results.Ok(); });
            }
            
            return Results.Ok(new { ids = results });
        });
```

---

## 8. Routing Conventions

### Route Naming

- **POST /resource** → Create
- **GET /resource/{id}** → Read single
- **GET /resource** → List (with filtering/pagination)
- **PUT /resource/{id}** → Full replace (or PATCH for partial)
- **DELETE /resource/{id}** → Delete

### Named Routes

Use `WithName()` for routes that other endpoints reference:

```csharp
app.MapGet("/{id:guid}", ...)
   .WithName(nameof(GetGameQuery));

// Then reference in CreatedAtRoute:
return Results.CreatedAtRoute(nameof(GetGameQuery), new { id });
```

---

## 9. Common Gotchas

❌ **Forgetting [FromBody]:** Without it, ASP.NET won't bind the request body.

```csharp
// BAD — ASP.NET doesn't know where to get the command
async (GameModule m, StartGameCommand cmd) => ...

// GOOD
async (GameModule m, [FromBody] StartGameCommand cmd) => ...
```

❌ **Mutable Response Objects:** Don't include mutable fields in responses.

```csharp
// BAD
return Results.Ok(new { command.Id, command.Name, list.Items }); // Items is mutable!

// GOOD
return Results.Ok(new { id = command.Id, name = command.Name, itemCount = list.Items.Count });
```

❌ **Not handling rejection:** If the Decider rejects a command, don't silently return 200.

```csharp
// BAD — returns 200 even if command was rejected
var events = decider.Decide(command, state);
return Results.Ok(); // What if events is empty?

// GOOD — check for rejection
var events = decider.Decide(command, state);
if (events.Length == 0)
    return Results.BadRequest("Game already started");
return Results.Ok();
```

❌ **Route conflicts:** Don't overlap routes without distinguishing them:

```csharp
// BAD — both map to POST /api/games
app.MapPost("/", CreateGame); // Ambiguous
app.MapPost("/", BulkCreate); // Ambiguous

// GOOD — distinguish by pattern
app.MapPost("/", CreateGame);        // Single
app.MapPost("/bulk", BulkCreate);    // Bulk
```

❌ **Leaking internal errors:** Don't expose Decider rejection reasons as-is; map to user-friendly messages:

```csharp
// BAD — exposes internal state
if (events.Length == 0)
    return Results.BadRequest("state.IsStarted was true");

// GOOD — user-friendly
if (events.Length == 0)
    return Results.BadRequest("Game has already started");
```

---

## 10. Integration with Program.cs

Full example of wiring up endpoints:

```csharp
// Program.cs
var builder = WebApplicationBuilder.CreateBuilder(args);

builder.Services.AddScoped<GameModule>();

var app = builder.Build();

var apiGroup = app.MapGroup("/api");
var gamesGroup = apiGroup.MapGroup("/games");

StartGameFeature.MapEndpoints(gamesGroup);
GetGameFeature.MapEndpoints(gamesGroup);

app.Run();
```

---

## References

- [Record Structures](records.md) — Command, Event, and State record patterns
- [Decider Pattern](decider.md) — Business logic for decision-making
- [ASP.NET Minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis) — Official Microsoft docs
- [Event Sourcing on Dapr (Research)](../research/event-modeling-ai-skills-for-slices.md) — Broader context
