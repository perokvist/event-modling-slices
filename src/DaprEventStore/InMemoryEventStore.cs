using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data;

namespace DaprEventStore;

// Simple in-memory implementation for unit tests. Not intended for production.
public class InMemoryEventStore(Func<VersionedEvent[], Task>? pub = null) : IEventStore
{
    private readonly object @lock = new();
    private readonly Dictionary<string, List<VersionedEvent>> events = new();
    private readonly Dictionary<string, StreamHead> heads = new();

    public string StoreName { get; set; } = "inmemory";

    public async Task<(IReadOnlyList<VersionedEvent> Events, DcbAppendCondition Condition)> ReadStreamsForDecisionAsync(params string[] streamNames)
    {
        var observed = new List<(string StreamName, long Version)>();
        var allEvents = new List<VersionedEvent>();

        lock (@lock)
        {
            foreach (var name in streamNames)
            {
                heads.TryGetValue(name, out var head);
                head ??= new StreamHead(0);
                observed.Add((name, head.Version));

                if (events.TryGetValue(name, out var list))
                {
                    allEvents.AddRange(list);
                }
            }
        }

        var sorted = allEvents
            .OrderBy(e => e.OccuredAt)
            .ThenBy(e => e.StreamName)
            .ThenBy(e => e.Version)
            .Select((e, i) => e with { Position = i + 1 })
            .ToList();

        return (sorted.AsReadOnly(), new DcbAppendCondition(observed.AsReadOnly()));
    }

    public async IAsyncEnumerable<VersionedEvent> LoadEventStreamAsync(string streamName, long version)
    {
        List<VersionedEvent>? list = null;
        lock (@lock)
        {
            if (events.TryGetValue(streamName, out var l))
                list = l.Where(e => e.Version > version).OrderBy(e => e.Version).ToList();
        }

        if (list == null)
            yield break;

        foreach (var e in list)
            yield return e;
    }

    public async Task<long> AppendToStreamAsync(string streamName, long version, params EventData[] events)
    {
        VersionedEvent[] versioned;
        long newVersion;
        lock (@lock)
        {
            heads.TryGetValue(streamName, out var head);
            head ??= new StreamHead(0);

            if (head.Version != version)
                throw new DBConcurrencyException($"wrong version - expected {version} but was {head.Version}");

            newVersion = head.Version + events.Length;
            versioned = events.Select((e, i) => new VersionedEvent(e.EventId, e.EventName, streamName, e.Data, (e.OccuredAt == default) ? DateTime.UtcNow : e.OccuredAt, head.Version + (i + 1))).ToArray();

            if (!this.events.ContainsKey(streamName))
                this.events[streamName] = new List<VersionedEvent>();

            this.events[streamName].AddRange(versioned);
            heads[streamName] = new StreamHead(newVersion);
        }

        if (pub != null) await pub.Invoke(versioned);
        return newVersion;
    }

    public async Task<long> AppendToStreamAsync(string streamName, params EventData[] events)
    {
        VersionedEvent[] versioned;
        long newVersion;
        lock (@lock)
        {
            heads.TryGetValue(streamName, out var head);
            head ??= new StreamHead(0);

            newVersion = head.Version + events.Length;
            versioned = events.Select((e, i) => new VersionedEvent(e.EventId, e.EventName, streamName, e.Data, (e.OccuredAt == default) ? DateTime.UtcNow : e.OccuredAt, head.Version + (i + 1))).ToArray();

            if (!this.events.ContainsKey(streamName))
                this.events[streamName] = new List<VersionedEvent>();

            this.events[streamName].AddRange(versioned);
            heads[streamName] = new StreamHead(newVersion);
        }

        if (pub != null) await pub.Invoke(versioned);
        return newVersion;
    }

    public async Task<long> AppendToStreamAsync(string streamName, DcbAppendCondition condition, params EventData[] events)
    {
        VersionedEvent[] versioned;
        long newVersion;
        lock (@lock)
        {
            // validate guard streams
            foreach (var (observedName, expectedVersion) in condition.ObservedStreams)
            {
                if (!heads.TryGetValue(observedName, out var h))
                    h = new StreamHead(0);

                if (h.Version != expectedVersion)
                    throw new DBConcurrencyException($"DCB guard stream '{observedName}' has version {h.Version} but condition expected {expectedVersion}");
            }

            heads.TryGetValue(streamName, out var head);
            head ??= new StreamHead(0);

            newVersion = head.Version + events.Length;
            versioned = events.Select((e, i) => new VersionedEvent(e.EventId, e.EventName, streamName, e.Data, (e.OccuredAt == default) ? DateTime.UtcNow : e.OccuredAt, head.Version + (i + 1))).ToArray();

            if (!this.events.ContainsKey(streamName))
                this.events[streamName] = new List<VersionedEvent>();

            this.events[streamName].AddRange(versioned);
            heads[streamName] = new StreamHead(newVersion);
        }

        if (pub != null) await pub.Invoke(versioned);
        return newVersion;
    }

    public async Task<Dictionary<string, long>> AppendToStreamsAsync(params (string StreamName, long ExpectedVersion, EventData[] Events)[] streams)
    {
        var allVersioned = new List<VersionedEvent>();
        Dictionary<string, long> results;
        lock (@lock)
        {
            // check expectations
            foreach (var s in streams)
            {
                heads.TryGetValue(s.StreamName, out var head);
                head ??= new StreamHead(0);
                if (s.ExpectedVersion >= 0 && head.Version != s.ExpectedVersion)
                    throw new DBConcurrencyException($"Stream '{s.StreamName}' has version {head.Version} but expected {s.ExpectedVersion}");
            }

            results = new Dictionary<string, long>();

            foreach (var s in streams)
            {
                var head = heads.GetValueOrDefault(s.StreamName, new StreamHead(0));
                if (s.Events == null || s.Events.Length == 0)
                {
                    results[s.StreamName] = head.Version;
                    continue;
                }

                var newVersion = head.Version + s.Events.Length;
                var versioned = s.Events.Select((e, i) => new VersionedEvent(e.EventId, e.EventName, s.StreamName, e.Data, (e.OccuredAt == default) ? DateTime.UtcNow : e.OccuredAt, head.Version + (i + 1))).ToArray();

                if (!events.ContainsKey(s.StreamName))
                    events[s.StreamName] = new List<VersionedEvent>();

                events[s.StreamName].AddRange(versioned);
                heads[s.StreamName] = new StreamHead(newVersion);
                results[s.StreamName] = newVersion;
                allVersioned.AddRange(versioned);
            }
        }

        if (pub != null && allVersioned.Count > 0) await pub.Invoke(allVersioned.ToArray());
        return results;
    }

    public Task<StreamHead?> GetStreamMetaData(string streamName)
    {
        lock (@lock)
        {
            heads.TryGetValue(streamName, out var head);
            return Task.FromResult<StreamHead?>(head);
        }
    }
}
