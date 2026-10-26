using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Backend.Tests;

/// <summary>The settings manager is a singleton that the UI thread, jobs and the task pool all read and write.</summary>
public class SettingsConcurrencyTests
{
    public record KeyedSettings : ISettings
    {
        public string Value { get; init; } = "";
        public static string SettingsId => "SilverModManager.Tests.KeyedSettings";
        public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder) => settingsBuilder;
    }

    [Fact]
    public async Task SettingsSetAndReadFromSeveralThreadsAtOnceKeepEveryValue()
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddSettings<KeyedSettings>()
            .BuildServiceProvider();
        var manager = new SettingsManager(provider);
        // Distinct keys make the store grow while other threads read it, which is what corrupts an unsynchronised map.
        var keys = Enumerable.Range(0, 4000).Select(i => $"key-{i}").ToArray();

        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 4 }, (key, _) =>
        {
            manager.Set(new KeyedSettings { Value = key }, key);
            manager.Get<KeyedSettings>(key).Value.Should().Be(key);
            return ValueTask.CompletedTask;
        });

        keys.Should().OnlyContain(key => manager.Get<KeyedSettings>(key).Value == key);
    }
}
