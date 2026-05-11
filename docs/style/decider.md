# Decider Pattern

The **Decider** record encapsulates the business logic for a command slice. It contains the decision function, state evolution, and stream management strategy.

---

## 1. Anatomy of a Decider

A Decider has four core responsibilities:

1. **Decide** — Evaluate the command against current state and decide what events to emit
2. **Evolve** — Apply events to state to compute the new state
3. **SelectGuardStreams** (optional) — Declare which streams must be read before decision (for consistency)
4. **RouteEvent** (optional) — Declare which streams each event targets

### Key Points

- Decider is a pure function — no side effects, no I/O
- Decision logic is completely testable and unit-testable
- Evolve must be idempotent — replaying events yields the same result
- Guard streams ensure consistency across multiple related aggregates (Dynamic Consistency Boundaries)

### Example Structure

```csharp
namespace SampleApp.Modules.Sample;

public record GameDecider() : Decider<Command, Event, GameState>(
    InitialState: new GameState(),
    
    Decide: (command, state) => command switch
    {
        StartGameCommand c when !state.IsStarted => [new GameStarted(c.Id, c.Name)],
        _ => []
    },
    
    Evolve: (state, @event) => @event switch
    {
        GameStarted e => state with { GameId = e.GameId, Name = e.Name, IsStarted = true },
        _ => state
    },
    
    IsTerminal: state => false,
    
    SelectGuardStreams: (cmd, _) => [$"Game-{cmd.Id}"],
    
    RouteEvent: evt => evt switch
    {
        GameEvent e => [($"Game-{e.Id}", e)],
        _ => []
    }
);
```

---

## 2. The Decide Function

The **Decide** function takes a command and current state, and returns a list of events (or an empty list if the command is rejected).

### Key Points

- Use a `switch` expression for clarity and exhaustiveness checking
- Return events only if conditions are met; return `[]` to reject
- Never mutate state — only return new events
- Consider all rejection cases explicitly

### Example: Conditional Logic

```csharp
Decide: (command, state) => command switch
{
    StartGameCommand c when !state.IsStarted 
        => [new GameStarted(c.Id, c.Name)],
    
    StartGameCommand c when state.IsStarted 
        => [], // Rejection: already started
    
    _ => [] // Rejection: unknown command
}
```

### Common Patterns

**Validation in Decide:** Include domain logic that depends on state:

```csharp
BookRoomCommand cmd when !state.RoomAvailable(cmd.RoomType, cmd.CheckIn, cmd.CheckOut)
    => [], // Room not available
```

**Multiple events per command:** A single command can emit multiple events:

```csharp
PlaceOrderCommand cmd
    => [
        new OrderPlaced(cmd.OrderId, cmd.CustomerId, cmd.Items),
        new PaymentInitiated(cmd.OrderId, cmd.Amount)
    ],
```

**No events (idle state):** A terminal command might emit no events:

```csharp
public record Decider(...,
    IsTerminal: state => state.Status == OrderStatus.Completed,
    ...
);
```

---

## 3. The Evolve Function

The **Evolve** function applies an event to the state, returning the new state.

### Key Points

- Evolve must be **idempotent** — replaying events produces the same result
- Use `state with { ... }` for immutable updates
- Handle all event types your aggregate can emit
- The default case should return state unchanged (forward compatibility for unknown events)

### Example

```csharp
Evolve: (state, @event) => @event switch
{
    GameStarted e => state with 
    { 
        GameId = e.GameId, 
        Name = e.Name, 
        IsStarted = true 
    },
    
    _ => state // Forward compatibility: ignore unknown events
}
```

### Idempotency Guarantee

```csharp
// If you replay GameStarted twice, state must be the same
var state1 = decider.Evolve(initialState, gameStartedEvent);
var state2 = decider.Evolve(state1, gameStartedEvent);
// state1 == state2 ✓
```

### Common Patterns

**Complex updates:**

