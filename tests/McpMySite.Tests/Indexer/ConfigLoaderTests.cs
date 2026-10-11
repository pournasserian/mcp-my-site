using McpMySite.Indexer.Configuration;

namespace McpMySite.Tests.Indexer;

public class ConfigLoaderTests
{
    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcpmysite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public async Task LayersBaseFileEnvironmentFileAndOverrides()
    {
        var folder = NewFolder();
        await File.WriteAllTextAsync(Path.Combine(folder, "appsettings.json"), """
            {
              "Site": { "Url": "https://example.test" },
              "Crawl": { "MaxPages": 100, "Exclude": ["/tag/**"] },
              "Output": { "Store": "sqlite" }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(folder, "appsettings.Staging.json"), """
            { "Crawl": { "MaxPages": 10 }, "Source": { "Type": "Folder", "Folder": "dist" } }
            """);

        var loaded = IndexerConfigLoader.Load(Path.Combine(folder, "appsettings.json"), "Staging", ["Chunking:MaxChars=900", "Safety__MinPages=3"]);

        Assert.Equal("Staging", loaded.EnvironmentName);
        Assert.Equal(folder, loaded.BaseDirectory);
        Assert.Equal("https://example.test", loaded.Config.Site.Url);
        Assert.Equal(10, loaded.Config.Crawl.MaxPages);
        Assert.Equal(["/tag/**"], loaded.Config.Crawl.Exclude);
        Assert.Equal(SourceType.Folder, loaded.Config.Source.Type);
        Assert.Equal("dist", loaded.Config.Source.Folder);
        Assert.Equal("sqlite", loaded.Config.Output.Store);
        Assert.Equal(900, loaded.Config.Chunking.MaxChars);
        Assert.Equal(3, loaded.Config.Safety.MinPages);
        Assert.Equal("OPENROUTER_API_KEY", loaded.Config.Embeddings.ApiKeyEnvironmentVariable);
    }

    [Fact]
    public void MissingFileGivesDefaultsAndCurrentDirectory()
    {
        var loaded = IndexerConfigLoader.Load(Path.Combine(NewFolder(), "appsettings.json"), "Production");

        Assert.Null(loaded.ConfigFile);
        Assert.Equal(Directory.GetCurrentDirectory(), loaded.BaseDirectory);
        Assert.Equal(string.Empty, loaded.Config.Site.Url);
        Assert.Equal(1000, loaded.Config.Crawl.MaxPages);
        Assert.Throws<InvalidOperationException>(loaded.Config.Validate);
    }

    [Fact]
    public async Task InitOutputRoundTripsThroughTheLoader()
    {
        var folder = NewFolder();
        var config = new IndexerConfig();
        config.Site.Url = "https://example.test";
        config.Output.Store = "sqlite";
        var json = IndexerConfigLoader.ToAppSettingsJson(config);
        await File.WriteAllTextAsync(Path.Combine(folder, "appsettings.json"), json);

        Assert.Contains("\"Logging\"", json);
        Assert.Contains("\"Type\": \"Crawl\"", json);
        Assert.DoesNotContain("ApiKey\":", json.Replace("ApiKeyEnvironmentVariable", string.Empty));

        var loaded = IndexerConfigLoader.Load(Path.Combine(folder, "appsettings.json"), "Production");
        Assert.Equal("https://example.test", loaded.Config.Site.Url);
        Assert.Equal("sqlite", loaded.Config.Output.Store);
        Assert.Equal(SourceType.Crawl, loaded.Config.Source.Type);
        Assert.Equal(FolderUrlStyle.Clean, loaded.Config.Source.FolderUrlStyle);
        loaded.Config.Validate();
    }

    [Fact]
    public void RejectsMalformedOverride()
    {
        Assert.Throws<InvalidOperationException>(() => IndexerConfigLoader.ParseOverrides(["NoEqualsSign"]));
    }
}
