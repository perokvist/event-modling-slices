namespace SampleApp.Modules.Sample.GetGame;

public record GetGameQuery(Guid Id) : Query<GameView>();

public record GameView() : ReadModel();

