using Dapr.Client;

using DaprEventStore.IntegrationTests.Infrastructure;

namespace DaprEventStore.IntegrationTests.Outbox;

/// <summary>
/// Layer 4: verifies the EventStore outbox pattern end-to-end.
/// AppendToStreamAsync with OutboxTopic set uses AutoPublish mode: a single transaction
/// on the outbox-configured store. All entries are auto-published individually by Dapr.
///
/// NOTE: All tests in this class require a real pub/sub backend (Redis/RabbitMQ).
/// Even a single-event append produces a 2-key transaction (stream-head + event), which
/// deadlocks with in-memory pub/sub. Tests are skipped until a Redis fixture is available.
/// </summary>
[Collection("DaprSidecar")]
public class EventStoreOutboxTests(DaprSidecarFixture fixture)
{
    // AutoPublish mode: StoreName points directly to the outbox-configured store.
    // A single transaction stores head + events; all are auto-published individually.
    private const string StoreName = "statestore-outbox";

    private DaprClient CreateClient() =>
        new DaprClientBuilder()
            .UseHttpEndpoint($"http://localhost:{DaprSidecarFixture.DaprHttpPort}")
            .UseGrpcEndpoint($"http://localhost:{DaprSidecarFixture.DaprGrpcPort}")
            .Build();

    /// <summary>
    /// AutoPublish mode: appending one event produces 2 pub/sub messages — stream-head update
    /// and the event. Consumers identify events by the presence of EventName / EventId fields.
    /// SKIPPED: 2-key transaction deadlocks with in-memory pub/sub. Requires Redis fixture.
    /// </summary>
    [Fact]
    public async Task AppendSingleEvent_PublishesEventToOutbox()
    {
        TestInfra.RequireRedisOrSkip();
        using var client = CreateClient();
        var store = new global::DaprEventStore.DaprEventStore(client)
        {
            StoreName = StoreName,
            OutboxTopic = TestAppServer.Topic,
            OutboxMode = OutboxMode.AutoPublish
        };

        var marker = Guid.NewGuid().ToString("N");
        await store.AppendToStreamAsync($"outbox-single-{marker}",
            EventData.Create($"OrderPlaced-{marker}", new { OrderId = 1 }));

        // AutoPublish: head message + event message (2 total for single-event append)
        var eventMsg = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains($"OrderPlaced-{marker}"), TimeSpan.FromSeconds(15));

        Assert.True(eventMsg.DataContains($"OrderPlaced-{marker}"));
    }

    /// <summary>
    /// AutoPublish mode: appending N events produces N+1 pub/sub messages (one stream-head
    /// update + one per event). Unlike the old projection-based design, events are NOT
    /// batched into a single array message.
    /// SKIPPED: multi-key transaction deadlocks with in-memory pub/sub. Requires Redis fixture.
    /// </summary>
    [Fact]
    public async Task AppendMultipleEvents_PublishesAllEventsIndividuallyToOutbox()
    {
        TestInfra.RequireRedisOrSkip();
        using var client = CreateClient();
        var store = new global::DaprEventStore.DaprEventStore(client)
        {
            StoreName = StoreName,
            OutboxTopic = TestAppServer.Topic,
            OutboxMode = OutboxMode.AutoPublish
        };

        var marker = Guid.NewGuid().ToString("N");
        await store.AppendToStreamAsync($"outbox-multi-{marker}",
            EventData.Create($"OrderPlaced-{marker}",  new { OrderId = 2 }),
            EventData.Create($"OrderShipped-{marker}", new { OrderId = 2 }));

        // AutoPublish: 3 messages — stream-head update + OrderPlaced + OrderShipped
        var msg1 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains($"OrderPlaced-{marker}"), TimeSpan.FromSeconds(15));
        var msg2 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains($"OrderShipped-{marker}"), TimeSpan.FromSeconds(15));

        Assert.True(msg1.DataContains($"OrderPlaced-{marker}"));
        Assert.True(msg2.DataContains($"OrderShipped-{marker}"));
    }
}
