using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.Undo;

/// <summary>
/// UndoService.RevertTo rewrites exactly the entities <c>loadouts.TrackedEntitiesForLoadout</c> returns and
/// follows no references. These tests fail when a model changes in a way that breaks that.
/// </summary>
/// <remarks>
/// Blind spot: an entity id stored as a scalar rather than as a reference attribute, such as
/// <c>SortOrder.ParentEntity</c> (a UInt128), is invisible to reflection. Those need a look by hand.
/// </remarks>
public class LoadoutOwnershipGuardTests(ITestOutputHelper helper) : ANexusCollectionTest<LoadoutOwnershipGuardTests>(helper)
{
    /// <summary>
    /// An attribute that links an owned entity to what owns it. <c>loadouts.TrackedEntitiesForLoadout</c> has to
    /// reach the entity through this attribute.
    /// </summary>
    private static readonly string[] OwnershipEdges =
    [
        "SilverModManager.Loadouts.LoadoutItem/Loadout",
        "SilverModManager.Loadouts.SortOrder/Loadout",
        "SilverModManager.Loadouts.SortableEntry/ParentSortOrder",
        "SilverModManager.Loadouts.LoadoutItemGroupPriority/Loadout",
        "SilverModManager.Loadouts.LoadoutGroupRule/Loadout",
        "SilverModManager.Loadouts.LoadoutFileWinner/Loadout",
        "SilverModManager.Loadouts.LoadoutIniState/Loadout",
    ];

    /// <summary>
    /// An attribute that points at an owned-closure type without making the holder owned. The revert writes the
    /// value as it was and follows it no further.
    /// </summary>
    private static readonly string[] PlainReferences =
    [
        "SilverModManager.Loadouts.LoadoutItem/Parent",
        "SilverModManager.Loadouts.LoadoutOverridesGroup/OverridesFor",
        "SilverModManager.Loadouts.GameInstalledGroup/InstalledFor",
        "SilverModManager.Loadouts.GameInstalledClaim/Loadout",
        "SilverModManager.Loadouts.LoadoutItemGroupPriority/Target",
        "SilverModManager.Loadouts.LoadoutGroupRule/Source",
        "SilverModManager.Loadouts.LoadoutGroupRule/Other",
        "SilverModManager.Loadouts.LoadoutFileWinner/WinnerGroup",
        "SilverModManager.Loadouts.GameMetadata/LastSyncedLoadout",
        "SilverModManager.Loadouts.LoadoutSnapshot/Snapshot",
        "SilverModManager.RedEngine.Cyberpunk2077.RedModLoadoutGroup/RedModInfoFile",
        "SilverModManager.MountAndBlade2Bannerlord.ModLoadoutItem/ModuleInfo",
        "SilverModManager.StardewValley.SMAPIModLoadoutItem/Manifest",
        "SilverModManager.StardewValley.SMAPILoadoutItem/ModDatabase",
        "SilverModManager.Wabbajack.SkippedFile/Group",
    ];

    /// <summary>
    /// The roots of the owned closure: what the loadout owns directly, besides the loadout row itself.
    /// </summary>
    private static readonly Type[] Roots = [typeof(LoadoutItem), typeof(SortOrder), typeof(SortOrderItem), typeof(LoadoutItemGroupPriority), typeof(LoadoutGroupRule), typeof(LoadoutFileWinner), typeof(LoadoutIniState)];

    [Fact]
    public void EveryReferenceToAnOwnedModelIsClassified()
    {
        var closure = OwnedClosure();
        var unclassified = ReferencesTo(closure)
            .Where(reference => !OwnershipEdges.Contains(reference.Id) && !PlainReferences.Contains(reference.Id))
            .Select(reference => $"{reference.Id} (declared on {reference.DeclaringType.Name}, points at {reference.Target.Name})")
            .Distinct()
            .Order()
            .ToArray();

        string.Join("\n", unclassified).Should().BeEmpty(
            "every attribute pointing at a model a loadout owns has to be listed as an ownership edge or a plain reference here, " +
            "and an ownership edge has to be reachable from loadouts.TrackedEntitiesForLoadout in LoadoutQueries.sql");
    }

