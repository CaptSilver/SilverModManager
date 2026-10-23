using System.Reflection;
using FluentAssertions;
using SilverModManager.App.UI.Pages.About;
using SilverModManager.App.UI.Pages.Changelog;
using SilverModManager.App.UI.Resources;
using SilverModManager.Collections;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Manifest resource names are the assembly's root namespace plus the file path, or an explicit LogicalName
/// in the csproj. A name that no longer matches compiles and only fails when the resource is first opened,
/// so each name is built here from the assembly's own name and opened.
/// </summary>
public class ManifestResourceTests
{
    private static readonly Assembly AppUi = typeof(AboutPageViewModel).Assembly;
    private static readonly Assembly Collections = typeof(CollectionDownloader).Assembly;
    private static readonly Assembly TestHelpers = typeof(StubbedGame).Assembly;

    private static string Name(Assembly assembly, string suffix) => $"{assembly.GetName().Name}.{suffix}";

    private static string ReadAll(Assembly assembly, string name, bool allowEmpty = false)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        stream.Should().NotBeNull($"{assembly.GetName().Name} must embed `{name}`; it has: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream!);
        var text = reader.ReadToEnd();
        if (!allowEmpty) text.Should().NotBeEmpty();
        return text;
    }

    [Fact]
    public void Test_MissingNameIsNotFound()
    {
        // Proves the positive assertions below can fail: a wrong name gives no stream.
        AppUi.GetManifestResourceStream(Name(AppUi, "NOTICE.missing")).Should().BeNull();
        Collections.GetManifestResourceStream(Name(Collections, "default-collection-image.webp.missing")).Should().BeNull();
        TestHelpers.GetManifestResourceStream(Name(TestHelpers, "Resources.question_mark_game.png.missing")).Should().BeNull();
    }

    [Fact]
    public void Test_NoticeIsEmbedded()
        => ReadAll(AppUi, Name(AppUi, "NOTICE")).Should().Contain("Copyright (C)");

    [Fact]
    public void Test_AboutTemplateIsEmbedded()
        => ReadAll(AppUi, Name(AppUi, "About.md")).Should().StartWith("# {{ProductName}}");

    [Fact]
    public void Test_ChangelogIsEmbedded()
        => ReadAll(AppUi, Name(AppUi, "CHANGELOG.md")).Should().Contain("#");

    [Theory]
    [InlineData(typeof(AboutPageViewModel), "TemplateResourceName", "About.md")]
    [InlineData(typeof(AboutPageViewModel), "NoticeResourceName", "NOTICE")]
    [InlineData(typeof(ChangelogPageViewModel), "ChangelogResourceName", "CHANGELOG.md")]
    public void Test_PageViewModelResourceNamesMatchTheManifest(Type viewModel, string constant, string suffix)
    {
        // The view models hold the names as private literals; read those, not a copy of them here.
        var field = viewModel.GetField(constant, BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"{viewModel.Name} has no constant {constant}");
        var literal = (string)field.GetRawConstantValue()!;
        literal.Should().Be(Name(AppUi, suffix));
        AppUi.GetManifestResourceNames().Should().Contain(literal);
    }

    [Fact]
    public void Test_DefaultCollectionImageIsEmbedded()
        => ReadAll(Collections, Name(Collections, "default-collection-image.webp")).Should().NotBeEmpty();

    [Fact]
    public void Test_StubbedGameIconIsEmbedded()
        // The icon file in the helper project is a zero-byte placeholder, so only its presence is checked.
        => ReadAll(TestHelpers, Name(TestHelpers, "Resources.question_mark_game.png"), allowEmpty: true);

    [Fact]
    public void Test_LanguageStringsAreReadable()
        => Language.Newsfeed.Should().NotBeNullOrEmpty();
}
