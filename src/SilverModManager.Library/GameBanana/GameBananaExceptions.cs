using JetBrains.Annotations;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Library.GameBanana;

/// <summary>
/// A downloaded GameBanana file did not match the checksum the site lists or the digest that was pinned, or there was
/// nothing to check it against. Nothing was added to the library.
/// </summary>
[PublicAPI]
public sealed class GameBananaIntegrityException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The GameBanana file was rejected";
}

/// <summary>
/// The app will not install this mod: the site's virus scan did not come back clean, or the author does not allow
/// installing it. <see cref="Page"/> is where the user can look at it and decide.
/// </summary>
[PublicAPI]
public sealed class GameBananaRefusedException : UserFacingException
{
    public GameBananaRefusedException(string reason, Uri? page, IReadOnlyList<GameBananaAlternateSource>? mirrors = null)
        : base(MessageOf(reason, page, mirrors))
    {
        Page = page;
    }

    /// <summary>The mod's page on GameBanana, null when the refusal is not about one mod.</summary>
    public Uri? Page { get; }

    /// <inheritdoc />
    public override string Title => "The mod was not installed";

    // Mirrors are the author's own links. They are shown for the user to open; the app never fetches from them.
    private static string MessageOf(string reason, Uri? page, IReadOnlyList<GameBananaAlternateSource>? mirrors)
    {
        var message = page is null ? reason : $"{reason} Look at {MarkdownText.Link("the mod's page", page)} and decide for yourself.";
        if (mirrors is not { Count: > 0 }) return message;

        var lines = mirrors.Select(mirror => string.IsNullOrWhiteSpace(mirror.Description)
            ? $"- {MirrorLink(mirror.Url)}"
            : $"- {MarkdownText.InlineMidLine(mirror.Description)}: {MirrorLink(mirror.Url)}");
        return $"{message}\n\nThe author also lists these links, which the app does not use:\n{string.Join('\n', lines)}";
    }

    // The link shows its own address, so it cannot name one site and open another; anything that is not a web address
    // is shown as text only.
    private static string MirrorLink(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var target) && MarkdownText.LinkTarget(target) is not null
            ? MarkdownText.Link(target.AbsoluteUri, target)
            : MarkdownText.Code(url);
}

/// <summary>
/// The mod has several files and the request did not say which. <see cref="Files"/> is what to pick from; the page
/// shows a picker and asks again with the file's id.
/// </summary>
[PublicAPI]
public sealed class GameBananaChooseFileException(Uri page, IReadOnlyList<GameBananaFile> files) : UserFacingException(MessageOf(page, files))
{
    /// <summary>The files the mod lists.</summary>
    public IReadOnlyList<GameBananaFile> Files { get; } = files;

    /// <inheritdoc />
    public override string Title => "Pick a file to install";

    private static string MessageOf(Uri page, IReadOnlyList<GameBananaFile> files)
    {
        var lines = files.Select(file => $"- {file.Id}: {MarkdownText.InlineMidLine(file.FileName)}");
        return $"{MarkdownText.Link("This mod", page)} has several files. Say which one with --file and its id:\n{string.Join('\n', lines)}";
    }
}
