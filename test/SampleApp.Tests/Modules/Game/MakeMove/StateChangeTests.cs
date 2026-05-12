using SampleApp.Modules.Game;
using SampleApp.Modules.Game.MakeMove;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Tests.Modules.Game.MakeMove;

public class StateChangeTests
{
    [Fact]
    public void MakeMove_produces_MoveMade_when_game_is_started()
    {
        // Given — game has been started
        var gameId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        GameEvent[] history = [new GameStarted(gameId, "test game")];

        var decider = new GameDecider();
        var state = history.Aggregate(decider.InitialState, decider.Evolve);

        // When — player makes a move
        var events = decider.Decide(new MakeMoveCommand(gameId, playerId, Move.Rock), state);

        // Then — exactly one MoveMade event with correct fields
        Assert.Collection(events, e =>
        {
            var ev = Assert.IsType<MoveMade>(e);
            Assert.Equal(gameId, ev.GameId);
            Assert.Equal(playerId, ev.PlayerId);
            Assert.Equal(Move.Rock, ev.Move);
        });
    }

    [Fact]
    public void MakeMove_is_rejected_when_game_is_not_started()
    {
        // Given — no prior events (game not started)
        var gameId = Guid.NewGuid();
        GameEvent[] history = [];

        var decider = new GameDecider();
        var state = history.Aggregate(decider.InitialState, decider.Evolve);

        // When — player attempts to make a move
        var events = decider.Decide(new MakeMoveCommand(gameId, Guid.NewGuid(), Move.Scissors), state);

        // Then — command rejected, no events produced
        Assert.Empty(events);
    }
}
