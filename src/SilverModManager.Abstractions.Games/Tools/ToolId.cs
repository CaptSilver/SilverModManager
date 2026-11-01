namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>Names a tool in the catalogue (<c>xedit</c>, <c>dyndolod</c>). Stored with the tools a user has installed.</summary>
public readonly record struct ToolId(string Value)
{
    public override string ToString() => Value;
}
