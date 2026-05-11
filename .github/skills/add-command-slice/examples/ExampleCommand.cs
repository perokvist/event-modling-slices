// =============================================================================
// Example: Command Slice types for an "Order" module
// =============================================================================
// This file shows the four record types needed for a command slice:
//   1. Module-level base types (in Modules/{Module}/Model.cs)
//   2. Command record (in Modules/{Module}/{CommandName}/{CommandName}Command.cs)
//   3. Event record  (in Modules/{Module}/{CommandName}/{EventName}.cs)
//   4. State record  (in Modules/{Module}/{CommandName}/{StateName}.cs)
//
// In the real codebase each record lives in its own file.
// They are combined here for illustration.
// =============================================================================

// --- File: src/App/Modules/Order/Model.cs ---
// Base records that all commands and events in the Order module inherit from.
// These extend the framework types defined in Modules/Model.cs.

namespace App.Modules.Order;

public record OrderEvent(Guid Id) : DomainEvent(Id);
public record OrderCommand(Guid Id) : Command(Id);


// --- File: src/App/Modules/Order/CreateOrder/CreateOrderCommand.cs ---
// The command captures the user's intent. Id identifies the aggregate stream.
// [property: JsonIgnore] on Id is inherited from the base Command record.

namespace App.Modules.Order.CreateOrder;

public record CreateOrderCommand(
    Guid Id, string CustomerName, string Product, int Quantity) : OrderCommand(Id);


// --- File: src/App/Modules/Order/CreateOrder/OrderCreated.cs ---
// The event records what actually happened. First parameter is the aggregate ID.

namespace App.Modules.Order.CreateOrder;

public record OrderCreated(
    Guid OrderId, string CustomerName, string Product, int Quantity) : OrderEvent(OrderId);


// --- File: src/App/Modules/Order/CreateOrder/OrderState.cs ---
// State is rebuilt by folding events via the Decider's Evolve function.
// All fields must have defaults so `new OrderState()` works as InitialState.

namespace App.Modules.Order.CreateOrder;

public record OrderState(
    Guid OrderId = default,
    string CustomerName = "",
    bool IsCreated = false) : State(OrderId);
