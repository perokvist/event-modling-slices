// =============================================================================
// Example: Given-When-Then tests for the CreateOrder command slice
// =============================================================================
// File: test/App.Tests/Modules/Order/CreateOrder/StateChangeTests.cs
//
// These tests ARE the specification of the command slice. Each test maps
// directly to a row in the event model:
//
//   Given: <prior events that establish state>
//   When:  <command under test>
//   Then:  <expected resulting events>
//
// The Decider is a pure function — no I/O, no mocking needed.
// Build state by folding history events through Evolve, then call Decide.
// =============================================================================

using App.Modules.Order;
using App.Modules.Order.CreateOrder;

namespace App.Tests.Modules.Order.CreateOrder;

public class StateChangeTests
{
    [Fact]
    public void CreateOrder_produces_OrderCreated_event()
    {
        // Given — no prior events (empty history)
        var id = Guid.NewGuid();
        OrderEvent[] history = [];

        var decider = new OrderDecider();
        var state = history.Aggregate(decider.InitialState, decider.Evolve);

        // When — execute the CreateOrder command
        var events = decider.Decide(
            new CreateOrderCommand(id, "Alice", "Widget", 5), state);

        // Then — exactly one OrderCreated event with correct fields
        Assert.Collection(events, e =>
        {
            var ev = Assert.IsType<OrderCreated>(e);
            Assert.Equal(id, ev.OrderId);
            Assert.Equal("Alice", ev.CustomerName);
            Assert.Equal("Widget", ev.Product);
            Assert.Equal(5, ev.Quantity);
        });
    }

    [Fact]
    public void CreateOrder_is_rejected_when_order_already_exists()
    {
        // Given — order was already created
        var id = Guid.NewGuid();
        OrderEvent[] history = [new OrderCreated(id, "Alice", "Widget", 5)];

        var decider = new OrderDecider();
        var state = history.Aggregate(decider.InitialState, decider.Evolve);

        // When — attempt to create the same order again
        var events = decider.Decide(
            new CreateOrderCommand(id, "Bob", "Gadget", 1), state);

        // Then — command is rejected, no events produced
        Assert.Empty(events);
    }

    [Fact]
    public void Evolve_applies_OrderCreated_to_state()
    {
        // Verify the Evolve function correctly updates state
        var id = Guid.NewGuid();
        var decider = new OrderDecider();

        var state = decider.Evolve(
            decider.InitialState,
            new OrderCreated(id, "Alice", "Widget", 5));

        Assert.Equal(id, state.OrderId);
        Assert.Equal("Alice", state.CustomerName);
        Assert.True(state.IsCreated);
    }
}

// =============================================================================
// Example: API integration test for the CreateOrder command slice
// =============================================================================
// File: test/App.Tests/Modules/Order/CreateOrder/CreateOrderApiTests.cs
//
// The Fixture wires InMemoryEventStore with InProcessEndpointPublisher.
// After AppendToStreamAsync, each event is POSTed as a CloudEvent to the
// domain-events endpoint, which calls IModule.When() on all registered modules.
// TestModule captures those events via Subject<Event> for assertion.
//
// Use the (client, events) overload of Test() to subscribe before triggering
// the command so no events are missed.
//
// Note: the feature generates a server-side ID (cmd with { Id = Guid.NewGuid() }),
// so assert on domain fields (CustomerName, Product) — not on OrderId.
// =============================================================================

using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Xunit.Abstractions;

public class CreateOrderApiTests(Fixture fixture, ITestOutputHelper outputHelper) : IClassFixture<Fixture>
{
    [Fact]
    public Task CreateOrder_publishes_OrderCreated() =>
        fixture
        .WithLogging(outputHelper.WriteLine)
        .Test(async (client, events) =>
        {
            // Arrange — subscribe before triggering the command
            var capture = events
                .Timeout(TimeSpan.FromMilliseconds(2500))
                .OfType<OrderCreated>()
                .FirstAsync()
                .ToTask();

            // Act
            var response = await client.PostAsJsonAsync(
                "/orders",
                new CreateOrderCommand(Guid.NewGuid(), "Alice", "Widget", 5));

            response.EnsureSuccessStatusCode();

            // Assert — event delivered through domain-events endpoint to IModule.When
            var received = await capture;
            Assert.Equal("Alice", received.CustomerName);
            Assert.Equal("Widget", received.Product);
        });
}
