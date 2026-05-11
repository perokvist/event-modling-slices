using DaprEventStore;
using SampleApp.Modules;
using SampleApp.Modules.Game;
using SampleApp.Modules.Sample.StartGame;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddDaprClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddOpenApi();
builder.Services.AddScoped<IEventStore>(sp =>
{
    var dapr = sp.GetRequiredService<Dapr.Client.DaprClient>();
    return new DaprEventStore.DaprEventStore(dapr)
    {
        StoreName = "statestore"
    }.PartitionAllStream();
});
builder.Services.AddModule<GameModule>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseModuleEndpoints<GameModule>(a => a
        .MapGroup("games")
        .WithTags("Sample")
        .WithDisplayName("Games"));

app.MapGet("/", () => "Hello World!");

app.Run();
