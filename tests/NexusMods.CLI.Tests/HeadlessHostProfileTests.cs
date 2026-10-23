using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.Secrets;
using NexusMods.Games.AdvancedInstaller;
using NexusMods.DataModel;
using NexusMods.App;
using NexusMods.Games.FileHashes;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.Secrets;
using NexusMods.Sdk.Settings;
using NexusMods.Secrets.Overlay;
using NexusMods.SingleProcess;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.CLI.Tests;

public class HeadlessHostProfileTests
{
    // The in-memory secret backend keeps these hosts off the session bus and the user's keyring.
    private static StartupMode Mode(params string[] args)
    {
        var mode = StartupMode.Parse(args);
        mode.SecretBackend = SecretBackendChoice.Memory;
        return mode;
    }

    private static IHost BuildHost(StartupMode mode, IFileSystem? fileSystem = null)
    {
        const KnownPath baseKnownPath = KnownPath.EntryDirectory;
        var baseDirectory = $"HeadlessHostProfileTests-{Guid.NewGuid()}";

        return new HostBuilder()
            .ConfigureServices(services => services
                .AddApp(addStandardGameLocators: false, startupMode: mode)
                .Validate()
                .AddSingleton(fileSystem ?? NexusMods.Paths.FileSystem.Shared)
                .OverrideSettingsForTests<FileHashesServiceSettings>(settings => settings with { UseInMemoryLocalBaseline = true, })
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
    public void HeadlessGraphRegistersNoSecretServiceStore()
    {
        Describe(Mode("as-main")).Where(d => d.ServiceType == typeof(ISecretServiceStore)).Should().BeEmpty();
    }

    [Fact]
    public void HeadlessGraphKeepsSecretsInMemory()
    {
        using var host = BuildHost(Mode("as-main"));

        host.Services.GetRequiredService<ISecretStore>()
            .Should().BeOfType<OverlaySecretStore>()
            .Which.Inner.Should().BeOfType<InMemorySecretStore>();
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
        host.Services.GetRequiredService<IGuidedInstaller>().SupportsUserInteraction.Should().BeFalse("a headless host has nobody to ask");
        host.Services.GetService<CliServer>().Should().BeNull();
        host.Services.GetService<SyncFile>().Should().NotBeNull();
        host.Services.GetService<ISettingsManager>()!.Get<CliSettings>().Should().NotBeNull();
    }

    [Theory]
    [InlineData("as-main", false)]
    [InlineData("run", false)]
    [InlineData("as-main-ui", true)]
    public void OnlyTheGuiProfileRegistersTheBaselineStartupPass(string input, bool registered)
    {
        var hostedServices = Describe(Mode(input.Split(' ')))
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType?.Name);

        if (registered) hostedServices.Should().Contain("LocalBaselineStartupPass");
        else hostedServices.Should().NotContain("LocalBaselineStartupPass");
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

    // The settings backend reads $XDG_DATA_HOME/SilverModManager/Configs, so every host gets a data
    // directory of its own and a test's settings file never reaches another test or the real one.
    private static (IFileSystem FileSystem, string DataHome) IsolatedDataHome(string? settingsJson)
    {
        var dataHome = Directory.CreateTempSubdirectory("headless-host-data-").FullName;
        if (settingsJson is not null)
        {
            var configs = Path.Combine(dataHome, ApplicationConstants.DataDirectoryName, "Configs");
            Directory.CreateDirectory(configs);
            File.WriteAllText(Path.Combine(configs, $"{FileHashesServiceSettings.SettingsId}.json"), settingsJson);
        }

        var fileSystem = NexusMods.Paths.FileSystem.Shared.CreateOverlayFileSystem(
            new Dictionary<AbsolutePath, AbsolutePath>(),
            new Dictionary<KnownPath, AbsolutePath>
            {
                { KnownPath.XDG_DATA_HOME, NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(dataHome) },
            });
        return (fileSystem, dataHome);
    }

    private static bool SkipUpdateCheckOf(string input, string? savedSettings)
    {
        var (fileSystem, dataHome) = IsolatedDataHome(savedSettings);
        try
        {
            using var host = BuildHost(Mode(input), fileSystem);
            return host.Services.GetRequiredService<ISettingsManager>().Get<FileHashesServiceSettings>().SkipUpdateCheck;
        }
        finally
        {
            Directory.Delete(dataHome, recursive: true);
        }
    }

    // What an install that predates the flipped default has on disk.
    private const string SavedDownloadChoice = """
        {
          "SkipUpdateCheck": false,
          "HashDatabaseUpdateInterval": "00:30:00",
          "ReleaseFilePath": "game-hashes-release.json"
        }
        """;

    [Theory]
    [InlineData("as-main")]
    [InlineData("run")]
    public void HeadlessProfilesNeverDownloadTheHashDatabase(string input)
    {
        SkipUpdateCheckOf(input, SavedDownloadChoice).Should().BeTrue("a game launch must not download even when the user opted in");
    }

    [Fact]
    public void GuiKeepsTheSavedDownloadChoice()
    {
        SkipUpdateCheckOf("as-main-ui", SavedDownloadChoice).Should().BeFalse();
    }

    [Theory]
    [InlineData("as-main")]
    [InlineData("run")]
    [InlineData("as-main-ui")]
    public void FreshInstallSkipsTheDownloadInEveryProfile(string input)
    {
        SkipUpdateCheckOf(input, savedSettings: null).Should().BeTrue();
    }

    [Theory]
    [InlineData("as-main", true)]
    [InlineData("as-main-ui", true)]
    [InlineData("run", false)]
    public void OnlyAHeadlessRunSkipsCompactingTheDatabaseAfterAMove(string input, bool compacts)
    {
        using var host = BuildHost(Mode(input.Split(' ')));

        host.Services.GetRequiredService<SecretStoreOptions>().CompactAfterMove.Should().Be(compacts);
    }

    [Fact]
    public void TheSecretsFileOnTheModeReachesTheStoreOptions()
    {
        var path = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/run/secrets.env");
        var mode = Mode("as-main", "secrets-status");
        mode.SecretsFile = path;

        using var host = BuildHost(mode);

        host.Services.GetRequiredService<SecretStoreOptions>().SecretsFile.Should().Be(path);
    }

    private async Task<(int ExitCode, string Output)> RunVerb(IHost host, params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await host.Services.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        var lines = renderer.Logs.OfType<Text>().Select(text =>
        {
            var line = text.Template;
            for (var i = 0; i < text.Arguments.Length; i++) line = line.Replace($"{{{i}}}", text.Arguments[i]);
            return line;
        });
        return (exitCode, string.Concat(lines));
    }

    [Fact]
    public async Task SecretsStatusPrintsKeyNamesAndTheKeysAFilePinsButNeverAValue()
    {
        var scratch = Directory.CreateTempSubdirectory("secrets-status-verb-").FullName;
        try
        {
            var file = Path.Combine(scratch, "secrets.env");
            await File.WriteAllTextAsync(file, "NEXUS_API_KEY=file-value-that-must-not-print\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var mode = Mode("as-main", "secrets-status", "--secrets-file", file);
            mode.SecretsFile = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(file);
            using var host = BuildHost(mode);
            await host.Services.GetRequiredService<ISecretStore>().SetAsync(new SecretKey("fromsoft.ersc-password"), "stored-value-that-must-not-print", TestContext.Current.CancellationToken);

            var (exitCode, output) = await RunVerb(host, "secrets-status", "--secrets-file", file);

            exitCode.Should().Be(0);
            output.Should().Contain("Backend: Memory").And.Contain("fromsoft.ersc-password");
            output.Should().Contain($"nexus.api-key (--secrets-file {file})");
            output.Should().NotContain("must-not-print");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task NexusWhoAmIWithNoCredentialSaysNotSignedInAndExitsOne()
    {
        using var host = BuildHost(Mode("as-main", "nexus-whoami"));

        var (exitCode, output) = await RunVerb(host, "nexus-whoami");

        exitCode.Should().Be(1);
        output.Should().Contain("Not signed in");
    }
}
