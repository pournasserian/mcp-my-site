namespace McpMySite.Server.Index;

/// <summary>
/// Loads the index from a local directory or a URL. Each load validates the manifest first and only
/// swaps the new store in once it opened successfully, so a broken publish never replaces a working index.
/// </summary>
public sealed class IndexLoader(
    IndexHolder holder,
    IOptions<IndexOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<IndexLoader> logger)
{
    private readonly IndexOptions _options = options.Value;
    private string? _currentTempDirectory;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Path) || !string.IsNullOrWhiteSpace(_options.Url);

    /// <summary>Where the index comes from, safe to log: the URL without its query string, or the full local path.</summary>
    public string? Source =>
        !string.IsNullOrWhiteSpace(_options.Url) && Uri.TryCreate(_options.Url, UriKind.Absolute, out var url) ? ServerDiagnostics.Redact(url)
        : !string.IsNullOrWhiteSpace(_options.Path) ? Path.GetFullPath(_options.Path)
        : null;

    /// <summary>Checks the source for a newer manifest and loads it when it changed. Returns true when a swap happened.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        using var activity = ServerDiagnostics.ActivitySource.StartActivity("mcpmysite.index.load");
        var stopwatch = Stopwatch.StartNew();
        string? outcome = null;
        try
        {
            bool swapped;
            if (!string.IsNullOrWhiteSpace(_options.Url))
            {
                activity?.SetTag("mcpmysite.index.source", "url");
                swapped = await RefreshFromUrlAsync(new Uri(_options.Url), cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(_options.Path))
            {
                activity?.SetTag("mcpmysite.index.source", "path");
                swapped = await RefreshFromPathAsync(Path.GetFullPath(_options.Path), cancellationToken);
            }
            else
            {
                outcome = "failed";
                holder.RecordError("Neither Index:Path nor Index:Url is configured.");
                return false;
            }

            outcome = swapped ? "loaded" : "unchanged";
            return swapped;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = "failed";
            holder.RecordError(ex.Message);
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            if (holder.Current is null)
            {
                logger.LogError(ex, "Index load failed and no index is loaded");
            }
            else
            {
                logger.LogWarning(ex, "Index refresh failed; keeping the index built at {BuiltAt:u}", holder.Current.Manifest.BuiltAt);
            }

            return false;
        }
        finally
        {
            // No outcome means the load was cancelled, which only happens when the server shuts down.
            outcome ??= "cancelled";
            var tags = new TagList { { "outcome", outcome } };
            ServerDiagnostics.IndexLoads.Add(1, tags);
            ServerDiagnostics.IndexLoadDuration.Record(stopwatch.Elapsed.TotalMilliseconds, tags);
            activity?.SetTag("mcpmysite.index.outcome", outcome);
        }
    }

    private async Task<bool> RefreshFromPathAsync(string directory, CancellationToken cancellationToken)
    {
        var manifest = await IndexStoreOpener.ReadManifestAsync(directory, cancellationToken);
        if (!IsNewer(manifest))
        {
            logger.LogDebug("Index in {Source} unchanged (built {BuiltAt:u})", directory, manifest.BuiltAt);
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        var store = await IndexStoreOpener.OpenAsync(directory, cancellationToken);
        holder.Swap(store);
        LogLoaded(manifest, directory, stopwatch.Elapsed);
        return true;
    }

    private async Task<bool> RefreshFromUrlAsync(Uri url, CancellationToken cancellationToken)
    {
        var manifestUrl = url.AbsolutePath.EndsWith(IndexFormat.ManifestFileName, StringComparison.OrdinalIgnoreCase)
            ? url
            : new Uri(url.ToString().TrimEnd('/') + "/" + IndexFormat.ManifestFileName);
        var baseUrl = new Uri(manifestUrl, ".");
        var source = ServerDiagnostics.Redact(manifestUrl);

        var http = httpClientFactory.CreateClient("index");
        var manifestJson = await http.GetStringAsync(manifestUrl, cancellationToken);
        var manifest = IndexManifest.Parse(manifestJson);
        if (!IsNewer(manifest))
        {
            logger.LogDebug("Index at {Source} unchanged (built {BuiltAt:u})", source, manifest.BuiltAt);
            return false;
        }

        logger.LogInformation("Downloading the {Store} index built {BuiltAt:u} from {Source}", manifest.StoreType, manifest.BuiltAt, source);
        var stopwatch = Stopwatch.StartNew();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "mcpmysite-index", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            foreach (var fileName in IndexStoreOpener.FileNames(manifest.StoreType))
            {
                var target = Path.Combine(tempDirectory, fileName);
                if (fileName == IndexFormat.ManifestFileName)
                {
                    await File.WriteAllTextAsync(target, manifestJson, cancellationToken);
                    continue;
                }

                await using (var response = await http.GetStreamAsync(new Uri(baseUrl, fileName), cancellationToken))
                await using (var file = File.Create(target))
                {
                    await response.CopyToAsync(file, cancellationToken);
                }

                logger.LogDebug("Downloaded {File} ({Bytes} bytes) to {Directory}", fileName, new FileInfo(target).Length, tempDirectory);
            }

            var store = await IndexStoreOpener.OpenAsync(tempDirectory, cancellationToken);
            holder.Swap(store);
            LogLoaded(manifest, source, stopwatch.Elapsed);

            var previous = _currentTempDirectory;
            _currentTempDirectory = tempDirectory;
            if (previous is not null)
            {
                _ = Task.Delay(TimeSpan.FromMinutes(1), CancellationToken.None).ContinueWith(_ => TryDelete(previous), TaskScheduler.Default);
            }

            return true;
        }
        catch
        {
            TryDelete(tempDirectory);
            throw;
        }
    }

    private void LogLoaded(IndexManifest manifest, string source, TimeSpan elapsed) =>
        logger.LogInformation(
            "Loaded {Store} index from {Source} in {ElapsedMs:F0} ms: {Pages} pages, {Chunks} chunks, built {BuiltAt:u}, embeddings {EmbeddingModel} ({EmbeddingDimensions} dimensions)",
            manifest.StoreType, source, elapsed.TotalMilliseconds, manifest.PageCount, manifest.ChunkCount, manifest.BuiltAt, manifest.EmbeddingModel, manifest.EmbeddingDimensions);

    private bool IsNewer(IndexManifest manifest)
    {
        var current = holder.Current?.Manifest;
        return current is null || current.BuiltAt != manifest.BuiltAt || current.PageCount != manifest.PageCount;
    }

    private void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind in the temp folder; harmless, so not worth more than a debug line.
            logger.LogDebug("Could not delete old index files in {Directory}: {Message}", directory, ex.Message);
        }
    }
}

/// <summary>Loads the index at startup and keeps polling for a new one.</summary>
public sealed class IndexRefreshService(IndexLoader loader, IndexHolder holder, IOptions<IndexOptions> options, ILogger<IndexRefreshService> logger) : BackgroundService
{
    private const string NotConfiguredError = "No index source configured. Set Index:Path or Index:Url.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!loader.IsConfigured)
        {
            // Recorded as well as logged so /healthz explains its 503 instead of showing lastError: null.
            holder.RecordError(NotConfiguredError);
            logger.LogError(NotConfiguredError);
            return;
        }

        var interval = options.Value.PollIntervalSeconds;
        if (interval > 0)
        {
            logger.LogInformation("Loading the index from {Source}, then checking for a new one every {Interval} s", loader.Source, interval);
        }
        else
        {
            logger.LogInformation("Loading the index from {Source} once; polling is off", loader.Source);
        }

        await loader.RefreshAsync(stoppingToken);
        if (interval <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await loader.RefreshAsync(stoppingToken);
        }
    }
}
