using SilverModManager.MnemonicDB.Abstractions.Attributes;

namespace SilverModManager.Games.FOMOD;

public static class EmptyDirectory
{
    private const string Namespace = "SilverModManager.Games.FOMOD.EmptyDirectory";
    
    public static readonly BooleanAttribute Directory = new(Namespace, nameof(EmptyDirectory));
}
