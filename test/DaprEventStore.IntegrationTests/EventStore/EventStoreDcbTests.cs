using Dapr.Client;
using System.Data;

using DaprEventStore.IntegrationTests.Infrastructure;

namespace DaprEventStore.IntegrationTests.EventStore;

/// <summary>
/// Integration tests for DCB (Dynamic Consistency Boundary) append semantics.
/// All tests require PartitionAllStream so that every stream head lives in the same
/// Dapr partition and can participate in one atomic transaction.
/// Uses the shared in-memory DaprSidecarFixture — no outbox or pub/sub needed.
/// </summary>
[Collection("DaprSidecar")]
public class EventStoreDcbTests(DaprSidecarFixture fixture)
{
    private const string StoreName = "statestore";

    private DaprEventStore CreateStore() =>
        new DaprEventStore(new DaprClientBuilder()
                .UseHttpEndpoint($"http://localhost:{DaprSidecarFixture.DaprHttpPort}")
                .UseGrpcEndpoint($"http://localhost:{DaprSidecarFixture.DaprGrpcPort}")
                .Build())
            { StoreName = StoreName }
            .PartitionAllStream();

    [Fact]
    public async Task DcbAppend_HappyPath_WritesToTargetAndLeavesGuardStreamUnchanged()
    {
        var store = CreateStore();
        var courseStream = $"course-{Guid.NewGuid()}";
        var studentStream = $"student-{Guid.NewGuid()}";

        // Seed both streams with initial events.
        await store.AppendToStreamAsync(courseStream, EventData.Create("CourseCreated", new { Title = "Dapr 101" }));
        await store.AppendToStreamAsync(studentStream, EventData.Create("StudentEnrolled", new { Name = "Alice" }));

        // Read both streams to build a DCB condition.
        var (events, condition) = await store.ReadStreamsForDecisionAsync(courseStream, studentStream);

        Assert.Equal(2, events.Count);
        Assert.NotNull(condition);

        // Append to the course stream using the multi-stream condition.
        var newVersion = await store.AppendToStreamAsync(
            courseStream,
            condition,
            EventData.Create("CourseClosed", new { Reason = "Completed" }));

        Assert.Equal(2, newVersion);

        // Verify the new event is in the course stream.
        var courseEvents = await store.LoadEventStreamAsync(courseStream, 0).ToListAsync();
        Assert.Equal(2, courseEvents.Count);
        Assert.Equal("CourseCreated", courseEvents[0].EventName);
        Assert.Equal("CourseClosed", courseEvents[1].EventName);

        // Guard stream (student) is untouched — still has only its original event.
        var studentEvents = await store.LoadEventStreamAsync(studentStream, 0).ToListAsync();
        Assert.Single(studentEvents);
        Assert.Equal("StudentEnrolled", studentEvents[0].EventName);
    }

    [Fact]
    public async Task DcbAppend_GuardStreamModifiedConcurrently_ThrowsDBConcurrencyException()
    {
        var store = CreateStore();
        var courseStream = $"course-{Guid.NewGuid()}";
        var studentStream = $"student-{Guid.NewGuid()}";

        // Seed both streams.
        await store.AppendToStreamAsync(courseStream, EventData.Create("CourseCreated", new { Title = "Dapr 201" }));
        await store.AppendToStreamAsync(studentStream, EventData.Create("StudentEnrolled", new { Name = "Bob" }));

        // Build DCB condition that observes both streams.
        var (_, condition) = await store.ReadStreamsForDecisionAsync(courseStream, studentStream);

        // Simulate a concurrent write to the guard stream — bumps its version.
        await store.AppendToStreamAsync(studentStream, EventData.Create("StudentWithdrawn", new { Name = "Bob" }));

        // Attempting to append with a stale condition should be rejected.
        await Assert.ThrowsAsync<DBConcurrencyException>(async () =>
            await store.AppendToStreamAsync(
                courseStream,
                condition,
                EventData.Create("CourseClosed", new { Reason = "Completed" })));

        // Course stream should be unchanged.
        var courseEvents = await store.LoadEventStreamAsync(courseStream, 0).ToListAsync();
        Assert.Single(courseEvents);
        Assert.Equal("CourseCreated", courseEvents[0].EventName);
    }

    [Fact]
    public async Task ReadStreamsForDecision_EventsMergedAndSortedByOccuredAt_PositionAssigned()
    {
        var store = CreateStore();
        var streamA = $"stream-a-{Guid.NewGuid()}";
        var streamB = $"stream-b-{Guid.NewGuid()}";

        // Interleave timestamps so stream B's first event is older than stream A's second.
        var t0 = DateTime.UtcNow;
        var t1 = t0.AddMilliseconds(10);
        var t2 = t0.AddMilliseconds(20);
        var t3 = t0.AddMilliseconds(30);

        await store.AppendToStreamAsync(streamA,
            new EventData(Guid.NewGuid().ToString(), "A1", new { }, t0),
            new EventData(Guid.NewGuid().ToString(), "A2", new { }, t2));

        await store.AppendToStreamAsync(streamB,
            new EventData(Guid.NewGuid().ToString(), "B1", new { }, t1),
            new EventData(Guid.NewGuid().ToString(), "B2", new { }, t3));

        var (events, _) = await store.ReadStreamsForDecisionAsync(streamA, streamB);

        Assert.Equal(4, events.Count);

        // Events should be interleaved by OccuredAt: A1, B1, A2, B2
        Assert.Equal("A1", events[0].EventName);
        Assert.Equal("B1", events[1].EventName);
        Assert.Equal("A2", events[2].EventName);
        Assert.Equal("B2", events[3].EventName);

        // Position is 1-based and sequential.
        Assert.Equal(1, events[0].Position);
        Assert.Equal(2, events[1].Position);
        Assert.Equal(3, events[2].Position);
        Assert.Equal(4, events[3].Position);
    }
}
