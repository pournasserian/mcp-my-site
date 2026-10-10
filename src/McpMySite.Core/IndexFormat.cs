namespace McpMySite.Core;

/// <summary>Constants that describe the on-disk index contract.</summary>
public static class IndexFormat
{
    /// <summary>Format version written by this build of the indexer.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Versions the server accepts: the current one and the previous one once there is one.</summary>
    public static readonly IReadOnlyList<int> SupportedVersions = [CurrentVersion];

    public const string ManifestFileName = "manifest.json";
    public const string PagesFileName = "pages.json";
    public const string ChunksFileName = "chunks.json";
    public const string VectorsFileName = "vectors.bin";
    public const string SqliteFileName = "index.sqlite";
    public const string ReportFileName = "report.json";

    public const string JsonStoreType = "json";
    public const string SqliteStoreType = "sqlite";

    public static bool IsSupported(int version) => SupportedVersions.Contains(version);
}
