using McpMySite.Indexer.Build;
using McpMySite.Indexer.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace McpMySite.Tests.Indexer;

/// <summary>A full build with Embeddings:Enabled false: no provider is contacted and no key is needed.</summary>
public class KeywordOnlyBuildTests
{
    public static TheoryData<string> StoreTypes => new() { IndexFormat.JsonStoreType, IndexFormat.SqliteStoreType };

    [Theory]
    [MemberData(nameof(StoreTypes))]
    public async Task BuildsAndServesAnIndexWithoutAnApiKey(string storeType)
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcpmysite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "site", "docs"));
        await File.WriteAllTextAsync(Path.Combine(folder, "site", "index.html"),
            "<html><head><title>Acme</title></head><body><main><h1>Welcome</h1><p>Acme makes rockets and widgets for everyone, every day.</p></main></body></html>");
        await File.WriteAllTextAsync(Path.Combine(folder, "site", "docs", "fuel.html"),
            "<html><head><title>Fuel</title></head><body><main><h1>Fuel</h1><p>Our rockets burn liquid oxygen and kerosene in every launch.</p></main></body></html>");

        var config = new IndexerConfig();
        config.Site.Url = "https://keyword-only.test";
        config.Source.Type = SourceType.Folder;
        config.Source.Folder = "site";
        config.Output.Store = storeType;
        config.Cache.Enabled = false;
        config.Embeddings.Enabled = false;
        config.Embeddings.ApiKeyEnvironmentVariable = "MCPMYSITE_TEST_KEY_THAT_IS_NOT_SET";

        var report = await new IndexBuilder(NullLoggerFactory.Instance).RunAsync(config, folder, CancellationToken.None);

        Assert.True(report.Succeeded, report.FailureReason);
        Assert.True(report.KeywordOnly);
        Assert.Equal(2, report.PagesIndexed);
        Assert.Equal(0, report.EmbeddingsGenerated);

        var outputDirectory = Path.Combine(folder, "index");
        var savedReport = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, IndexFormat.ReportFileName))).RootElement;
        Assert.True(savedReport.GetProperty("keywordOnly").GetBoolean());

        using var store = await IndexStoreOpener.OpenAsync(outputDirectory);
        Assert.True(store.Manifest.KeywordOnly);
        Assert.Null(store.Manifest.EmbeddingModel);
        Assert.Equal(0, store.Manifest.EmbeddingDimensions);

        var response = await store.SearchAsync(new SearchRequest { Query = "kerosene", Limit = 5 });
        Assert.True(response.KeywordOnly);
        Assert.Equal("https://keyword-only.test/docs/fuel", response.Hits[0].Url);
    }
}
