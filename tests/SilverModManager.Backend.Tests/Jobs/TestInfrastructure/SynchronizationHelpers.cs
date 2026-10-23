using FluentAssertions;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Backend.Tests.Jobs.TestInfrastructure;

public static class SynchronizationHelpers
{
    public static async Task WaitForJobState(IJob job, JobStatus expectedStatus, TimeSpan timeout)
    {
        var startTime = DateTime.UtcNow;
        while (job.Status != expectedStatus && DateTime.UtcNow - startTime < timeout)
            await Task.Delay(10);

        job.Status.Should().Be(expectedStatus, $"Job should have reached {expectedStatus} within {timeout}");
    }
}
