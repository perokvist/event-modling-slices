using DaprEventStore;
using SampleApp.Modules;
using SampleApp.Modules.Game;
using SampleApp.Modules.Game.GetGame;
using SampleApp.Modules.Game.MakeMove;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Tests.Modules.Game.GetGame;

public class GameModuleGetGameTests
{
    [Fact]
    public async Task Query_returns_projected_game_view_after_events()
    {
        var module = new global::SampleApp.Modules.Game.GameModule(
            new InMemoryEventStore(),
            new InMemoryStateStore(),
            new GameProjection());
        var gameId = Guid.NewGuid();
        var playerId = Guid.NewGuid();

        await module.When(new GameStarted(gameId, "Test Game"));
        await module.When(new MoveMade(gameId, playerId, Move.Scissors));

        var view = await module.Query(new GetGameQuery(gameId));
        Assert.NotNull(view);
        Assert.Equal(gameId, view.GameId);
        Assert.Equal("Test Game", view.Name);
        Assert.True(view.IsStarted);
        Assert.Single(view.Moves);
        Assert.Equal(new GameMoveView(playerId, Move.Scissors), view.Moves[0]);
    }

    [Fact]
    public async Task Query_returns_null_for_unknown_game()
    {
        var module = new global::SampleApp.Modules.Game.GameModule(
            new InMemoryEventStore(),
            new InMemoryStateStore(),
            new GameProjection());

        var view = await module.Query(new GetGameQuery(Guid.NewGuid()));
        Assert.Null(view);
    }
}
