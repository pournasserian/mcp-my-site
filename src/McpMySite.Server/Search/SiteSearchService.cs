namespace McpMySite.Server.Search;

/// <summary>
/// The one search path shared by the MCP tool and the HTTP search API. Embeds the query with the model the
/// index was built with; if the provider fails, the request is answered from keyword search and the fallback is recorded.
/// </summary>
public sealed class SiteSearchService : IDisposable
{
    private readonly IndexHolder _holder;
    private readonly EmbeddingOptions _embeddingOptions;
    private readonly TelemetryOptions _telemetry;
    private readonly ILogger<SiteSearchService> _logger;
    private readonly object _lock = new();
    private IEmbeddingGenerator<string, Embedding<float>>? _generator;
    private (string? Model, int Dimensions)? _generatorKey;
    private int _embeddingFailing;

    public SiteSearchService(IndexHolder holder, IOptions<EmbeddingOptions> embeddingOptions, IOptions<TelemetryOptions> telemetry, ILogger<SiteSearchService> logger)
    {
        _holder = holder;
        _embeddingOptions = embeddingOptions.Value;
        _telemetry = telemetry.Value;
        _logger = logger;
    }

    public async Task<SearchResponse> SearchAsync(string query, int limit, int offset, string channel, CancellationToken cancellationToken)
    {
        using var activity = ServerDiagnostics.ActivitySource.StartActivity("mcpmysite.search");
        activity?.SetTag("mcpmysite.channel", channel);
        activity?.SetTag("mcpmysite.limit", limit);
        activity?.SetTag("mcpmysite.offset", offset);
        if (_telemetry.LogQueryText)
        {
            activity?.SetTag("mcpmysite.query", query);
        }

        var store = RequireIndex(activity, "Search", channel);
        var stopwatch = Stopwatch.StartNew();
        ReadOnlyMemory<float>? vector = null;
        var fallback = false;
        try
        {
            // A keyword-only index has no vectors to compare against, so the query is not embedded either.
            if (!store.Manifest.KeywordOnly && _embeddingOptions.Enabled)
            {
                vector = await GetGenerator(store.Manifest).GenerateVectorAsync(query, cancellationToken: cancellationToken);
            }

            if (Interlocked.Exchange(ref _embeddingFailing, 0) == 1)
            {
                _logger.LogInformation("Query embedding works again; searches are hybrid again");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fallback = true;
            activity?.AddException(ex);

            // Warn once per outage rather than per search, so a missing key or a provider outage does not write a
            // stack trace for every query. Each fallback is still counted, tagged and noted in the search line below.
            if (Interlocked.Exchange(ref _embeddingFailing, 1) == 0)
            {
                _logger.LogWarning(ex, "Embedding failed; answering from keyword search until it recovers ({Channel})", channel);
            }
            else
            {
                _logger.LogDebug(ex, "Embedding failed again; answering from keyword search ({Channel})", channel);
            }
        }

        var response = await store.SearchAsync(new SearchRequest { Query = query, QueryVector = vector, Limit = limit, Offset = offset }, cancellationToken);
        var elapsed = stopwatch.Elapsed.TotalMilliseconds;

        var tags = new TagList { { "channel", channel }, { "fallback", fallback } };
        ServerDiagnostics.SearchRequests.Add(1, tags);
        ServerDiagnostics.SearchDuration.Record(elapsed, tags);
        activity?.SetTag("mcpmysite.fallback", fallback);
        activity?.SetTag("mcpmysite.hits", response.Hits.Count);

        if (_telemetry.LogQueryText)
        {
            _logger.LogInformation("Search ({Channel}) {Query} -> {Hits} hits in {ElapsedMs:F0} ms (limit {Limit}, keyword fallback {Fallback})", channel, query, response.Hits.Count, elapsed, limit, fallback);
        }
        else
        {
            _logger.LogInformation("Search ({Channel}) -> {Hits} hits in {ElapsedMs:F0} ms (limit {Limit}, keyword fallback {Fallback})", channel, response.Hits.Count, elapsed, limit, fallback);
        }

        return response;
    }

    public async Task<PageDocument?> FetchAsync(string url, string channel, CancellationToken cancellationToken)
    {
        using var activity = ServerDiagnostics.ActivitySource.StartActivity("mcpmysite.fetch");
        activity?.SetTag("mcpmysite.channel", channel);

        var store = RequireIndex(activity, "Fetch", channel);
        var stopwatch = Stopwatch.StartNew();
        var page = await store.GetPageAsync(url, cancellationToken);
        var elapsed = stopwatch.Elapsed.TotalMilliseconds;

        var tags = new TagList { { "channel", channel }, { "found", page is not null } };
        ServerDiagnostics.FetchRequests.Add(1, tags);
        ServerDiagnostics.FetchDuration.Record(elapsed, tags);
        activity?.SetTag("mcpmysite.found", page is not null);

        // A page that was found has a public URL from the index. A miss is whatever the client sent, so it is
        // treated like query text.
        if (page is not null)
        {
            activity?.SetTag("mcpmysite.url", page.Url);
            _logger.LogInformation("Fetch ({Channel}) {Url} in {ElapsedMs:F0} ms", channel, page.Url, elapsed);
        }
        else if (_telemetry.LogQueryText)
        {
            activity?.SetTag("mcpmysite.url", url);
            _logger.LogInformation("Fetch ({Channel}) {Url}: no such page", channel, url);
        }
        else
        {
            _logger.LogInformation("Fetch ({Channel}): no such page", channel);
        }

        return page;
    }

    private IIndexStore RequireIndex(Activity? activity, string operation, string channel)
    {
        try
        {
            return _holder.Require();
        }
        catch (IndexUnavailableException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning("{Operation} ({Channel}) refused: {Reason}", operation, channel, ex.Message);
            throw;
        }
    }

    private IEmbeddingGenerator<string, Embedding<float>> GetGenerator(IndexManifest manifest)
    {
        lock (_lock)
        {
            var key = (manifest.EmbeddingModel, manifest.EmbeddingDimensions);
            if (_generator is null || _generatorKey != key)
            {
                _generator?.Dispose();
                _generator = EmbeddingGeneratorFactory.Create(_embeddingOptions, manifest.EmbeddingModel, manifest.EmbeddingDimensions);
                _generatorKey = key;
                _logger.LogInformation(
                    "Embedding queries with {Model} ({Dimensions} dimensions) at {Endpoint}",
                    manifest.EmbeddingModel, manifest.EmbeddingDimensions, _embeddingOptions.Endpoint);
            }

            return _generator;
        }
    }

    public void Dispose() => _generator?.Dispose();
}
