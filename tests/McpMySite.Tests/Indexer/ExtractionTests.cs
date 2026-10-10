using McpMySite.Indexer.Chunking;
using McpMySite.Indexer.Extraction;

namespace McpMySite.Tests.Indexer;

public class HtmlExtractorTests
{
    private readonly HtmlExtractor _extractor = new();

    private const string PageWithMain = """
        <!doctype html>
        <html lang="en-CA">
        <head>
          <title>Rockets | Acme</title>
          <meta name="description" content="All about rockets.">
          <meta property="og:site_name" content="Acme">
          <meta property="article:modified_time" content="2026-03-04T10:00:00Z">
          <link rel="canonical" href="https://acme.test/docs/rockets">
        </head>
        <body>
          <header><nav><a href="/">Home</a><a href="/docs">Docs</a></nav></header>
          <main>
            <article>
              <h1>Rockets</h1>
              <p>Our rockets fly <strong>high</strong>. See <a href="/docs/fuel">fuel</a>.</p>
              <h2>Fuel</h2>
              <p>They burn kerosene.</p>
              <nav class="toc"><a href="#fuel">Fuel</a></nav>
            </article>
          </main>
          <footer><a href="/privacy">Privacy</a><a href="/terms">Terms</a><a href="/contact">Contact</a></footer>
        </body>
        </html>
        """;

    [Fact]
    public void UsesArticleInsideMainAndStripsChrome()
    {
        var page = _extractor.Extract(PageWithMain, "https://acme.test/docs/rockets/?utm=1");

        Assert.Equal("article", page.ContentSource);
        Assert.Equal("https://acme.test/docs/rockets", page.Url);
        Assert.Equal("Rockets", page.Title);
        Assert.Equal("All about rockets.", page.Description);
        Assert.Equal("en-CA", page.Language);
        Assert.Equal("Acme", page.SiteName);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero), page.Modified);
        Assert.Equal(["Rockets", "Fuel"], page.Headings);
        Assert.False(page.NoIndex);

        Assert.Contains("# Rockets", page.Markdown);
        Assert.Contains("**high**", page.Markdown);
        Assert.Contains("[fuel](https://acme.test/docs/fuel)", page.Markdown);
        Assert.DoesNotContain("Privacy", page.Markdown);
        Assert.DoesNotContain("Home", page.Markdown);
        Assert.DoesNotContain("<", page.Markdown);
    }

    [Fact]
    public void FallsBackToDensityHeuristicWithoutMainOrArticle()
    {
        var html = """
            <html><head><title>Plain</title></head><body>
            <div id="menu"><a href="/a">A</a> <a href="/b">B</a> <a href="/c">C</a> <a href="/d">D</a></div>
            <div id="wrapper">
              <div class="sidebar"><a href="/x">X</a><a href="/y">Y</a><a href="/z">Z</a></div>
              <div class="content">
                <h1>Plain page</h1>
                <p>This paragraph carries the real content of the page and is long enough to count as the body text of the document.</p>
                <p>A second paragraph adds more words so the density heuristic has something substantial to find here.</p>
              </div>
            </div>
            </body></html>
            """;

        var page = _extractor.Extract(html, "https://acme.test/plain");

        Assert.Equal("auto", page.ContentSource);
        Assert.Contains("real content", page.Markdown);
        Assert.DoesNotContain("[A](", page.Markdown);
        Assert.Equal("Plain", page.Title);
    }

    [Fact]
    public void DetectsNoIndexAndFallsBackToUndLanguage()
    {
        var html = """<html><head><title>Secret</title><meta name="robots" content="noindex, follow"></head><body><main><p>Hidden text that is long enough.</p></main></body></html>""";
        var page = _extractor.Extract(html, "https://acme.test/secret");

        Assert.True(page.NoIndex);
        Assert.Equal("und", page.Language);
        Assert.Equal("main", page.ContentSource);
    }

    [Fact]
    public void UsesLastModifiedHintWhenPageHasNoDate()
    {
        var hint = new DateTimeOffset(2025, 5, 6, 0, 0, 0, TimeSpan.Zero);
        var page = _extractor.Extract("<html><head><title>T</title></head><body><main><p>Body text here.</p></main></body></html>", "https://acme.test/t", hint);
        Assert.Equal(hint, page.Modified);
    }

    [Theory]
    [InlineData("Rockets | Acme", "Acme", "Rockets")]
    [InlineData("Rockets - Acme", "Acme", "Rockets")]
    [InlineData("Rockets - Other", "Acme", "Rockets - Other")]
    [InlineData("Rockets - Short", null, "Rockets")]
    [InlineData("A | Very long trailing brand that is not short at all and should stay", null, "A | Very long trailing brand that is not short at all and should stay")]
    public void StripsSiteSuffixFromTitle(string title, string? siteName, string expected)
    {
        Assert.Equal(expected, HtmlExtractor.StripSiteSuffix(title, siteName));
    }
}

public class MarkdownChunkerTests
{
    [Fact]
    public void TracksHeadingPathPerSection()
    {
        var chunker = new MarkdownChunker(maxChars: 1000, minChars: 1);
        var chunks = chunker.Chunk("# Title\n\nIntro text.\n\n## Part A\n\nA text.\n\n### Detail\n\nDetail text.\n\n## Part B\n\nB text.");

        Assert.Equal(4, chunks.Count);
        Assert.Equal(["Title"], chunks[0].HeadingPath);
        Assert.Equal(["Title", "Part A"], chunks[1].HeadingPath);
        Assert.Equal(["Title", "Part A", "Detail"], chunks[2].HeadingPath);
        Assert.Equal(["Title", "Part B"], chunks[3].HeadingPath);
        Assert.StartsWith("## Part B", chunks[3].Text);
    }

    [Fact]
    public void SplitsLongSectionsAtParagraphBoundaries()
    {
        var paragraph = string.Join(" ", Enumerable.Repeat("word", 40));
        var markdown = "## Long\n\n" + string.Join("\n\n", Enumerable.Repeat(paragraph, 6));
        var chunks = new MarkdownChunker(maxChars: 500, minChars: 50).Chunk(markdown);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 500));
        Assert.All(chunks, c => Assert.Equal(["Long"], c.HeadingPath));
        Assert.All(chunks, c => Assert.DoesNotContain("word word\nword", c.Text));
    }

    [Fact]
    public void MergesTinySectionsIntoNeighbour()
    {
        var chunks = new MarkdownChunker(maxChars: 1000, minChars: 100).Chunk("# A\n\n## B\n\nShort.\n\n## C\n\n" + string.Join(" ", Enumerable.Repeat("long", 60)));

        Assert.Single(chunks);
        Assert.Contains("# A", chunks[0].Text);
        Assert.Contains("Short.", chunks[0].Text);
    }

    [Fact]
    public void KeepsFencedCodeBlocksIntact()
    {
        var code = "```cs\nvar x = 1;\n\n# not a heading\n```";
        var chunks = new MarkdownChunker(maxChars: 1000, minChars: 1).Chunk("## Code\n\nBefore.\n\n" + code + "\n\nAfter.");

        Assert.Single(chunks);
        Assert.Contains(code, chunks[0].Text);
    }

    [Fact]
    public void HardSplitsOversizedParagraph()
    {
        var huge = new string('x', 2600);
        var chunks = new MarkdownChunker(maxChars: 1000, minChars: 1).Chunk(huge);
        Assert.Equal(3, chunks.Count);
    }
}
