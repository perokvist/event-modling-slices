using DaprEventStore;
using SampleApp.Modules;
using SampleApp.Modules.Game;
using SampleApp.Modules.Game.GetGame;
using SampleApp.Modules.Game.EmailSender;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddDaprClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IEventStore>(sp =>
{
    var dapr = sp.GetRequiredService<Dapr.Client.DaprClient>();
    return new DaprEventStore.DaprEventStore(dapr)
    {
        StoreName = "statestore"
    }.PartitionAllStream();
});
builder.Services.AddModule<GameModule>();
builder.Services.AddSingleton<IStateStore, InMemoryStateStore>();
builder.Services.AddSingleton<GameProjection>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapSubscribeHandler(); // Dapr subscription discovery: GET /dapr/subscribe
app.MapDomainEvents();

app.UseModuleEndpoints<GameModule>(a => a
        .MapGroup("games")
        .WithTags("Sample")
        .WithDisplayName("Games"));

app.MapGet("/", () => "Hello World!");

app.Run();
