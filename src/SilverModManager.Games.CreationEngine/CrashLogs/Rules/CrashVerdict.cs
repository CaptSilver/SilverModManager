using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.CrashLogs.Rules;

/// <summary>What a rule concluded about a crash.</summary>
/// <param name="NamedMod">The mod to blame or to fix, when the rule can name one.</param>
/// <param name="Startup">The game never got as far as running; a start-up crash outranks the others in Health Check.</param>
/// <param name="SubjectFile">The file the rule is about, when it is about one. Used to tell whether disabling a mod made the cause go away.</param>
public sealed record CrashVerdict(string Cause, string Fix, LoadoutItemGroupReference? NamedMod, bool Startup, GamePath? SubjectFile = null);
