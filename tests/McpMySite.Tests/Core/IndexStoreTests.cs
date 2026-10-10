using McpMySite.Core;
using McpMySite.Core.Model;
using McpMySite.Core.Stores;

namespace McpMySite.Tests.Core;

/// <summary>The same behaviour is expected from every store, so each test runs against both.</summary>
public class IndexStoreTests
{
    public static TheoryData<string> StoreTypes => new() { IndexFormat.JsonStoreType, IndexFormat.SqliteStoreType };

    [Theory]
    [MemberData(nameof(StoreTypes))]
    public async Task WritesAndReopensWithManifestAndPages(string storeType)
    {
        var directory = await TestIndex.WriteAsync(storeType);
        using var store = await IndexStoreOpener.OpenAsync(directory);

        Assert.Equal(storeType, store.Manifest.StoreType);
        Assert.Equal(3, store.Manifest.PageCount);
        Assert.Equal(3, store.ListPages().Count);
        Assert.Equal("Rockets", store.ListPages()[1].Title);
    }

    [Theory]
    [MemberData(nameof(StoreTypes))]
    public async Task HybridSearchCombinesKeywordAndVectorRankings(string storeType)
    {
        var directory = await TestIndex.WriteAsync(storeType);
        using var store = await IndexStoreOpener.OpenAsync(directory);

        var response = await store.SearchAsync(new SearchRequest { Query = "kerosene", QueryVector = TestIndex.FuelQuery, Limit = 5 });

        Assert.False(response.KeywordOnly);
        Assert.Equal("https://example.test/docs/rockets", response.Hits[0].Url);
        Assert.Equal(["Fuel"], response.Hits[0].HeadingPath);
        Assert.Contains("kerosene", response.Hits[0].Snippet);
        Assert.Equal(response.Hits.Select(h => h.Url).Distinct().Count(), response.Hits.Count);
    }

    [Theory]
    [MemberData(nameof(StoreTypes))]
    public async Task VectorOnlyMatchStillSurfacesPage(string storeType)
    {
        var directory = await TestIndex.WriteAsync(storeType);
        using var store = await IndexStoreOpener.OpenAsync(directory);

        // "gizmos" matches nothing by keyword; the widget vector must carry the page.
        var response = await store.SearchAsync(new SearchRequest { Query = "gizmos", QueryVector = TestIndex.WidgetQuery, Limit = 5 });

        Assert.Equal("https://example.test/docs/widgets", response.Hits[0].Url);
    }

    [Theory]
    [MemberData(nameof(StoreTypes))]
    public async Task KeywordOnlyWhenNoVector(string storeType)
    {
        var directory = await TestIndex.WriteAsync(storeType);
        using var store = await IndexStoreOpener.OpenAsync(directory);

        var response = await store.SearchAsync(new SearchRequest { Query = "widgets gears", Limit = 5 });

        Assert.True(response.KeywordOnly);
        Assert.Equal("https://example.test/docs/widgets", response.Hits[0].Url);
    }

    [Theory]
    [MemberData(nameof(StoreTypes))]
    public async Task GetPageToleratesUrlVariants(string storeType)
    {
        var directory = await TestIndex.WriteAsync(storeType);
        using var store = await IndexStoreOpener.OpenAsync(directory);

        var page = await store.GetPageAsync("http://EXAMPLE.test/docs/rockets/");

        Assert.NotNull(page);
        Assert.Equal("Rockets", page.Title);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), page.Modified);
        Assert.Contains("liquid oxygen", page.Markdown);
        Assert.Null(await store.GetPageAsync("https://example.test/missing"));
    }

    [Theory]
    [MemberData(nameof(StoreTypes))]
    public async Task RejectsQueryVectorWithWrongDimensions(string storeType)
    {
        var directory = await TestIndex.WriteAsync(storeType);
        using var store = await IndexStoreOpener.OpenAsync(directory);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SearchAsync(new SearchRequest { Query = "x", QueryVector = new float[3] }));
    }

    [Fact]
    public async Task JsonStoreRefusesTruncatedVectorFile()
    {
        var directory = await TestIndex.WriteAsync(IndexFormat.JsonStoreType);
        await File.WriteAllBytesAsync(Path.Combine(directory, IndexFormat.VectorsFileName), new byte[8]);

        await Assert.ThrowsAsync<InvalidDataException>(() => IndexStoreOpener.OpenAsync(directory));
    }

    [Fact]
    public async Task SqliteKeywordSearchIsSafeAgainstFtsSyntax()
    {
        var directory = await TestIndex.WriteAsync(IndexFormat.SqliteStoreType);
        using var store = await IndexStoreOpener.OpenAsync(directory);

        var response = await store.SearchAsync(new SearchRequest { Query = "rockets\" OR NEAR( AND *", Limit = 5 });

        Assert.NotEmpty(response.Hits);
    }
}
