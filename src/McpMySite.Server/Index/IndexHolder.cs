using McpMySite.Core.Stores;

namespace McpMySite.Server.Index;

/// <summary>
/// Holds the current index and swaps in a new one without a restart. The previous store is disposed a little
/// later so requests that already hold a reference can finish.
/// </summary>
public sealed class IndexHolder
{
    private static readonly TimeSpan DisposeDelay = TimeSpan.FromSeconds(30);
    private IIndexStore? _current;

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
