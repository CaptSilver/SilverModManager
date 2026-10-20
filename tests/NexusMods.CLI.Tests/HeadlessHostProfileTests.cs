using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Games.AdvancedInstaller;
using NexusMods.DataModel;
using NexusMods.App;
using NexusMods.Games.FileHashes;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.Sdk.Settings;
using NexusMods.SingleProcess;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.CLI.Tests;

public class HeadlessHostProfileTests
{
    private static StartupMode Mode(params string[] args) => StartupMode.Parse(args);

    private static IHost BuildHost(StartupMode mode)
    {
        const KnownPath baseKnownPath = KnownPath.EntryDirectory;
        var baseDirectory = $"HeadlessHostProfileTests-{Guid.NewGuid()}";

        return new HostBuilder()
            .ConfigureServices(services => services
                .AddApp(addStandardGameLocators: false, startupMode: mode)
                .Validate()
                .OverrideSettingsForTests<DataModelSettings>(settings => settings with
                {
                    UseInMemoryDataModel = true,
                    MnemonicDBPath = new ConfigurablePath(baseKnownPath, $"{baseDirectory}/MnemonicDB.rocksdb"),
                    ArchiveLocations = [new ConfigurablePath(baseKnownPath, $"{baseDirectory}/Archives")],
                })
                .ReplaceDesktopNotifier())
            .Build();
    }

    private static IServiceCollection Describe(StartupMode mode)
    {
        var services = new ServiceCollection();
        services.AddApp(addStandardGameLocators: false, startupMode: mode).Validate();
        return services;
    }

    [Theory]
    [InlineData("", HostProfile.Gui)]
    [InlineData("as-main-ui", HostProfile.Gui)]
    [InlineData("as-main", HostProfile.Headless)]
    [InlineData("as-main status", HostProfile.Headless)]
    [InlineData("run", HostProfile.Headless)]
    [InlineData("status", HostProfile.Slim)]
    public void ProfileFollowsStartupMode(string input, HostProfile expected)
    {
        var args = input.Length == 0 ? [] : input.Split(' ');
        NexusMods.App.Services.ProfileOf(Mode(args)).Should().Be(expected);
    }

    [Fact]
    public void DesignerModeWinsOverTheOtherFlags()
    {
        var mode = new StartupMode { RunAsMain = true, ShowUI = false, IsAvaloniaDesigner = true };
        NexusMods.App.Services.ProfileOf(mode).Should().Be(HostProfile.Designer);
    }

    [Fact]
    public void HeadlessGraphResolvesTheCommandLineAndLaunchSession()
    {
        using var host = BuildHost(Mode("as-main"));

        host.Services.GetRequiredService<CommandLineConfigurator>().Should().NotBeNull();
        host.Services.GetRequiredService<LaunchSession>().Should().NotBeNull();
    }

    [Fact]
    public void HeadlessGraphResolvesEveryServiceAVerbInjects()
    {
        using var host = BuildHost(Mode("as-main"));
        var verbs = host.Services.GetServices<VerbDefinition>().ToArray();
        verbs.Should().NotBeEmpty();

        var missing = new List<string>();
        foreach (var verb in verbs)
        {
            foreach (var option in verb.Options.Where(o => o.IsInjected))
            {
                // The configurator supplies these two per invocation.
                if (option.Type == typeof(IRenderer) || option.Type == typeof(CancellationToken)) continue;
                if (host.Services.GetService(option.Type) is null) missing.Add($"{verb.Name}: {option.Type}");
            }
        }

        missing.Should().BeEmpty();
    }

