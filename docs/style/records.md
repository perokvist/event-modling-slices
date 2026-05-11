# Command Slice: Record Structures

This guide covers the anatomy of Command, Event, and State records in event-sourced command slices. All examples follow the [Coding Standard](coding-standard.md) and use C# records for immutability.

---

## 1. State Records

The **State** record holds the current aggregate state. It evolves as events are applied.

### Key Points

- State is immutable; use `with` expressions to evolve it
- Include an `Id` field matching the aggregate identity
- Keep state minimal — include only what the Decider needs to make decisions
- Use nullable fields sparingly; prefer explicit fields with default values

### Example

```csharp
namespace SampleApp.Modules.Sample.StartGame;

public record GameState(
    Guid GameId = default,
    string Name = "",
    bool IsStarted = false) : State(GameId);
```

### Common Patterns

**Nested state objects:** Group related fields into sub-records for clarity:

```csharp
public record Order(Guid OrderId, Customer Customer, List<Item> Items, OrderStatus Status);
public record Customer(Guid CustomerId, string Name, Address Address);
public record Address(string Street, string City);
```

**Derived properties:** Avoid them in State — compute them in the Decider or View instead. State should be pure data.

---

## 2. Command Records

The **Command** record encapsulates user intent. Commands are immutable inputs to the Decider.

### Key Points

- Commands are POCOs with data properties; no behavior
- Always inherit from a base `Command` type scoped to your module
- Include validation attributes (`[Required]`, `[StringLength]`, etc.) for API binding
- Use meaningful, action-oriented names (not `UpdateOrder`, but `PlaceOrder` or `CancelOrder`)

### Example

```csharp
namespace SampleApp.Modules.Sample.StartGame;

public record StartGameCommand(
    Guid Id,
    [Required]
    [StringLength(100, MinimumLength = 1)]
    string Name) : GameCommand(Id);

public abstract record GameCommand(Guid Id) : Command(Id);
```

### Common Patterns

**Module-level base command:** Create a scoped base record to establish identity and domain context:

```csharp
public abstract record GameCommand(Guid Id) : Command(Id);
public abstract record OrderCommand(Guid Id) : Command(Id);
```

**Validation attributes:** Use `System.ComponentModel.DataAnnotations` for cross-concern validation:

```csharp
[Range(1, 100)]
int Quantity,

[EmailAddress]
string CustomerEmail
```

**Immutability:** All properties should be `init`-only (the default for records). Never use `{ set; }`.

---

## 3. Event Records

The **Event** record captures what actually changed. Events are immutable and represent fact.

### Key Points

- Events are named in **past tense** (`RoomBooked`, not `BookRoom`)
- Always inherit from a module-level base `Event` type
- Include the aggregate identity field (usually matching the command)
- Events should be as specific as possible — one event per distinct state change

### Example

```csharp
namespace SampleApp.Modules.Sample.StartGame;

public record GameStarted(Guid GameId, string Name) : GameEvent(GameId);

public abstract record GameEvent(Guid Id) : DomainEvent(Id);
```

### Common Patterns

**Rich event payloads:** Include all context needed to evolve state and materialize projections:

```csharp
public record RoomBooked(
    Guid BookingId,
    Guid GuestId,
    string RoomType,
    DateTime CheckIn,
    DateTime CheckOut,
    decimal NightlyRate,
    int NightsCount,
    decimal TotalAmount) : BookingEvent(BookingId);
```

**Rejection events:** Represent rejection as an event if needed by projections (e.g., `BookingRejected`):

```csharp
public record BookingRejected(
    Guid BookingId,
    string Reason) : BookingEvent(BookingId);
```

**Upcasting events:** If an event schema evolves, the deserialization layer can upcast old events. No special record structure needed — it's a infrastructure concern.

---

## 4. Record Hierarchy

All records converge at top-level base types. Here's the typical structure:

```
Command (from Modules base)
├── GameCommand
│   └── StartGameCommand
├── OrderCommand
│   └── PlaceOrderCommand
└── ...

Event (from Modules base)
├── GameEvent
│   └── GameStarted
├── OrderEvent
│   ├── OrderPlaced
│   ├── OrderShipped
│   └── OrderCancelled
└── ...

AggregateState (from Modules base)
├── GameState
├── OrderState
└── ...
```

---

## 5. Record Equality and Serialization

### Equality

Records have structural equality by default — two instances with the same property values are equal. This is correct for commands and events.

### Serialization

When storing events or commands as JSON (via Dapr):

- Property names follow `PascalCase` (JSON convention) unless you configure a serializer
- Ensure the serializer preserves property order for versioning
- Use a custom `JsonConverter` if you need to handle legacy field names (upcasting)

### Example

```csharp
// Event as JSON in state store
{
  "eventName": "GameStarted",
  "data": {
    "gameId": "550e8400-e29b-41d4-a716-446655440000",
    "name": "Chess Championship"
  }
}
```

---

## 6. Common Gotchas

❌ **Mutable State:** Don't use `{ get; set; }` properties — use records or init-only properties.

```csharp
// BAD
public class GameState
{
    public string Name { get; set; } // Mutable!
}

// GOOD
public record GameState(Guid GameId, string Name);
```

❌ **Over-populated State:** Don't include fields the Decider doesn't need. Move view-only fields to projections.

```csharp
// BAD — includes fields needed only by views
public record Order(Guid OrderId, List<Item> Items, CustomerCache FullCustomerProfile, ...);

// GOOD — minimal state for decisions
public record Order(Guid OrderId, List<Item> Items, Guid CustomerId, OrderStatus Status);
```

❌ **Event Naming:** Don't use imperative tense for events (e.g., `BookRoom` is a command; `RoomBooked` is an event).

```csharp
// BAD
public record BookRoom(...) : HotelEvent(...);

// GOOD
public record RoomBooked(...) : HotelEvent(...);
```

❌ **Identity Inconsistency:** Ensure the command identity flows through to events and state.

```csharp
// BAD — event uses different field name
public record PlaceOrderCommand(Guid Id, ...) : OrderCommand(Id);
public record OrderPlaced(Guid OrderNumber, ...) : OrderEvent(...); // Different field!

// GOOD — consistent identity
public record PlaceOrderCommand(Guid OrderId, ...) : OrderCommand(OrderId);
public record OrderPlaced(Guid OrderId, ...) : OrderEvent(OrderId);
```

---

## References

- [Coding Standard](coding-standard.md) — C# naming and formatting conventions
- [Decider Pattern](decider.md) — How records flow through decision logic
- [ASP.NET Endpoints](endpoints.md) — Mapping commands to HTTP requests
