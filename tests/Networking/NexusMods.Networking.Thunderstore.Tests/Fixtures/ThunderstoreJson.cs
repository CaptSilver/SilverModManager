namespace NexusMods.Networking.Thunderstore.Tests.Fixtures;

/// <summary>
/// Synthetic Thunderstore payloads shaped like the live API: the v1 list and the chunks carry
/// <c>file_size</c> and no namespace, the experimental endpoint carries a namespace and no <c>file_size</c>.
/// </summary>
internal static class ThunderstoreJson
{
    public static string ChunkVersion(string team, string name, string version, string[] dependencies)
        => $$"""
        {
          "name": "{{name}}",
          "full_name": "{{team}}-{{name}}-{{version}}",
          "version_number": "{{version}}",
          "description": "Synthetic {{name}}",
          "icon": "https://gcdn.thunderstore.io/live/repository/icons/{{team}}-{{name}}-{{version}}.png",
          "dependencies": [{{string.Join(",", dependencies.Select(d => $"\"{d}\""))}}],
          "download_url": "https://thunderstore.io/package/download/{{team}}/{{name}}/{{version}}/",
          "downloads": 12,
          "date_created": "2025-09-01T10:00:00.000000Z",
          "website_url": "",
          "is_active": true,
          "uuid4": "00000000-0000-0000-0000-000000000001",
          "file_size": 4096
        }
        """;

    public static string Listing(string team, string name, params string[] versionJson)
        => $$"""
        {
          "name": "{{name}}",
          "full_name": "{{team}}-{{name}}",
          "owner": "{{team}}",
          "package_url": "https://thunderstore.io/c/repo/p/{{team}}/{{name}}/",
          "donation_link": null,
          "date_created": "2025-09-01T10:00:00.000000Z",
          "date_updated": "2025-09-02T10:00:00.000000Z",
          "uuid4": "00000000-0000-0000-0000-000000000002",
          "rating_score": 5,
          "is_pinned": false,
          "is_deprecated": false,
          "has_nsfw_content": false,
          "categories": ["Mods"],
          "versions": [{{string.Join(",", versionJson)}}]
        }
        """;

    public static string RepoLib => Listing("Zehs", "REPOLib",
        ChunkVersion("Zehs", "REPOLib", "4.2.0", ["BepInEx-BepInExPack-5.4.2305"]));

    public static string RepoConfig => Listing("nickklmao", "REPOConfig",
        ChunkVersion("nickklmao", "REPOConfig", "1.2.6", ["BepInEx-BepInExPack-5.4.2100", "nickklmao-MenuLib-2.5.2"]));

    public static string BepInExPack => Listing("BepInEx", "BepInExPack",
        ChunkVersion("BepInEx", "BepInExPack", "5.4.2305", []),
        ChunkVersion("BepInEx", "BepInExPack", "5.4.2100", []));

    /// <summary>The experimental per-version record: namespace present, no file_size.</summary>
    public static string ExperimentalVersion => """
        {
          "namespace": "Zehs",
          "name": "REPOLib",
          "version_number": "4.2.0",
          "full_name": "Zehs-REPOLib-4.2.0",
          "description": "Synthetic REPOLib",
          "icon": "https://gcdn.thunderstore.io/live/repository/icons/Zehs-REPOLib-4.2.0.png",
          "dependencies": ["BepInEx-BepInExPack-5.4.2305"],
          "download_url": "https://thunderstore.io/package/download/Zehs/REPOLib/4.2.0/",
          "downloads": 12,
          "date_created": "2025-09-01T10:00:00.000000Z",
          "website_url": "",
          "is_active": true
        }
        """;

    /// <summary>The experimental package record: no version list, only <c>latest</c>.</summary>
    public static string ExperimentalPackage => $$"""
        {
          "namespace": "BepInEx",
          "name": "BepInExPack",
          "full_name": "BepInEx-BepInExPack",
          "owner": "BepInEx",
          "package_url": "https://thunderstore.io/c/riskofrain2/p/BepInEx/BepInExPack/",
          "date_created": "2020-01-01T00:00:00.000000Z",
          "date_updated": "2025-09-02T10:00:00.000000Z",
          "rating_score": 100,
          "is_pinned": true,
          "is_deprecated": false,
          "total_downloads": 1000,
          "latest": {
            "namespace": "BepInEx",
            "name": "BepInExPack",
            "version_number": "5.4.2305",
            "full_name": "BepInEx-BepInExPack-5.4.2305",
            "description": "Synthetic pack",
            "icon": "https://gcdn.thunderstore.io/live/repository/icons/BepInEx-BepInExPack-5.4.2305.png",
            "dependencies": [],
            "download_url": "https://thunderstore.io/package/download/BepInEx/BepInExPack/5.4.2305/",
            "downloads": 1000,
            "date_created": "2025-09-01T10:00:00.000000Z",
            "website_url": "",
            "is_active": true
          },
          "community_listings": [
            { "community": "riskofrain2", "categories": ["Libraries"], "review_status": "unreviewed", "has_nsfw_content": false, "url": "https://thunderstore.io/c/riskofrain2/p/BepInEx/BepInExPack/" },
            { "community": "repo", "categories": ["Libraries"], "review_status": "approved", "has_nsfw_content": false, "url": "https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/" }
          ]
        }
        """;
}
