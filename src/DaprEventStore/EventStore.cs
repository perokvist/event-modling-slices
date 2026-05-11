using System.Data;

namespace DaprEventStore;

public class DaprEventStore(Dapr.Client.DaprClient client) : IEventStore //, ILogger<DaprEventStore> logger)
{
    public static class Naming
    {
        public static string StreamKey(string streamName, long version) => $"{streamName}|{version}";

        public static string StreamHead(string streamName) => $"{streamName}|head";
    }

    public string StoreName { get; set; } = "statestore";

    public string? OutboxTopic { get; set; }

    /// <summary>The underlying DaprClient. Useful for advanced operations such as QueryStateAsync.</summary>
    public Dapr.Client.DaprClient DaprClient => client;

    /// <summary>
    /// Controls how events are published via the Dapr outbox when OutboxTopic is set.
    /// Default: AutoPublish — all transaction entries are published individually (head + each event).
    /// </summary>
    public OutboxMode OutboxMode { get; set; } = OutboxMode.AutoPublish;

    public Func<string, Dictionary<string, string>> MetaProvider { get; set; } = streamName => [];

    public Task<long> AppendToStreamAsync(string streamName, long version, params EventData[] events)
    => AppendToStreamAsync(
        streamName,
        Concurrency.Match(version),
        events);

    public async Task<long> AppendToStreamAsync(string streamName, params EventData[] events)
        => await AppendToStreamAsync(
            streamName,
            Concurrency.Ignore(),
            events);

    /// <summary>
    /// Reads events from one or more streams and returns an opaque DCB condition that captures
    /// the observed version of each stream. Pass the condition to AppendToStreamAsync to assert
    /// that none of the observed streams have changed since this read.
    /// Events from all streams are merged and sorted by OccuredAt (then StreamName, then Version
    /// as a stable tiebreak). Each event's Position is set to its 1-based index in the merged
    /// result — Position is computed on read only and not persisted to the state store.
    /// Requires PartitionAllStream.
    /// </summary>
    public async Task<(IReadOnlyList<VersionedEvent> Events, DcbAppendCondition Condition)>
        ReadStreamsForDecisionAsync(params string[] streamNames)
    {
        var observed = new List<(string StreamName, long Version)>();
        var allEvents = new List<VersionedEvent>();

        foreach (var name in streamNames)
        {
            var meta = MetaProvider(name);
            var head = await client.GetStateAsync<StreamHead>(StoreName, Naming.StreamHead(name), metadata: meta);
            head ??= new StreamHead();
            observed.Add((name, head.Version));

            await foreach (var e in client.LoadAsyncBulkEventsAsync(StoreName, name, 0, meta, head))
                allEvents.Add(e);
        }

        var sorted = allEvents
            .OrderBy(e => e.OccuredAt)
            .ThenBy(e => e.StreamName)
            .ThenBy(e => e.Version)
            .Select((e, i) => e with { Position = i + 1 })
            .ToList();

        return (sorted.AsReadOnly(), new DcbAppendCondition(observed.AsReadOnly()));
    }

    public async Task<long> AppendToStreamAsync(string streamName, Action<StreamHead> concurrencyGuard, params EventData[] events)
    {
        var streamHeadKey = Naming.StreamHead(streamName);
        var meta = MetaProvider(streamName);
        var (head, headetag) = await client.GetStateAndETagAsync<StreamHead>(StoreName, streamHeadKey, metadata: meta);

        if (head == null)
            head = new StreamHead();

        if (events.Length == 0)
            return head.Version;

        concurrencyGuard(head);

        var newVersion = head.Version + events.Length;
        var versionedEvents = events
            .Select((e, i) => new VersionedEvent(
                e.EventId,
                e.EventName,
                streamName,
                e.Data,
                (e.OccuredAt == default(DateTime)) ? DateTime.UtcNow : e.OccuredAt,
                head.Version + (i + 1)))
            .ToArray();

        head = new StreamHead(newVersion);

        await client.EventsTransactionAsync(StoreName, streamName, streamHeadKey, head, headetag, meta, versionedEvents, OutboxTopic, OutboxMode);

        return newVersion;
    }

    public async Task<StreamHead?> GetStreamMetaData(string streamName)
    {
        var meta = MetaProvider(streamName);

        var head = await client.GetStateEntryAsync<StreamHead>(StoreName, Naming.StreamHead(streamName), metadata: meta);

        return head.Value;
    }