```csharp
RoomBooked e => state with 
{ 
    Bookings = state.Bookings.Concat([new Booking(e.GuestId, e.RoomType, e.CheckIn, e.CheckOut)]).ToList(),
    AvailableRooms = state.AvailableRooms - 1
}
```

**Derived fields:** Compute them on-demand in views, not in Evolve:

```csharp
// DON'T do this in Evolve:
TotalRevenue = state.Bookings.Sum(b => b.Amount), // Derived!

// GOOD: Compute in View/Projection
public decimal TotalRevenue => Bookings.Sum(b => b.Amount);
```

---

## 4. Guard Streams (SelectGuardStreams)

**SelectGuardStreams** declares which streams must be read before decision to ensure consistency across multiple aggregates (Dynamic Consistency Boundaries / DCB).

### Key Points

- Return a list of stream names the Decide function depends on
- All named streams are locked atomically during the write
- Required for multi-stream consistency; omit if single-stream operations only
- The default stream (the command target) is always guarded

### Example: Single-Stream Guard

```csharp
SelectGuardStreams: (cmd, _) => [$"Game-{cmd.Id}"]
```

### Example: Multi-Stream Guard

```csharp
SelectGuardStreams: (cmd, _) => cmd switch
{
    BookRoomCommand c => [
        $"Room-{c.RoomId}",      // Need current availability
        $"Guest-{c.GuestId}",    // Need guest details
        $"Booking-{c.BookingId}" // The target stream
    ],
    _ => [] // Other commands don't need guards
}
```

### Common Patterns

**Fallback stream selection:** The ApplicationService can provide a default if SelectGuardStreams returns null:

```csharp
var guards = decider.GetGuardStreams(command, state, defaultGuardStream: cmd => $"{cmd.GetType().Name}-{cmd.Id}");
```

**Empty guard streams:** If your command doesn't need consistency with other streams, return an empty list:

```csharp
SelectGuardStreams: (_, _) => [] // No multi-stream consistency needed
```

---

## 5. Event Routing (RouteEvent)

**RouteEvent** declares which streams each emitted event should be appended to. By default, all events go to a single "primary" stream; routing allows multi-stream appends.

### Key Points

- Returns tuples of `(streamName, event)`
- Each event can target multiple streams
- Required only if events should be appended to multiple streams
- Omit or return empty if all events target the primary stream

### Example: Single-Stream Routing (Common Case)

```csharp
RouteEvent: evt => evt switch
{
    GameEvent e => [($"Game-{e.Id}", e)],
    _ => []
}
```

### Example: Multi-Stream Routing

```csharp
RouteEvent: evt => evt switch
{
    // Publish to both order stream and customer stream
    OrderPlaced e => [
        ($"Order-{e.OrderId}", e),
        ($"Customer-{e.CustomerId}", e)
    ],
    
    PaymentInitiated e => [
        ($"Payment-{e.PaymentId}", e),
        ($"Order-{e.OrderId}", e)
    ],
    
    _ => []
}
```

### Common Patterns

**Single event, multiple targets:**

```csharp
public record OrderShipped(Guid OrderId, Guid CustomerId, List<string> TrackingNumbers);

RouteEvent: evt => evt switch
{
    OrderShipped e => [
        ($"Order-{e.OrderId}", e),
        ($"Customer-{e.CustomerId}", e),
        ($"Fulfillment-log", e) // Audit trail
    ],
    _ => []
}
```

---

## 6. Common Decider Patterns

### Pattern: State-Dependent Decisions

Decisions that depend on aggregate state:

```csharp
Decide: (command, state) => command switch
{
    ApproveOrderCommand c when state.TotalAmount < 1000 && state.Status == OrderStatus.Pending
        => [new OrderApproved(c.OrderId)],
    
    ApproveOrderCommand c when state.TotalAmount >= 1000
        => [new OrderApprovalRequired(c.OrderId, "Requires manual review")],
    
    _ => []
}
```

### Pattern: State Machine

Model a command as a state machine transition:

