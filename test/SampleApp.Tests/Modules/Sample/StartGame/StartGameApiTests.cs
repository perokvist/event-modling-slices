using SampleApp.Modules.Sample;
using SampleApp.Modules.Sample.StartGame;
using System.Net.Http.Json;
using Xunit.Abstractions;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;

namespace SampleApp.Tests.Modules.Sample.StartGame;

public class StartGameApiTests(Fixture fixture, ITestOutputHelper outputHelper) : IClassFixture<Fixture>
{
    [Fact]
    public Task StartGameIgnoresPreviousId() =>
        fixture
        .WithLogging(outputHelper.WriteLine)
        .WithHistory(new GameDecider(), new GameStarted(Guid.NewGuid(), "game name"))
        .Test(async client =>
        {
            var id = Guid.NewGuid();
            var response = await client.PostAsJsonAsync("/games", new StartGameCommand(id, "new game"));
            response.EnsureSuccessStatusCode();
        });

    [Fact]
    public Task StartGame() =>
       fixture
       .WithLogging(outputHelper.WriteLine)
       .Test(async (client, o) =>
       {
           // Arrange
           var id = Guid.NewGuid();

           var capture = o
                      .Timeout(TimeSpan.FromMilliseconds(2500))
                      .OfType<GameStarted>()
                      .FirstAsync()
                      .ToTask();

           // Act
           var response = await client.PostAsJsonAsync("/games", new StartGameCommand(id, "new game"));

           // Assert
           response.EnsureSuccessStatusCode();

           Assert.NotNull(await capture);
       });

}