    public async IAsyncEnumerable<VersionedEvent> LoadEventStreamAsync(string streamName, long version)
    {
        var head = await GetStreamMetaData(streamName);

        if (head == null)
            yield break;

        var meta = MetaProvider(streamName);

        await foreach (var e in client.LoadAsyncBulkEventsAsync(StoreName, streamName, version, meta, head))
            yield return e;
        yield break;
    }

    /// <summary>
    /// Appends events to a stream with multi-stream consistency guarantees.
    /// Re-reads all guard stream heads immediately before the transaction to get fresh etags,
    /// validates that versions still match the condition, then builds an atomic transaction
    /// that includes guard-stream no-op writes to detect concurrent modification.
    /// Requires PartitionAllStream — all guarded streams must share one Dapr partition.
    /// </summary>
    public async Task<long> AppendToStreamAsync(string streamName, DcbAppendCondition condition, params EventData[] events)
    {
        var meta = MetaProvider(streamName);
        var streamHeadKey = Naming.StreamHead(streamName);

        // Re-read all guard streams (observed but not written) for fresh etags + version validation.
        var guardEntries = new List<(string HeadKey, StreamHead Head, string Etag, Dictionary<string, string> Meta)>();
        foreach (var (observedName, expectedVersion) in condition.ObservedStreams)
        {
            if (observedName == streamName)
                continue; // target is handled below

            var guardMeta = MetaProvider(observedName);
            var guardHeadKey = Naming.StreamHead(observedName);
            var (guardHead, guardEtag) = await client.GetStateAndETagAsync<StreamHead>(StoreName, guardHeadKey, metadata: guardMeta);
            guardHead ??= new StreamHead();

            if (guardHead.Version != expectedVersion)
                throw new DBConcurrencyException(
                    $"DCB guard stream '{observedName}' has version {guardHead.Version} but condition expected {expectedVersion}");

            guardEntries.Add((guardHeadKey, guardHead, guardEtag, guardMeta));
        }

        // Re-read target stream head for fresh etag + current version.
        var (head, headEtag) = await client.GetStateAndETagAsync<StreamHead>(StoreName, streamHeadKey, metadata: meta);
        head ??= new StreamHead();

        // If the target stream was also observed, validate its version too.
        var targetObserved = condition.ObservedStreams.FirstOrDefault(s => s.StreamName == streamName);
        if (targetObserved != default && head.Version != targetObserved.Version)
            throw new DBConcurrencyException(
                $"DCB target stream '{streamName}' has version {head.Version} but condition expected {targetObserved.Version}");

        if (events.Length == 0)
            return head.Version;

        var newVersion = head.Version + events.Length;
        var versionedEvents = events
            .Select((e, i) => new VersionedEvent(
                e.EventId,
                e.EventName,
                streamName,
                e.Data,
                (e.OccuredAt == default(DateTime)) ? DateTime.UtcNow : e.OccuredAt,
                head.Version + (i + 1)))
            .ToArray();

        var newHead = new StreamHead(newVersion);

        await client.ExecuteStateTransactionAsync(
            storeName: StoreName,
            operations: versionedEvents.ToStateTransactionsDcb(streamName, streamHeadKey, newHead, headEtag, meta, guardEntries, client.JsonSerializerOptions),
            metadata: meta);

        return newVersion;
    }

