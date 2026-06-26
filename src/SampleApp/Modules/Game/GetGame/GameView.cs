namespace SampleApp.Modules.Game.GetGame;

public record GameView(
    Guid GameId,
    string Name,
    bool IsStarted,
    IReadOnlyList<GameMoveView> Moves) : ReadModel();
