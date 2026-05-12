using SampleApp.Modules.Game.MakeMove;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Modules.Game;

public record GameDecider() : Decider<Command, Event, GameState>
    (InitialState: new GameState(),
        Decide: (command, state) => command switch // This could be replace for single use cases 
        {
            StartGameCommand c when !state.IsStarted => [new GameStarted(c.Id, c.Name)],
            MakeMoveCommand c when state.IsStarted => [new MoveMade(c.Id, c.PlayerId, c.Move)],
            _ => []
        },
        Evolve: (state, @event) => @event switch
        {
            GameStarted e => state with { GameId = e.GameId, Name = e.Name, IsStarted = true },
            MoveMade => state,
            _ => state
        },
        IsTerminal: state => false,
        SelectGuardStreams: (cmd, _) => [$"Game-{cmd.Id}"],
        RouteEvent: evt => evt switch
        {
            GameEvent e => [($"Game-{e.Id}", e)],
            _ => []
        }
    );

