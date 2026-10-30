using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using SilverModManager.Abstractions.NexusWebApi.DTOs;
using SilverModManager.Abstractions.NexusWebApi.DTOs.Interfaces;

namespace SilverModManager.Networking.NexusWebApi.Tests;

/// <summary>
/// The REST DTOs mark the members the app reads as required; members nothing reads stay optional. A payload that leaves one out, or sends it as
/// JSON null, has to fail at the boundary naming the property instead of handing a null to a reader.
/// </summary>
public class DtoRequiredFieldsTests
{
    private const string CategoryJson = """{"category_id": 3, "name": "Armour", "parent_category": false}""";
    private const string GameInfoJson = """{"id": 1704, "name": "Skyrim", "domain_name": "skyrim", "forum_url": "https://forums.example/skyrim", "nexusmods_url": "https://example/skyrim", "genre": "RPG", "file_count": 5, "downloads": 9, "approved_date": 10, "authors": 2, "file_endorsements": 3, "mods": 4, "categories": []}""";
    private const string ModFileJson = """{"id": [7, 1704], "uid": 7000, "file_id": 7, "name": "Main", "version": "1.0", "category_id": 1, "category_name": "MAIN", "is_primary": true, "size": 1, "file_name": "Main-1-0.7z", "uploaded_timestamp": 5, "uploaded_time": "2024-01-01T00:00:00Z", "mod_version": "1.0", "external_virus_scan_url": null, "description": null, "size_kb": 1, "changelog_html": null, "content_preview_link": null}""";
    private const string ModFilesJson = """{"files": [{"file_id": 7, "name": "Main"}]}""";
    private const string ModUpdateJson = """{"mod_id": 12, "latest_file_update": 100, "latest_mod_activity": 200}""";
    private const string DownloadLinkJson = """{"name": "Nexus CDN", "short_name": "Nexus CDN", "URI": "https://cdn.example/file.7z"}""";

    private static readonly Dictionary<string, (string Json, Action<string> Parse)> Dtos = new()
    {
        [nameof(Category)] = (CategoryJson, json => Parse<Category>(json)),
        [nameof(GameInfo)] = ($"[{GameInfoJson}]", json => ParseArray<GameInfo>(json)),
        [nameof(ModFile)] = (ModFileJson, json => Parse<ModFile>(json)),
        [nameof(ModFiles)] = (ModFilesJson, json => Parse<ModFiles>(json)),
        [nameof(ModUpdate)] = ($"[{ModUpdateJson}]", json => ParseArray<ModUpdate>(json)),
        [nameof(DownloadLink)] = ($"[{DownloadLinkJson}]", json => ParseArray<DownloadLink>(json)),
    };

    public static TheoryData<string, string> RequiredMembers => new()
    {
        { nameof(ModFile), "file_id" },
        { nameof(ModFiles), "files" },
        { nameof(ModUpdate), "mod_id" },
        { nameof(ModUpdate), "latest_mod_activity" },
        { nameof(DownloadLink), "short_name" },
        { nameof(DownloadLink), "URI" },
    };

    private static T Parse<T>(string json) where T : IJsonSerializable<T> => JsonSerializer.Deserialize(json, T.GetTypeInfo()) ?? throw new JsonException("null");
    private static T[] ParseArray<T>(string json) where T : IJsonArraySerializable<T> => JsonSerializer.Deserialize(json, T.GetArrayTypeInfo()) ?? throw new JsonException("null");

    /// <summary>The payload with <paramref name="property"/> removed, or set to null, on the first object.</summary>
    private static string Mutate(string json, string property, bool setNull)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("fixture is not JSON");
        var target = (root is JsonArray array ? array[0] : root) as JsonObject ?? throw new InvalidOperationException("fixture has no object");
        if (setNull) target[property] = null;
        else target.Remove(property);
        return root.ToJsonString();
    }

    [Fact]
    public void Test_FullPayloadsDeserialiseToTheSentValues()
    {
        var category = Parse<Category>(CategoryJson);
        category.CategoryId.Should().Be(3);
        category.Name.Should().Be("Armour");

        var game = ParseArray<GameInfo>($"[{GameInfoJson}]").Single();
        game.Id.Value.Should().Be(1704u);
        game.Name.Should().Be("Skyrim");
        game.DomainName.Should().Be("skyrim");
        game.Genre.Should().Be("RPG");
        game.FileCount.Should().Be(5u);

        var file = Parse<ModFile>(ModFileJson);
        file.FileId.Value.Should().Be(7u);
        file.Name.Should().Be("Main");
        file.Version.Should().Be("1.0");
        file.FileName.Should().Be("Main-1-0.7z");

        var files = Parse<ModFiles>(ModFilesJson);
        files.Files.Should().ContainSingle().Which.Name.Should().Be("Main");

        var update = ParseArray<ModUpdate>($"[{ModUpdateJson}]").Single();
        update.ModId.Value.Should().Be(12u);
        update.LatestFileUpdated.Should().Be(100);
        update.LatestModActivity.Should().Be(200);

        var link = ParseArray<DownloadLink>($"[{DownloadLinkJson}]").Single();
        link.Uri.Should().Be(new Uri("https://cdn.example/file.7z"));
        link.Name.Should().Be("Nexus CDN");
    }

    [Theory]
    [InlineData(nameof(Category), "category_id")]
    [InlineData(nameof(Category), "name")]
    [InlineData(nameof(GameInfo), "id")]
    [InlineData(nameof(GameInfo), "name")]
    [InlineData(nameof(GameInfo), "domain_name")]
    [InlineData(nameof(ModUpdate), "latest_file_update")]
    public void Test_MembersNothingReadsMayBeOmitted(string dto, string property)
    {
        var (json, parse) = Dtos[dto];
        var act = () => parse(Mutate(json, property, setNull: false));
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(nameof(Category), "name")]
    [InlineData(nameof(GameInfo), "name")]
    [InlineData(nameof(GameInfo), "domain_name")]
    public void Test_ReferenceMembersNothingReadsMayBeNull(string dto, string property)
    {
        var (json, parse) = Dtos[dto];
        var act = () => parse(Mutate(json, property, setNull: true));
        act.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(RequiredMembers))]
    public void Test_OmittedRequiredMemberFailsNamingTheProperty(string dto, string property)
    {
        var (json, parse) = Dtos[dto];
        var act = () => parse(Mutate(json, property, setNull: false));
        act.Should().Throw<JsonException>().Which.Message.Should().Contain(property);
    }

    [Theory]
    [MemberData(nameof(RequiredMembers))]
    public void Test_NullRequiredMemberFailsNamingTheProperty(string dto, string property)
    {
        var (json, parse) = Dtos[dto];
        var act = () => parse(Mutate(json, property, setNull: true));
        act.Should().Throw<JsonException>().Which.Message.Should().Contain(property);
    }

    [Fact]
    public void Test_NullableMembersMayBeOmittedOrNull()
    {
        var file = Parse<ModFile>("""{"file_id": 7, "content_preview_link": null, "description": null}""");
        file.ContentPreviewLink.Should().BeNull();
        file.FileId.Value.Should().Be(7u);
        file.Name.Should().BeNull();
    }
}
