using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using SampleApp.Modules.Game;
using SampleApp.Modules.Game.MakeMove;
using SampleApp.Modules.Game.StartGame;
using Xunit.Abstractions;

namespace SampleApp.Tests.Modules.Game.MakeMove;

public class MakeMoveApiTests(Fixture fixture, ITestOutputHelper outputHelper) : IClassFixture<Fixture>
{
    [Fact]
    public Task MakeMove_succeeds_when_game_is_started() =>
        fixture
        .WithLogging(outputHelper.WriteLine)
        .WithHistory(new GameDecider(), new GameStarted(Guid.Parse("00000000-0000-0000-0000-000000000001"), "test game"))
        .Test(async client =>
        {
            var gameId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var response = await client.PostAsJsonAsync(
                $"/games/{gameId}/moves",
                new MakeMoveCommand(gameId, Guid.NewGuid(), Move.Rock));

            response.EnsureSuccessStatusCode();
        });

    public static TheoryData<Guid> GameIds =>
        [Guid.Parse("00000000-0000-0000-0000-000000000002")];

    [Theory]
    [MemberData(nameof(GameIds))]
    public Task MakeMove_publishes_MoveMade(Guid gameId) =>
        fixture
        .WithLogging(outputHelper.WriteLine)
        .WithHistory(new GameDecider(), new GameStarted(gameId, "test game"))
        .Test(async (client, events) =>
        {
            var playerId = Guid.NewGuid();

            var capture = events
                .Timeout(TimeSpan.FromMilliseconds(2500))
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
