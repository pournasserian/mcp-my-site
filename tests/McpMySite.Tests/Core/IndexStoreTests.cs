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
    public async Task OffsetReturnsTheNextSliceOfTheSameRanking(string storeType)
    {
        var directory = await TestIndex.WriteAsync(storeType);
        using var store = await IndexStoreOpener.OpenAsync(directory);

        var all = await store.SearchAsync(new SearchRequest { Query = "rockets", Limit = 5 });
        var firstPage = await store.SearchAsync(new SearchRequest { Query = "rockets", Limit = 1 });
        var secondPage = await store.SearchAsync(new SearchRequest { Query = "rockets", Limit = 1, Offset = 1 });
        var pastTheEnd = await store.SearchAsync(new SearchRequest { Query = "rockets", Limit = 1, Offset = 5 });

        Assert.Equal(2, all.Hits.Count);
        Assert.False(all.HasMore);
        Assert.Equal(all.Hits[0].Url, Assert.Single(firstPage.Hits).Url);
        Assert.True(firstPage.HasMore);
        Assert.Equal(all.Hits[1].Url, Assert.Single(secondPage.Hits).Url);
        Assert.False(secondPage.HasMore);
        Assert.Empty(pastTheEnd.Hits);
        Assert.False(pastTheEnd.HasMore);
        Assert.All(new[] { all, firstPage, secondPage, pastTheEnd }, r => Assert.Equal(2, r.Total));
    }

    [Theory]
    [MemberData(nameof(StoreTypes))]
    public async Task TotalIsTheSameOnEveryPageOfOneQuery(string storeType)
    {
        // More matching pages than the old first-page candidate pool held, so a pool grown from the offset would
        // find more of them on a later page and report a larger total there (#82).
        var pages = Enumerable.Range(1, 70).Select(i => new PageDocument
        {
            Id = i, Url = $"https://example.test/rockets/{i}", Title = $"Rocket {i}", Language = "en", Headings = [], Markdown = $"Rocket {i} flies.",
        }).ToList();
        var chunks = pages.Select(p => new ChunkRecord { Id = p.Id, PageId = p.Id, Ordinal = 0, HeadingPath = [], Text = $"Rocket {p.Id} flies with rockets." }).ToList();
        var directory = await TestIndex.WriteAsync(storeType, pages, chunks);
        using var store = await IndexStoreOpener.OpenAsync(directory);

        var firstPage = await store.SearchAsync(new SearchRequest { Query = "rockets", Limit = 1 });
        var laterPage = await store.SearchAsync(new SearchRequest { Query = "rockets", Limit = 1, Offset = 60 });

        Assert.Equal(70, firstPage.Total);
        Assert.Equal(firstPage.Total, laterPage.Total);
        Assert.Single(laterPage.Hits);
        Assert.True(laterPage.HasMore);
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

    [Theory]
    [MemberData(nameof(StoreTypes))]
    public async Task KeywordOnlyIndexHasNoVectorsAndSearchesByKeyword(string storeType)
    {
        var directory = await TestIndex.WriteAsync(storeType, keywordOnly: true);
        using var store = await IndexStoreOpener.OpenAsync(directory);

        Assert.True(store.Manifest.KeywordOnly);
        Assert.Null(store.Manifest.EmbeddingModel);
        Assert.Equal(0, store.Manifest.EmbeddingDimensions);
        Assert.Equal(4, store.Manifest.ChunkCount);

        var response = await store.SearchAsync(new SearchRequest { Query = "kerosene", Limit = 5 });
        Assert.True(response.KeywordOnly);
        Assert.Equal("https://example.test/docs/rockets", response.Hits[0].Url);
        Assert.Contains("<mark>kerosene</mark>", response.Hits[0].HighlightedSnippet);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SearchAsync(new SearchRequest { Query = "kerosene", QueryVector = TestIndex.FuelQuery }));
    }

    [Fact]
    public void ManifestRequiresEmbeddingsUnlessKeywordOnly()
    {
        var hybrid = TestIndex.Manifest(IndexFormat.JsonStoreType);
        var keywordOnly = TestIndex.Manifest(IndexFormat.JsonStoreType, keywordOnly: true);

        IndexManifest.Parse(keywordOnly.ToJson()).Validate();
        Assert.Contains("\"keywordOnly\": true", keywordOnly.ToJson());
        Assert.DoesNotContain("embeddingModel", keywordOnly.ToJson());
        Assert.Throws<InvalidDataException>(() => (hybrid with { EmbeddingModel = null }).Validate());
        Assert.Throws<InvalidDataException>(() => (hybrid with { KeywordOnly = true }).Validate());
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
