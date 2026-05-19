using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using SampleApp.Modules.Game;
using SampleApp.Modules.Game.MakeMove;
using SampleApp.Modules.Game.StartGame;
using Xunit.Abstractions;

namespace SampleApp.Tests.Modules.Game.MakeMove;

[Trait("Category", "Dapr")]
public class MakeMoveApiTests_Dapr(DaprFixture fixture, ITestOutputHelper outputHelper) : IClassFixture<DaprFixture>
{
    public static TheoryData<Guid> GameIds => new()
    {
        Guid.Parse("00000000-0000-0000-0000-000000000003")
    };

    [Theory]
    [MemberData(nameof(GameIds))]
    public Task MakeMove_publishes_MoveMade_via_dapr(Guid gameId) =>
        fixture
        .WithLogging(outputHelper.WriteLine)
        .WithHistory(new GameDecider(), new GameStarted(gameId, "test game"))
        .Test(async (client, events) =>
        {
            var playerId = Guid.NewGuid();

            var capture = events
                .Timeout(TimeSpan.FromSeconds(5))
                .OfType<MoveMade>()
                .FirstAsync()
                .ToTask();

            var response = await client.PostAsJsonAsync(
                $"/games/{gameId}/moves",
                new MakeMoveCommand(gameId, playerId, Move.Rock));

            response.EnsureSuccessStatusCode();

            var received = await capture;
            Assert.Equal(playerId, received.PlayerId);
            Assert.Equal(Move.Rock, received.Move);
        });
}
