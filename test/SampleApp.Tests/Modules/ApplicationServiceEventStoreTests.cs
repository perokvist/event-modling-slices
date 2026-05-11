using DaprEventStore;
using SampleApp.Modules;

namespace SampleApp.Tests.Modules;

public record AppTestCmd(Guid Id, string Payload);
public record AppTestEvt(string Payload);
public record AppTestState(string Value);

public class ApplicationServiceEventStoreTests
{
    [Fact]
    public async Task Execute_SingleStream_DcbAppend()
    {
        var store = new InMemoryEventStore();
        var stream = "TestState-" + Guid.NewGuid();

        // seed history
        await store.AppendToStreamAsync(stream, EventData.Create("Init", new AppTestEvt("init")));

        var decider = new Decider<AppTestCmd, AppTestEvt, AppTestState>(
            InitialState: new AppTestState(""),
            Decide: (cmd, state) => [new AppTestEvt(cmd.Payload)],
            Evolve: (state, evt) => new AppTestState(evt.Payload),
            IsTerminal: s => false,
            SelectGuardStreams: (cmd, state) => new[] { stream },
            RouteEvent: evt => [(stream, evt)]
        );

        var cmd = new AppTestCmd(Guid.NewGuid(), "hello");
        await store.Execute(cmd, decider);

        var head = await store.GetStreamMetaData(stream);
        Assert.Equal(2, head?.Version);

        var loaded = await store.LoadEventStreamAsync(stream, 0).ToListAsync();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("hello", loaded.Last().EventName == "AppTestEvt" ? ((AppTestEvt)loaded.Last().Data).Payload : ((AppTestEvt)loaded.Last().Data).Payload);
    }

    [Fact]
    public async Task Execute_MultiStream_RoutesCorrectly()
    {
        var store = new InMemoryEventStore();
        var s1 = "s1-" + Guid.NewGuid();
        var s2 = "s2-" + Guid.NewGuid();

        var decider = new Decider<AppTestCmd, AppTestEvt, AppTestState>(
            InitialState: new AppTestState(""),
            Decide: (cmd, state) => [new AppTestEvt("a"), new AppTestEvt("b")],
            Evolve: (state, evt) => state,
            IsTerminal: s => false,
            SelectGuardStreams: (cmd, state) => [s1, s2],
            RouteEvent: (AppTestEvt evt) =>
            {
                var id = Guid.NewGuid();
                return [(evt.Payload.Contains("a") ? s1 : s2, evt)];
            }
        );

        var cmd = new AppTestCmd(Guid.NewGuid(), "p");
        await store.Execute(cmd, decider);

        var head1 = await store.GetStreamMetaData(s1);
        var head2 = await store.GetStreamMetaData(s2);

        Assert.Equal(1, head1?.Version);
        Assert.Equal(1, head2?.Version);
    }
}
