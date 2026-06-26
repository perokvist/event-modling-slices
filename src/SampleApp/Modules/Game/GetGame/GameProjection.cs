using SampleApp.Modules.Game.MakeMove;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Modules.Game.GetGame;

public class GameProjection
{
    private readonly Dictionary<Guid, GameView> store = [];

    public void Apply(GameStarted @event)
        => ProjectionFlow.Apply<Guid, GameView, GameStarted>(
            store,
            @event.GameId,
            @event,
            static (_, started) => new GameView(
                GameId: started.GameId,
                Name: started.Name,
                IsStarted: true,
                Moves: []));

    public void Apply(MoveMade @event)
        => ProjectionFlow.Apply<Guid, GameView, MoveMade>(
            store,
            @event.GameId,
            @event,
            static (state, moveMade) =>
            {
                if (state is null)
                    return null;

                return state with
                {
                    Moves = [.. state.Moves, new GameMoveView(moveMade.PlayerId, moveMade.Move)]
                };
            });

    public GameView? Get(Guid id)
        => store.GetValueOrDefault(id);
}
