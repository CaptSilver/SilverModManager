using JetBrains.Annotations;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.App.UI.Pages.Diagnostics.SaveToLibrary;

/// <summary>A file counts as in the library when a library item was made from the Nexus Mods file with that id.</summary>
[UsedImplicitly]
public sealed class LibraryProbe(IConnection connection) : ILibraryProbe
{
    public bool Contains(NexusModsGameId game, uint fileId)
    {
        var db = connection.Db;
        return NexusModsFileMetadata.FindByUid(db, new FileUid(FileId.From(fileId), game))
            .Any(file => NexusModsLibraryItem.FindByFileMetadata(db, file).Count > 0);
    }
}
