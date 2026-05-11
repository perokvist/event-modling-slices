namespace DaprEventStore;

public interface IEventStore
{
    string StoreName { get; set; }

    Task<(IReadOnlyList<VersionedEvent> Events, DcbAppendCondition Condition)> ReadStreamsForDecisionAsync(params string[] streamNames);

    IAsyncEnumerable<VersionedEvent> LoadEventStreamAsync(string streamName, long version);

    Task<long> AppendToStreamAsync(string streamName, long version, params EventData[] events);

    Task<long> AppendToStreamAsync(string streamName, params EventData[] events);

    Task<long> AppendToStreamAsync(string streamName, DcbAppendCondition condition, params EventData[] events);

    Task<Dictionary<string, long>> AppendToStreamsAsync(params (string StreamName, long ExpectedVersion, EventData[] Events)[] streams);

    Task<StreamHead?> GetStreamMetaData(string streamName);
}
