namespace SampleApp.Modules.Game.SendGameStartedEmail;

public static class SendGameStartedEmailGateway
{
    public static GameStartedEmailSent Execute(SendGameStartedEmailCommand command)
        => new(command.GameId);
}
