using System.Diagnostics;
using System.Diagnostics.Metrics;
using McpMySite.Core.Embeddings;
using McpMySite.Core.Model;
using McpMySite.Server.Index;
using McpMySite.Server.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace McpMySite.Server.Search;

/// <summary>
/// The one search path shared by the MCP tool and the HTTP search API. Embeds the query with the model the
/// index was built with; if the provider fails, the request is answered from keyword search and the fallback is recorded.
/// </summary>
public sealed class SiteSearchService : IDisposable
{
    public const string ActivitySourceName = "McpMySite.Server";
    public const string MeterName = "McpMySite.Server";

    private static readonly ActivitySource Activity = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> SearchCounter = Meter.CreateCounter<long>("mcpmysite.search.requests", description: "Search requests by channel and fallback state.");
    private static readonly Histogram<double> SearchDuration = Meter.CreateHistogram<double>("mcpmysite.search.duration", unit: "ms");
    private static readonly Counter<long> FetchCounter = Meter.CreateCounter<long>("mcpmysite.fetch.requests");

    private readonly IndexHolder _holder;
    private readonly EmbeddingOptions _embeddingOptions;
    private readonly TelemetryOptions _telemetry;
    private readonly ILogger<SiteSearchService> _logger;
    private readonly object _lock = new();
    private IEmbeddingGenerator<string, Embedding<float>>? _generator;
    private (string Model, int Dimensions)? _generatorKey;

    public SiteSearchService(IndexHolder holder, IOptions<EmbeddingOptions> embeddingOptions, IOptions<TelemetryOptions> telemetry, ILogger<SiteSearchService> logger)
    {
        _holder = holder;
        _embeddingOptions = embeddingOptions.Value;
        _telemetry = telemetry.Value;
        _logger = logger;
    }

    public async Task<SearchResponse> SearchAsync(string query, int limit, string channel, CancellationToken cancellationToken)
    {
        var store = _holder.Require();
        using var activity = Activity.StartActivity("mcpmysite.search");
        activity?.SetTag("mcpmysite.channel", channel);
        activity?.SetTag("mcpmysite.limit", limit);
        if (_telemetry.LogQueryText)
        {
            activity?.SetTag("mcpmysite.query", query);
        }

        var stopwatch = Stopwatch.StartNew();
        ReadOnlyMemory<float>? vector = null;
        var fallback = false;
        try
        {
            vector = await GetGenerator(store.Manifest).GenerateVectorAsync(query, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fallback = true;
            _logger.LogWarning(ex, "Embedding failed; answering from keyword search ({Channel})", channel);
        }

        var response = await store.SearchAsync(new SearchRequest { Query = query, QueryVector = vector, Limit = limit }, cancellationToken);

        var tags = new TagList { { "channel", channel }, { "fallback", fallback } };
        SearchCounter.Add(1, tags);
        SearchDuration.Record(stopwatch.Elapsed.TotalMilliseconds, tags);
        activity?.SetTag("mcpmysite.fallback", fallback);
        activity?.SetTag("mcpmysite.hits", response.Hits.Count);

        if (_telemetry.LogQueryText)
        {
            _logger.LogInformation("Search ({Channel}) {Query} -> {Hits} hits in {Elapsed} ms{Fallback}", channel, query, response.Hits.Count, stopwatch.ElapsedMilliseconds, fallback ? " (keyword fallback)" : string.Empty);
        }
        else
        {
            _logger.LogInformation("Search ({Channel}) -> {Hits} hits in {Elapsed} ms{Fallback}", channel, response.Hits.Count, stopwatch.ElapsedMilliseconds, fallback ? " (keyword fallback)" : string.Empty);
        }

        return response;
    }

    public async Task<PageDocument?> FetchAsync(string url, string channel, CancellationToken cancellationToken)
    {
        var store = _holder.Require();
        using var activity = Activity.StartActivity("mcpmysite.fetch");
        activity?.SetTag("mcpmysite.channel", channel);
        var page = await store.GetPageAsync(url, cancellationToken);
        FetchCounter.Add(1, new TagList { { "channel", channel }, { "found", page is not null } });
        activity?.SetTag("mcpmysite.found", page is not null);
        return page;
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
            }

            return _generator;
        }
    }

    public void Dispose() => _generator?.Dispose();
}
