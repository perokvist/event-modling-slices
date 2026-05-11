using Dapr.Client;
using System.Buffers;
using System.Data;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using static DaprEventStore.DaprEventStore;

namespace DaprEventStore;

public static class Extensions
{
    public static DaprEventStore PartitionPerStream(this DaprEventStore daprEventStore)
    {
        daprEventStore.MetaProvider = streamName => new Dictionary<string, string>
                {
                    { "partitionKey", streamName },
                    { "contentType", "application/json" }
                };
        return daprEventStore;
    }

    public static DaprEventStore PartitionAllStream(this DaprEventStore daprEventStore, string allStream = "all")
        => daprEventStore.PartitionCustom(allStream);

    public static DaprEventStore PartitionCustom(this DaprEventStore daprEventStore, string partitionKey)
    {
        daprEventStore.MetaProvider = streamName => new Dictionary<string, string>
                {
                    { "partitionKey", partitionKey },
                    { "contentType", "application/json" }
                };
        return daprEventStore;
    }
    public static async IAsyncEnumerable<VersionedEvent> LoadAsyncBulkEventsAsync(
     this DaprClient client,
     string storeName,
     string streamName,
     long version,
     Dictionary<string, string> meta,
     StreamHead head,
     int chunkSize = 20)
    {
        var startVersion = version == default ? 1 : (int)version;
        var keys = Enumerable
            .Range(startVersion, (int)head.Version - startVersion + 1)
            .Select(x => Naming.StreamKey(streamName, x))
            .ToList();

        if (keys.Count == 0)
            yield break;

        foreach (var chunk in keys.Chunk(chunkSize))
        {
            var events = (await client.GetBulkStateAsync(storeName, chunk, null, metadata: meta))
                .Select(x => JsonSerializer.Deserialize<VersionedEvent>(x.Value, client.JsonSerializerOptions))
                .OrderBy(x => x!.Version);

            foreach (var e in events)
                yield return e;
        }
    }

    public static async Task EventsTransactionAsync(
        this DaprClient client,
        string storeName,
        string streamName,
        string streamHeadKey,
        StreamHead streamHead,
        string headetag,
        Dictionary<string, string> meta,
        VersionedEvent[] events,
        string? outboxTopic = null,
        OutboxMode outboxMode = OutboxMode.AutoPublish)
    {
        if (outboxTopic == null)
        {
            // No outbox: plain transaction on the primary store.
            await client.ExecuteStateTransactionAsync(
                storeName: storeName,
                operations: events.ToStateTransactions(
                    streamName, streamHeadKey, streamHead, headetag, meta, client.JsonSerializerOptions),
                metadata: meta);
            return;
        }

        switch (outboxMode)
        {
            case OutboxMode.AutoPublish:
                // Single transaction to the outbox-configured store.
                // No projection metadata — Dapr auto-publishes all entries individually.
                // Produces N+1 messages: one stream-head update + one per event.
                // Requires a real pub/sub backend for multi-event appends (in-memory pub/sub
                // deadlocks on multi-key transactions).
                await client.ExecuteStateTransactionAsync(
                    storeName: storeName,
                    operations: events.ToStateTransactions(
                        streamName, streamHeadKey, streamHead, headetag, meta, client.JsonSerializerOptions),
                    metadata: meta);
                break;

            case OutboxMode.EventsArray:
                // Future: blocked by Dapr — outbox.projection:false does not suppress entries
                // without a matching projection (they still auto-publish).
                throw new NotSupportedException(
                    "OutboxMode.EventsArray is not yet supported. Dapr must fix outbox.projection:false " +
                    "suppression behaviour before this mode can be implemented.");

            case OutboxMode.PerEvent:
                // Future: blocked by Dapr runtime panic when a transaction contains multiple
                // outbox.projection:true entries with different keys.
                throw new NotSupportedException(
                    "OutboxMode.PerEvent is not yet supported. Dapr must fix the runtime panic " +
                    "that occurs with multiple outbox.projection:true entries with different keys.");

            default:
                throw new ArgumentOutOfRangeException(nameof(outboxMode), outboxMode, null);
        }
    }



    public static StateTransactionRequest[] ToStateTransactions(
        this VersionedEvent[] events,
        string streamName,
        string streamHeadKey,
        StreamHead streamHead,
        string headetag,
        Dictionary<string, string> meta,
        JsonSerializerOptions serializerOptions)
        => [streamHead.CreateStreamHeadStateRequest(streamHeadKey, headetag, meta),
            .. events.CreateEventStateRequests(streamName, meta, serializerOptions)];

    /// <summary>
    /// Builds the StateTransactionRequest list for a DCB append:
    /// the target stream head write, one guard no-op entry per observed stream, then all event entries.
    /// Guard entries write the same head value back with a fresh etag so Dapr rejects the transaction
    /// if any guard stream was modified by a concurrent writer between the fresh re-read and commit.
    /// </summary>
    public static StateTransactionRequest[] ToStateTransactionsDcb(
        this VersionedEvent[] events,
        string streamName,
        string streamHeadKey,
        StreamHead streamHead,
        string headEtag,
        Dictionary<string, string> meta,
        IEnumerable<(string HeadKey, StreamHead Head, string Etag, Dictionary<string, string> Meta)> guardEntries,
        JsonSerializerOptions serializerOptions)
    {
        var result = new List<StateTransactionRequest>
        {
            streamHead.CreateStreamHeadStateRequest(streamHeadKey, headEtag, meta)
        };

        foreach (var (headKey, head, etag, guardMeta) in guardEntries)
            result.Add(head.CreateStreamHeadStateRequest(headKey, etag, guardMeta));

        result.AddRange(events.CreateEventStateRequests(streamName, meta, serializerOptions));

        return result.ToArray();
    }

