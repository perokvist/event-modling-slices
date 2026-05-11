using System.Runtime.CompilerServices;

namespace DaprEventStore;

/// <summary>
/// Extensions for loading events via the Dapr query API.
/// REQUIRES a query-capable state store — Redis with RedisSearch + RedisJSON modules
/// (e.g. the redis/redis-stack-server Docker image), MongoDB, or PostgreSQL with
/// the queryIndexes metadata configured on the component.
/// NOT compatible with the in-memory state store.
/// </summary>
public static class QueryableExtensions
{
    /// <summary>
    /// Queries events across streams using the Dapr state query API. Returns events in
    /// ascending OccuredAt order (sorted client-side after all pages are fetched).
    /// Position on each returned event is the 1-based index in the result set
    /// (computed on read, not persisted).
    /// <para>
    /// Optionally filter by <paramref name="streamName"/> and/or <paramref name="eventName"/>.
    /// Omit both to load all events in the store.
    /// </para>
    /// <para>
    /// Automatically pages through results using the continuation token returned by Dapr.
    /// </para>
    /// </summary>
    /// <param name="store">The event store. Must be configured with a query-capable state store.</param>
    /// <param name="streamName">Optional: restrict results to a single stream.</param>
    /// <param name="eventName">Optional: restrict results to a specific event type.</param>
    /// <param name="pageSize">Number of results per page (default 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async IAsyncEnumerable<VersionedEvent> QueryEventsAsync(
        this DaprEventStore store,
        string? streamName = null,
        string? eventName = null,
        int pageSize = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filter = BuildFilter(streamName, eventName);
        var all    = new List<VersionedEvent>();

        // Collect all pages first — sorting must be done client-side because the Cosmos DB
        // v2 emulator (vnext-preview / PostgreSQL backend) does not support ORDER BY on the
        // cosmos_variant type used for document fields.
        string? token = null;
        do
        {
            var query = BuildQuery(filter, pageSize, token);
            // Do NOT forward partition key metadata to QueryStateAsync — the query API searches
            // across the whole store index, not a per-partition subset.
            var response = await store.DaprClient.QueryStateAsync<VersionedEvent>(
                store.StoreName, query, cancellationToken: cancellationToken);

            foreach (var item in response.Results)
                if (item.Data is not null)
                    all.Add(item.Data);

            token = response.Token;
        } while (!string.IsNullOrEmpty(token));

        // Sort by OccuredAt ascending; use (StreamName, Version) as a deterministic tiebreak.
        int position = 0;
        foreach (var e in all.OrderBy(e => e.OccuredAt).ThenBy(e => e.StreamName).ThenBy(e => e.Version))
            yield return e with { Position = ++position };
    }

    private static string BuildFilter(string? streamName, string? eventName)
    {
        var conditions = new List<string>();

        if (streamName is not null)
            conditions.Add($"{{\"EQ\":{{\"streamName\":\"{streamName}\"}}}}");

        if (eventName is not null)
            conditions.Add($"{{\"EQ\":{{\"eventName\":\"{eventName}\"}}}}");

        return conditions.Count switch
        {
            0 => string.Empty,
            1 => $"\"filter\":{conditions[0]},",
            _ => $"\"filter\":{{\"AND\":[{string.Join(",", conditions)}]}},"
        };
    }

    private static string BuildQuery(string filterClause, int pageSize, string? token)
    {
        var tokenPart = string.IsNullOrEmpty(token) ? string.Empty : $",\"token\":\"{token}\"";
        return $"{{{filterClause}\"page\":{{\"limit\":{pageSize}{tokenPart}}}}}";
    }
}
