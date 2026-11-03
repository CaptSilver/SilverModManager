using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;

/// <summary>
/// Helper utility methods/services around mod files.
/// </summary>
/// <remarks>
///     This is functionality lifted out of the old `View Mod Files` page, into
///     a separate helper, to make it more ready for deletion. This will be
///     reintegrated into the new `View Mod Files` page at a later date.
/// </remarks>
public static class LoadoutItemGroupHelpers
{
    /// <summary>
    /// Removes files from loadout item groups through <see cref="ILoadoutFileRemover"/>. The path can name a file or
    /// a folder; for a folder, every file inside it goes. Only rows in the given groups are touched.
    /// </summary>
    /// <param name="connection">The connection used to MnemonicDB.</param>
    /// <param name="remover">What deletes the rows and writes the restore point.</param>
    /// <param name="groupIds">The <see cref="LoadoutItemGroup"/> IDs (usually mod) to remove files from</param>
    /// <param name="gamePath">The file or folder to remove.</param>
    /// <param name="requireAllGroups">If true, throws InvalidOperationException when any group is missing. If false, continues with available groups.</param>
    /// <returns>A task that completes with the operation status when the files are removed.</returns>
    /// <exception cref="InvalidOperationException">Thrown when requireAllGroups is true and any group is missing</exception>
    public static async Task<GroupOperationStatus> RemoveFileOrFolder(IConnection connection, ILoadoutFileRemover remover, LoadoutItemGroupId[] groupIds, GamePath gamePath, bool requireAllGroups = true)
    {
        var (validGroups, missingGroups) = LoadAllGroups(connection, groupIds, requireAllGroups);

        if (validGroups.Length == 0)
            return GroupOperationStatus.NoItemsDeleted;

        // The page already refuses what the user may not delete, so what it could delete before it still can.
        var request = new FileRemovalRequest(validGroups[0].AsLoadoutItem().LoadoutId, [gamePath], groupIds);
        try
        {
            await remover.Remove(request, includeLocked: true);
        }
        catch (FileRemovalRefusedException)
        {
            return GroupOperationStatus.NoItemsDeleted;
        }

        return missingGroups.Length > 0 ? GroupOperationStatus.MissingGroups : GroupOperationStatus.Success;
    }

    /// <summary>
    /// Finds a LoadoutItemWithTargetPath matching the specified game path within the given loadout item groups.
    /// </summary>
    /// <param name="connection">The connection used to MnemonicDB.</param>
    /// <param name="groupIds">The <see cref="LoadoutItemGroup"/> IDs (usually mod) to search within.</param>
    /// <param name="gamePath">The game path to match. Searches for an exact match.</param>
    /// <param name="requireAllGroups">If true, throws <see cref="InvalidOperationException"/> when any group is missing. If false, continues with available groups.</param>
    /// <returns>The matching <see cref="LoadoutItemWithTargetPath.ReadOnly"/> if found; otherwise, null.</returns>
    /// <exception cref="InvalidOperationException">Thrown when requireAllGroups is true and any group is missing.</exception>
    public static LoadoutItemWithTargetPath.ReadOnly? FindMatchingFile(IConnection connection, LoadoutItemGroupId[] groupIds, GamePath gamePath, bool requireAllGroups = true)
    {
        var (validGroups, _) = LoadAllGroups(connection, groupIds, requireAllGroups);
        
        if (validGroups.Length == 0)
            return null;
            
        foreach (var group in validGroups)
        {
            foreach (var item in group.Children.OfTypeLoadoutItemWithTargetPath())
            {
                if (item.TargetPath == gamePath)
                    return item;
            }
        }
        
        return null;
    }

    /// <summary>
    /// The paths of the files in the groups that are one of <paramref name="selected"/> or sit inside one of them
    /// when it names a folder, so a selection of folders and files can be handed on as plain file paths.
    /// </summary>
    public static GamePath[] FilePathsUnder(IConnection connection, LoadoutItemGroupId[] groupIds, IEnumerable<GamePath> selected)
    {
        var (validGroups, _) = LoadAllGroups(connection, groupIds, requireAllGroups: false);
        var selection = selected.ToArray();
        return validGroups
            .SelectMany(group => group.Children.OfTypeLoadoutItemWithTargetPath())
            .Select(item => (GamePath)item.TargetPath)
            .Where(path => selection.Any(chosen => path.LocationId == chosen.LocationId
                                                   && (path.Path == chosen.Path || path.Path.InFolder(chosen.Path))))
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Loads all specified <see cref="LoadoutItemGroup"/>(s) from the database.
    /// </summary>
    /// <param name="connection">Database connection to use for loading</param>
    /// <param name="groupIds">Array of group IDs to load</param>
    /// <param name="requireAllGroups">If true, throws an exception if any groups are missing</param>
    /// <returns>Tuple containing (valid groups array, missing group IDs array)</returns>
    public static (LoadoutItemGroup.ReadOnly[] validGroups, LoadoutItemGroupId[] missingGroups) LoadAllGroups(IConnection connection, Span<LoadoutItemGroupId> groupIds, bool requireAllGroups)
    {
        var validGroups = new List<LoadoutItemGroup.ReadOnly>();
        var missingGroups = new List<LoadoutItemGroupId>();

        foreach (var groupId in groupIds)
        {
            var group = LoadoutItemGroup.Load(connection.Db, groupId);
            if (group.IsValid())
                validGroups.Add(group);
            else
                missingGroups.Add(groupId);
        }

        if (requireAllGroups && missingGroups.Count > 0)
            throw new InvalidOperationException($"Missing group IDs: {string.Join(", ", missingGroups)}");

        return (validGroups.ToArray(), missingGroups.ToArray());
    }

    /// <summary>
    /// Gets the first valid <see cref="LoadoutItemGroup"/> from the specified group IDs.
    /// </summary>
    /// <param name="connection">Database connection to use for loading</param>
    /// <param name="groupIds">Array of group IDs to check</param>
    /// <returns>The first valid group found, or null if none are valid</returns>
    public static LoadoutItemGroup.ReadOnly? GetFirstValidGroup(IConnection connection, Span<LoadoutItemGroupId> groupIds)
    {
        foreach (var groupId in groupIds)
        {
            var group = LoadoutItemGroup.Load(connection.Db, groupId);
            if (group.IsValid())
                return group;
        }

        return null;
    }
    
    /// <summary>
    /// Gets the first valid <see cref="LoadoutItemGroup"/> from the specified group IDs.
    /// </summary>
    /// <param name="connection">Database connection to use for loading</param>
    /// <param name="groupIds">Array of group IDs to check</param>
    /// <returns>The first valid group found, or null if none are valid</returns>
    public static LoadoutItemGroup.ReadOnly? GetFirstValidGroup(IConnection connection, Span<EntityId> groupIds)
    {
        foreach (var groupId in groupIds)
        {
            var group = LoadoutItemGroup.Load(connection.Db, groupId);
            if (group.IsValid())
                return group;
        }

        return null;
    }

    /// <summary>
    /// Result status for group operations
    /// </summary>
    public enum GroupOperationStatus
    {
        /// <summary>
        /// No items were deleted from any of the groups.
        /// </summary>
        NoItemsDeleted,
        
        /// <summary>
        /// Some groups were missing but delete operation completed for existing ones
        /// </summary>
        MissingGroups,
        
        /// <summary>
        /// All items from all groups were deleted successfully
        /// </summary>
        Success,
    }
}
