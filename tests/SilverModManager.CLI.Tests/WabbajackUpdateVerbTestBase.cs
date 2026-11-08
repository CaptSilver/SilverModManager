using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.CLI.Tests;

/// <summary>An Elden Ring loadout with Wabbajack list files and installs written straight into the database, and the verbs run against it.</summary>
public abstract class WabbajackUpdateVerbTestBase<TSelf>(ITestOutputHelper helper) : EldenRingVerbTest<TSelf>(helper)
{
    protected async Task<(int ExitCode, LoggingRenderer Renderer)> RunVerb(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, renderer);
    }

    protected static string[] LinesOf(LoggingRenderer renderer)
        => renderer.Logs.OfType<Text>().Select(t => string.Format(t.Template, t.Arguments.Cast<object>().ToArray()).TrimEnd('\n')).ToArray();

    /// <summary>The text exactly as printed, for JSON output whose braces would be read as format placeholders.</summary>
    protected static string RawText(LoggingRenderer renderer) => string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template));

    /// <summary>Adds a list file to the library under the gallery identity <c>(wabbajack-tools/mod-lists, machineUrl)</c>.</summary>
    protected async Task<EntityId> AddListFile(string name, string version, string machineUrl)
    {
        using var tx = Connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = $"{name} {version}.wabbajack",
            Hash = NexusMods.Hashing.xxHash3.Hash.From((ulong)HashCode.Combine(name, version)),
            Size = Size.From(1),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        _ = new WabbajackModlistLibraryFile.New(tx, id)
        {
            LibraryFile = file,
            Name = name,
            Author = "someone",
            Version = version,
            GameType = "EldenRing",
            ArchiveCount = 1,
            ArchiveSize = Size.From(1),
            InstallSize = Size.From(1),
            IsNsfw = false,
        };
        tx.Add(id, WabbajackModlistLibraryFile.MachineUrl, machineUrl);
        tx.Add(id, WabbajackModlistLibraryFile.Repository, "wabbajack-tools/mod-lists");
        return (await tx.Commit())[id];
    }

    /// <summary>Writes the loadout's install of <paramref name="file"/>.</summary>
    protected async Task<EntityId> InstallGroup(LoadoutId loadout, EntityId file, string name)
    {
        using var tx = Connection.BeginTransaction();
        _ = new WabbajackModlistLoadoutGroup.New(tx, out var groupId)
        {
            ModlistId = file,
            CollectionGroup = new CollectionGroup.New(tx, groupId)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, groupId)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = name, LoadoutId = loadout },
                },
            },
        };
        return (await tx.Commit())[groupId];
    }

    protected async Task InstallList(LoadoutId loadout, string name, string version, string machineUrl)
        => await InstallGroup(loadout, await AddListFile(name, version, machineUrl), name);
}
