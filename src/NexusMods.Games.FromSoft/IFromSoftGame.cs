using NexusMods.Abstractions.Games;
using NexusMods.Games.FromSoft.Cleanup;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.FromSoft.Saves;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft;

/// <summary>
/// A FromSoft game. Shared services decide whether an installation is theirs with
/// <c>installation.Game is IFromSoftGame</c> and read the game's values from here, never by comparing game ids.
/// </summary>
/// <remarks>
/// A test that substitutes this interface gets <c>null</c> from the shortcut properties, because NSubstitute
/// intercepts default interface members. It must configure <see cref="Definition"/> and every shortcut the code under
/// test reads, or use a real game class.
/// </remarks>
public interface IFromSoftGame : IGame
{
    FromSoftGameDefinition Definition { get; }

    FromSoftGameLayout Layout => Definition.Layout;

    FromSoftSaveNames SaveNames => Definition.SaveNames;

    /// <summary>What the game folder checks look for.</summary>
    GameFolderCheckRules CheckRules => Definition.CheckRules;

    /// <summary>What the game-folder cleanup recognises.</summary>
    CleanupRules Cleanup => Definition.Cleanup;

    /// <summary>Which files of the upper folder a modded session may write without being flagged.</summary>
    OverlayAllowlist Overlay => Definition.Overlay;

    /// <summary>Rewrites of single files of the configuration layer as they are copied into the upper folder.</summary>
    IReadOnlyList<IConfigurationLayerTransform> LayerTransforms { get; }

    /// <summary>What loads mods into the game.</summary>
    IFromSoftModLoader Loader { get; }
}
