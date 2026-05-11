namespace SampleApp.Modules.Sample.StartGame;

public record GameState(Guid GameId = default, string Name = "", bool IsStarted = false) : State(GameId);
