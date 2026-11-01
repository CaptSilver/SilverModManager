namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>What a tool's exit code means.</summary>
public enum ExitCodePolicy
{
    /// <summary>Any other code is a failed run.</summary>
    ZeroIsSuccess,

    /// <summary>
    /// Every exit is fine. Editors exit non-zero for ordinary reasons (a cancelled save, a window closed with
    /// unsaved changes).
    /// </summary>
    AnyExitIsFine,
}
