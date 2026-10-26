namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>How a member both mods changed is merged.</summary>
public enum MemberMerge
{
    /// <summary>The bytes are not read: one side's copy is kept whole.</summary>
    Opaque,

    /// <summary>The member is a TPF, merged texture by texture.</summary>
    Texture,
}

/// <summary>What a binder's merge does with a member, chosen by the extension of its name.</summary>
public static class MemberPolicy
{
    public static MemberMerge For(string member) => Extension(member) switch
    {
        ".tpf" => MemberMerge.Texture,
        _ => MemberMerge.Opaque,
    };

    /// <summary>The plain noun a note uses for the member, so a card says "the model" rather than a file extension.</summary>
    public static string Noun(string member) => Extension(member) switch
    {
        ".flver" => "the model",
        ".anibnd" => "the animations",
        ".tpf" => "the textures",
        _ => "the file",
    };

    // Member names are Windows paths whatever the host is, and a directory may itself contain a dot.
    private static string Extension(string member)
    {
        var name = member[(member.LastIndexOf('\\') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot < 0 ? string.Empty : name[dot..].ToLowerInvariant();
    }
}
