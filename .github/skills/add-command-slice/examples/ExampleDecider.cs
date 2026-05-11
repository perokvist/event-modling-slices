// =============================================================================
// Example: Decider for the "Order" module
// =============================================================================
// File: src/App/Modules/Order/OrderDecider.cs
//
// The Decider is a pure function that encapsulates all command-handling logic:
//   - Decide: (command, state) → events[]    — validates and produces events
//   - Evolve: (state, event) → state         — applies an event to state
//   - IsTerminal: state → bool               — whether the aggregate is closed
//   - SelectGuardStreams: streams to read for the Dynamic Consistency Boundary
//   - RouteEvent: which stream each event is appended to
//
// Pattern: Decider<TCommand, TEvent, TState>
// See: src/SampleApp/Modules/Decider.cs for the interface definition
// =============================================================================

using App.Modules.Order.CreateOrder;

namespace App.Modules.Order;

public record OrderDecider() : Decider<Command, Event, OrderState>
    (InitialState: new OrderState(),

        // Decide validates preconditions and returns events.
        // Return an empty array to reject the command silently.
        Decide: (command, state) => command switch
        {
            CreateOrderCommand c when !state.IsCreated =>
                [new OrderCreated(c.Id, c.CustomerName, c.Product, c.Quantity)],

            // Add additional command arms here as the module grows, e.g.:
            // CancelOrderCommand c when state.IsCreated && !state.IsCancelled =>
            //     [new OrderCancelled(c.Id, c.Reason)],

            _ => []
        },

        // Evolve applies each event to produce a new state snapshot.
        Evolve: (state, @event) => @event switch
        {
            OrderCreated e => state with
            {
                OrderId = e.OrderId,
                CustomerName = e.CustomerName,
                IsCreated = true
            },

            // OrderCancelled e => state with { IsCancelled = true },

            _ => state
        },

        // IsTerminal: return true if no further commands should be accepted.
        IsTerminal: state => false,

        // SelectGuardStreams: which event streams to read before deciding.
        // Typically one stream per aggregate instance.
        SelectGuardStreams: (cmd, _) => [$"Order-{cmd.Id}"],

        // RouteEvent: which stream(s) each event is appended to.
        RouteEvent: evt => evt switch
        {
            OrderEvent e => [($"Order-{e.Id}", e)],
            _ => []
        }
    );
