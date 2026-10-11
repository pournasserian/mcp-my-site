using McpMySite.Core;
using McpMySite.Core.Model;
using McpMySite.Core.Stores;
using McpMySite.Server.Options;
using Microsoft.Extensions.Options;

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

    /// <summary>Checks the source for a newer manifest and loads it when it changed. Returns true when a swap happened.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.Url))
            {
                return await RefreshFromUrlAsync(new Uri(_options.Url), cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(_options.Path))
            {
                return await RefreshFromPathAsync(Path.GetFullPath(_options.Path), cancellationToken);
            }

            holder.RecordError("Neither Index:Path nor Index:Url is configured.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            holder.RecordError(ex.Message);
            if (holder.Current is null)
            {
                logger.LogError(ex, "Index load failed and no index is loaded");
            }
            else
            {
                logger.LogWarning(ex, "Index refresh failed; keeping the index built at {BuiltAt}", holder.Current.Manifest.BuiltAt);
            }

            return false;
        }
    }

    private async Task<bool> RefreshFromPathAsync(string directory, CancellationToken cancellationToken)
    {
        var manifest = await IndexStoreOpener.ReadManifestAsync(directory, cancellationToken);
        if (!IsNewer(manifest))
        {
            return false;
        }

        var store = await IndexStoreOpener.OpenAsync(directory, cancellationToken);
        holder.Swap(store);
        logger.LogInformation("Loaded {Store} index from {Directory}: {Pages} pages, built {BuiltAt}", manifest.StoreType, directory, manifest.PageCount, manifest.BuiltAt);
        return true;
    }

    private async Task<bool> RefreshFromUrlAsync(Uri url, CancellationToken cancellationToken)
    {
        var manifestUrl = url.AbsolutePath.EndsWith(IndexFormat.ManifestFileName, StringComparison.OrdinalIgnoreCase)
            ? url
            : new Uri(url.ToString().TrimEnd('/') + "/" + IndexFormat.ManifestFileName);
        var baseUrl = new Uri(manifestUrl, ".");

        var http = httpClientFactory.CreateClient("index");
        var manifestJson = await http.GetStringAsync(manifestUrl, cancellationToken);
        var manifest = IndexManifest.Parse(manifestJson);
        if (!IsNewer(manifest))
        {
            return false;
        }

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

                await using var response = await http.GetStreamAsync(new Uri(baseUrl, fileName), cancellationToken);
                await using var file = File.Create(target);
                await response.CopyToAsync(file, cancellationToken);
            }

            var store = await IndexStoreOpener.OpenAsync(tempDirectory, cancellationToken);
            holder.Swap(store);
            logger.LogInformation("Loaded {Store} index from {Url}: {Pages} pages, built {BuiltAt}", manifest.StoreType, manifestUrl, manifest.PageCount, manifest.BuiltAt);

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

    private bool IsNewer(IndexManifest manifest)
    {
        var current = holder.Current?.Manifest;
        return current is null || current.BuiltAt != manifest.BuiltAt || current.PageCount != manifest.PageCount;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Loads the index at startup and keeps polling for a new one.</summary>
public sealed class IndexRefreshService(IndexLoader loader, IOptions<IndexOptions> options, ILogger<IndexRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!loader.IsConfigured)
        {
            logger.LogError("No index source configured. Set Index:Path or Index:Url.");
            return;
        }

        await loader.RefreshAsync(stoppingToken);

        var interval = options.Value.PollIntervalSeconds;
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
