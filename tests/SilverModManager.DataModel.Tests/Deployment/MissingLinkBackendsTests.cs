using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Links.Backends;
using SilverModManager.Sdk.Native;

namespace SilverModManager.DataModel.Tests.Deployment;

/// <summary>
/// A build that does not know the link backends, opened on an install whose rows and files a build that did
/// know them left behind.
/// </summary>
public class MissingLinkBackendsTests(ITestOutputHelper helper) : AppliedGameTest<MissingLinkBackendsTests>(helper)
{
    private static readonly Type[] LinkBackends = [typeof(ReflinkDeploymentBackend), typeof(HardlinkDeploymentBackend), typeof(SymlinkDeploymentBackend)];

    private readonly WarningCapture _warnings = new();

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LibcLink(string existing, string newPath);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        services.AddSingleton<ILoggerProvider>(_warnings);
        base.AddServices(services);

        // The data model registers the link backends unconditionally, so the build being imitated is made by
        // taking them out again, descriptor for descriptor.
        services.RemoveAll(typeof(IDeploymentBackend));
        foreach (var type in LinkBackends) services.RemoveAll(type);
        services.AddSingleton<IDeploymentBackend>(sp => sp.GetRequiredService<CopyDeploymentBackend>());
        return services;
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AStaleRowNamingHardlinkOnABuildWithoutTheLinkBackendsIsRepairable()
    {
        ServiceProvider.GetServices<IDeploymentBackend>().Select(b => b.Id).Should().Equal(DeploymentBackendId.Copy);
        var loadout = await NewLoadoutWithMod();
        await Synchronizer.Synchronize(loadout);

        // What a hardlink deploy leaves: the deployed file shares its inode with a second name.
        var deployed = OnDisk(ModFile).ToString();
        LibcLink(deployed, deployed + ".other").Should().Be(0);
        using (var tx = Connection.BeginTransaction())
        {
            Plans.Write(tx, Connection.Db, MetadataId, GameRoot, DeploymentBackendId.Hardlink, chosen: false);
            await tx.Commit();
        }

        var changed = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, DeploymentBackendId.Copy, chosen: true);

        changed.Should().BeTrue();
        _warnings.Messages.Should().Contain(m => m.Contains("hardlink") && m.Contains("skipping the reset"));
        Plan.BackendFor(GameRoot).Should().Be(DeploymentBackendId.Copy);
        File.ReadAllText(deployed).Should().Be($"content of {ModFile}");
        File.ReadAllText(OnDisk(SecondModFile).ToString()).Should().Be($"content of {SecondModFile}");

        // The copy apply sees the linked file as a file with the right hash and has nothing to do about it.
        await Synchronizer.Synchronize(loadout);
        LibcStat.TryLStat(deployed, out var stat).Should().Be(0);
        stat.NLink.Should().Be(2, "the copy backend leaves a file that already has the right content alone");
        File.ReadAllText(deployed).Should().Be($"content of {ModFile}");
    }

    private sealed class WarningCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new Capture(_messages);

        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) sink.Enqueue(formatter(state, exception));
            }
        }
    }
}
