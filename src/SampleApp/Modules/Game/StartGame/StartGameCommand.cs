using SampleApp.Modules.Game;

namespace SampleApp.Modules.Game.StartGame;

public record StartGameCommand(
    Guid Id, string Name) : GameCommand(Id);