    [Fact]
    public void ClassifiedAttributesStillExist()
    {
        var existing = ReferencesTo(OwnedClosure()).Select(reference => reference.Id).ToHashSet();

        OwnershipEdges.Concat(PlainReferences).Where(id => !existing.Contains(id)).Should().BeEmpty(
            "an attribute that was renamed or removed has to come out of the lists above");
    }

    [Fact]
    public void NoOwnedModelHasUniqueOrMultiValuedAttributes()
    {
        // The loadout row itself is never written by a revert, so its multi-valued attributes don't matter.
        var offenders = OwnedClosure()
            .Where(model => model != typeof(Loadout))
            .SelectMany(Attributes)
            .Where(attribute => attribute.IsUnique || attribute.Cardinalty == Cardinality.Many)
            .Select(attribute => attribute.Id.ToString())
            .Distinct()
            .Order()
            .ToArray();

        string.Join("\n", offenders).Should().BeEmpty(
            "a revert brings deleted entities back under their original ids, which a unique attribute could collide with, " +
            "and it replaces a changed value by asserting the old one, which is wrong for a multi-valued attribute");
    }

    [Fact]
    public async Task EveryOwnershipEdgeIsReachedByTheTrackedEntitiesQuery()
    {
        var loadout = await CreateLoadout();
        var item = await AddLinkedItem(loadout.LoadoutId, "Item");
        var priority = await AddPriority(loadout.LoadoutId, item);
        await AddRedMod(loadout.LoadoutId, "ModA");
        var variety = ServiceProvider.GetRequiredService<SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder.RedModSortOrderVariety>();
        var sortOrder = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await variety.ReconcileSortOrder(sortOrder);
        var sortItems = Connection.Db.Datoms(SortOrderItem.ParentSortOrder, sortOrder).Select(d => d.E).ToArray();
        sortItems.Should().NotBeEmpty();

        var owned = OwnedIds(Connection.Db, loadout.Id);

        owned.Should().Contain(item.Value, "a LoadoutItem is owned through LoadoutItem/Loadout");
        owned.Should().Contain(priority.Value, "a LoadoutItemGroupPriority is owned through LoadoutItemGroupPriority/Loadout");
        owned.Should().Contain(sortOrder.Value, "a SortOrder is owned through SortOrder/Loadout");
        owned.Should().Contain(sortItems, "a SortOrderItem is owned through its SortOrder");
    }

