using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SilverModManager.Games.Larian.BaldursGate3;
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

    private static IEnumerable<IReadOnlyDictionary<string, string>> GameNameMaps(string fieldSuffix)
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
                    .Where(f => f.FieldType == typeof(IReadOnlyDictionary<string, string>) && f.Name.EndsWith(fieldSuffix, StringComparison.Ordinal)).ToArray();
                if (fields.Length == 0) continue;
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                foreach (var field in fields) yield return (IReadOnlyDictionary<string, string>)field.GetValue(null)!;
            }
        }
    }

    // Ids whose label is deliberately the id itself: the plain word is what a player calls the place.
    private static readonly string[] MayShareTheirLabel = [LocationId.Saves.ToString(), LocationId.AppData.ToString(), Bg3Constants.ModsLocationId.ToString()];

    [Fact]
    public void EveryLocationIdDefinedInTheApp_IsNamedByTheAppOrItsGame()
    {
        var all = DefinedLocationIds().ToArray();
        var gameNames = GameNameMaps("LocationNames").SelectMany(d => d).ToDictionary(kv => kv.Key, kv => kv.Value);

        all.Select(a => a.Id.ToString()).Should().Contain(["WitcherDocuments", "PlayerProfiles", "ScriptExtenderConfig", "OpenMwConfig", "Mods", "Me3Mods", "Me3Host", "Data"]);
        var unnamed = all.Where(a => a.Id != LocationId.Unknown && !a.Id.IsNamed && !gameNames.ContainsKey(a.Id.ToString())).Select(a => a.Field);
        unnamed.Should().BeEmpty();
        all.Where(a => a.Id.IsNamed && a.Id.DisplayName == a.Id.ToString() && !MayShareTheirLabel.Contains(a.Id.ToString()))
            .Select(a => a.Field).Should().BeEmpty();
        gameNames.Where(kv => kv.Value == kv.Key && !MayShareTheirLabel.Contains(kv.Key))
            .Select(kv => kv.Key).Should().BeEmpty();
    }

    // A sentence phrase for a folder sits after "in" and "to" ("in the mod loader folder"), so a phrase that
    // names a folder needs its article. An app row label is not a phrase, but a game's label is the phrase when the
    // game gives no sentence name for that location, the same fallback the backup question uses.
    [Fact]
    public void AFolderNameInASentence_TakesItsArticle()
    {
        var gameSentences = GameNameMaps("LocationSentenceNames").SelectMany(d => d).ToDictionary(kv => kv.Key, kv => kv.Value);
        var gamePhrases = GameNameMaps("LocationNames").SelectMany(d => d)
            .Select(kv => (Field: kv.Key, Sentence: gameSentences.GetValueOrDefault(kv.Key, kv.Value)))
            .Concat(gameSentences.Select(kv => (Field: kv.Key, Sentence: kv.Value)));
        var appPhrases = DefinedLocationIds().Where(a => a.Id.IsNamed).Select(a => (a.Field, Sentence: a.Id.SentenceName));

        gamePhrases.Concat(appPhrases)
            .Where(n => n.Sentence.Contains("folder", StringComparison.OrdinalIgnoreCase) && !n.Sentence.StartsWith("the ", StringComparison.Ordinal))
            .Select(n => n.Field).Should().BeEmpty();
    }
}
