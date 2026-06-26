using SampleApp.Modules.Game.GetGame;
using SampleApp.Modules.Game;
using SampleApp.Modules.Game.MakeMove;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Tests.Modules.Game.GetGame;

public class GameProjectionTests
{
    [Fact]
    public void GameStarted_creates_view()
    {
        var projection = new GameProjection();
        var gameId = Guid.NewGuid();

        projection.Apply(new GameStarted(gameId, "Test Game"));

        var view = projection.Get(gameId);
        Assert.NotNull(view);
        Assert.Equal(gameId, view.GameId);
        Assert.Equal("Test Game", view.Name);
        Assert.True(view.IsStarted);
        Assert.Empty(view.Moves);
    }

    [Fact]
    public void MoveMade_appends_moves_in_order()
    {
        var projection = new GameProjection();
        var gameId = Guid.NewGuid();
        var player1 = Guid.NewGuid();
        var player2 = Guid.NewGuid();

        projection.Apply(new GameStarted(gameId, "Test Game"));
        projection.Apply(new MoveMade(gameId, player1, Move.Rock));
        projection.Apply(new MoveMade(gameId, player2, Move.Paper));

        var view = projection.Get(gameId);
        Assert.NotNull(view);
        Assert.Equal(2, view.Moves.Count);
        Assert.Equal(new GameMoveView(player1, Move.Rock), view.Moves[0]);
        Assert.Equal(new GameMoveView(player2, Move.Paper), view.Moves[1]);
    }

    [Fact]
    public void Returns_null_for_unknown_id()
    {
        var projection = new GameProjection();
        Assert.Null(projection.Get(Guid.NewGuid()));
    }
}
