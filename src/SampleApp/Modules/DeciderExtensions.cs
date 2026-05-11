namespace SampleApp.Modules;

public static class DeciderExtensions
{
    // Returns guard stream names. If Decider.SelectGuardStreams is not provided,
    // the caller may pass a defaultStream selector; otherwise an empty array is returned.
    public static string[] GetGuardStreams<TCommand, TEvent, TState>(
        this Decider<TCommand, TEvent, TState> decider,
        TCommand command,
        TState state,
        Func<TCommand, string>? defaultStream = null)
    {
        if (decider.SelectGuardStreams != null)
            return decider.SelectGuardStreams(command, state);

        if (defaultStream != null)
            return new[] { defaultStream(command) };

        return [];
    }

    // Routes decided events into stream -> events mapping.
    // If Decider.RouteEvent is not provided, a defaultStream selector must be supplied.
    public static IEnumerable<(string StreamName, TEvent[] Events)> RouteEvents<TCommand, TEvent, TState>(
        this Decider<TCommand, TEvent, TState> decider,
        TEvent[] events,
        Func<TEvent, string>? defaultStream = null)
    {
        var map = new Dictionary<string, List<TEvent>>();

        if (decider.RouteEvent != null)
        {
            foreach (var e in events)
            {
                foreach (var routed in decider.RouteEvent(e))
                {
                    if (!map.TryGetValue(routed.StreamName, out var list))
                    {
                        list = new List<TEvent>();
                        map[routed.StreamName] = list;
                    }

                    // Attempt to cast the boxed event to the expected TEvent.
                    if (routed.Event is TEvent te)
                        list.Add(te);
                    else
                        throw new InvalidCastException("Routed event type does not match decider event type parameter.");
                }
            }
        }
        else if (defaultStream != null)
        {
            foreach (var e in events)
            {
                var s = defaultStream(e);
                if (!map.TryGetValue(s, out var list))
                {
                    list = new List<TEvent>();
                    map[s] = list;
                }
                list.Add(e);
            }
        }
        else
        {
            throw new InvalidOperationException("No routing defined on decider and no defaultStream provided.");
        }

        return map.Select(kvp => (kvp.Key, kvp.Value.ToArray()));
    }
}
