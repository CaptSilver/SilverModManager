using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.FileExtractor;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.CLI;

/// <summary>
/// Dev-time checks on the textures a modlist installs from. Not part of any install.
/// </summary>
public static class TexturesVerbs
{
    public static IServiceCollection AddTexturesVerbs(this IServiceCollection services) =>
        services
            .AddModule("textures", "Commands for checking texture files")
            .AddVerb(() => Census);

    [Verb("textures census", "Reads the header of every .dds file in the archives of a folder, without decoding, and prints a count per layout and the headers it cannot read")]
    private static async Task<int> Census([Injected] IRenderer renderer,
        [Option("d", "directory", "The folder holding the downloaded archives")] AbsolutePath directory,
        [Injected] IFileExtractor extractor,
        [Injected] TemporaryFileManager temporaryFileManager,
        [Injected] CancellationToken token)
    {
        if (!directory.DirectoryExists())
            throw new CensusFolderMissingException($"{directory} is not a folder");

        var census = new TextureCensus();
        var archives = 0;
        foreach (var archive in directory.EnumerateFiles().OrderBy(f => f.FileName, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!await extractor.CanExtract(archive)) continue;
            archives++;

            await using var folder = temporaryFileManager.CreateFolder();
            try
            {
                await extractor.ExtractAllAsync(archive, folder.Path, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One archive we cannot open should not hide what the others hold.
                census.AddUnreadable(archive.FileName, $"the archive could not be extracted: {ex.Message}");
                continue;
            }

            foreach (var file in folder.Path.EnumerateFiles("*.dds", recursive: true).OrderBy(f => f.ToString(), StringComparer.Ordinal))
            {
                var name = $"{archive.FileName}: {file.RelativeTo(folder.Path).ToString().Replace('\\', '/')}";
                census.Add(name, await File.ReadAllBytesAsync(file.ToString(), token));
            }
        }

        await renderer.TextLine("Read {0} archives in {1}", archives, directory.ToString());
        if (census.Layouts.Count > 0)
            await renderer.Table(["Layout", "Count"], census.Layouts.Select(l => new object[] { l.Key, l.Value }));
        if (census.Unreadable.Count > 0)
            await renderer.Table(["File", "Why"], census.Unreadable.Select(u => new object[] { u.Name, u.Reason }));
        return 0;
    }
}

/// <summary>The folder given to <c>textures census</c> does not exist.</summary>
public sealed class CensusFolderMissingException(string message) : UserFacingException(message)
{
    public override string Title => "Cannot read the archives";
}