```csharp
Decide: (command, state) => command switch
{
    TransitionCommand c => (state.Status, c.Action) switch
    {
        (Status.Draft, Action.Submit) => [new Submitted(c.Id)],
        (Status.Submitted, Action.Approve) => [new Approved(c.Id)],
        (Status.Approved, Action.Execute) => [new Executed(c.Id)],
        (_, _) => [] // Invalid transition
    },
    _ => []
}
```

### Pattern: Multiple Events with Compensation

Emit related events for consistency:

```csharp
PlaceOrderCommand cmd
    => [
        new OrderPlaced(cmd.OrderId, cmd.CustomerId, cmd.Items),
        new InventoryReserved(cmd.OrderId, cmd.Items),
        new PaymentCharged(cmd.OrderId, cmd.Amount)
    ]
```

---

## 7. Testing a Decider

Deciders are pure — test them with unit tests:

```csharp
[Fact]
public void Decide_StartsGameWhenNotStarted()
{
    var decider = new GameDecider();
    var state = new GameState(GameId: Guid.Empty, Name: "", IsStarted: false);
    var command = new StartGameCommand(Id: Guid.NewGuid(), Name: "Chess");
    
    var events = decider.Decide(command, state);
    
    Assert.Single(events);
    Assert.IsType<GameStarted>(events[0]);
}

[Fact]
public void Decide_RejectsStartGameWhenAlreadyStarted()
{
    var decider = new GameDecider();
    var state = new GameState(GameId: Guid.NewGuid(), Name: "Chess", IsStarted: true);
    var command = new StartGameCommand(Id: state.GameId, Name: "Chess");
    
    var events = decider.Decide(command, state);
    
    Assert.Empty(events); // Rejection
}

[Fact]
public void Evolve_UpdatesStateCorrectly()
{
    var decider = new GameDecider();
    var state = new GameState();
    var @event = new GameStarted(GameId: Guid.NewGuid(), Name: "Chess");
    
    var newState = decider.Evolve(state, @event);
    
    Assert.Equal(@event.GameId, newState.GameId);
    Assert.Equal(@event.Name, newState.Name);
    Assert.True(newState.IsStarted);
}
```

---

## 8. Common Gotchas

❌ **Side effects in Decide:** Don't call async functions, log, or modify state.

```csharp
// BAD
Decide: (command, state) => {
    await eventStore.LoadAsync(...); // Side effect!
    Console.WriteLine(...);            // Side effect!
    state.SomeField = "new";           // Mutation!
    return [...];
}

// GOOD
Decide: (command, state) => command switch { ... } // Pure function
```

❌ **Non-idempotent Evolve:** Ensure replaying doesn't change the result.

```csharp
// BAD — not idempotent if replayed
Evolve: (state, @event) => state with 
{ 
    Count = state.Count + 1 // Incrementing — will increase on each replay!
}

// GOOD — idempotent
Evolve: (state, @event) => @event switch
{
    CountIncremented e => state with { Count = e.NewCount },
    _ => state
}
```

❌ **Incomplete event handling:** Don't forget to handle all event types:

```csharp
// BAD — silently ignores unknown events (forward-incompatibility risk)
Evolve: (state, @event) => state,

// GOOD — explicit default case
Evolve: (state, @event) => @event switch
{
    GameStarted e => ...,
    _ => state // Forward-compatible: ignore unknown
}
```

❌ **Overly complex Decide:** If your switch is too large, break it into helpers:

```csharp
// BAD — massive switch
Decide: (cmd, state) => cmd switch { ... /* 50 branches */ }

// GOOD — helper functions
private static Event[] DecideBooking(BookingCommand cmd, HotelState state) => ...;

Decide: (cmd, state) => cmd switch
{
    BookingCommand c => DecideBooking(c, state),
    PaymentCommand c => DecidePayment(c, state),
    _ => []
}
```

---

## References

- [Record Structures](records.md) — Command, Event, and State record patterns
- [ASP.NET Endpoints](endpoints.md) — How to invoke Deciders from endpoints
- [DCB Pattern (Research)](../research/dcb-dynamic-consistency-boundaries.md) — Deep dive into guard streams and consistency