    [Fact]
    public void HeadlessGraphHasNoUiTypes()
    {
        var offenders = Describe(Mode("as-main"))
            .SelectMany(d => new[] { d.ServiceType, d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType, d.IsKeyedService ? d.KeyedImplementationInstance?.GetType() : d.ImplementationInstance?.GetType() })
            .Where(t => t is not null)
            .Select(t => t!)
            .Where(IsUiType)
            .Select(t => t.FullName)
            .Distinct()
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void GuiGraphStillHasUiTypes()
    {
        // Guards the scan above: if it matched nothing in the GUI graph it would pass vacuously.
        Describe(Mode("as-main-ui"))
            .SelectMany(d => new[] { d.ServiceType, d.ImplementationType })
            .Where(t => t is not null)
            .Should().Contain(t => IsUiType(t!));
    }

    private static bool IsUiType(Type type)
    {
        if (type.IsGenericType && type.GetGenericArguments().Any(IsUiType)) return true;
        var assembly = type.Assembly.GetName().Name ?? "";
        return assembly == "NexusMods.App.UI" || assembly.StartsWith("NexusMods.App.UI.", StringComparison.Ordinal)
            || assembly.StartsWith("Avalonia", StringComparison.Ordinal)
            || (type.Namespace ?? "").StartsWith("Avalonia", StringComparison.Ordinal);
    }

    [Fact]
    public void HeadlessGraphHasNoInstallerViewsOrNxmRegistration()
    {
        var services = Describe(Mode("as-main"));

        services.Should().NotContain(d => d.ServiceType == typeof(CliServer) || d.ImplementationType == typeof(CliServer));
        services.Should().NotContain(d => d.ImplementationType != null && d.ImplementationType.Name == "HandlerRegistration");
        services.Should().NotContain(d => d.ServiceType == typeof(IAdvancedInstallerHandler));

        using var host = BuildHost(Mode("as-main"));
        host.Services.GetRequiredService<IGuidedInstaller>().Should().BeOfType<NullGuidedInstaller>();
        host.Services.GetService<CliServer>().Should().BeNull();
        host.Services.GetService<SyncFile>().Should().NotBeNull();
        host.Services.GetService<ISettingsManager>()!.Get<CliSettings>().Should().NotBeNull();
    }

    [Fact]
    public void GuiGraphKeepsTheCliServerAndNxmRegistration()
    {
        var services = Describe(Mode("as-main-ui"));

        services.Should().Contain(d => d.ImplementationType == typeof(CliServer));
        services.Should().Contain(d => d.ImplementationType != null && d.ImplementationType.Name == "HandlerRegistration");
    }

    [Fact]
    public void GuiGraphRegistersOnlyTheWindowsApiKeyPrompt()
    {
        // The web API's non-interactive prompt is a TryAdd fallback; the window's prompt must be there before it.
        Describe(Mode("as-main-ui"))
            .Where(d => d.ServiceType == typeof(IApiKeyPrompt))
            .Select(d => d.ImplementationType?.Name)
            .Should().Equal("ApiKeyPrompt");
    }

    [Fact]
    public void HeadlessGraphFallsBackToTheNonInteractiveApiKeyPrompt()
    {
        Describe(Mode("as-main"))
            .Where(d => d.ServiceType == typeof(IApiKeyPrompt))
            .Select(d => d.ImplementationType?.Name)
            .Should().Equal("NoInteractiveApiKeyPrompt");
    }

    [Fact]
    public void RunUsesTheNoSignalHostLifetimeAndRegistersStopHandler()
    {
        using var host = BuildHost(Mode("run"));

        host.Services.GetRequiredService<IHostLifetime>().Should().BeOfType<NoSignalHostLifetime>();
        Describe(Mode("run")).Should().Contain(d => d.ServiceType == typeof(StopHandler));
    }

    [Fact]
    public void AsMainKeepsTheDefaultHostLifetime()
    {
        using var host = BuildHost(Mode("as-main"));

        host.Services.GetRequiredService<IHostLifetime>().Should().NotBeOfType<NoSignalHostLifetime>();
        Describe(Mode("as-main")).Should().NotContain(d => d.ServiceType == typeof(StopHandler));
    }

    [Fact]
    public void DesignerGraphHasNoSyncFile()
    {
        var mode = new StartupMode { RunAsMain = true, ShowUI = false, IsAvaloniaDesigner = true };

        Describe(mode).Should().NotContain(d => d.ServiceType == typeof(SyncFile));
    }

    [Theory]
    [InlineData("as-main", true)]
    [InlineData("run", true)]
    [InlineData("as-main-ui", false)]
    public void HashDatabaseUpdateCheckIsSkippedOnlyHeadless(string input, bool expected)
    {
        using var host = BuildHost(Mode(input));

        host.Services.GetRequiredService<ISettingsManager>().Get<FileHashesServiceSettings>().SkipUpdateCheck.Should().Be(expected);
    }
}
