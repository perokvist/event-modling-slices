using Dapr.Client;
using System.Text;
using System.Text.Json;

using DaprEventStore.IntegrationTests.Infrastructure;

namespace DaprEventStore.IntegrationTests.Outbox;

/// <summary>
/// Layer 2 (Redis): verifies Dapr outbox behaviour with a real Redis pub/sub backend.
/// Uses the DaprSidecarRedis collection to avoid in-memory pub/sub deadlocks on multi-key transactions.
/// Mirrors OutboxInfraTests but runs tests that require multi-key outbox transactions.
/// </summary>
[Collection("DaprSidecarRedis")]
public class OutboxInfraRedisTests(DaprSidecarRedisFixture fixture)
{
    private const string StoreName = "statestore-outbox";

    private DaprClient CreateClient() =>
        new DaprClientBuilder()
            .UseHttpEndpoint($"http://localhost:{DaprSidecarRedisFixture.DaprHttpPort}")
            .UseGrpcEndpoint($"http://localhost:{DaprSidecarRedisFixture.DaprGrpcPort}")
            .Build();

    private static byte[] Json(object value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));

    /// <summary>
    /// AutoPublish mode baseline: plain multi-entry transaction with no projection metadata.
    /// All entries are auto-published individually — one pub/sub message per state entry.
    /// This is the foundation of AutoPublish mode and requires a real pub/sub backend.
    /// </summary>
    [Fact]
    public async Task PlainMultiEntry_AutoPublishesAllEntriesIndividually()
    {
        using var client = CreateClient();
        var marker = Guid.NewGuid().ToString("N");

        await client.ExecuteStateTransactionAsync(StoreName,
        [
            new($"head-{marker}",   Json(new { type = "StreamHead",    marker }), StateOperationType.Upsert),
            new($"event1-{marker}", Json(new { type = "OrderPlaced",   marker }), StateOperationType.Upsert),
            new($"event2-{marker}", Json(new { type = "OrderShipped",  marker }), StateOperationType.Upsert),
        ]);

        var msg1 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains("StreamHead")   && m.DataContains(marker), TimeSpan.FromSeconds(20));
        var msg2 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains("OrderPlaced")  && m.DataContains(marker), TimeSpan.FromSeconds(20));
        var msg3 = await fixture.TestAppServer.WaitForMatchingMessageAsync(
            m => m.DataContains("OrderShipped") && m.DataContains(marker), TimeSpan.FromSeconds(20));

        Assert.True(msg1.DataContains("StreamHead"));
        Assert.True(msg2.DataContains("OrderPlaced"));
        Assert.True(msg3.DataContains("OrderShipped"));
    }
}