    /// <summary>
    /// The models whose entities a loadout owns: the roots, and every model that includes one of them
    /// directly or through other models. The loadout itself is part of the closure because plain
    /// references to it matter too.
    /// </summary>
    private static HashSet<Type> OwnedClosure()
    {
        var models = ModelDefinitions();
        var closure = new HashSet<Type>(Roots) { typeof(Loadout) };

        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var model in models.Where(model => !closure.Contains(model)))
            {
                if (!IncludedModels(model).Any(closure.Contains)) continue;
                closure.Add(model);
                grew = true;
            }
        }

        return closure;
    }

    private static IEnumerable<Type> IncludedModels(Type model) =>
        model.GetCustomAttributes()
            .Select(attribute => attribute.GetType())
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Include<>))
            .Select(type => type.GetGenericArguments()[0]);

    private static List<Type> ModelDefinitions()
    {
        return LoadedFirstPartyAssemblies()
            .SelectMany(LoadableTypes)
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsAssignableTo(typeof(IModelDefinition)))
            .Distinct()
            .ToList();
    }

    private static IEnumerable<IAttribute> Attributes(Type model) =>
        model.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(field => field.GetValue(null))
            .OfType<IAttribute>();

    private record Reference(string Id, Type DeclaringType, Type Target);

    /// <summary>
    /// Every reference attribute on any model whose target is in <paramref name="targets"/>.
    /// </summary>
    private static List<Reference> ReferencesTo(HashSet<Type> targets)
    {
        var references = new List<Reference>();
        foreach (var model in ModelDefinitions())
        {
            foreach (var field in model.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (field.GetValue(null) is not IAttribute attribute) continue;
                var target = ReferenceTarget(field.FieldType);
                if (target is null || !targets.Contains(target)) continue;
                references.Add(new Reference(attribute.Id.ToString(), model, target));
            }
        }
        return references;
    }

    private static Type? ReferenceTarget(Type attributeType)
    {
        for (var type = attributeType; type is not null; type = type.BaseType)
        {
            if (!type.IsGenericType) continue;
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(SilverModManager.MnemonicDB.Abstractions.Attributes.ReferenceAttribute<>) ||
                definition == typeof(SilverModManager.MnemonicDB.Abstractions.Attributes.ReferencesAttribute<>))
                return type.GetGenericArguments()[0];
        }
        return null;
    }

    /// <summary>
    /// Fewest first-party assemblies the scan may find before it is considered to be looking in the wrong
    /// place; count them with <c>ls tests/SilverModManager.DataModel.Tests/bin/Debug/net*/SilverModManager.*.dll | wc -l</c>.
    /// </summary>
    private const int MinimumFirstPartyAssemblies = 35;

    private static IEnumerable<Assembly> LoadedFirstPartyAssemblies() =>
        LoadedFirstPartyAssemblies(FirstPartyNames.AssemblyGlob, FirstPartyNames.AssemblyPrefix, MinimumFirstPartyAssemblies);

    /// <summary>
    /// The first-party assemblies next to the test, loaded by name so that models in assemblies nothing else in
    /// the test touches are seen too. Throws when it finds fewer than <paramref name="floor"/>, so a scan that
    /// matches nothing cannot let the guards pass.
    /// </summary>
    private static Assembly[] LoadedFirstPartyAssemblies(string glob, string prefix, int floor)
    {
        string[] required =
        [
            "Abstractions.Loadouts",
            "Abstractions.Collections",
            "Games.RedEngine",
            "Games.StardewValley",
            "Games.MountAndBlade2Bannerlord",
        ];
        foreach (var name in required)
            Assembly.Load(FirstPartyNames.AssemblyPrefix + name);

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, glob))
        {
            try
            {
                Assembly.LoadFrom(path);
            }
            catch (BadImageFormatException)
            {
                // A native library with a first-party name.
            }
        }

        var found = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name is { } name
                && (name.StartsWith(prefix, StringComparison.Ordinal) || name == FirstPartyNames.AppAssemblyName))
            .ToArray();

        if (found.Length < floor)
            throw new InvalidOperationException($"The first-party assembly scan found {found.Length} assemblies (prefix '{prefix}', glob '{glob}') in {AppContext.BaseDirectory}, expected at least {floor}");

        return found;
    }

    [Fact]
    public void LoadedFirstPartyAssemblies_FindsTheLoadoutAssemblies()
    {
        var names = LoadedFirstPartyAssemblies().Select(a => a.GetName().Name).ToArray();

        names.Should().Contain(FirstPartyNames.AssemblyPrefix + "Abstractions.Loadouts");
        names.Should().Contain(FirstPartyNames.AssemblyPrefix + "DataModel");
        names.Should().Contain(FirstPartyNames.AssemblyPrefix + "Abstractions.Loadouts.Synchronizers");
    }

    [Fact]
    public void LoadedFirstPartyAssemblies_ThrowsWhenItMatchesNothing()
    {
        var act = () => LoadedFirstPartyAssemblies("Nothing.Matches.This.*.dll", "Nothing.Matches.This.", MinimumFirstPartyAssemblies);

        act.Should().Throw<InvalidOperationException>().WithMessage("*first-party assembly scan found*");
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
