namespace SampleApp.Modules;

public record Decider<TCommand, TEvent, TState>(
    TState InitialState,
    Func<TCommand, TState, TEvent[]> Decide,
    Func<TState, TEvent, TState> Evolve,
    Func<TState, bool> IsTerminal,
    Func<TCommand, TState, string[]>? SelectGuardStreams = null,
    Func<TEvent, IEnumerable<(string StreamName, TEvent Event)>>? RouteEvent = null);

public record Decider(
    State InitialState,
    Func<Command, State, Event[]> Decide,
    Func<State, Event, State> Evolve,
    Func<State, bool> IsTerminal)
    : Decider<Command, Event, State>(InitialState, Decide, Evolve, IsTerminal);