    /// <summary>
    /// Appends events to multiple streams in a single atomic Dapr state transaction.
    /// Re-reads each target stream head and its etag, validates expected versions when provided,
    /// constructs VersionedEvent entries (setting OccuredAt to UtcNow when default), builds
    /// StreamHead updates and event state operations for all streams, verifies all streams
    /// share the same partitionKey in their meta, then executes one combined state transaction.
    /// Returns a mapping of streamName -> newVersion.
    /// </summary>
    public async Task<Dictionary<string, long>> AppendToStreamsAsync(params (string StreamName, long ExpectedVersion, EventData[] Events)[] streams)
    {
        if (streams == null || streams.Length == 0)
            return new Dictionary<string, long>();

        // Use metadata from the first stream as the transaction metadata; ensure partitionKey consistency.
        var firstMeta = MetaProvider(streams[0].StreamName);
        firstMeta.TryGetValue("partitionKey", out var partitionKey);

        var streamInfos = new List<(string StreamName, StreamHead Head, string Etag, Dictionary<string, string> Meta, VersionedEvent[] Events, StreamHead NewHead, long NewVersion)>();

        foreach (var s in streams)
        {
            var meta = MetaProvider(s.StreamName);
            if (!meta.TryGetValue("partitionKey", out var pk) || pk != partitionKey)
                throw new ArgumentException("All streams must share the same partitionKey in meta");

            var headKey = Naming.StreamHead(s.StreamName);
            var (head, etag) = await client.GetStateAndETagAsync<StreamHead>(StoreName, headKey, metadata: meta);
            head ??= new StreamHead();

            if (s.ExpectedVersion >= 0 && head.Version != s.ExpectedVersion)
                throw new DBConcurrencyException($"Stream '{s.StreamName}' has version {head.Version} but expected {s.ExpectedVersion}");

            if (s.Events == null || s.Events.Length == 0)
            {
                streamInfos.Add((s.StreamName, head, etag, meta, Array.Empty<VersionedEvent>(), head, head.Version));
                continue;
            }

            var newVersion = head.Version + s.Events.Length;
            var versionedEvents = s.Events
                .Select((e, i) => new VersionedEvent(
                    e.EventId,
                    e.EventName,
                    s.StreamName,
                    e.Data,
                    (e.OccuredAt == default(DateTime)) ? DateTime.UtcNow : e.OccuredAt,
                    head.Version + (i + 1)))
                .ToArray();

            var newHead = new StreamHead(newVersion);

            streamInfos.Add((s.StreamName, head, etag, meta, versionedEvents, newHead, newVersion));
        }

        // Build combined operations for the transaction by concatenating per-stream operations.
        var allOperations = streamInfos
            .SelectMany(si => si.Events.ToStateTransactionsDcb(
                si.StreamName,
                Naming.StreamHead(si.StreamName),
                si.NewHead,
                si.Etag,
                si.Meta,
                new List<(string HeadKey, StreamHead Head, string Etag, Dictionary<string, string> Meta)>(),
                client.JsonSerializerOptions))
            .ToList();

        await client.ExecuteStateTransactionAsync(
            storeName: StoreName,
            operations: allOperations,
            metadata: firstMeta);

        return streamInfos.ToDictionary(si => si.StreamName, si => si.NewVersion);
    }

    

    public class Concurrency
    {
        public static Action<StreamHead> Match(long version) => head =>
        {
            if (head.Version != version)
                throw new DBConcurrencyException($"wrong version - expected {version} but was {head.Version}");
        };

        public static Action<StreamHead> Ignore() => _ => { };
    }
}

public record StreamHead(long Version = 0);

public record EventData(string EventId, string EventName, object Data, DateTime OccuredAt)
{
    public static EventData Create(string eventName, object data)
        => new(Guid.NewGuid().ToString(), eventName, data, default);
}

public record VersionedEvent(string EventId, string EventName, string StreamName, object Data, DateTime OccuredAt, long Version, long Position = 0);

/// <summary>
/// Opaque token capturing the observed stream names and versions at decision time.
/// Created by ReadStreamsForDecisionAsync; passed to AppendToStreamAsync.
/// Etags are NOT stored — they are re-read fresh at append time to minimise the conflict window.
/// </summary>
public sealed class DcbAppendCondition
{
    public IReadOnlyList<(string StreamName, long Version)> ObservedStreams { get; }
    public DcbAppendCondition(IReadOnlyList<(string StreamName, long Version)> streams) => ObservedStreams = streams;
}

public enum OutboxMode
{
    /// <summary>
    /// Default. All entries in the transaction are auto-published individually by Dapr
    /// (no outbox.projection metadata). Produces N+1 messages per append: one per event
    /// plus one stream-head update. Consumers filter by message shape.
    /// Works with any pub/sub backend. Note: in-memory pub/sub deadlocks for multi-event appends.
    /// </summary>
    AutoPublish,

    /// <summary>
    /// Future. Publishes one array message per append (all events as a single payload).
    /// The stream-head entry is suppressed. Requires Dapr to fix outbox.projection:false
    /// suppression behaviour (currently false without a matching projection still auto-publishes).
    /// </summary>
    EventsArray,

    /// <summary>
    /// Future. Publishes one message per event individually, with the stream-head suppressed.
    /// Requires Dapr to fix the panic that occurs when a transaction contains multiple
    /// outbox.projection:true entries with different keys.
    /// </summary>
    PerEvent,
}

