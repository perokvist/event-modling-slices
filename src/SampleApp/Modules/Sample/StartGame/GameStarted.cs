namespace SampleApp.Modules.Sample.StartGame;

public record GameStarted(Guid GameId, string Name) : GameEvent(GameId);
