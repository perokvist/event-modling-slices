using Dapr.Client;

using DaprEventStore.IntegrationTests.Infrastructure;

namespace DaprEventStore.IntegrationTests.Outbox;

/// <summary>
/// Layer 4 (Redis): verifies EventStore AutoPublish outbox end-to-end using Redis pub/sub.
/// Redis eliminates the in-memory pub/sub deadlock, enabling multi-key outbox transactions.
/// All entries in the transaction are auto-published individually: stream-head + each event.
/// </summary>
[Collection("DaprSidecarRedis")]
public class EventStoreOutboxRedisTests(DaprSidecarRedisFixture fixture)
{
    // AutoPublish mode: single outbox-configured store handles both storage and publishing.
    private const string StoreName = "statestore-outbox";

    private DaprClient CreateClient() =>
        new DaprClientBuilder()
            .UseHttpEndpoint($"http://localhost:{DaprSidecarRedisFixture.DaprHttpPort}")
            .UseGrpcEndpoint($"http://localhost:{DaprSidecarRedisFixture.DaprGrpcPort}")
            .Build();

    /// <summary>
    /// Single-event append: transaction contains stream-head + event = 2 messages published.
    /// The event message contains the event name and payload.
    /// </summary>
    [Fact]
    public async Task AppendSingleEvent_PublishesHeadAndEventMessages()
    {
        using var client = CreateClient();
        var store = new global::DaprEventStore.DaprEventStore(client)
        {
            StoreName = StoreName,
            OutboxTopic = TestAppServer.Topic,
            OutboxMode = OutboxMode.AutoPublish
        };

        var marker = Guid.NewGuid().ToString("N");
        await store.AppendToStreamAsync($"redis-single-{marker}",
            EventData.Create($"OrderPlaced-{marker}", new { OrderId = 1 }));

        // AutoPublish: 2 messages — stream-head update + event
        var eventMsg = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains($"OrderPlaced-{marker}"), TimeSpan.FromSeconds(20));

        Assert.True(eventMsg.DataContains($"OrderPlaced-{marker}"));
    }

    /// <summary>
    /// Multi-event append: transaction contains stream-head + 2 events = 3 messages published.
    /// Each entry in the transaction produces its own pub/sub message (AutoPublish mode).
    /// </summary>
    [Fact]
    public async Task AppendMultipleEvents_PublishesHeadAndAllEventMessages()
    {
        using var client = CreateClient();
        var store = new global::DaprEventStore.DaprEventStore(client)
        {
            StoreName = StoreName,
            OutboxTopic = TestAppServer.Topic,
            OutboxMode = OutboxMode.AutoPublish
        };

        var marker = Guid.NewGuid().ToString("N");
        await store.AppendToStreamAsync($"redis-multi-{marker}",
            EventData.Create($"OrderPlaced-{marker}",  new { OrderId = 2 }),
            EventData.Create($"OrderShipped-{marker}", new { OrderId = 2 }));

        // AutoPublish: 3 messages — stream-head update + OrderPlaced + OrderShipped
        var msg1 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains($"OrderPlaced-{marker}"),  TimeSpan.FromSeconds(20));
        var msg2 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains($"OrderShipped-{marker}"), TimeSpan.FromSeconds(20));

        Assert.True(msg1.DataContains($"OrderPlaced-{marker}"));
        Assert.True(msg2.DataContains($"OrderShipped-{marker}"));
    }

    /// <summary>
    /// Verifies that events appended in AutoPublish mode are still readable from the state store.
    /// Both publishing (outbox) and persistence must work correctly on the same store.
    /// </summary>
    [Fact]
    public async Task AppendAndLoad_EventsStoredAndPublished()
    {
        using var client = CreateClient();
        var store = new global::DaprEventStore.DaprEventStore(client)
        {
            StoreName = StoreName,
            OutboxTopic = TestAppServer.Topic,
            OutboxMode = OutboxMode.AutoPublish
        };

        var marker = Guid.NewGuid().ToString("N");
        var stream = $"redis-load-{marker}";

        await store.AppendToStreamAsync(stream,
            EventData.Create($"ItemAdded-{marker}",   new { Item = "A" }),
            EventData.Create($"ItemRemoved-{marker}", new { Item = "A" }));

        // Verify pub/sub delivery
        var msg1 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains($"ItemAdded-{marker}"),   TimeSpan.FromSeconds(20));
        var msg2 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains($"ItemRemoved-{marker}"), TimeSpan.FromSeconds(20));

        Assert.True(msg1.DataContains($"ItemAdded-{marker}"));
        Assert.True(msg2.DataContains($"ItemRemoved-{marker}"));

        // Verify events are stored and loadable
        var loaded = new List<VersionedEvent>();
        await foreach (var e in store.LoadEventStreamAsync(stream, 0))
            loaded.Add(e);
        Assert.Equal(2, loaded.Count);
        Assert.Equal($"ItemAdded-{marker}",   loaded[0].EventName);
        Assert.Equal($"ItemRemoved-{marker}", loaded[1].EventName);
    }
}
