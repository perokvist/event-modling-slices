namespace SampleApp.Modules.Game.StartGame;

public record GameState(Guid GameId = default, string Name = "", bool IsStarted = false) : State(GameId);
