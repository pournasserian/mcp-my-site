using McpMySite.Core.Search;

namespace McpMySite.Tests.Core;

public class TokenizerTests
{
    [Fact]
    public void LowercasesStripsDiacriticsAndSplitsOnPunctuation()
    {
        var tokens = Tokenizer.Tokenize("Café-Résumé, 2026! a");
        Assert.Equal(["cafe", "resume", "2026"], tokens);
    }

    [Fact]
    public void KeepsSingleDigitsButDropsSingleLetters()
    {
        Assert.Equal(["7", "ok"], Tokenizer.Tokenize("7 x ok"));
    }
}

public class Bm25IndexTests
{
    [Fact]
    public void RanksDocumentWithRarerTermHigher()
    {
        var index = new Bm25Index(["rockets fly high", "rockets burn kerosene fuel", "widgets have gears"]);
        var results = index.Search("kerosene rockets", 10);
        Assert.Equal(1, results[0].DocIndex);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void ReturnsEmptyForUnknownTerms()
    {
        var index = new Bm25Index(["rockets fly high"]);
        Assert.Empty(index.Search("zebra", 10));
        Assert.Empty(index.Search("", 10));
    }
}

public class VectorMathTests
{
    [Fact]
    public void TopKOrdersByCosineSimilarity()
    {
        float[] vectors = [1, 0, 0, 1, 0.7f, 0.7f];
        var top = VectorMath.TopK(vectors, 2, [1, 0], 3);
        Assert.Equal([0, 2, 1], top.Select(t => t.Row));
    }

    [Fact]
    public void BytesRoundTrip()
    {
        float[] vector = [0.25f, -1.5f, 3f];
        Assert.Equal(vector, VectorMath.FromBytes(VectorMath.ToBytes(vector)));
    }
}

public class HybridRankerTests
{
    [Fact]
    public void FuseRewardsChunksPresentInBothLists()
    {
        var fused = HybridRanker.Fuse([1, 2, 3], [3, 4]);
        Assert.Equal(3, fused[0].ChunkId);
        Assert.Equal(4, fused.Count);
    }

    [Fact]
    public void ToPageHitsCollapsesToOneHitPerPage()
    {
        var chunks = TestIndex.Chunks.ToDictionary(c => c.Id, c => new HybridRanker.ChunkCandidate(c.Id, c.PageId, c.Ordinal, c.HeadingPath, c.Text));
        var pages = TestIndex.Pages.ToDictionary(p => p.Id);
        var fused = HybridRanker.Fuse([2, 1, 0], []);

        var response = HybridRanker.ToPageHits(fused, id => chunks[id], id => pages[id], new SearchRequest { Query = "rockets", Limit = 10 });

        Assert.Equal(2, response.Hits.Count);
        Assert.Equal("https://example.test/docs/rockets", response.Hits[0].Url);
        Assert.Equal(["Fuel"], response.Hits[0].HeadingPath);
        Assert.Contains("<mark>Rockets</mark>", response.Hits[0].HighlightedSnippet);
        Assert.Equal(2, response.Total);
        Assert.False(response.HasMore);
    }

    [Fact]
    public void ToPageHitsSkipsOffsetPagesAndReportsTotal()
    {
        var chunks = TestIndex.Chunks.ToDictionary(c => c.Id, c => new HybridRanker.ChunkCandidate(c.Id, c.PageId, c.Ordinal, c.HeadingPath, c.Text));
        var pages = TestIndex.Pages.ToDictionary(p => p.Id);
        var fused = HybridRanker.Fuse([2, 1, 0], []);

        var firstPage = HybridRanker.ToPageHits(fused, id => chunks[id], id => pages[id], new SearchRequest { Query = "rockets", Limit = 1 });
        var secondPage = HybridRanker.ToPageHits(fused, id => chunks[id], id => pages[id], new SearchRequest { Query = "rockets", Limit = 1, Offset = 1 });
        var pastTheEnd = HybridRanker.ToPageHits(fused, id => chunks[id], id => pages[id], new SearchRequest { Query = "rockets", Limit = 1, Offset = 5 });

        Assert.Equal("https://example.test/docs/rockets", Assert.Single(firstPage.Hits).Url);
        Assert.True(firstPage.HasMore);
        Assert.Equal("https://example.test/", Assert.Single(secondPage.Hits).Url);
        Assert.False(secondPage.HasMore);
        Assert.Empty(pastTheEnd.Hits);
        Assert.False(pastTheEnd.HasMore);
        Assert.All(new[] { firstPage, secondPage, pastTheEnd }, r => Assert.Equal(2, r.Total));
    }

    [Fact]
    public void CandidateCountGrowsWithTheRequestedPage()
    {
        Assert.Equal(50, new SearchRequest { Query = "x", Limit = 10 }.CandidateCount);
        Assert.Equal(100, new SearchRequest { Query = "x", Limit = 20 }.CandidateCount);
        Assert.Equal(100, new SearchRequest { Query = "x", Limit = 10, Offset = 10 }.CandidateCount);
    }
}

public class SnippetsTests
{
    [Fact]
    public void FlattenRemovesMarkdownSyntax()
    {
        var flat = Snippets.Flatten("## Title\n\nSome **bold** text with a [link](https://x.test) and `code`.\n\n- item one\n- item two");
        Assert.Equal("Title Some bold text with a link and code. item one item two", flat);
    }

    [Fact]
    public void HighlightsWholeWordsOnlyAndEscapesHtml()
    {
        var (plain, highlighted) = Snippets.Build("The cat <sat> on the catalogue.", "cat");
        Assert.Equal("The cat <sat> on the catalogue.", plain);
        Assert.Equal("The <mark>cat</mark> &lt;sat&gt; on the catalogue.", highlighted);
    }

    [Fact]
    public void WindowsAroundFirstMatchInLongText()
    {
        var text = string.Join(" ", Enumerable.Repeat("filler", 100)) + " needle here " + string.Join(" ", Enumerable.Repeat("tail", 50));
        var (plain, _) = Snippets.Build(text, "needle", 120);
        Assert.StartsWith("…", plain);
        Assert.EndsWith("…", plain);
        Assert.Contains("needle", plain);
        Assert.True(plain.Length <= 130);
    }
}

public class UrlKeyTests
{
    [Theory]
    [InlineData("https://Example.test/docs/", "example.test/docs")]
    [InlineData("http://example.test/docs#section", "example.test/docs")]
    [InlineData("https://example.test/", "example.test/")]
    [InlineData("https://example.test", "example.test/")]
    public void NormalizesSchemeCaseSlashAndFragment(string url, string expected)
    {
        Assert.Equal(expected, UrlKey.Normalize(url));
    }
}

public class IndexManifestTests
{
    [Fact]
    public void RejectsUnsupportedFormatVersion()
    {
        var json = TestIndex.Manifest("json").ToJson().Replace("\"formatVersion\": 1", "\"formatVersion\": 99");
        Assert.Throws<InvalidDataException>(() => IndexManifest.Parse(json));
    }

    [Fact]
    public void RejectsEmptyIndex()
    {
        var manifest = TestIndex.Manifest("json") with { PageCount = 0 };
        Assert.Throws<InvalidDataException>(manifest.Validate);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var manifest = TestIndex.Manifest("sqlite");
        Assert.Equal(manifest, IndexManifest.Parse(manifest.ToJson()));
    }
}
