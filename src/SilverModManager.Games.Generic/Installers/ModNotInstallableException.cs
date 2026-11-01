using SilverModManager.Sdk.Errors;

namespace SilverModManager.Games.Generic.Installers;

/// <summary>A mod archive the game cannot take on purpose; the message says why and what to do instead.</summary>
public sealed class ModNotInstallableException(string message) : UserFacingException(message)
{
    public override string Title => "This mod can't be installed here";
}
