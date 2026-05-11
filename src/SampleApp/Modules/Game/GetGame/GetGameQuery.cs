namespace SampleApp.Modules.Game.GetGame;

public record GetGameQuery(Guid Id) : Query<GameView>();

public record GameView() : ReadModel();

