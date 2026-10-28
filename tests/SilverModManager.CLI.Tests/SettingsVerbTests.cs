using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.Commandline;
using SilverModManager.Backend;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.SingleProcess;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod settings list|get|set</c> against a real settings manager holding a few made-up settings next to the
/// proxy setting.
/// </summary>
public class SettingsVerbTests
{
    public enum Speed { Fast, Slow, Warp }

    public record SampleSettings : ISettings
    {
        public string Label { get; init; } = "";
        public bool Enabled { get; init; }
        public int Count { get; init; } = 3;
        public long Big { get; init; }
        public double Ratio { get; init; } = 0.5;
        public int Level { get; init; } = 1;
        public Speed Speed { get; init; }
        public string[] Tags { get; init; } = [];

        public static string SettingsId => "SilverModManager.Tests.SampleSettings";

        public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder) => settingsBuilder
            .ConfigureProperty(x => x.Label, Options<string>("Label"), new TextContainerOptions())
            .ConfigureProperty(x => x.Enabled, Options<bool>("Enabled"), new BooleanContainerOptions())
            .ConfigureProperty(x => x.Count, Options<int>("Count", validation: value => value >= 0
                    ? ValidationResult.CreateSuccessful()
                    : ValidationResult.CreateFailed("Count can't be negative.")),
                null)
            .ConfigureProperty(x => x.Big, Options<long>("Big"), null)
            .ConfigureProperty(x => x.Ratio, Options<double>("Ratio"), new SliderContainerOptions(0, 1, 0.05))
            .ConfigureProperty(x => x.Level, Options<int>("Level"), SingleValueMultipleChoiceContainerOptions.Create<int>(
                EqualityComparer<int>.Default, [1, 2, 3, 4, 5], static i => i.ToString()))
            .ConfigureProperty(x => x.Speed, Options<Speed>("Speed", restart: true), SingleValueMultipleChoiceContainerOptions.Create<Speed>(
                EqualityComparer<Speed>.Default, [Speed.Fast, Speed.Slow], static s => s.ToString()))
            .ConfigureProperty(x => x.Tags, Options<string[]>("Tags"), null);

