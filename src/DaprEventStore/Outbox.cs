using Dapr.Client;

namespace DaprEventStore;

public static class Outbox
{
    /// <summary>
    /// Saves <paramref name="newState"/> and publishes <paramref name="events"/> via the Dapr
    /// outbox pattern in a single state transaction.  The state store must have
    /// <c>outboxPublishPubsub</c> and <c>outboxPublishTopic</c> configured.
    /// </summary>
    public static async Task SaveWithOutboxProjection<TState, TEvent>(
        this DaprClient dapr,
        string stateStoreName,
        string stateKey,
        TState newState,
        TEvent[] events)
    {
        var meta = new Dictionary<string, string> { { "contentType", "application/json" } };
        var suppressedMeta = new Dictionary<string, string>(meta) { ["outbox.projection"] = "false" };

        var stateOperation = new StateTransactionRequest(
            key: stateKey,
            value: System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(newState, dapr.JsonSerializerOptions),
            operationType: StateOperationType.Upsert,
            metadata: suppressedMeta);

        var projectionOperation = events.CreateOutboxProjection(stateKey, dapr.JsonSerializerOptions);

        await dapr.ExecuteStateTransactionAsync(stateStoreName,
            [stateOperation, projectionOperation], metadata: meta);
    }
}
