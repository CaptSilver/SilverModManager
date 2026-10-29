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

    [Fact]
    public void EveryLocationIdDefinedInTheApp_HasAPlayerLabelThatIsNotItsId()
    {
        var all = DefinedLocationIds().ToArray();

        all.Select(a => a.Id.ToString()).Should().Contain(["WitcherDocuments", "PlayerProfiles", "ScriptExtenderConfig", "OpenMwConfig", "Mods", "Me3Mods", "Me3Host"]);
        all.Where(a => a.Id.DisplayName == a.Id.ToString() && a.Id.DisplayName is not ("Saves" or "AppData"))
            .Select(a => a.Field).Should().BeEmpty();
        all.Where(a => a.Id.DisplayName == "Game data").Select(a => a.Field).Should().BeEmpty();
    }

    [Fact]
    public void WitcherDocuments_IsCalledDocuments()
        => SilverModManager.Games.Witcher.WitcherConstants.WitcherDocuments.DisplayName.Should().Be("Documents");
}
