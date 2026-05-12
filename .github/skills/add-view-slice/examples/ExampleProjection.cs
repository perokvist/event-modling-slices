// =============================================================================
// Example: View Slice projection for the "Order" module
// =============================================================================
// This file shows two things:
//   1. The Projection class that handles events and builds the read model
//   2. The updated OrderModule wiring (When + Query)
//
// In the real codebase each type lives in its own file.
// They are combined here for illustration.
// =============================================================================

// --- File: src/App/Modules/Order/GetOrder/OrderView.cs ---
// The read model returned by the query endpoint.

namespace App.Modules.Order.GetOrder;

public record OrderView(
    Guid OrderId,
    string CustomerName,
    string Status) : ReadModel();


// --- File: src/App/Modules/Order/GetOrder/GetOrderQuery.cs ---
// The query record. Passed to IModule.Query<T>().

namespace App.Modules.Order.GetOrder;

public record GetOrderQuery(Guid Id) : Query<OrderView>();


// --- File: src/App/Modules/Order/GetOrder/OrderViewProjection.cs ---
// The projection: pure event → state function stored in an in-memory dictionary.
//
// Rules:
//   - One Apply overload per event type handled by this view
//   - Each Apply is a pure state transition — no I/O, no side effects
//   - The projection is a singleton in DI so state persists across requests

namespace App.Modules.Order.GetOrder;

public class OrderViewProjection
{
    private readonly Dictionary<Guid, OrderView> store = [];

    // Apply is called by OrderModule.When for each incoming OrderCreated event.
    public void Apply(OrderCreated e)
        => store[e.OrderId] = new OrderView(
            OrderId: e.OrderId,
            CustomerName: e.CustomerName,
            Status: "Open");

    // Add further Apply overloads as more events affect this view, e.g.:
    // public void Apply(OrderCancelled e)
    //     => store[e.OrderId] = store[e.OrderId] with { Status = "Cancelled" };

    public OrderView? Get(Guid id) => store.GetValueOrDefault(id);
}


// --- File: src/App/Modules/Order/OrderModule.cs (updated) ---
// Shows how When and Query<T> are wired once the projection is in place.
// Compare with the command-slice version where both threw NotImplementedException.

namespace App.Modules.Order;

public class OrderModule(
    IEventStore store,
    Func<IntegrationEvent, Task> pub,
    OrderViewProjection projection) : IModule
{
    // --- Command side (unchanged from add-command-slice) ---
    public Task<Result> Dispatch(Command command)
        => command switch
        {
            CreateOrderCommand cmd => store.Execute(cmd, new OrderDecider()).ToResultAsync(),
            _ => throw new NotImplementedException()
        };

    // --- View side: event → projection ---
    // When is called by the infrastructure after a command appends events.
    // Dispatch to the appropriate projection Apply overload.
    public Task When(Event @event)
    {
        switch (@event)
        {
            case OrderCreated e: projection.Apply(e); break;
            // case OrderCancelled e: projection.Apply(e); break;
        }
        return Task.CompletedTask;
    }

    // --- View side: query → read model ---
    public ValueTask<T?> Query<T>(Query<T> query) where T : ReadModel?
        => query switch
        {
            GetOrderQuery q => new ValueTask<T?>((T?)(object?)projection.Get(q.Id)),
            _ => throw new NotImplementedException()
        };
}


// --- File: src/App/Modules/Order/GetOrder/OrderViewHandler.cs ---
// The GET endpoint that serves the projected read model.

namespace App.Modules.Order.GetOrder;

public static class OrderViewHandler
{
    public static void MapEndpoints(RouteGroupBuilder app)
        => app.MapGet("/{id}",
            async (Guid id, OrderModule m, LinkGenerator linkGenerator, HttpContext http) =>
                await m.Query(new GetOrderQuery(id)) switch
                {
                    null => Results.NotFound(),
                    var v => Results.Ok(new ViewResponse<OrderView>(
                        View: v,
                        Links:
                        [
                            new(
                                Href: linkGenerator.GetUriByName(http, nameof(GetOrderQuery), values: new { id })!,
                                Rel: "self",
                                Method: "GET")
                        ]))
                })
           .WithName(nameof(GetOrderQuery));
}
