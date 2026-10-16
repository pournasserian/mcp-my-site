using McpMySite.Indexer.Configuration;
using System.Text.Json.Nodes;

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
        Assert.Equal("AI_API_KEY", loaded.Config.Embeddings.ApiKeyEnvironmentVariable);
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
    public async Task KeywordOnlyConfigNeedsNoEmbeddingSettings()
    {
        var folder = NewFolder();
        var config = new IndexerConfig();
        config.Site.Url = "https://example.test";
        config.Embeddings.Enabled = false;
        var json = IndexerConfigLoader.ToAppSettingsJson(config);
        await File.WriteAllTextAsync(Path.Combine(folder, "appsettings.json"), json);
        Assert.Contains("\"Enabled\": false", json);

        var loaded = IndexerConfigLoader.Load(Path.Combine(folder, "appsettings.json"), "Production");
        Assert.False(loaded.Config.Embeddings.Enabled);
        loaded.Config.Validate();

        // Without a provider the model and dimensions are irrelevant; with one they are still required.
        loaded.Config.Embeddings.Model = string.Empty;
        loaded.Config.Validate();
        loaded.Config.Embeddings.Enabled = true;
        Assert.Throws<InvalidOperationException>(loaded.Config.Validate);
    }

    [Fact]
    public void ShippedDefaultsFileMatchesCodeDefaults()
    {
        var path = Path.Combine(AppContext.BaseDirectory, IndexerConfig.DefaultsFileName);
        Assert.True(File.Exists(path), $"{path} was not copied to the output folder.");

        var shipped = JsonNode.Parse(File.ReadAllText(path));
        var expected = JsonNode.Parse(IndexerConfigLoader.ToAppSettingsJson(new IndexerConfig()));
        Assert.True(JsonNode.DeepEquals(expected, shipped), $"{IndexerConfig.DefaultsFileName} is out of date. Expected:\n{expected}");
    }

    [Fact]
    public async Task ShippedDefaultsAreTheLowestLayer()
    {
        var folder = NewFolder();
        await File.WriteAllTextAsync(Path.Combine(folder, "appsettings.json"), """
            { "Site": { "Url": "https://example.test" }, "Logging": { "LogLevel": { "Default": "Debug" } } }
            """);

        var loaded = IndexerConfigLoader.Load(Path.Combine(folder, "appsettings.json"), "Production");

        Assert.Equal("https://example.test", loaded.Config.Site.Url);
        Assert.Equal("Debug", loaded.Configuration["Logging:LogLevel:Default"]);
        // Only the shipped file sets this key, so seeing it proves that file was layered in.
        Assert.Equal("Warning", loaded.Configuration["Logging:LogLevel:System.Net.Http"]);
        Assert.Equal(1000, loaded.Config.Crawl.MaxPages);
        loaded.Config.Validate();
    }

    [Fact]
    public void RejectsMalformedOverride()
    {
        Assert.Throws<InvalidOperationException>(() => IndexerConfigLoader.ParseOverrides(["NoEqualsSign"]));
    }
}
