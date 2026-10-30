using System.Reactive;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.LibraryPage.Collections;

/// <summary>A Wabbajack modlist as a card on the Collections tab.</summary>
public interface IWabbajackModlistCardViewModel : ILibraryCollectionCardViewModel
{
    LibraryFileId File { get; }

    string Name { get; }
    string Author { get; }
    string Version { get; }

    /// <summary>The app's name for the list's game, or "Unknown game".</summary>
    string GameName { get; }

    /// <summary>"Wabbajack" for a list from the gallery, "Local file" for one added from disk.</summary>
    string Source { get; }

    /// <summary>What kind of collection this is, shown where the Nexus card says "Read Only".</summary>
    string Kind { get; }

    /// <summary>The summed size of the list's downloads.</summary>
    Size ArchiveSize { get; }

    /// <summary>The summed size of the files the list installs.</summary>
    Size InstallSize { get; }

    /// <summary>How many of the list's archives the library already holds.</summary>
    int ArchivesHad { get; }

    /// <summary>How many archives the list names.</summary>
    int ArchivesNeeded { get; }

    WabbajackModlistCardState State { get; }

    ReactiveCommand<Unit, Unit> OpenCommand { get; }

    /// <summary>Opens the list's page on the Library's loadout, where the install choices are made.</summary>
    ReactiveCommand<Unit, Unit> InstallCommand { get; }

    ReactiveCommand<Unit, Unit> RemoveCommand { get; }
}
