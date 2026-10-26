using System.Reflection;
using FluentAssertions;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Every refusal type the dialog can show carries its own heading. These build each one and check that the
/// heading reads as a sentence and the message is there, so a type reparented onto <see cref="UserFacingException"/>
/// cannot ship with an empty or copied title. The list is checked against the app's assemblies so a new type has
/// to be added here.
/// </summary>
public class UserFacingExceptionTitleTests
{
    private static readonly Func<UserFacingException>[] Factories =
    [
        () => new Abstractions.Games.GameLaunchException("The game is already running."),
        () => new Abstractions.Games.GameDidNotStopException("Process 1 survived."),
        () => new Abstractions.Loadouts.Exceptions.ExecutableInUseException("The executable is in use."),
        () => new Abstractions.Loadouts.Synchronizers.SynchronizerPreflightException("Preflight refused."),
        () => new Games.FromSoft.Merge.MergeRefusedException("the plan failed"),
        () => new Games.FromSoft.Heal.HealException("Nothing to heal."),
        () => new Games.FromSoft.Saves.SaveRefusedException("The save is locked."),
        () => new Games.CreationEngine.UndecodablePluginListException("Plugins.txt", "windows-1252"),
        () => new Games.CreationEngine.UnencodablePluginNamesException("Plugins.txt", "windows-1252", ["a.esp"]),
        () => new Games.CreationEngine.UnreadablePluginsException(["a.esp"]),
        () => new Library.ReleaseAssetIntegrityException("The asset does not match."),
        () => new Library.ThunderstoreIntegrityException("The package does not match."),
        () => new Library.Lock.LockException("The lock file is unreadable."),
        () => new Networking.Steam.Exceptions.SteamLoginException("Steam rejected the login."),
        () => new Networking.Thunderstore.DependencyResolutionException("A dependency is missing."),
        () => new Networking.Thunderstore.ThunderstoreIndexUnavailableException("riskofrain2"),
        () => new Wabbajack.Format.UnsupportedModlistException("version 9"),
        () => new Wabbajack.Gallery.GalleryFormatException("The gallery is malformed."),
        () => new Wabbajack.Gallery.GalleryUnavailableException("The gallery is offline."),
        () => new Wabbajack.Gallery.GalleryDownloadException("The download failed."),
        () => new Wabbajack.Install.ArchivesMissingException([]),
        () => new App.UI.Overlays.Updater.AppImageUpdateException("The update failed."),
        () => new App.UI.Pages.Diagnostics.ModsNotFoundException("The mods are gone."),
        () => new App.UI.Pages.Tools.WindowsProgramRefusedException("Not a Linux program."),
    ];

    public static IEnumerable<object[]> Cases => Factories.Select(f => new object[] { f().GetType().Name, f });

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachRefusal_HasASentenceCasedTitle_AndAMessage(string typeName, Func<UserFacingException> make)
    {
        var exception = make();

        exception.Message.Should().NotBeNullOrWhiteSpace(typeName);
        exception.Title.Should().NotBeNullOrWhiteSpace(typeName);
        exception.Title.Trim().Should().Be(exception.Title, "{0} should not pad its title", typeName);
        char.IsUpper(exception.Title[0]).Should().BeTrue("{0}'s title starts a sentence", typeName);
        exception.Title.Should().NotEndWith(".", "{0}'s title is a heading, not a sentence with a full stop", typeName);

        // Product names are capitalised mid-title, so only a heading in Title Case Throughout is rejected.
        var words = exception.Title.Split(' ').Skip(1).Where(w => w.Length > 3).ToArray();
        (words.Length == 0 || words.Any(w => !char.IsUpper(w[0]))).Should().BeTrue("{0}'s title '{1}' is in sentence case", typeName, exception.Title);
    }

    [Fact]
    public void EveryRefusalTypeInTheApp_IsInTheList()
    {
        var covered = Factories.Select(f => f().GetType()).ToHashSet();
        var missing = AppAssemblies()
            .SelectMany(SafeTypes)
            .Where(t => t is { IsAbstract: false, IsNestedPrivate: false } && typeof(UserFacingException).IsAssignableFrom(t))
            .Except(covered)
            .Select(t => t.FullName)
            .Order();

        missing.Should().BeEmpty("a new UserFacingException needs a row in Factories so its title is checked");
    }

    private static IEnumerable<Assembly> AppAssemblies()
    {
        var seen = new HashSet<string>();
        var queue = new Queue<Assembly>([typeof(App.Program).Assembly]);
        while (queue.TryDequeue(out var assembly))
        {
            if (assembly.GetName().Name is not { } name || !name.StartsWith("SilverModManager.", StringComparison.Ordinal) || !seen.Add(name)) continue;
            yield return assembly;
            foreach (var reference in assembly.GetReferencedAssemblies().Where(r => r.Name?.StartsWith("SilverModManager.", StringComparison.Ordinal) == true))
            {
                try { queue.Enqueue(Assembly.Load(reference)); }
                catch (FileNotFoundException) { }
            }
        }
    }

    private static Type[] SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>().ToArray(); }
    }
}
