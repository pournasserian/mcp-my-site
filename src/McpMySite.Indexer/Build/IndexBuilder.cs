using McpMySite.Core.Stores;
using McpMySite.Indexer.Chunking;
using McpMySite.Indexer.Embeddings;
using McpMySite.Indexer.Extraction;
using McpMySite.Indexer.Sources;
using System.Diagnostics;
using System.Reflection;

namespace McpMySite.Indexer.Build;

/// <summary>Runs one full rebuild: read pages, extract, chunk, embed, check, write.</summary>
public sealed class IndexBuilder(ILogger<IndexBuilder> logger)
{
    public sealed class BuildFailedException(string message) : Exception(message);

    public async Task<BuildReport> RunAsync(IndexerConfig config, string baseDirectory, CancellationToken cancellationToken)
    {
        config.Validate();
        var siteUri = new Uri(config.Site.Url);
        var outputDirectory = Path.GetFullPath(Path.Combine(baseDirectory, config.Output.Directory));
        var report = new BuildReport { SiteUrl = siteUri.ToString(), Store = config.Output.Store };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Hybrid search needs vectors, so refuse to do any work without an embedding provider.
            var generator = EmbeddingGeneratorFactory.Create(config.Embeddings);

            var previous = await ReadPreviousManifestAsync(outputDirectory, cancellationToken);
            report.PreviousPageCount = previous?.PageCount ?? 0;

            var (pages, chunks, embeddingTexts, siteName) = await CollectAsync(config, siteUri, baseDirectory, report, cancellationToken);
            report.PagesIndexed = pages.Count;
            report.Chunks = chunks.Count;

            CheckSafety(config.Safety, report, pages.Count);

            using var cache = config.Cache.Enabled ? new EmbeddingCache(Path.GetFullPath(Path.Combine(baseDirectory, config.Cache.Path))) : null;
            var embedder = new BatchEmbedder(generator, cache, config.Embeddings.Model, config.Embeddings.Dimensions, config.Embeddings.BatchSize, logger);
            var vectors = await embedder.EmbedAsync(embeddingTexts, cancellationToken);
            report.EmbeddingsGenerated = embedder.Embedded;
            report.EmbeddingsFromCache = embedder.CacheHits;

            var builtAt = DateTimeOffset.UtcNow;
            var resolvedSiteName = config.Site.Name ?? siteName ?? siteUri.Host;
            var homeKey = UrlKey.Normalize(siteUri.ToString());
            var siteDescription = config.Site.Description ?? pages.FirstOrDefault(p => UrlKey.Normalize(p.Url) == homeKey)?.Description;
            var manifest = new IndexManifest
            {
                FormatVersion = IndexFormat.CurrentVersion,
                StoreType = config.Output.Store,
                SiteUrl = siteUri.ToString(),
                SiteName = resolvedSiteName,
                SiteDescription = siteDescription,
                EmbeddingModel = config.Embeddings.Model,
                EmbeddingDimensions = config.Embeddings.Dimensions,
                BuiltAt = builtAt,
                PageCount = pages.Count,
                ChunkCount = chunks.Count,
                Instructions = SiteOutline.Build(resolvedSiteName, siteUri.ToString(), siteDescription, pages, builtAt, config.Instructions.Preamble),
                IndexerVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
                Stats = BuildStats(report, config.Source.Type, pages, stopwatch.Elapsed),
            };

            var writer = IndexStoreOpener.CreateWriter(config.Output.Store);
            await writer.WriteAsync(outputDirectory, manifest, pages, chunks, vectors, cancellationToken);
            logger.LogInformation("Wrote {Store} index with {Pages} pages and {Chunks} chunks to {Directory}", config.Output.Store, pages.Count, chunks.Count, outputDirectory);

            report.Succeeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Succeeded = false;
            report.FailureReason = ex.Message;
            logger.LogError("Build failed: {Message}", ex.Message);
        }
        finally
        {
            report.FinishedAt = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(outputDirectory);
            await report.SaveAsync(Path.Combine(outputDirectory, IndexFormat.ReportFileName), CancellationToken.None);
            logger.LogInformation("Finished in {Elapsed:F1}s", stopwatch.Elapsed.TotalSeconds);
        }

