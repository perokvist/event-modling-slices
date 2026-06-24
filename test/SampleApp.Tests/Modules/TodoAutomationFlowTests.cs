using SampleApp.Modules;

namespace SampleApp.Tests.Modules;

public class TodoAutomationFlowTests
{
    [Fact]
    public async Task ApplyAsync_persists_evolved_state_and_dispatches_on_trigger()
    {
        var stateStore = new InMemoryStateStore();
        var dispatched = new List<Command>();

        await TodoAutomationFlow.ApplyAsync<TestState, TriggerEvent>(
            stateStore: stateStore,
            stateId: "state",
            projection: Projection,
            @event: new NonTriggerEvent(Guid.NewGuid(), 2),
            execute: Execute,
            dispatch: command =>
            {
                dispatched.Add(command);
                return Task.FromResult(Result.Success());
            });

        await TodoAutomationFlow.ApplyAsync<TestState, TriggerEvent>(
            stateStore: stateStore,
            stateId: "state",
            projection: Projection,
            @event: new TriggerEvent(Guid.NewGuid(), 3),
            execute: Execute,
            dispatch: command =>
            {
                dispatched.Add(command);
                return Task.FromResult(Result.Success());
            });

        var saved = await stateStore.GetAsync<TestState>("state");
        Assert.NotNull(saved);
        Assert.Equal(5, saved.Counter);
        Assert.Single(dispatched);
        Assert.IsType<TestCommand>(dispatched[0]);
    }

    [Fact]
    public async Task ApplyAsync_does_not_save_when_state_is_unchanged()
    {
        var stateStore = new InMemoryStateStore();

        await TodoAutomationFlow.ApplyAsync<TestState, TriggerEvent>(
            stateStore: stateStore,
            stateId: "state",
            projection: Projection,
            @event: new IgnoredEvent(Guid.NewGuid()),
            execute: Execute,
            dispatch: _ => Task.FromResult(Result.Success()));

        var saved = await stateStore.GetAsync<TestState>("state");
        Assert.Null(saved);
    }

    private static TestState Evolve(TestState state, Event @event)
        => @event switch
        {
            NonTriggerEvent e => state with { Counter = state.Counter + e.Amount },
            TriggerEvent e => state with { Counter = state.Counter + e.Amount },
            _ => state
        };

    private static readonly Projection<Event, TestState> Projection = new(
        InitialState: new TestState(0),
        Evolve: Evolve);

    private static Command? Execute(TestState state, TriggerEvent trigger)
        => state.Counter > 0 ? new TestCommand(Guid.NewGuid(), trigger.Amount) : null;

    private record TestState(int Counter);
    private record NonTriggerEvent(Guid EventId, int Amount) : Event(EventId);
    private record TriggerEvent(Guid EventId, int Amount) : Event(EventId);
    private record IgnoredEvent(Guid EventId) : Event(EventId);
    private record TestCommand(Guid Id, int Amount) : Command(Id);
}
