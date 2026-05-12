// =============================================================================
// Example: Given-Then tests for the OrderView view slice
// =============================================================================
// File: test/App.Tests/Modules/Order/GetOrder/OrderViewProjectionTests.cs
//
// View slice tests use Given-Then (no "When" step because the projection is
// passive — it cannot reject events, it only transforms them).
//
//   Given: <events applied to the projection>
//   Then:  <expected read model state>
//
// The projection is a plain class — no I/O, no mocking needed.
// Instantiate it, call Apply for each given event, then assert Get().
// =============================================================================

using App.Modules.Order;
using App.Modules.Order.CreateOrder;
using App.Modules.Order.GetOrder;

namespace App.Tests.Modules.Order.GetOrder;

public class OrderViewProjectionTests
{
    [Fact]
    public void OrderCreated_produces_OrderView_with_Open_status()
    {
        // Given — an OrderCreated event
        var id = Guid.NewGuid();
        var projection = new OrderViewProjection();

        // When — apply the event to the projection
        projection.Apply(new OrderCreated(id, "Alice", "Widget", 5));

        // Then — view reflects the event
        var view = projection.Get(id);
        Assert.NotNull(view);
        Assert.Equal(id, view.OrderId);
        Assert.Equal("Alice", view.CustomerName);
        Assert.Equal("Open", view.Status);
    }

    [Fact]
    public void Returns_null_for_unknown_id()
    {
        // Given — empty projection (no events applied)
        var projection = new OrderViewProjection();

        // Then — unknown id returns null
        Assert.Null(projection.Get(Guid.NewGuid()));
    }

    // Uncomment when OrderCancelled is added:
    // [Fact]
    // public void OrderCancelled_updates_status_to_Cancelled()
    // {
    //     var id = Guid.NewGuid();
    //     var projection = new OrderViewProjection();
    //     projection.Apply(new OrderCreated(id, "Alice", "Widget", 5));
    //
    //     projection.Apply(new OrderCancelled(id, "Changed mind"));
    //
    //     var view = projection.Get(id);
    //     Assert.NotNull(view);
    //     Assert.Equal("Cancelled", view.Status);
    // }
}


// =============================================================================
// Example: API integration test for the GET /orders/{id} endpoint
// =============================================================================
// File: test/App.Tests/Modules/Order/GetOrder/OrderViewApiTests.cs
//
// This test verifies that:
//   1. History events are seeded into the in-memory event store
//   2. The module's When handler is called and updates the projection
//   3. The GET endpoint returns the projected view
//
// Uses the Fixture class which wires everything together in-process.
// =============================================================================

using System.Net.Http.Json;
using App.Modules.Order;
using App.Modules.Order.CreateOrder;
using App.Modules.Order.GetOrder;

namespace App.Tests.Modules.Order.GetOrder;

public class OrderViewApiTests(Fixture fixture) : IClassFixture<Fixture>
{
    [Fact]
    public Task GetOrder_returns_view_after_OrderCreated() =>
        fixture
        .WithHistory(
            new OrderDecider(),
            new OrderCreated(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Alice", "Widget", 5))
        .Test(async client =>
        {
            var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var response = await client.GetAsync($"/orders/{id}");

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<ViewResponse<OrderView>>();
            Assert.NotNull(body?.View);
            Assert.Equal(id, body.View.OrderId);
            Assert.Equal("Alice", body.View.CustomerName);
            Assert.Equal("Open", body.View.Status);
        });

    [Fact]
    public Task GetOrder_returns_404_for_unknown_id() =>
        fixture
        .Test(async client =>
        {
            var response = await client.GetAsync($"/orders/{Guid.NewGuid()}");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        });
}
