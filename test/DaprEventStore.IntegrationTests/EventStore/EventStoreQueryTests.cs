using Dapr.Client;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

using DaprEventStore.IntegrationTests.Infrastructure;

namespace DaprEventStore.IntegrationTests.EventStore;

/// <summary>
/// Integration tests for QueryEventsAsync backed by Azure Cosmos DB.
/// Verifies the Dapr query API and that events are stored as JSON documents.
/// Uses DaprSidecarCosmosFixture which starts the Cosmos DB Linux emulator container.
/// Requires Docker to be running.
/// </summary>
[Collection("DaprSidecarCosmos")]
public class EventStoreQueryTests(DaprSidecarCosmosFixture fixture)
{
    private DaprEventStore CreateStore() =>
        new DaprEventStore(new DaprClientBuilder()
                .UseHttpEndpoint($"http://localhost:{DaprSidecarCosmosFixture.DaprHttpPort}")
                .UseGrpcEndpoint($"http://localhost:{DaprSidecarCosmosFixture.DaprGrpcPort}")
                .Build())
            { StoreName = DaprSidecarCosmosFixture.StoreName }
            .PartitionAllStream();

    [Fact]
    public async Task QueryEventsAsync_NoFilter_ReturnsAllEventsInOccuredAtOrder()
    {
        var store = CreateStore();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var streamA = $"{prefix}-course";
        var streamB = $"{prefix}-student";

        var t0 = DateTime.UtcNow;
        var t1 = t0.AddMilliseconds(10);
        var t2 = t0.AddMilliseconds(20);
        var t3 = t0.AddMilliseconds(30);

        await store.AppendToStreamAsync(streamA,
            new EventData(Guid.NewGuid().ToString(), "CourseCreated",   new { }, t0),
            new EventData(Guid.NewGuid().ToString(), "CoursePublished", new { }, t2));

        await store.AppendToStreamAsync(streamB,
            new EventData(Guid.NewGuid().ToString(), "StudentEnrolled",  new { }, t1),
            new EventData(Guid.NewGuid().ToString(), "StudentGraduated", new { }, t3));

        var all = await store.QueryEventsAsync()
            .Where(e => e.StreamName == streamA || e.StreamName == streamB)
            .ToListAsync();

        Assert.Equal(4, all.Count);
        Assert.Equal("CourseCreated",    all[0].EventName);
        Assert.Equal("StudentEnrolled",  all[1].EventName);
        Assert.Equal("CoursePublished",  all[2].EventName);
        Assert.Equal("StudentGraduated", all[3].EventName);

        Assert.True(all[0].Position < all[1].Position);
        Assert.True(all[1].Position < all[2].Position);
        Assert.True(all[2].Position < all[3].Position);
    }

    [Fact]
    public async Task QueryEventsAsync_FilterByStreamName_ReturnsOnlyThatStreamsEvents()
    {
        var store = CreateStore();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var streamA = $"{prefix}-orders";
        var streamB = $"{prefix}-payments";

        await store.AppendToStreamAsync(streamA,
            EventData.Create("OrderPlaced",  new { }),
            EventData.Create("OrderShipped", new { }));

        await store.AppendToStreamAsync(streamB,
            EventData.Create("PaymentProcessed", new { }));

        var ordersOnly = await store.QueryEventsAsync(streamName: streamA).ToListAsync();

        Assert.Equal(2, ordersOnly.Count);
        Assert.All(ordersOnly, e => Assert.Equal(streamA, e.StreamName));
        Assert.Equal("OrderPlaced",  ordersOnly[0].EventName);
        Assert.Equal("OrderShipped", ordersOnly[1].EventName);
    }

    [Fact]
    public async Task QueryEventsAsync_FilterByEventName_ReturnsOnlyMatchingEventType()
    {
        var store = CreateStore();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var stream = $"{prefix}-inventory";

        await store.AppendToStreamAsync(stream,
            EventData.Create("ItemAdded",   new { }),
            EventData.Create("ItemRemoved", new { }),
            EventData.Create("ItemAdded",   new { }));

        var added = await store.QueryEventsAsync(eventName: "ItemAdded").ToListAsync();

        Assert.Equal(2, added.Count);
        Assert.All(added, e => Assert.Equal("ItemAdded", e.EventName));
    }

    /// <summary>
    /// Verifies that events written via AppendToStreamAsync are stored as structured
    /// JSON documents in Cosmos DB — with the event payload accessible as a nested
    /// JSON object (not a base64 blob), confirming schema visibility for queries and
    /// direct reads.
    /// </summary>
    [Fact]
    public async Task AppendToStreamAsync_EventsStoredAsJsonDocuments_InCosmos()
    {
        var store      = CreateStore();
        var streamName = $"verify-json-{Guid.NewGuid():N}";

        await store.AppendToStreamAsync(streamName,
            EventData.Create("ItemOrdered", new { OrderId = 99, Product = "Widget" }));

        // Query Cosmos directly — bypassing Dapr — to confirm the raw document shape.
        using var cosmos    = fixture.CreateCosmosClient();
        var       container = cosmos.GetContainer(
            DaprSidecarCosmosFixture.DatabaseName,
            DaprSidecarCosmosFixture.ContainerName);

        // All docs share partitionKey "all" because the store uses PartitionAllStream.
        var qd = new QueryDefinition(
            "SELECT * FROM c WHERE c[\"value\"].streamName = @stream AND c[\"value\"].eventName = @event")
            .WithParameter("@stream", streamName)
            .WithParameter("@event",  "ItemOrdered");

        // Use stream iterator to get raw JSON — CosmosClient's default Newtonsoft serializer
        // cannot deserialize System.Text.Json types, so we parse the response stream directly.
        var docs = new List<JsonElement>();
        var iter = container.GetItemQueryStreamIterator(
            qd, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("all") });

        while (iter.HasMoreResults)
        {
            using var responseMessage = await iter.ReadNextAsync();
            responseMessage.EnsureSuccessStatusCode();
            using var jsonDoc = JsonDocument.Parse(responseMessage.Content);
            foreach (var doc in jsonDoc.RootElement.GetProperty("Documents").EnumerateArray())
                docs.Add(doc.Clone());
        }

        // One document per appended event (the stream-head doc is a separate key).
        Assert.Single(docs);

        var value = docs[0].GetProperty("value");

        // streamName and eventName are top-level fields in the stored JSON.
        Assert.Equal(streamName,    value.GetProperty("streamName").GetString());
        Assert.Equal("ItemOrdered", value.GetProperty("eventName").GetString());

        // The event payload must be a JSON object — not a string or base64 blob.
        Assert.Equal(JsonValueKind.Object, value.GetProperty("data").ValueKind);
        Assert.Equal(99, value.GetProperty("data").GetProperty("orderId").GetInt32());
    }
}
