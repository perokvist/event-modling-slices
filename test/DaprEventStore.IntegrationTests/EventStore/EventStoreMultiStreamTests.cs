using Dapr.Client;
using DaprEventStore;
using System.Collections.Generic;

using DaprEventStore.IntegrationTests.Infrastructure;

namespace DaprEventStore.IntegrationTests.EventStore;

[Collection("DaprSidecarRedis")]
public class EventStoreMultiStreamTests(DaprSidecarRedisFixture fixture)
{
    private DaprClient CreateClient() =>
        new DaprClientBuilder()
            .UseHttpEndpoint($"http://localhost:{DaprSidecarRedisFixture.DaprHttpPort}")
            .UseGrpcEndpoint($"http://localhost:{DaprSidecarRedisFixture.DaprGrpcPort}")
            .Build();

    [Fact]
    public async Task AppendToStreamsAsync_AppendsToMultipleStreamsAtomically()
    {
        using var client = CreateClient();
        var store = new global::DaprEventStore.DaprEventStore(client)
        {
            StoreName = "statestore-outbox",
        }.PartitionAllStream("all");

        var marker = Guid.NewGuid().ToString("N");
        var s1 = $"multi1-{marker}";
        var s2 = $"multi2-{marker}";

        var streams = new[]
        {
            (StreamName: s1, ExpectedVersion: 0L, Events: new[]{ EventData.Create($"E1-{marker}", new { X = 1 }) }),
            (StreamName: s2, ExpectedVersion: 0L, Events: new[]{ EventData.Create($"E2-{marker}", new { Y = 2 }) })
        };

        var result = await store.AppendToStreamsAsync(streams);

        Assert.Equal(1L, result[s1]);
        Assert.Equal(1L, result[s2]);

        var loaded1 = new List<VersionedEvent>();
        await foreach (var e in store.LoadEventStreamAsync(s1, 0)) loaded1.Add(e);
        var loaded2 = new List<VersionedEvent>();
        await foreach (var e in store.LoadEventStreamAsync(s2, 0)) loaded2.Add(e);

        Assert.Single(loaded1);
        Assert.Single(loaded2);
        Assert.Equal($"E1-{marker}", loaded1[0].EventName);
        Assert.Equal($"E2-{marker}", loaded2[0].EventName);
    }
}