    /// <summary>
    /// Produces a single outbox projection entry whose key matches the state entry key.
    /// Dapr publishes this entry's value (an array of all items) to the configured pub/sub topic
    /// and does NOT store it in the state store.
    /// </summary>
    public static StateTransactionRequest CreateOutboxProjection<T>(
        this T[] items,
        string key,
        JsonSerializerOptions serializerOptions)
        => new(key: key,
               value: JsonSerializer.SerializeToUtf8Bytes(items, serializerOptions),
               operationType: StateOperationType.Upsert,
               metadata: new Dictionary<string, string>
               {
                   { "outbox.projection", "true" },
                   { "contentType", "application/json" },
                   { "datacontenttype", "application/json" }
               });

    public static StateTransactionRequest CreateOutboxProjection(
        this VersionedEvent[] events,
        string streamHeadKey,
        JsonSerializerOptions serializerOptions)
        => events.CreateOutboxProjection<VersionedEvent>(streamHeadKey, serializerOptions);

    public static StateTransactionRequest CreateStreamHeadStateRequest(
        this StreamHead head,
        string streamHeadKey,
        string headetag,
        Dictionary<string, string> meta)
        => new(streamHeadKey, JsonSerializer.SerializeToUtf8Bytes(head), StateOperationType.Upsert,
            metadata: meta,
            etag: string.IsNullOrWhiteSpace(headetag) ? null : headetag);

    public static IEnumerable<StateTransactionRequest> CreateEventStateRequests(
        this IEnumerable<VersionedEvent> events,
        string streamName,
        Dictionary<string, string> meta,
        JsonSerializerOptions serializerOptions)
        => events.Select(x => new StateTransactionRequest(
                Naming.StreamKey(streamName, x.Version),
                JsonSerializer.SerializeToUtf8Bytes(x, serializerOptions), StateOperationType.Upsert, metadata: meta));

    public static T EventAs<T>(this VersionedEvent eventData, JsonSerializerOptions options = null)
     => eventData.Data switch
     {
         JsonElement d => d.ToObject<T>(options),
         T d => d,
         _ => throw new Exception($"Data was not of type {typeof(T).Name}")
     };

    public static T ToObject<T>(this JsonElement element, JsonSerializerOptions options = null)
    {
        var bufferWriter = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bufferWriter))
            element.WriteTo(writer);
        var result = JsonSerializer.Deserialize<T>(bufferWriter.WrittenSpan, options);
        return result;
    }

    // Typed helper: materialize versioned events into typed domain events as an async stream.
    public static async IAsyncEnumerable<T> LoadEventStreamAsync<T>(this IEventStore store, string streamName, long version, JsonSerializerOptions? options = null)
    {
        await foreach (var v in store.LoadEventStreamAsync(streamName, version))
            yield return v.EventAs<T>(options);
    }

    // Typed helper: materialize versioned events into typed domain events as an async stream (DaprEventStore overload).
    public static async IAsyncEnumerable<T> LoadEventStreamAsync<T>(this DaprEventStore store, string streamName, long version)
    {
        await foreach (var v in store.LoadEventStreamAsync(streamName, version))
            yield return v.EventAs<T>(store.DaprClient.JsonSerializerOptions);
    }

    // Typed helper: read merged streams for decision and convert events to T.
    public static async Task<(IReadOnlyList<T> Events, DcbAppendCondition Condition)> ReadStreamsForDecisionAsync<T>(this IEventStore store, params string[] streamNames)
    {
        return await store.ReadStreamsForDecisionAsync<T>(streamNames, options: null);
    }

    // Typed helper: read merged streams for decision and convert events to T (with custom serializer options).
    public static async Task<(IReadOnlyList<T> Events, DcbAppendCondition Condition)> ReadStreamsForDecisionAsync<T>(this IEventStore store, string[] streamNames, JsonSerializerOptions? options)
    {
        var (events, condition) = await store.ReadStreamsForDecisionAsync(streamNames);
        var converted = events.Select(e => e.EventAs<T>(options)).ToList().AsReadOnly();
        return (converted, condition);
    }

    // Typed helper: read merged streams for decision and convert events to T (DaprEventStore overload).
    public static async Task<(IReadOnlyList<T> Events, DcbAppendCondition Condition)> ReadStreamsForDecisionAsync<T>(this DaprEventStore store, params string[] streamNames)
    {
        var (events, condition) = await store.ReadStreamsForDecisionAsync(streamNames);
        var converted = events.Select(e => e.EventAs<T>(store.DaprClient.JsonSerializerOptions)).ToList().AsReadOnly();
        return (converted, condition);
    }
}
