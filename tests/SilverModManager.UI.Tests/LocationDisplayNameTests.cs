using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests;

public class LocationDisplayNameTests
{
    private static IEnumerable<(string Field, LocationId Id)> DefinedLocationIds()
    {
        foreach (var dll in Directory.GetFiles(AppContext.BaseDirectory, "SilverModManager.*.dll"))
        {
            Type[] types;
            try { types = Assembly.LoadFrom(dll).GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
            catch (BadImageFormatException) { continue; }

            foreach (var type in types.Where(t => !t.ContainsGenericParameters))
            {
                var fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(f => f.FieldType == typeof(LocationId)).ToArray();
                if (fields.Length == 0) continue;
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                foreach (var field in fields) yield return ($"{type.Name}.{field.Name}", (LocationId)field.GetValue(null)!);
            }
        }
    }

    private static IEnumerable<IReadOnlyDictionary<string, string>> GameLocationNames()
    {
        foreach (var dll in Directory.GetFiles(AppContext.BaseDirectory, "SilverModManager.*.dll"))
        {
            Type[] types;
            try { types = Assembly.LoadFrom(dll).GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
            catch (BadImageFormatException) { continue; }

            foreach (var type in types.Where(t => !t.ContainsGenericParameters))
            {
                var fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(f => f.FieldType == typeof(IReadOnlyDictionary<string, string>) && f.Name.EndsWith("LocationNames", StringComparison.Ordinal)).ToArray();
                if (fields.Length == 0) continue;
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                foreach (var field in fields) yield return (IReadOnlyDictionary<string, string>)field.GetValue(null)!;
            }
        }
    }

    private static readonly LocationId[] MayShareTheirLabel = [LocationId.Saves, LocationId.AppData];

    [Fact]
    public void EveryLocationIdDefinedInTheApp_IsNamedByTheAppOrItsGame()
    {
        var all = DefinedLocationIds().ToArray();
        var gameNames = GameLocationNames().SelectMany(d => d).ToDictionary(kv => kv.Key, kv => kv.Value);

        all.Select(a => a.Id.ToString()).Should().Contain(["WitcherDocuments", "PlayerProfiles", "ScriptExtenderConfig", "OpenMwConfig", "Mods", "Me3Mods", "Me3Host", "Data"]);
        var unnamed = all.Where(a => a.Id != LocationId.Unknown && !a.Id.IsNamed && !gameNames.ContainsKey(a.Id.ToString())).Select(a => a.Field);
        unnamed.Should().BeEmpty();
        all.Where(a => a.Id.IsNamed && a.Id.DisplayName == a.Id.ToString() && !MayShareTheirLabel.Contains(a.Id))
            .Select(a => a.Field).Should().BeEmpty();
    }

    [Fact]
    public void WitcherDocuments_IsCalledDocuments()
        => SilverModManager.Games.Witcher.WitcherConstants.LocationNames["WitcherDocuments"].Should().Be("Documents");
}
