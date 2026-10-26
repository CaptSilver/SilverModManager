using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.App.GarbageCollection.Interfaces;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.GarbageCollection.DataModel;

/// <summary>
/// This class is responsible for finding all used files within the data store
/// and marking them as 'used'.
/// </summary>
public static class DataStoreReferenceMarker
{
    /// <summary>
    /// This method walks through the data store for all <see cref="LoadoutItem"/>(s)
    /// to determine all files used by the data store.
    /// </summary>
    /// <param name="connection">The connection to the MnemonicDB Database.</param>
    /// <param name="archiveGc">The garbage collector from which to reference count all files.</param>
    public static void MarkUsedFiles<TParsedHeaderState, TFileEntryWrapper>(IConnection connection, ArchiveGarbageCollector<TParsedHeaderState, TFileEntryWrapper> archiveGc)
        where TParsedHeaderState : ICanProvideFileHashes<TFileEntryWrapper>
        where TFileEntryWrapper : IHaveFileHash
    {
        var db = connection.Db;
        var loadoutFiles = LoadoutFile.All(db);
        var isLoadoutValidDict = new Dictionary<LoadoutId, bool>();
        
        // Loadouts will have items like 'Game Files', these do not have a corresponding
        // library item.
        MarkItemsUsedInLoadouts(archiveGc, loadoutFiles, db, isLoadoutValidDict);

        // Library will have all of our mods and other things installed from the outside.
        MarkItemsUsedInLibrary(archiveGc, db);
        
        // A loadout's copy of each game INI, and the bytes last baked from it, live only in the file store.
        MarkGameIniCopies(archiveGc, db, isLoadoutValidDict);

        // Mark non Loadout/Library items that are backups (e.g. Backups of original game files)
        MarkItemsMarkedAsBackups(archiveGc, db);

        // Bytes a running job stored but has not yet recorded in any of the above.
        foreach (var hash in FileStorePins.Pinned())
            archiveGc.AddReferencedFile(hash);
    }

    private static void MarkGameIniCopies<TParsedHeaderState, TFileEntryWrapper>(ArchiveGarbageCollector<TParsedHeaderState, TFileEntryWrapper> archiveGc, IDb db, Dictionary<LoadoutId, bool> isLoadoutValidDict)
        where TParsedHeaderState : ICanProvideFileHashes<TFileEntryWrapper> where TFileEntryWrapper : IHaveFileHash
    {
        foreach (var state in LoadoutIniState.All(db))
        {
            if (!isLoadoutValidDict.TryGetValue(state.LoadoutId, out var isLoadoutValid))
            {
                isLoadoutValid = state.Loadout.IsValid();
                isLoadoutValidDict[state.LoadoutId] = isLoadoutValid;
            }

            if (!isLoadoutValid) continue;

            archiveGc.AddReferencedFile(state.Baseline);
            if (LoadoutIniState.LastBakedHash.TryGetValue(state, out var baked)) archiveGc.AddReferencedFile(baked);
        }
    }

    private static void MarkItemsMarkedAsBackups<TParsedHeaderState, TFileEntryWrapper>(ArchiveGarbageCollector<TParsedHeaderState, TFileEntryWrapper> archiveGc, IDb db) where TParsedHeaderState : ICanProvideFileHashes<TFileEntryWrapper> where TFileEntryWrapper : IHaveFileHash
    {
        // All files explicitly marked as roots should be preserved.
        var gameBackedUpFile = GameBackedUpFile.All(db);
        foreach (var file in gameBackedUpFile)
            archiveGc.AddReferencedFile(file.Hash);
    }

    private static void MarkItemsUsedInLibrary<TParsedHeaderState, TFileEntryWrapper>(ArchiveGarbageCollector<TParsedHeaderState, TFileEntryWrapper> archiveGc, IDb db) where TParsedHeaderState : ICanProvideFileHashes<TFileEntryWrapper> where TFileEntryWrapper : IHaveFileHash
    {
        /*
            Note(sewer)

            How the whole system is built is not particularly easy to understand
            at first, so here's a short explainer that's also in the docs.

            This is based on reading the implementation of AddLibraryFileJobWorker 
            (which is recursive across a base type, and thus not trivial to follow).

            Essentially, we want to scan for all `LibraryFile` items here. An `LibraryArchive`
            is the primitive you usually interact with in the UI to add items, and each of its
            `LibraryArchiveFileEntry` rows is a `LibraryFile` too.
         */

        var libraryFiles = LibraryFile.All(db);
        foreach (var file in libraryFiles)
            archiveGc.AddReferencedFile(file.Hash);

        /*
            This covers every `LibraryFile`: the members of an archive, and plain files added on their own
            (an .esp, say), which keep their bytes in the store under their own hash. An archive the library
            expanded has no bytes under its own hash; marking it is harmless, as the collector ignores hashes
            it holds no archive for.

            There is however a caveat that has to be considered here.

            There is a base assumption that every `LibraryFile` hash is a file kept in the File Store.
            If this assumption is broken, then the GC will miss files.
            An appropriate warning has been added to the relevant classes.
        */
    }

    private static void MarkItemsUsedInLoadouts<TParsedHeaderState, TFileEntryWrapper>(ArchiveGarbageCollector<TParsedHeaderState, TFileEntryWrapper> archiveGc, Entities<LoadoutFile.ReadOnly> loadoutFiles, IDb db, Dictionary<LoadoutId, bool> isLoadoutValidDict)
        where TParsedHeaderState : ICanProvideFileHashes<TFileEntryWrapper> where TFileEntryWrapper : IHaveFileHash
    {
        foreach (var loadoutFile in loadoutFiles)
        {
            // TODO: Implement recursive includes, e.g. AsLoadoutItemWithTargetPath + AsLoadoutItem
            // into a single method.
            var loadoutItem = new LoadoutItem.ReadOnly(db, loadoutFile.Id);
            if (!isLoadoutValidDict.TryGetValue(loadoutItem.LoadoutId, out var isLoadoutValid))
            {
                var loadout = loadoutItem.Loadout;
                isLoadoutValid = loadout.IsValid();
                isLoadoutValidDict[loadoutItem.LoadoutId] = isLoadoutValid;
            }
            
            if (!isLoadoutValid)
                continue;
            
            // If the loadout is valid, mark the file as used.
            archiveGc.AddReferencedFile(loadoutFile.Hash);
        }
    }
}
