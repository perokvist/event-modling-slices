using Dapr.Client;

using DaprEventStore.IntegrationTests.Infrastructure;

namespace DaprEventStore.IntegrationTests.StateStore;

/// <summary>
/// Integration tests that verify a Dapr sidecar with an in-memory state store
/// supports basic read and write operations, and that DaprEventStore can append
/// and load event streams.
/// </summary>
[Collection("DaprSidecar")]
public class StateStoreTests(DaprSidecarFixture fixture)
{
    private const string StoreName = "statestore";

    private DaprClient CreateClient() =>
        new DaprClientBuilder()
            .UseHttpEndpoint($"http://localhost:{DaprSidecarFixture.DaprHttpPort}")
            .UseGrpcEndpoint($"http://localhost:{DaprSidecarFixture.DaprGrpcPort}")
            .Build();

    [Fact]
    public async Task StateStore_WriteAndReadString_ReturnsExpectedValue()
    {
        using var client = CreateClient();
        var key = $"test-string-{Guid.NewGuid()}";
        const string expected = "hello-dapr";

        await client.SaveStateAsync(StoreName, key, expected);
        var actual = await client.GetStateAsync<string>(StoreName, key);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task StateStore_WriteAndReadTypedObject_ReturnsExpectedObject()
    {
        using var client = CreateClient();
        var key = $"test-object-{Guid.NewGuid()}";
        var expected = new TestPayload(Id: Guid.NewGuid(), Name: "Dapr Integration Test", Value: 42);

        await client.SaveStateAsync(StoreName, key, expected);
        var actual = await client.GetStateAsync<TestPayload>(StoreName, key);

        Assert.NotNull(actual);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Value, actual.Value);
    }

    [Fact]
    public async Task EventStore_AppendAndLoadStream_ReturnsExpectedEvents()
    {
        using var client = CreateClient();
        var streamName = $"test-stream-{Guid.NewGuid()}";

        var eventStore = new global::DaprEventStore.DaprEventStore(client)
        {
            StoreName = StoreName
        };

        var events = new[]
        {
            EventData.Create("OrderPlaced",   new { OrderId = 1, Amount = 100.0 }),
            EventData.Create("OrderShipped",  new { OrderId = 1, Carrier = "DHL" }),
            EventData.Create("OrderDelivered",new { OrderId = 1, DeliveredAt = DateTime.UtcNow })
        };

        var versionAfterAppend = await eventStore.AppendToStreamAsync(streamName, events);

        Assert.Equal(3, versionAfterAppend);

        var loaded = await eventStore.LoadEventStreamAsync(streamName, 0).ToListAsync();

        Assert.Equal(3, loaded.Count);
        Assert.Equal("OrderPlaced",    loaded[0].EventName);
        Assert.Equal("OrderShipped",   loaded[1].EventName);
        Assert.Equal("OrderDelivered", loaded[2].EventName);
    }
}

/// <summary>Collection fixture definition tying tests to the shared sidecar fixture.</summary>
[CollectionDefinition("DaprSidecar")]
public class DaprSidecarCollection : ICollectionFixture<DaprSidecarFixture>;

/// <summary>Test payload used in typed object state store test.</summary>
public record TestPayload(Guid Id, string Name, int Value);
