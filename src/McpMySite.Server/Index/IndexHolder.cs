namespace McpMySite.Server.Index;

/// <summary>
/// Holds the current index and swaps in a new one without a restart. The previous store is disposed a little
/// later so requests that already hold a reference can finish.
/// </summary>
public sealed class IndexHolder
{
    private static readonly TimeSpan DisposeDelay = TimeSpan.FromSeconds(30);
    private IIndexStore? _current;

    /// <param name="meterFactory">
    /// Publishes the age and size of the loaded index as gauges, so an index that stopped being rebuilt can be
    /// alerted on. The host supplies it; a holder created without one records nothing.
    /// </param>
    public IndexHolder(IMeterFactory? meterFactory = null)
    {
        if (meterFactory is null)
        {
            return;
        }

        var meter = meterFactory.Create(ServerDiagnostics.Name);
        meter.CreateObservableGauge(
            "mcpmysite.index.age",
            () => Current is { } store ? [new Measurement<double>((DateTimeOffset.UtcNow - store.Manifest.BuiltAt).TotalSeconds)] : Array.Empty<Measurement<double>>(),
            unit: "s",
            description: "Time since the loaded index was built. Absent while no index is loaded.");
        meter.CreateObservableGauge(
            "mcpmysite.index.pages",
            () => Current is { } store ? [new Measurement<int>(store.Manifest.PageCount)] : Array.Empty<Measurement<int>>(),
            description: "Pages in the loaded index.");
    }

    public IIndexStore? Current => Volatile.Read(ref _current);

    public DateTimeOffset? LoadedAt { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? LastErrorAt { get; private set; }

    public void Swap(IIndexStore store)
    {
        var old = Interlocked.Exchange(ref _current, store);
        LoadedAt = DateTimeOffset.UtcNow;
        if (old is not null)
        {
            _ = Task.Delay(DisposeDelay).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
        }
    }

    public void RecordError(string message)
    {
        LastError = message;
        LastErrorAt = DateTimeOffset.UtcNow;
    }

    public IIndexStore Require() => Current ?? throw new IndexUnavailableException(LastError);
}

public sealed class IndexUnavailableException(string? detail)
    : Exception(detail is null ? "No index is loaded yet." : $"No index is loaded. Last load error: {detail}");
