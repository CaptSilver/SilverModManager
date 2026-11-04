using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;

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
        for (var i = 0; i < 100; i++)
        {
            var blocks = await Dispatcher.UIThread.InvokeAsync(() => window.GetVisualDescendants().OfType<CTextBlock>().Count());
            if (blocks > 0) break;
            await Task.Delay(50);
        }
        await Task.Delay(300);

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
