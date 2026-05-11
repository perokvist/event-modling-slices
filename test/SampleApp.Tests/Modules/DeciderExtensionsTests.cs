using SampleApp.Modules;

namespace SampleApp.Tests.Modules;

public record TestCommand(Guid Id, string Payload);
public record TestEvent(Guid TargetId, string Payload);
public record TestState(Guid Id);

public class DeciderExtensionsTests
{
    [Fact]
    public void GetGuardStreams_Uses_SelectGuardStreams()
    {
        var decider = new Decider<TestCommand, TestEvent, TestState>(
            InitialState: new TestState(Guid.NewGuid()),
            Decide: (cmd, state) => new TestEvent[0],
            Evolve: (state, evt) => state,
            IsTerminal: s => false,
            SelectGuardStreams: (cmd, state) => new[] { $"stream-{cmd.Id}" }
        );

        var guards = decider.GetGuardStreams(new TestCommand(Guid.NewGuid(), "p"), decider.InitialState);
        Assert.Single(guards);
        Assert.StartsWith("stream-", guards[0]);
    }

    [Fact]
    public void RouteEvents_Uses_RouteEvent()
    {
        var decider = new Decider<TestCommand, TestEvent, TestState>(
            InitialState: new TestState(Guid.NewGuid()),
            Decide: (cmd, state) => [new TestEvent(cmd.Id, "x")],
            Evolve: (state, evt) => state,
            IsTerminal: s => false,
            RouteEvent: evt => [($"stream-{evt.TargetId}", evt)]
        );

        var decided = new[] { new TestEvent(Guid.NewGuid(), "a"), new TestEvent(Guid.NewGuid(), "b") };
        var routed = decider.RouteEvents(decided).ToList();

        Assert.Equal(2, routed.Count);
        foreach (var group in routed)
        {
            Assert.StartsWith("stream-", group.StreamName);
            Assert.Single(group.Events);
        }
    }

    [Fact]
    public void RouteEvents_DefaultStream_Fallback()
    {
        var decider = new Decider<TestCommand, TestEvent, TestState>(
            InitialState: new TestState(Guid.NewGuid()),
            Decide: (cmd, state) => new TestEvent[0],
            Evolve: (state, evt) => state,
            IsTerminal: s => false
        );

        var events = new[] { new TestEvent(Guid.NewGuid(), "p") };
        var mapped = decider.RouteEvents(events, evt => "default-stream").Single();
        Assert.Equal("default-stream", mapped.StreamName);
        Assert.Single(mapped.Events);
    }
}
