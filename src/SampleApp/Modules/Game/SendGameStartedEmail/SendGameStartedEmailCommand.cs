using SampleApp.Modules.Game;

namespace SampleApp.Modules.Game.SendGameStartedEmail;

public record SendGameStartedEmailCommand(Guid Id, Guid GameId, string GameName) : GameCommand(Id);

public record GameStartedEmailSent(Guid GameId) : GameEvent(GameId);
