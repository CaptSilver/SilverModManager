using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
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

    private sealed record Rendered(int Links, int Images, int Blocks, string Seen, IReadOnlyList<string> Targets)
    {
        public void Deconstruct(out int links, out int images, out int blocks, out string seen) =>
            (links, images, blocks, seen) = (Links, Images, Blocks, Seen);
    }

    private async Task<Rendered> Render(IEnumerable<string> paragraphs, string separator = "\n\n")
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
            return new Rendered(
                texts.Sum(t => t.Content.OfType<CHyperlink>().Count()),
                texts.Sum(t => t.Content.OfType<CImage>().Count()),
                texts.Count,
                string.Join(" || ", texts.Where(t => t.Content.OfType<CHyperlink>().Any() || t.Content.OfType<CImage>().Any()).Select(t => string.Concat(t.Content.OfType<CRun>().Select(r => r.Text)))),
                texts.SelectMany(t => t.Content.OfType<CHyperlink>()).Select(l => l.CommandParameter?.ToString() ?? "").ToList());
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
    public async Task Test_Detector_ReadsWhereALinkGoes()
    {
        var rendered = await Render(["[x](https://a.example/page)"]);

        rendered.Targets.Should().ContainSingle().Which.Should().Contain("a.example/page");
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
        var exercised = 0;

        // A few templates have a link of their own (a NamedLink to the fixture URL). A hostile name may only ever lose such a
        // link's text or the link itself, so every link target must be one the same diagnostics have with a plain fill.
        var benign = DiagnosticTexts("plain", out _);
        var benignRendered = await Render(benign);
        var allowed = benignRendered.Targets.Distinct().ToList();
        allowed.Should().NotBeEmpty("the fixture NamedLink gives some diagnostics a link of their own");
        foreach (var payload in Payloads)
        {
            var hostile = DiagnosticTexts(payload, out exercised);
            var rendered = await Render(hostile);
            var (_, images, blocks, seen) = rendered;
            var targets = rendered.Targets;

            blocks.Should().BeGreaterThan(0);
            targets.Should().OnlyContain(t => allowed.Contains(t), $"a string placeholder holding {payload.ReplaceLineEndings("\\n")} must not add a link: " + seen);
            images.Should().Be(0, $"a string placeholder holding {payload.ReplaceLineEndings("\\n")} must not add an image: " + seen);
        }

        exercised.Should().BeGreaterThan(40, "the guard has to reach the diagnostics that take strings, not skip them all");
    }

    /// <summary>
    /// The text of every diagnostic the generated <c>Create</c> methods can make, as the details page builds it (its heading,
    /// details and more-details), with every string and name filled with <paramref name="text"/>. A
    /// <see cref="MarkdownFragment"/> is the app's own Markdown, so it gets a harmless one.
    /// </summary>
    private List<string> DiagnosticTexts(string text, out int exercised)
    {
        LoadReferencedAssemblies();
        var texts = new List<string>();
        var count = 0;
        var writer = Provider.GetRequiredService<IDiagnosticWriter>();
        var connection = Connection;

        var creators = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("SilverModManager", StringComparison.Ordinal) == true)
            .SelectMany(SafeTypes)
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.StartsWith("Create", StringComparison.Ordinal)
                        && m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Diagnostic<>)
                        && m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .OrderBy(m => $"{m.DeclaringType?.FullName}.{m.Name}", StringComparer.Ordinal)
            .ToList();

        // The view model builds the page's Markdown, so the guard reads what it makes rather than repeating how.
        Dispatcher.UIThread.Invoke(() =>
        {
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

                var page = new DiagnosticDetailsViewModel(
                    new DesignWindowManager(),
                    writer,
                    Provider.GetRequiredService<IMarkdownRendererViewModel>(),
                    diagnostic,
                    default,
                    [],
                    Substitute.For<IErrorPresenter>(),
                    NullLogger<DiagnosticDetailsViewModel>.Instance,
                    Provider.GetRequiredService<IMarkdownRendererViewModel>());
                texts.Add(page.MarkdownRendererViewModel.Contents);
                if (page.MoreDetailsRendererViewModel is { } more) texts.Add(more.Contents);
                count++;
            }
        });

        exercised = count;
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
