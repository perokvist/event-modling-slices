using System;
using Xunit;

namespace DaprEventStore.IntegrationTests.Infrastructure;

public static class TestInfra
{
    public static void RequireRedisOrSkip()
    {
        var v = Environment.GetEnvironmentVariable("TEST_REDIS");
        if (string.IsNullOrEmpty(v) || !v.Equals("true", StringComparison.OrdinalIgnoreCase))
            // Tests will run; previously skipped when TEST_REDIS not set. Adjust environment to skip if needed.
            return;
    }
}
