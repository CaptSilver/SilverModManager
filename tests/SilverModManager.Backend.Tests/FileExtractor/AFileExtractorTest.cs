using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SilverModManager.CrossPlatform;
using NexusMods.Paths;
using SilverModManager.Sdk.FileExtractor;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Backend.Tests.FileExtractor;

public class AFileExtractorTest : IAsyncLifetime
{
    private readonly IHost _host;

    public AFileExtractorTest()
    {
        _host = new HostBuilder()
            .ConfigureServices(services =>
                {
                    var baseDirectory = $"SilverModManager.Examples.Tests-{Guid.NewGuid()}";
                    var prefix = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(TestScratch.Combine(baseDirectory));

                    services
                        .AddJobMonitor()
                        .AddSingleton<TimeProvider>(_ => TimeProvider.System)
                        .AddFileSystem()
                        // Every test builds its own host, and disposing the manager deletes its folder: sharing one
                        // folder lets a finishing test delete the files of another that is still running.
                        .AddSingleton<TemporaryFileManager>(_ => new TemporaryFileManager(NexusMods.Paths.FileSystem.Shared, prefix))
                        .AddSettingsManager()
                        .AddFileExtractors()
                        .AddOSInterop()
                        .AddRuntimeDependencies()
                        .RemoveRuntimeDependencyChecker()
                        .AddSettings<LoggingSettings>();
                }
            ).Build();
    }

    public async ValueTask InitializeAsync()
    {
        await _host.StartAsync(TestContext.Current.CancellationToken);
        ServiceProvider = _host.Services;
        JobMonitor = ServiceProvider.GetRequiredService<IJobMonitor>();
        TemporaryFileManager = ServiceProvider.GetRequiredService<TemporaryFileManager>();
        FileExtractor = ServiceProvider.GetRequiredService<IFileExtractor>();
        FileSystem = ServiceProvider.GetRequiredService<IFileSystem>();
    }

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        return ValueTask.CompletedTask;
    }

    public IFileSystem FileSystem { get; set; }

    public IFileExtractor FileExtractor { get; private set; } = null!;
    public TemporaryFileManager TemporaryFileManager { get; private set; } = null!;

    public IJobMonitor JobMonitor { get; private set; } = null!;

    public IServiceProvider ServiceProvider { get; private set; } = null!;
}
