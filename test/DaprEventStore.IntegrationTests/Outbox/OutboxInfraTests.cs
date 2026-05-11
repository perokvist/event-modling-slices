using Dapr.Client;
using System.Text;
using System.Text.Json;

using DaprEventStore.IntegrationTests.Infrastructure;

namespace DaprEventStore.IntegrationTests.Outbox;

/// <summary>
/// Layer 2: verifies Dapr's documented outbox projection behaviour using raw DaprClient.
/// No EventStore library involved — these tests validate Dapr config and projection semantics
/// so that EventStoreOutboxTests can rely on confirmed behaviour.
/// </summary>
[Collection("DaprSidecar")]
public class OutboxInfraTests(DaprSidecarFixture fixture)
{
    private const string StoreName = "statestore-outbox";

    private DaprClient CreateClient() =>
        new DaprClientBuilder()
            .UseHttpEndpoint($"http://localhost:{DaprSidecarFixture.DaprHttpPort}")
            .UseGrpcEndpoint($"http://localhost:{DaprSidecarFixture.DaprGrpcPort}")
            .Build();

    private static byte[] Json(object value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));

    /// <summary>
    /// Baseline: plain transaction with no projection metadata — all entries are auto-published
    /// because the state store is configured with outboxPublishPubsub/outboxPublishTopic.
    /// </summary>
    [Fact]
    public async Task PlainTransaction_AutoPublishesAllEntries()
    {
        using var client = CreateClient();
        var key = $"plain-{Guid.NewGuid()}";

        await client.ExecuteStateTransactionAsync(StoreName,
        [
            new(key, Json(new { marker = "auto-publish" }), StateOperationType.Upsert)
        ]);

        var msg = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains("auto-publish"), TimeSpan.FromSeconds(15));
        Assert.True(msg.DataContains("auto-publish"));
    }

    /// <summary>
    /// Docs: a regular entry + matching projection (same key) → projection value is published,
    /// NOT the regular value. The regular value is stored; the projection value is not stored.
    /// </summary>
    [Fact]
    public async Task SingleProjection_PublishesProjectionValue_NotRegularValue()
    {
        using var client = CreateClient();
        var key = $"proj-{Guid.NewGuid()}";

        await client.ExecuteStateTransactionAsync(StoreName,
        [
            // Regular entry — stored in state store
            new(key, Json(new { stored = true }),  StateOperationType.Upsert),
            // Projection entry (same key) — published to pub/sub, not stored
            new(key, Json(new { published = true, marker = "projection-value" }),
                StateOperationType.Upsert,
                metadata: new Dictionary<string, string> { { "outbox.projection", "true" } })
        ]);

        var msg = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains("projection-value"), TimeSpan.FromSeconds(15));
        Assert.True(msg.DataContains("projection-value"));
        Assert.DoesNotContain("\"stored\":true", msg.Data.GetRawText());
    }

    /// <summary>
    /// Docs: outbox.projection:false on a regular entry suppresses its auto-publication.
    /// Combined with a matching projection:true entry, only the projection value is published.
    /// Verifies that false + true (same key) → exactly ONE message with the projection value.
    /// </summary>
    [Fact]
    public async Task ProjectionFalse_SuppressesAutoPublish_TruePublishesProjection()
    {
        using var client = CreateClient();
        var key = $"suppress-{Guid.NewGuid()}";

        await client.ExecuteStateTransactionAsync(StoreName,
        [
            // Explicit false — should NOT be published
            new(key, Json(new { suppressed = true }),
                StateOperationType.Upsert,
                metadata: new Dictionary<string, string> { { "outbox.projection", "false" } }),
            // Projection — should be published
            new(key, Json(new { marker = "array-projection", events = new[] { "EventA", "EventB" } }),
                StateOperationType.Upsert,
                metadata: new Dictionary<string, string> { { "outbox.projection", "true" } })
        ]);

        var msg = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains("array-projection"), TimeSpan.FromSeconds(15));
        Assert.True(msg.DataContains("array-projection"));
        Assert.DoesNotContain("suppressed", msg.Data.GetRawText());
    }

    /// <summary>
    /// Multi-key transaction: all non-projection entries marked false + one projection.
    /// FINDING: This deadlocks with in-memory pub/sub because:
    ///   1. outbox.projection:false WITHOUT a matching projection still auto-publishes (Dapr source confirmed)
    ///   2. Multiple concurrent internal Publish calls deadlock in-memory pub/sub
    /// The in-memory deadlock is pub/sub-specific; with Redis/RabbitMQ, multiple entries publish fine.
    /// Superseded by AutoPublish mode (no projection metadata, all entries auto-published individually).
    /// </summary>
    [Fact]
    public async Task MultipleKeys_AllSuppressedExceptProjection_OnlyProjectionPublished()
    {
        TestInfra.RequireRedisOrSkip();
        using var client = CreateClient();
        var headKey  = $"head-{Guid.NewGuid()}";
        var event1Key = $"event1-{Guid.NewGuid()}";
        var event2Key = $"event2-{Guid.NewGuid()}";
        var marker = Guid.NewGuid().ToString("N");

        await client.ExecuteStateTransactionAsync(StoreName,
        [
            // Stream head — stored, not published
            new(headKey,   Json(new { version = 2 }),
                StateOperationType.Upsert,
                metadata: new Dictionary<string, string> { { "outbox.projection", "false" } }),
            // Event entries — stored, not published
            new(event1Key, Json(new { eventName = $"OrderPlaced-{marker}" }),
                StateOperationType.Upsert,
                metadata: new Dictionary<string, string> { { "outbox.projection", "false" } }),
            new(event2Key, Json(new { eventName = $"OrderShipped-{marker}" }),
                StateOperationType.Upsert,
                metadata: new Dictionary<string, string> { { "outbox.projection", "false" } }),
            // Single projection on head key — published with full event array
            new(headKey,   Json(new { events = new[] { $"OrderPlaced-{marker}", $"OrderShipped-{marker}" } }),
                StateOperationType.Upsert,
                metadata: new Dictionary<string, string>
                {
                    { "outbox.projection",  "true" },
                    { "contentType",        "application/json" },
                    { "datacontenttype",    "application/json" }
                })
        ]);

        var msg = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains(marker), TimeSpan.FromSeconds(15));

        Assert.True(msg.DataContains($"OrderPlaced-{marker}"));
        Assert.True(msg.DataContains($"OrderShipped-{marker}"));
    }

    /// <summary>
    /// AutoPublish mode prerequisite: plain multi-entry transaction with no projection metadata.
    /// FINDING: This ALSO deadlocks with in-memory pub/sub (same root cause as multi-key projections).
    /// ANY multi-key transaction on an outbox-configured store with in-memory pub/sub deadlocks.
    /// Root cause: multiple distinct keys → multiple internal Publish calls → in-memory pub/sub deadlock.
    /// AutoPublish mode works correctly with Redis/RabbitMQ pub/sub backends.
    /// </summary>
    [Fact]
    public async Task PlainMultiEntry_AutoPublishesAllEntriesIndividually()
    {
        TestInfra.RequireRedisOrSkip();
        using var client = CreateClient();
        var marker = Guid.NewGuid().ToString("N");
        var headKey   = $"head-{Guid.NewGuid()}";
        var event1Key = $"e1-{Guid.NewGuid()}";
        var event2Key = $"e2-{Guid.NewGuid()}";

        await client.ExecuteStateTransactionAsync(StoreName,
        [
            new(headKey,   Json(new { type = "StreamHead", marker }),  StateOperationType.Upsert),
            new(event1Key, Json(new { type = "OrderPlaced",  marker }), StateOperationType.Upsert),
            new(event2Key, Json(new { type = "OrderShipped", marker }), StateOperationType.Upsert),
        ]);

        // All three entries should be published individually
        var msg1 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains("StreamHead") && m.DataContains(marker), TimeSpan.FromSeconds(15));
        var msg2 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains("OrderPlaced") && m.DataContains(marker), TimeSpan.FromSeconds(15));
        var msg3 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains("OrderShipped") && m.DataContains(marker), TimeSpan.FromSeconds(15));

        Assert.True(msg1.DataContains("StreamHead"));
        Assert.True(msg2.DataContains("OrderPlaced"));
        Assert.True(msg3.DataContains("OrderShipped"));
    }


    /// transaction causes a daprd panic ("slice bounds out of range"). Skip until fixed upstream.
    /// Finding: the single-projection array approach (one entry per transaction) must be used.
    /// </summary>
    [Fact]
    public async Task MultipleProjections_DifferentKeys_EachPublishSeparately()
    {
        TestInfra.RequireRedisOrSkip();
        using var client = CreateClient();
        var key1 = $"multi-a-{Guid.NewGuid()}";
        var key2 = $"multi-b-{Guid.NewGuid()}";

        await client.ExecuteStateTransactionAsync(StoreName,
        [
            new(key1, Json(new { stored = 1 }), StateOperationType.Upsert),
            new(key1, Json(new { marker = "projection-key1" }),
                StateOperationType.Upsert,
                metadata: new Dictionary<string, string> { { "outbox.projection", "true" } }),
            new(key2, Json(new { stored = 2 }), StateOperationType.Upsert),
            new(key2, Json(new { marker = "projection-key2" }),
                StateOperationType.Upsert,
                metadata: new Dictionary<string, string> { { "outbox.projection", "true" } }),
        ]);

        var msg1 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains("projection-key1"), TimeSpan.FromSeconds(15));
        var msg2 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains("projection-key2"), TimeSpan.FromSeconds(15));

        Assert.True(msg1.DataContains("projection-key1"));
        Assert.True(msg2.DataContains("projection-key2"));
    }
}
