using Dapr.Client;
using DaprEventStore;

namespace SampleApp.Modules;

public static partial class ApplicationService
{
    public static Task Execute(
        this DaprClient dapr,
        string stateStoreName,
        Command command,
        Decider decider)
            => dapr.Execute(stateStoreName, command.Id.ToString(), decider.InitialState with { Id = command.Id }, state =>
            {
                var events = decider.Decide(command, state);
                var newState = events.Aggregate(state, decider.Evolve);
                return (newState, events);
            });


    public static async Task Execute<TState>(
        this DaprClient dapr,
        string stateStoreName,
        string stateKey,
        TState defaultState,
        Func<TState, (TState, Event[])> f)
    {
        var state = await dapr.GetStateAsync<TState>(stateStoreName, stateKey);

        var currentState = state ?? defaultState;
        var (newState, events) = f(currentState);

        await dapr.SaveWithOutboxProjection(stateStoreName, stateKey, newState, events);
    }

    /// <summary>
    /// Uses a Dapr EventStore
    /// </summary>
    public static async Task Execute<TState>(
        this DaprClient dapr,
        string eventStateStoreName,
        Command command,
        Decider decider)
    {
        var eventStore = new DaprEventStore.DaprEventStore(dapr)
        {
            StoreName = eventStateStoreName,
        }.PartitionPerStream();

        var streamName = $"{typeof(TState).Name}-{command.Id}"; //TODO make configurable

        // Use typed helper to read merged stream(s) for decision and obtain a DCB condition.
        var (historyEvents, condition) = await eventStore.ReadStreamsForDecisionAsync<Event>(streamName);

        var currentState = historyEvents.Aggregate(decider.InitialState, decider.Evolve);

        var events = decider.Decide(command, currentState);

        var eventData = events
                .Select(x => EventData.Create(eventName: x.GetType().Name, data: x))
                .ToArray();

        // Use DCB-aware append passing the condition returned by the read.
        await eventStore.AppendToStreamAsync(streamName, condition, eventData);
    }

    public static async Task Execute<TCommand, TEvent, TState>(
           this IEventStore store,
           TCommand command,
           Decider<TCommand, TEvent, TState> decider,
           Func<TCommand, string>? defaultGuardStream = null,
           Func<TEvent, string>? defaultRouteStream = null)
    {
        // Determine guard streams
        var guards = decider.GetGuardStreams(command, decider.InitialState, defaultGuardStream);

        // Read merged events and get DCB condition
        var (versionedEvents, condition) = await store.ReadStreamsForDecisionAsync(guards);

        // Convert to typed events
        var history = versionedEvents.Select(e => e.EventAs<TEvent>()).ToList();

        // Compute current state
        var currentState = history.Aggregate(decider.InitialState, decider.Evolve);

        // Decide
        var decided = decider.Decide(command, currentState);

        // Route events to streams
        var routed = decider.RouteEvents(decided, defaultRouteStream).ToList();

        if (routed.Count == 0)
            return;

        if (routed.Count == 1)
        {
            var single = routed[0];
            var eventData = single.Events.Select(e => DaprEventStore.EventData.Create(e.GetType().Name, e)).ToArray();

            // If the target stream was among observed guards, use DCB append
            var observed = condition.ObservedStreams.FirstOrDefault(s => s.StreamName == single.StreamName);
            if (observed != default)
            {
                await store.AppendToStreamAsync(single.StreamName, condition, eventData);
                return;
            }

            // else append with optimistic concurrency (match observed head if present)
            var meta = await store.GetStreamMetaData(single.StreamName);
            var version = meta?.Version ?? 0;
            await store.AppendToStreamAsync(single.StreamName, version, eventData);
            return;
        }

        // Multi-stream atomic append: compute expected versions from condition when available
        var entries = new List<(string StreamName, long ExpectedVersion, EventData[] Events)>();
        foreach (var g in routed)
        {
            var expected = condition.ObservedStreams.FirstOrDefault(s => s.StreamName == g.StreamName);
            var expVersion = expected != default ? expected.Version : -1;
            var eventData = g.Events.Select(e => DaprEventStore.EventData.Create(e.GetType().Name, e)).ToArray();
            entries.Add((g.StreamName, expVersion, eventData));
        }

        await store.AppendToStreamsAsync(entries.ToArray());
    }

    public static Task ExecuteStateless<TCommand, TEvent>(
        this IEventStore store,
        TCommand command,
        Func<TCommand, TEvent> gateway,
        Func<TEvent, Task> publish)
        where TCommand : Command
        where TEvent : Event
    {
        _ = store;
        var @event = gateway(command);
        return publish(@event);
    }

    public static async Task Execute<TState>(
       this DaprClient dapr,
       string eventStateStoreName,
       Command command,
       Decider decider, 
       (string StreamName, Type EventType)[] streamInfo) //TODO need type to group streams on
    {
        var eventStore = new DaprEventStore.DaprEventStore(dapr)
        {
            StoreName = eventStateStoreName,
        }.PartitionPerStream();

        var streamMeta = await Task.WhenAll(streamInfo.Select(async x =>  (streamInfo: x, (await eventStore.LoadFoo(x.StreamName)))));

        var history = eventStore
            .LoadEventStreams([.. streamMeta.Select(x => x.streamInfo.StreamName)])
            .OrderByDescending(x => x.OccuredAt)
            .Select(x => x.EventAs<Event>());

        var currentState = await history.AggregateAsync(decider.InitialState, decider.Evolve);

        var events = decider.Decide(command, currentState);

        var foo = streamMeta.SelectMany(s => events
                .Where(x => x.GetType().IsAssignableTo(s.streamInfo.EventType))
                .Select((x, i) => (meta: s, EventData.Create(eventName: x.GetType().Name, data: x)))
                .ToArray());

        foreach (var f in foo)
        {
            //TODO this in single transaction
            await eventStore.AppendToStreamAsync(f.meta.streamInfo.StreamName, f.meta.Item2.version, f.Item2);
        }
    }

    public static async Task<(DaprEventStore.StreamHead? meta,long version)> LoadFoo(this DaprEventStore.DaprEventStore eventStore, string streamName)
    {
        var meta = await eventStore.GetStreamMetaData(streamName); //TODO fix
        var version = meta?.Version ?? 0;
        return (meta, version);
    }

    public static IAsyncEnumerable<DaprEventStore.VersionedEvent> LoadEventStreams(this DaprEventStore.DaprEventStore eventStore, params string[] streamNames)
     => MergeSequentially(streamNames.Select(s => eventStore.LoadEventStreamAsync(s, 0)).ToArray());

    static async IAsyncEnumerable<T> MergeSequentially<T>(params IAsyncEnumerable<T>[] sources)
    {
        foreach (var source in sources)
        {
            await foreach (var item in source)
            {
                yield return item;
            }
        }
    }


}
