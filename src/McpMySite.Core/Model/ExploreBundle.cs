namespace McpMySite.Core.Model;

/// <summary>
/// The content of explore.json: what a site's index holds, as signals that views can require before they are
/// shown. Written by the indexer next to the index; not part of the index the server loads. New fields are
/// added without bumping <see cref="FormatVersion"/>; readers ignore fields they do not know.
/// </summary>
public sealed record ExploreBundle
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }

    public required string SiteUrl { get; init; }

    public required DateTimeOffset BuiltAt { get; init; }

    public required int PageCount { get; init; }

    public required int ChunkCount { get; init; }

    /// <summary>One entry per signal in <see cref="SignalNames"/>; a signal the probe found nowhere has zeros.</summary>
    public required IReadOnlyDictionary<string, Signal> Profile { get; init; }

    /// <summary>The nodes of the link graph: one entry per indexed page, in id order. Empty in bundles from older indexers.</summary>
    public IReadOnlyList<ExplorePage> Pages { get; init; } = [];

    /// <summary>Internal links between indexed pages as [fromPageId, toPageId], each pair once; a page linking to itself is left out.</summary>
    public IReadOnlyList<int[]> Edges { get; init; } = [];

    /// <summary>Internal link targets that are not in the index, most linked first.</summary>
    public IReadOnlyList<Ghost> Ghosts { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, IndexManifest.JsonOptions);

    public static ExploreBundle Parse(string json)
    {
        var bundle = JsonSerializer.Deserialize<ExploreBundle>(json, IndexManifest.JsonOptions) ?? throw new InvalidDataException("Explore bundle is empty.");
        if (bundle.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException($"Explore bundle format version {bundle.FormatVersion} is not supported. Supported: {CurrentFormatVersion}.");
        }

        return bundle;
    }
}

/// <param name="Pages">Pages that carry the signal at least once.</param>
/// <param name="Count">Occurrences across the site, or the number of distinct values where that is the useful figure.</param>
public sealed record Signal(int Pages, int Count)
{
    public static readonly Signal None = new(0, 0);
}

/// <param name="Id">The page id, as in the index.</param>
/// <param name="Section">The top-level URL section the page belongs to, "/" for the home page.</param>
/// <param name="Words">Whitespace-separated words of Markdown.</param>
/// <param name="Depth">Clicks from the home page over internal links; null when no path leads there, or the home page is not indexed.</param>
/// <param name="Inbound">Indexed pages that link to this one.</param>
/// <param name="Outbound">Indexed pages this one links to.</param>
public sealed record ExplorePage(int Id, string Url, string Section, int Words, int? Depth, int Inbound, int Outbound);

/// <param name="Url">The target, normalised: lower-case host, no trailing slash, no fragment.</param>
/// <param name="Pages">Indexed pages that link to it.</param>
public sealed record Ghost(string Url, int Pages);

/// <summary>Keys of <see cref="ExploreBundle.Profile"/>. Each names what counts as one occurrence.</summary>
public static class SignalNames
{
    /// <summary>Top-level URL sections other than the home page; count is the number of sections.</summary>
    public const string Sections = "sections";

    /// <summary>Headings of any level.</summary>
    public const string Headings = "headings";

    /// <summary>Whitespace-separated words of Markdown.</summary>
    public const string Words = "words";

    /// <summary>Heading-aware chunks.</summary>
    public const string Chunks = "chunks";

    /// <summary>Pages with a last-modified date; count equals pages.</summary>
    public const string Dates = "dates";

    /// <summary>Pages whose language is known; count is the number of distinct languages.</summary>
    public const string Languages = "languages";

    /// <summary>Markdown links to the site's own host.</summary>
    public const string InternalLinks = "internalLinks";

    /// <summary>Markdown links to other hosts.</summary>
    public const string ExternalLinks = "externalLinks";

    public const string Images = "images";

    /// <summary>Markdown tables, counted by their header separator row.</summary>
    public const string Tables = "tables";

    /// <summary>Fenced code blocks.</summary>
    public const string CodeBlocks = "codeBlocks";

    /// <summary>Inline code spans outside fenced blocks.</summary>
    public const string CodeSpans = "codeSpans";

    /// <summary>Bulleted and numbered list items.</summary>
    public const string ListItems = "listItems";

    /// <summary>Chunks with a vector; zero in a keyword-only index.</summary>
    public const string Embeddings = "embeddings";

    /// <summary>Pages with less text than Safety:LowTextChars; count equals pages.</summary>
    public const string ThinPages = "thinPages";

    /// <summary>Pages whose content was picked by the fallback scorer rather than main or article; count equals pages.</summary>
    public const string AutoExtracted = "autoExtracted";

    public static readonly IReadOnlyList<string> All =
    [
        Sections, Headings, Words, Chunks, Dates, Languages, InternalLinks, ExternalLinks, Images,
        Tables, CodeBlocks, CodeSpans, ListItems, Embeddings, ThinPages, AutoExtracted,
    ];
}
