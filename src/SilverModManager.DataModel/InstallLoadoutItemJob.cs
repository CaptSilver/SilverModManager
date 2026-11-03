using System.Diagnostics;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Games.AdvancedInstaller;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Library;

namespace SilverModManager.DataModel;

internal class InstallLoadoutItemJob : IJobDefinitionWithStart<InstallLoadoutItemJob, InstallLoadoutItemJobResult>, IInstallLoadoutItemJob
{
    public required ILogger Logger { get; init; }
    public ILibraryItemInstaller? Installer { get; init; }
    public ILibraryItemInstaller? FallbackInstaller { get; init; }
    public LibraryItem.ReadOnly LibraryItem { get; init; }
    public Optional<LoadoutItemGroupId> ParentGroupId { get; set; }
    public LoadoutId LoadoutId { get; init; }

    /// <summary>
    /// Never asks anyone: a mod no installer claims, or a FOMOD with choices to make, ends the job with
    /// <see cref="ReviewRequiredException"/> instead of opening the Advanced Manual Installer or a guided installer.
    /// </summary>
    public bool Unattended { get; init; }

    public required ITransaction Transaction { get; init; }
    internal required IConnection Connection { get; init; }
    internal required IServiceProvider ServiceProvider { get; init; }

    /// <remarks>
    /// Returns null <see cref="LoadoutItemGroup.ReadOnly"/> after running job
    /// if supplied an external transaction via <paramref name="transaction"/>.
    ///
    /// (i.e. if you are running this job as part of a larger transaction)
    /// </remarks>
    public static InstallLoadoutItemJob Create(
        IServiceProvider serviceProvider,
        LibraryItem.ReadOnly libraryItem,
        LoadoutId targetLoadout,
        ITransaction transaction,
        Optional<LoadoutItemGroupId> groupId = default,
        ILibraryItemInstaller? installer = null,
        ILibraryItemInstaller? fallbackInstaller = null,
        bool unattended = false)
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var job = new InstallLoadoutItemJob
        {
            Logger = serviceProvider.GetRequiredService<ILogger<InstallLoadoutItemJob>>(),
            Installer = installer,
            FallbackInstaller = fallbackInstaller,
            Unattended = unattended,
            LibraryItem = libraryItem,
            LoadoutId = targetLoadout,
            ParentGroupId = groupId,
            Connection = connection,
            ServiceProvider = serviceProvider,
            Transaction = transaction,
        };

        return job;
    }

    public async ValueTask<InstallLoadoutItemJobResult> StartAsync(IJobContext<InstallLoadoutItemJob> context)
    {
        if (!ParentGroupId.HasValue)
        {
            var collection = DefaultCollection.Of(Connection.Db, LoadoutId)
                ?? throw new InvalidOperationException($"Loadout {LoadoutId.Value} has no collection of its own to install into.");
            ParentGroupId = LoadoutItemGroupId.From(collection.Value);
        }

        await context.YieldAsync();
        
        var loadout = Loadout.Load(Connection.Db, LoadoutId);

        var installers = Installer is not null
            ? [Installer]
            : loadout.InstallationInstance.GetGame().LibraryItemInstallers;

        var result = await ExecuteInstallersAsync(installers, loadout, context);

        if (result == null)
        {
            if (Installer is AdvancedManualInstaller)
            {
                throw new InvalidOperationException($"Advanced installer did not succeed for `{LibraryItem.Name}` (`{LibraryItem.Id}`)");
            }

            if (FallbackInstaller is not null)
                result = await ExecuteInstallersAsync([FallbackInstaller], loadout, context);

            if (result == null && Unattended)
                throw new ReviewRequiredException(ReviewKind.NoInstaller, LibraryItem.Name, _lastDeclineReason);

            result ??= await ExecuteInstallersAsync([AdvancedManualInstaller.Create(ServiceProvider)], loadout, context);

            if (result == null)
            {
                throw new InvalidOperationException($"Found no installer that supports `{LibraryItem.Name}` (`{LibraryItem.Id}`), including the fallback installer!");
            }
        }

        // TODO(erri120): rename this entity to something unique, like "LoadoutItemInstalledFromLibrary"
        var loadoutGroup = result;
        _ = new LibraryLinkedLoadoutItem.New(Transaction, loadoutGroup.Id)
        {
            LoadoutItemGroup = loadoutGroup,
            LibraryItemId = LibraryItem,
        };

        // An item installed into a loadout is for that loadout's game, whatever it was added for.
        LibraryDestinations.Add(Transaction, LibraryItem.Id, new LoadoutAcquisition(LoadoutId).Destination(Connection.Db));

        return new InstallLoadoutItemJobResult(null, loadoutGroup);
    }

    // The reason the last installer that took a look at the item and declined gave; empty when it gave none.
    private string _lastDeclineReason = string.Empty;

    private async ValueTask<LoadoutItemGroup.New?> ExecuteInstallersAsync(
        ILibraryItemInstaller[] installers,
        Loadout.ReadOnly loadout,
        IJobContext<InstallLoadoutItemJob> context)
    {
        foreach (var installer in installers)
        {
            var isSupported = installer.IsSupportedLibraryItem(LibraryItem);
            if (!isSupported) continue;

            using var subTransaction = context.Definition.Transaction.CreateSubTransaction();
            
            var loadoutGroup = new LoadoutItemGroup.New(subTransaction, out var groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(subTransaction, groupId)
                {
                    Name = LibraryItem.Name,
                    LoadoutId = LoadoutId,
                    ParentId = ParentGroupId.Value,
                },
            };

            // TODO(erri120): add safeguards to only allow groups to be added to the parent groups
            try
            {
                var result = Unattended && installer is IUnattendedInstaller unattendedInstaller
                    ? await unattendedInstaller.ExecuteUnattendedAsync(LibraryItem, loadoutGroup, subTransaction, loadout, context.CancellationToken)
                    : await installer.ExecuteAsync(LibraryItem, loadoutGroup, subTransaction, loadout, context.CancellationToken);
                if (result.IsNotSupported(out var reason))
                {
                    _lastDeclineReason = reason ?? string.Empty;
                    if (Logger.IsEnabled(LogLevel.Trace) && !string.IsNullOrEmpty(reason))
                        Logger.LogTrace("Installer doesn't support library item `{LibraryItem}` because \"{Reason}\"", LibraryItem.Name, reason);
                    continue;
                }

                Debug.Assert(result.IsSuccess);
                subTransaction.CommitToParent();
                return loadoutGroup;
            }
            catch (OperationCanceledException ex)
            {
                context.CancelAndThrow(ex.Message);
            }
        }

        return null;
    }
}
