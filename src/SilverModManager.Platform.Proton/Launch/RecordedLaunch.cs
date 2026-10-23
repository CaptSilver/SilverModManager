using System.Text.Json;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// What the window decided the game should be started with, as the state file records it. A Steam wrapper
/// that joins the launch has no database to ask, so it applies this to the command Steam handed it.
/// </summary>
/// <param name="GameRoot">The installation's game folder.</param>
/// <param name="Exe">The executable to start, relative to <paramref name="GameRoot"/>.</param>
/// <param name="ExtraArgs">Arguments the game needs, placed right after the executable.</param>
public sealed record RecordedLaunch(string GameRoot, string Exe, IReadOnlyList<string> ExtraArgs)
{
    /// <summary>The state file keys, in the order they are written.</summary>
    internal const string GameRootKey = "GameRoot";
    internal const string ExeKey = "Exe";
    internal const string ExtraArgsKey = "ExtraArgs";

    /// <summary>
    /// Whether two records say the same thing; the argument lists compare element by element.
    /// </summary>
    public bool Equals(RecordedLaunch? other) =>
        other is not null && GameRoot == other.GameRoot && Exe == other.Exe && ExtraArgs.SequenceEqual(other.ExtraArgs);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(GameRoot, Exe, ExtraArgs.Count);

    /// <summary>
    /// The arguments as one line, since a state file value cannot span lines. JSON keeps arguments with spaces
    /// and quotes apart.
    /// </summary>
    internal string SerialiseArgs() => JsonSerializer.Serialize(ExtraArgs);

    /// <summary>
    /// Reads the record from state file <paramref name="values"/>. Null when the file has none, as one written
    /// by an older build.
    /// </summary>
    /// <exception cref="InvalidDataException">The keys are there but the arguments are not a JSON list of strings.</exception>
    internal static RecordedLaunch? From(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue(GameRootKey, out var root) || !values.TryGetValue(ExeKey, out var exe)) return null;

        var args = values.GetValueOrDefault(ExtraArgsKey, "[]");
        try
        {
            return new RecordedLaunch(root, exe, JsonSerializer.Deserialize<string[]>(args) ?? []);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The recorded launch arguments are not a list of strings: {args}", e);
        }
    }
}
