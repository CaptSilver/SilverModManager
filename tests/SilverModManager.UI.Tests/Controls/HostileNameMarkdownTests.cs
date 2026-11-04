using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests.Controls;

/// <summary>
/// Names from Nexus, collections and archives end up in the Markdown of refusals and dialogs. Whatever such a name
/// says, the renderer must not draw a link or an image out of it.
/// </summary>
public class HostileNameMarkdownTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly string[] Payloads =
    [
        "[x](file:///etc/passwd)",
        "![p](https://evil.example/a.png)",
        "`](http://evil.example)`",
        "a](b) [c](smb://host/share)",
        "```\n[x](http://evil.example)\n```",
        "line one\n[y](nxm://skyrim/mods/1/files/2)",
        "<a href=\"http://evil.example\">z</a> <http://evil.example>",
        "'/g/a`b'",
        "lone ` backtick",
    ];

    private async Task<(int Links, int Images, int Blocks, string Seen)> Render(IEnumerable<string> paragraphs, string separator = "\n\n")
    {
        var contents = string.Join(separator, paragraphs);
        var window = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var viewModel = new MarkdownRendererViewModel(
                Provider,
                Provider.GetRequiredService<ILogger<MarkdownRendererViewModel>>(),
                Substitute.For<IOSInterop>(),
                Substitute.For<IAvaloniaInterop>()
            ) { Contents = contents };
            var created = Track(new Window { Width = 800, Height = 600, Content = new MarkdownRendererView { DataContext = viewModel } });
            created.Show();
            return created;
        });

        // The view binds its contents when it is activated, so the blocks appear a beat after Show().
        // A long document fills in over several beats, so wait until the number of blocks stops changing.
        int previous = -1, steady = 0;
        for (var i = 0; i < 200 && steady < 4; i++)
        {
            var blocks = await Dispatcher.UIThread.InvokeAsync(() => window.GetVisualDescendants().OfType<CTextBlock>().Count());
            steady = blocks > 0 && blocks == previous ? steady + 1 : 0;
            previous = blocks;
            await Task.Delay(100);
        }

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var texts = window.GetVisualDescendants().OfType<CTextBlock>().ToList();
            return (
                texts.Sum(t => t.Content.OfType<CHyperlink>().Count()),
                texts.Sum(t => t.Content.OfType<CImage>().Count()),
                texts.Count,
                string.Join(" || ", texts.Where(t => t.Content.OfType<CHyperlink>().Any() || t.Content.OfType<CImage>().Any()).Select(t => string.Concat(t.Content.OfType<CRun>().Select(r => r.Text)))));
        });
    }

    [Fact]
    public async Task Test_Detector_SeesALinkAndAnImageInUnescapedText()
    {
        var (links, images, _, _) = await Render(["[x](https://a.example)", "![p](https://a.example/a.png)"]);

        links.Should().BeGreaterThan(0);
        images.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Test_InlineAndCode_NeverRenderALinkOrImage()
    {
        var paragraphs = Payloads.SelectMany(p => new[] { $"Mod {MarkdownText.Inline(p)} failed", $"Mod {MarkdownText.Code(p)} failed" }).ToList();

        var (links, images, blocks, seen) = await Render(paragraphs);

        blocks.Should().BeGreaterThan(0);
        links.Should().Be(0, seen);
        images.Should().Be(0, seen);
    }

    [Fact]
    public async Task Test_ALoneBacktickInEscapedText_DoesNotExposeTheNextCodeSpan()
    {
        // One paragraph, as UserFacingErrors.Find makes by joining refusals with a single line break.
        var paragraph = new[] { MarkdownText.Inline("'/g/a`b'"), MarkdownText.Code("[x](https://evil.example)") };

        var (links, images, blocks, seen) = await Render(paragraph, "\n");

        blocks.Should().BeGreaterThan(0);
        links.Should().Be(0, seen);
        images.Should().Be(0, seen);
    }

    [Fact]
    public async Task Test_RefusalsWithDifferentPayloadsJoinedBySingleLineBreaks_RenderNoLinkOrImage()
    {
        var messages = RefusalMessages(out _, rotatePayloads: true);

        var (links, images, blocks, seen) = await Render(messages, Environment.NewLine);

        blocks.Should().BeGreaterThan(0);
        links.Should().Be(0, "a payload in one refusal must not change how the next one reads: " + seen);
        images.Should().Be(0, seen);
    }

    [Fact]
    public async Task Test_EveryRefusalWithNamedParameters_RendersNoLinkOrImage()
    {
        var messages = RefusalMessages(out var exercised);

        exercised.Should().BeGreaterThan(20, "the guard has to reach the refusals that carry names, not skip them all");

        var (links, images, blocks, seen) = await Render(messages);

        blocks.Should().BeGreaterThan(0);
        links.Should().Be(0, "a name from outside must not turn a refusal into a link: " + seen);
        images.Should().Be(0, seen);
    }

    [Fact]
    public async Task Test_EveryDiagnosticWithStringPlaceholders_RendersNoLinkOrImageFromTheStrings()
    {
        var writer = Provider.GetRequiredService<IDiagnosticWriter>();
        var exercised = 0;

        // A few templates have a link of their own, so each payload is compared with the same diagnostics filled with plain text.
        // A name that breaks a NamedLink's text shows as text without its link, which is safe and not counted against it.
        var benign = DiagnosticTexts(writer, Connection, "plain", out _);
        var (benignLinks, benignImages, _, _) = await Render(benign);
        foreach (var payload in Payloads)
        {
            var hostile = DiagnosticTexts(writer, Connection, payload, out exercised);
            var (links, images, blocks, seen) = await Render(hostile);

            blocks.Should().BeGreaterThan(0);
            links.Should().BeLessThanOrEqualTo(benignLinks, $"a string placeholder holding {payload.ReplaceLineEndings("\\n")} must not add a link: " + seen);
            images.Should().BeLessThanOrEqualTo(benignImages, $"a string placeholder holding {payload.ReplaceLineEndings("\\n")} must not add an image: " + seen);
        }

        exercised.Should().BeGreaterThan(40, "the guard has to reach the diagnostics that take strings, not skip them all");
    }

    /// <summary>
    /// The summary and details, in Markdown, of every diagnostic the generated <c>Create</c> methods can make, with every
    /// string and name filled with <paramref name="text"/>. A <see cref="MarkdownFragment"/> is the app's own Markdown, so it
    /// gets a harmless one.
    /// </summary>
    private static List<string> DiagnosticTexts(IDiagnosticWriter writer, IConnection connection, string text, out int exercised)
    {
        LoadReferencedAssemblies();
        var texts = new List<string>();
        exercised = 0;

        var creators = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("SilverModManager", StringComparison.Ordinal) == true)
            .SelectMany(SafeTypes)
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.StartsWith("Create", StringComparison.Ordinal)
                        && m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Diagnostic<>)
                        && m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .OrderBy(m => $"{m.DeclaringType?.FullName}.{m.Name}", StringComparer.Ordinal);

        foreach (var creator in creators)
        {
            Diagnostic diagnostic;
            try
            {
                diagnostic = (Diagnostic)creator.Invoke(null, creator.GetParameters().Select(p => Argument(p.ParameterType, connection, text)).ToArray())!;
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            texts.Add(diagnostic.FormatSummary(writer, DiagnosticWriterMode.Markdown));
            texts.Add(diagnostic.FormatDetails(writer, DiagnosticWriterMode.Markdown));
            exercised++;
        }

        return texts;
    }

    private static object? Argument(Type type, IConnection connection, string text)
    {
        // A reference to a row that is not there, as of the current database.
        if (typeof(IDataReference).IsAssignableFrom(type) && type.GetProperty("TxId") is { CanWrite: true } txId)
        {
            var reference = Activator.CreateInstance(type);
            txId.SetValue(reference, connection.Db.BasisTxId);
            return reference;
        }

        if (type == typeof(string)) return text;
        if (type == typeof(MarkdownFragment)) return new MarkdownFragment("- **a fragment** of `code`");
        if (type == typeof(NamedLink)) return new NamedLink(text, new Uri("https://example.test/mod"));
        if (type == typeof(GamePath)) return new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput(text));
        if (type == typeof(RelativePath)) return RelativePath.FromUnsanitizedInput(text);
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    /// <summary>
    /// The message of every refusal that is built from names handed to its constructor, once per hostile payload.
    /// Refusals that take a finished <c>message</c> are not here: the code that throws them composes the text.
    /// </summary>
    private static List<string> RefusalMessages(out int exercised, bool rotatePayloads = false)
    {
        LoadReferencedAssemblies();
        var messages = new List<string>();
        var types = new HashSet<Type>();

        var refusals = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("SilverModManager", StringComparison.Ordinal) == true)
            .SelectMany(SafeTypes)
            .Where(t => t is { IsAbstract: false, IsPublic: true } && typeof(UserFacingException).IsAssignableFrom(t));

        foreach (var type in refusals)
        {
            foreach (var ctor in type.GetConstructors())
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length == 0 || parameters.Any(p => p.Name is "message" or "what" or "title")) continue;
                if (!parameters.All(p => p.ParameterType == typeof(string) || p.ParameterType == typeof(Exception) || p.ParameterType == typeof(IReadOnlyList<string>))) continue;

                // Rotating gives each parameter of a refusal a different payload, and the next refusal another start.
                for (var start = 0; start < Payloads.Length; start++)
                {
                    var arguments = parameters.Select((p, index) =>
                    {
                        var payload = Payloads[rotatePayloads ? (start + index) % Payloads.Length : start];
                        return p.ParameterType == typeof(string) ? (object)payload
                            : p.ParameterType == typeof(Exception) ? new InvalidOperationException(payload)
                            : new[] { payload };
                    }).ToArray();
                    try
                    {
                        var exception = (UserFacingException)ctor.Invoke(arguments);
                        messages.Add($"{exception.Title}: {exception.Message}");
                        types.Add(type);
                    }
                    catch (TargetInvocationException)
                    {
                        // A constructor that validates its input has nothing to say about hostile text.
                    }
                }
            }
        }

        exercised = types.Count;
        return messages;
    }

    private static void LoadReferencedAssemblies()
    {
        var seen = new HashSet<string>();
        var pending = new Queue<Assembly>(AppDomain.CurrentDomain.GetAssemblies());
        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();
            foreach (var name in assembly.GetReferencedAssemblies())
            {
                if (name.Name?.StartsWith("SilverModManager", StringComparison.Ordinal) != true || !seen.Add(name.FullName)) continue;
                try { pending.Enqueue(Assembly.Load(name)); }
                catch (Exception e) when (e is IOException or BadImageFormatException) { }
            }
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); }
    }
}