        private static PropertyOptions<SampleSettings, T> Options<T>(string name, bool restart = false, Func<T, ValidationResult>? validation = null) => new()
        {
            Section = Sections.General,
            DisplayName = name,
            DescriptionFactory = _ => "",
            RequiresRestart = restart,
            Validation = validation,
        };
    }

    private sealed class Harness
    {
        public ServiceProvider Provider { get; }
        public ISettingsManager Settings => Provider.GetRequiredService<ISettingsManager>();

        public Harness()
        {
            Provider = new ServiceCollection()
                .AddLogging()
                .AddDefaultParsers()
                .AddSingleton<CommandLineConfigurator>()
                .AddCLI()
                .AddSettings<SampleSettings>()
                .AddSettings<HttpProxySettings>()
                .AddSettings<SilverModManager.Secrets.SecretsSettings>()
                .AddSingleton(new SectionDescriptor(Sections.General, "General", () => default!))
                .AddSingleton<ISettingsManager>(sp => new SettingsManager(sp))
                .AddSettingsVerbs()
                .BuildServiceProvider();
        }

        public async Task<(int ExitCode, LoggingRenderer Renderer, string Output)> Run(params string[] args)
        {
            var renderer = new LoggingRenderer();
            var exitCode = await Provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
            var output = string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Arguments is { Length: > 0 } args ? string.Format(t.Template, args) : t.Template));
            return (exitCode, renderer, output);
        }
    }

    [Fact]
    public async Task List_ShowsEverySettingWithItsKeyValueDefaultAndRestartFlag()
    {
        var harness = new Harness();

        var (exitCode, renderer, _) = await harness.Run("settings", "list");

        exitCode.Should().Be(0);
        renderer.LastTableColumns.Should().Equal("Key", "Name", "Section", "Value", "Default", "Restart");
        var rows = renderer.LastTable.Rows.Select(row => row.OfType<Text>().Select(t => t.Template).ToArray()).ToArray();
        rows.Should().Contain(row => row[0] == "HttpProxySettings.HttpProxy" && row[1] == "HTTP proxy" && row[3] == "" && row[5] == "yes");
        rows.Should().Contain(row => row[0] == "SampleSettings.Count" && row[2] == "General" && row[3] == "3" && row[4] == "3" && row[5] == "no");
        rows.Should().Contain(row => row[0] == "SampleSettings.Enabled" && row[3] == "false");
    }

    [Fact]
    public async Task ListJson_IsAnArrayOfSettingsWithKeyAndRestartFlag()
    {
        var harness = new Harness();

        var (exitCode, _, output) = await harness.Run("settings", "list", "--json");

        exitCode.Should().Be(0);
        using var doc = JsonDocument.Parse(output);
        var proxy = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("key").GetString() == "HttpProxySettings.HttpProxy");
        proxy.GetProperty("name").GetString().Should().Be("HTTP proxy");
        proxy.GetProperty("value").GetString().Should().Be("");
        proxy.GetProperty("requiresRestart").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Get_PrintsTheCurrentValue_AndKeysAreCaseInsensitive()
    {
        var harness = new Harness();
        harness.Settings.Update<SampleSettings>(s => s with { Label = "hello" });

        var (exitCode, _, output) = await harness.Run("settings", "get", "--key", "samplesettings.LABEL");

        exitCode.Should().Be(0);
        output.Trim().Should().Be("SampleSettings.Label = hello");
    }

    [Fact]
    public async Task Set_StoresTheValue_AndSaysWhenARestartIsNeeded()
    {
        var harness = new Harness();

        var (exitCode, _, output) = await harness.Run("settings", "set", "--key", "HttpProxySettings.HttpProxy", "--value", "http://127.0.0.1:8888");

        exitCode.Should().Be(0);
        harness.Settings.Get<HttpProxySettings>().HttpProxy.Should().Be("http://127.0.0.1:8888");
        output.Should().Contain("http://127.0.0.1:8888").And.Contain("Restart SilverModManager");
    }

    [Fact]
    public async Task Set_WithAnEmptyValue_ClearsAString()
    {
        var harness = new Harness();
        harness.Settings.Update<HttpProxySettings>(s => s with { HttpProxy = "http://127.0.0.1:8888" });

        var (exitCode, _, _) = await harness.Run("settings", "set", "--key", "HttpProxySettings.HttpProxy", "--value", "");

        exitCode.Should().Be(0);
        harness.Settings.Get<HttpProxySettings>().HttpProxy.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_ParsesTheValueByThePropertysType()
    {
        var harness = new Harness();

        (await harness.Run("settings", "set", "--key", "SampleSettings.Enabled", "--value", "true")).ExitCode.Should().Be(0);
        (await harness.Run("settings", "set", "--key", "SampleSettings.Count", "--value", "42")).ExitCode.Should().Be(0);
        (await harness.Run("settings", "set", "--key", "SampleSettings.Big", "--value", "5000000000")).ExitCode.Should().Be(0);
        (await harness.Run("settings", "set", "--key", "SampleSettings.Ratio", "--value", "0.25")).ExitCode.Should().Be(0);
        (await harness.Run("settings", "set", "--key", "SampleSettings.Speed", "--value", "slow")).ExitCode.Should().Be(0);

        var sample = harness.Settings.Get<SampleSettings>();
        sample.Enabled.Should().BeTrue();
        sample.Count.Should().Be(42);
        sample.Big.Should().Be(5_000_000_000);
        sample.Ratio.Should().Be(0.25);
        sample.Speed.Should().Be(Speed.Slow);
    }

    [Theory]
    [InlineData("SampleSettings.Enabled", "maybe", "true or false")]
    [InlineData("SampleSettings.Count", "lots", "whole number")]
    [InlineData("SampleSettings.Speed", "bogus", "Fast, Slow, Warp")]
    public async Task Set_RefusesAValueOfTheWrongTypeAndLeavesTheSettingAlone(string key, string value, string expectedHint)
    {
        var harness = new Harness();
        var before = harness.Settings.Get<SampleSettings>();

        var (exitCode, _, output) = await harness.Run("settings", "set", "--key", key, "--value", value);

        exitCode.Should().Be(1);
        output.Should().Contain(value).And.Contain(expectedHint);
        harness.Settings.Get<SampleSettings>().Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData("SampleSettings.Speed", "Warp", "Fast, Slow")]
    [InlineData("SampleSettings.Level", "0", "1, 2, 3, 4, 5")]
    [InlineData("SampleSettings.Level", "6", "1, 2, 3, 4, 5")]
    [InlineData("SampleSettings.Ratio", "1.5", "between 0 and 1")]
    [InlineData("SampleSettings.Ratio", "-0.1", "between 0 and 1")]
    public async Task Set_RefusesAValueTheSettingsPageDoesNotOffer_AndListsWhatItDoes(string key, string value, string expectedHint)
    {
        var harness = new Harness();
        var before = harness.Settings.Get<SampleSettings>();

        var (exitCode, _, output) = await harness.Run("settings", "set", "--key", key, "--value", value);

        exitCode.Should().Be(1);
        output.Should().Contain(value).And.Contain(expectedHint);
        harness.Settings.Get<SampleSettings>().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Set_AcceptsAnOfferedChoiceAndTheEdgesOfARange()
    {
        var harness = new Harness();

        (await harness.Run("settings", "set", "--key", "SampleSettings.Level", "--value", "5")).ExitCode.Should().Be(0);
        (await harness.Run("settings", "set", "--key", "SampleSettings.Ratio", "--value", "1")).ExitCode.Should().Be(0);
        (await harness.Run("settings", "set", "--key", "SampleSettings.Speed", "--value", "Fast")).ExitCode.Should().Be(0);

        var sample = harness.Settings.Get<SampleSettings>();
        sample.Level.Should().Be(5);
        sample.Ratio.Should().Be(1);
    }

    [Fact]
    public async Task Set_RefusesTheSecretBackendTheSettingsPageDoesNotOffer()
    {
        var harness = new Harness();

        var (exitCode, _, output) = await harness.Run("settings", "set", "--key", "SecretsSettings.Backend", "--value", "Memory");

        exitCode.Should().Be(1);
        output.Should().Contain("Auto").And.Contain("FileOnly");
    }

    [Fact]
    public async Task Set_RefusesAValueTheSettingsValidationRejects_WithItsMessage()
    {
        var harness = new Harness();

        var (exitCode, _, output) = await harness.Run("settings", "set", "--key", "SampleSettings.Count", "--value", "-4");

        exitCode.Should().Be(1);
        output.Should().Contain("Count can't be negative.");
        harness.Settings.Get<SampleSettings>().Count.Should().Be(3);
    }

    [Fact]
    public async Task AnUnknownKey_IsRefusedWithTheNearMatches()
    {
        var harness = new Harness();

        var (exitCode, _, output) = await harness.Run("settings", "get", "--key", "SampleSettings.Cnt");

        exitCode.Should().Be(1);
        output.Should().Contain("SampleSettings.Cnt").And.Contain("SampleSettings.Count");
    }

    [Fact]
    public async Task ATypeTheCommandLineCantEdit_IsRefusedByName()
    {
        var harness = new Harness();

        var (exitCode, _, output) = await harness.Run("settings", "set", "--key", "SampleSettings.Tags", "--value", "a");

        exitCode.Should().Be(1);
        output.Should().Contain("SampleSettings.Tags").And.Contain("can't be set from the command line");
    }
}
