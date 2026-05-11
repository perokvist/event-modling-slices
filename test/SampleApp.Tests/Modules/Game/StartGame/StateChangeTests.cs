using SampleApp.Modules.Game;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Tests.Modules.Game.StartGame;

public class StateChangeTests
{
    [Fact]
    public void Foo()
    {
        // Arrange
        var id = Guid.NewGuid();
        GameEvent[] history = [];

        var decider = new GameDecider();
        var state = history.Aggregate(decider.InitialState, decider.Evolve);

        // Act
        var events = decider.Decide(new StartGameCommand(id, "new game"), state);

        // Assert
        Assert.Collection(events, e =>
        {
            var ev = Assert.IsType<GameStarted>(e);
            Assert.Equal(id, ev.Id);
            Assert.Equal("new game", ev.Name);
        });
    }
}
