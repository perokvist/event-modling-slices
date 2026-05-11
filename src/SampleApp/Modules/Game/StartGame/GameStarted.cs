using SampleApp.Modules.Game;

namespace SampleApp.Modules.Game.StartGame;

public record GameStarted(Guid GameId, string Name) : GameEvent(GameId);
