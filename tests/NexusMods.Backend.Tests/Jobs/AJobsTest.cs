using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusMods.Backend;
using NexusMods.Backend.Jobs;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;

namespace NexusMods.Jobs.Tests;

public class AJobsTest : IAsyncLifetime
{
    private readonly IHost _host;

    public AJobsTest()
    {
        _host = new HostBuilder()
            .ConfigureServices(services =>
                {
                    const KnownPath baseKnownPath = KnownPath.EntryDirectory;
                    var baseDirectory = $"NexusMods.Examples.Tests-{Guid.NewGuid()}";
                    var prefix = FileSystem.Shared.GetKnownPath(baseKnownPath).Combine(baseDirectory);

                    services
                        .AddFileSystem()
                        .AddSingleton<TemporaryFileManager>(_ => new TemporaryFileManager(FileSystem.Shared, prefix))
                        .AddJobMonitor()
                        .AddSingleton<HttpClient>();
                }
            ).Build();
    }

    public async ValueTask InitializeAsync()
    {
        await _host.StartAsync(TestContext.Current.CancellationToken);
        ServiceProvider = _host.Services;
        JobMonitor = ServiceProvider.GetRequiredService<IJobMonitor>();
    }

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        return ValueTask.CompletedTask;
    }

    public IJobMonitor JobMonitor { get; private set; }

    public IServiceProvider ServiceProvider { get; private set; }
}