        return report;
    }

    private async Task<(List<PageDocument> Pages, List<ChunkRecord> Chunks, List<string> EmbeddingTexts, string? SiteName)> CollectAsync(
        IndexerConfig config, Uri siteUri, string baseDirectory, BuildReport report, CancellationToken cancellationToken)
    {
        var filter = new GlobMatcher(config.Crawl.Include, config.Crawl.Exclude);
        using var http = CreateHttpClient(config.Crawl);
        ISiteSource source = config.Source.Type switch
        {
            SourceType.Folder => new FolderSource(Path.GetFullPath(Path.Combine(baseDirectory, config.Source.Folder!)), siteUri, config.Source.FolderUrlStyle, filter, config.Crawl.MaxPages),
            _ => new CrawlSource(http, config.Crawl, siteUri, filter, logger),
        };

        var extractor = new HtmlExtractor();
        var chunker = new MarkdownChunker(config.Chunking.MaxChars, config.Chunking.MinChars);

        var pages = new List<PageDocument>();
        var chunks = new List<ChunkRecord>();
        var embeddingTexts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? siteName = null;

        await foreach (var raw in source.GetPagesAsync(cancellationToken))
        {
            report.PagesFetched++;
            ExtractedPage extracted;
            try
            {
                extracted = extractor.Extract(raw.Html, raw.Url, raw.LastModifiedHint);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.Errors.Add($"{raw.Url}: extraction failed: {ex.Message}");
                continue;
            }

            siteName ??= extracted.SiteName;

            if (extracted.NoIndex)
            {
                report.PagesSkippedNoIndex++;
                continue;
            }

            if (!seen.Add(UrlKey.Normalize(extracted.Url)))
            {
                report.PagesSkippedDuplicate++;
                continue;
            }

            if (extracted.TextLength == 0)
            {
                report.PagesSkippedEmpty++;
                continue;
            }

            report.ContentSources[extracted.ContentSource] = report.ContentSources.GetValueOrDefault(extracted.ContentSource) + 1;
            if (extracted.TextLength < config.Safety.LowTextChars)
            {
                report.LowTextPages.Add(new BuildReport.LowTextPage(extracted.Url, extracted.TextLength, extracted.ContentSource));
            }

            var page = new PageDocument
            {
                Id = pages.Count + 1,
                Url = extracted.Url,
                Title = extracted.Title,
                Description = extracted.Description,
                Language = extracted.Language,
                Headings = extracted.Headings,
                Modified = extracted.Modified,
                Markdown = extracted.Markdown,
            };
            pages.Add(page);

            var ordinal = 0;
            foreach (var chunk in chunker.Chunk(extracted.Markdown))
            {
                // Drop a leading heading that merely repeats the page title; the title is prefixed anyway.
                var path = chunk.HeadingPath.Count > 0 && string.Equals(chunk.HeadingPath[0], page.Title, StringComparison.OrdinalIgnoreCase)
                    ? chunk.HeadingPath.Skip(1).ToList()
                    : chunk.HeadingPath;

                chunks.Add(new ChunkRecord
                {
                    Id = chunks.Count,
                    PageId = page.Id,
                    Ordinal = ordinal++,
                    HeadingPath = path,
                    Text = chunk.Text,
                });
                embeddingTexts.Add(ChunkRecord.BuildEmbeddingText(page.Title, path, chunk.Text));
            }

            logger.LogInformation("Indexed {Url} ({Chunks} chunks, {Chars} chars, {Source})", page.Url, ordinal, extracted.TextLength, extracted.ContentSource);
        }

        report.Errors.AddRange(source.Errors.Select(e => $"{e.Url}: {e.Message}"));
        return (pages, chunks, embeddingTexts, siteName);
    }

    internal static IndexStats BuildStats(BuildReport report, SourceType source, IReadOnlyList<PageDocument> pages, TimeSpan elapsed) => new()
    {
        Source = source.ToString().ToLowerInvariant(),
        StartedAt = report.StartedAt,
        DurationSeconds = Math.Round(elapsed.TotalSeconds, 1),
        PagesFetched = report.PagesFetched,
        PagesSkippedNoIndex = report.PagesSkippedNoIndex,
        PagesSkippedDuplicate = report.PagesSkippedDuplicate,
        PagesSkippedEmpty = report.PagesSkippedEmpty,
        Errors = report.Errors.Count,
        EmbeddingsGenerated = report.EmbeddingsGenerated,
        EmbeddingsFromCache = report.EmbeddingsFromCache,
        Words = pages.Sum(p => (long)p.Markdown.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length),
        NewestPageModified = pages.Max(p => p.Modified),
        Languages = pages.GroupBy(p => p.Language).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        Sections = SiteOutline.Sections(pages).Select(s => new SectionStats(s.Path, s.Title, s.Pages.Count)).ToList(),
    };

    private static void CheckSafety(SafetyConfig safety, BuildReport report, int pageCount)
    {
        if (pageCount < Math.Max(1, safety.MinPages))
        {
            throw new BuildFailedException($"Only {pageCount} page(s) indexed; the minimum is {Math.Max(1, safety.MinPages)}.");
        }

        if (report.PreviousPageCount > 0)
        {
            var drop = 100.0 * (report.PreviousPageCount - pageCount) / report.PreviousPageCount;
            if (drop > safety.MaxPageDropPercent)
            {
                throw new BuildFailedException(
                    $"Page count fell from {report.PreviousPageCount} to {pageCount} ({drop:F0}%), more than the allowed {safety.MaxPageDropPercent}%. The previous index was kept.");
            }
        }

        if (report.ErrorPercent > safety.MaxErrorPercent)
        {
            throw new BuildFailedException(
                $"{report.Errors.Count} of {report.PagesFetched + report.Errors.Count} pages failed ({report.ErrorPercent:F0}%), more than the allowed {safety.MaxErrorPercent}%.");
        }
    }

    private static async Task<IndexManifest?> ReadPreviousManifestAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(Path.Combine(outputDirectory, IndexFormat.ManifestFileName))
                ? await IndexStoreOpener.ReadManifestAsync(outputDirectory, cancellationToken)
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient(CrawlConfig crawl)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(5, crawl.TimeoutSeconds)) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(crawl.UserAgent);
        return client;
    }
}
