using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Backend;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.DataModel.SchemaVersions.Tests;

/// <summary>
/// A settings file written by an earlier build has to be read back by this one. <c>Fixtures/Configs</c> holds
/// one file per settings type that <see cref="JsonStorageBackend"/> stores, named by the type's id and carrying
/// non-default values, exactly as they sit in <c>$XDG_DATA_HOME/SilverModManager/Configs</c>. Changing a settings
/// id, a property name or the file naming makes the stored value silently revert to its default, and this
/// is where it shows up.
/// </summary>
public class SettingsFileCompatibilityTests(ISettingsManager settingsManager, JsonSerializerOptions jsonOptions, IServiceProvider appServices)
{
    private static readonly string FixtureFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Configs");

    private static readonly MethodInfo LoadMethod = typeof(JsonStorageBackend).GetMethod(nameof(JsonStorageBackend.Load))!;

    private SettingsConfig[] JsonBackedConfigs() => settingsManager.Configs.Values
        .Where(c => c.StorageBackendOptions is { IsDisabled: false } options && options.Id == StorageBackends.Json)
        .ToArray();

    private static string IdOf(Type type) => (string)type.GetProperty(nameof(ISettings.SettingsId), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void EveryJsonBackedSettingsTypeHasAFixtureAndEveryFixtureHasAType()
    {
        var ids = JsonBackedConfigs().Select(c => IdOf(c.Type)).Order().ToArray();
        ids.Should().NotBeEmpty("the settings manager lists no JSON-backed settings, so the scan is not looking at the app's settings");

        var fixtures = Directory.EnumerateFiles(FixtureFolder, "*.json").Select(Path.GetFileNameWithoutExtension).Order().ToArray();

        fixtures.Should().Equal(ids, "a new JSON-backed settings type needs a fixture written under its current id, and a removed one needs its fixture removed");
    }

    [Fact]
    public void EveryFixtureIsReadBackWithItsNonDefaultValues()
    {
        var fs = new InMemoryFileSystem(OSInformation.FakeUnix);
        var backendServices = new ServiceCollection().AddSingleton<IFileSystem>(fs).AddSingleton(jsonOptions).BuildServiceProvider();
        var backend = new JsonStorageBackend(NullLogger<JsonStorageBackend>.Instance, backendServices);
        // The backend's own folder helper is internal; ConfigsFolderTests in the backend tests pins the same path.
        var configs = fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine($"{ApplicationConstants.DataDirectoryName}/Configs");
        configs.CreateDirectory();

        var configsToCheck = JsonBackedConfigs();
        configsToCheck.Should().NotBeEmpty();
        foreach (var config in configsToCheck)
        {
            var id = IdOf(config.Type);
            var fixtureJson = File.ReadAllText(Path.Combine(FixtureFolder, $"{id}.json"));
            using (var stream = configs.Combine($"{id}.json").Create()) stream.Write(Encoding.UTF8.GetBytes(fixtureJson));

            var loaded = LoadMethod.MakeGenericMethod(config.Type).Invoke(backend, [null]);

            loaded.Should().NotBeNull($"{id} must be found by its file name and deserialise");
            var loadedNode = JsonSerializer.SerializeToNode(loaded, config.Type, jsonOptions);
            var defaultNode = JsonSerializer.SerializeToNode(config.DefaultValueFactory(appServices), config.Type, jsonOptions);
            JsonNode.DeepEquals(loadedNode, JsonNode.Parse(fixtureJson)).Should().BeTrue($"every stored property of {id} must come back under the name it was stored with");
            JsonNode.DeepEquals(loadedNode, defaultNode).Should().BeFalse($"the {id} fixture must hold non-default values, or reverting to the default would go unnoticed");
        }
    }
}
