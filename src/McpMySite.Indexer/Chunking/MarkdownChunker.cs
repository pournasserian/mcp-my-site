using System.Text;
using System.Text.RegularExpressions;

namespace McpMySite.Indexer.Chunking;

/// <summary>
/// Splits Markdown into heading-aware chunks. Each chunk knows the heading path it sits under, so the
/// embedded text can carry page title plus headings. Sections longer than the limit are split at paragraph
/// boundaries; tiny sections are merged with a neighbour.
/// </summary>
public sealed partial class MarkdownChunker(int maxChars = 1500, int minChars = 200)
{
    public sealed record Piece(IReadOnlyList<string> HeadingPath, string Text);

    public List<Piece> Chunk(string markdown)
    {
        var sections = ParseSections(markdown);
        var chunks = new List<Piece>();

        foreach (var section in sections)
        {
            foreach (var text in SplitBlocks(section.Blocks))
            {
                chunks.Add(new Piece(section.Path, text));
            }
        }

        return Merge(chunks);
    }

    private sealed record Section(IReadOnlyList<string> Path, List<string> Blocks);

    private static List<Section> ParseSections(string markdown)
    {
        var sections = new List<Section>();
        var path = new List<(int Level, string Text)>();
        var current = new Section([], []);
        sections.Add(current);

        var paragraph = new StringBuilder();
        var inFence = false;
        var fence = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length > 0)
            {
                current.Blocks.Add(paragraph.ToString().Trim());
                paragraph.Clear();
            }
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (inFence)
            {
                fence.Append(line).Append('\n');
                if (FenceRegex().IsMatch(line))
                {
                    inFence = false;
                    current.Blocks.Add(fence.ToString().TrimEnd());
                    fence.Clear();
                }

                continue;
            }

            if (FenceRegex().IsMatch(line))
            {
                FlushParagraph();
                inFence = true;
                fence.Append(line).Append('\n');
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                var level = heading.Groups[1].Value.Length;
                var text = PlainText(heading.Groups[2].Value.Trim());
                while (path.Count > 0 && path[^1].Level >= level)
                {
                    path.RemoveAt(path.Count - 1);
                }

                path.Add((level, text));
                current = new Section(path.Select(p => p.Text).ToList(), [line]);
                sections.Add(current);
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.Append('\n');
            }

            paragraph.Append(line);
        }

        if (inFence && fence.Length > 0)
        {
            current.Blocks.Add(fence.ToString().TrimEnd());
        }

        FlushParagraph();
        return sections.Where(s => s.Blocks.Count > 0).ToList();
    }

    private IEnumerable<string> SplitBlocks(List<string> blocks)
    {
        var current = new StringBuilder();
        foreach (var block in blocks)
        {
            foreach (var piece in SplitLarge(block))
            {
                if (current.Length > 0 && current.Length + piece.Length + 2 > maxChars)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append("\n\n");
                }

                current.Append(piece);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>Breaks a single oversized block at sentence ends, then hard-wraps as a last resort.</summary>
    private IEnumerable<string> SplitLarge(string block)
    {
        if (block.Length <= maxChars)
        {
            yield return block;
            yield break;
        }

        var sentences = SentenceRegex().Split(block).Where(s => s.Length > 0);
        var current = new StringBuilder();
        foreach (var sentence in sentences)
        {
            if (sentence.Length > maxChars)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString().Trim();
                    current.Clear();
                }

                for (var i = 0; i < sentence.Length; i += maxChars)
                {
                    yield return sentence.Substring(i, Math.Min(maxChars, sentence.Length - i)).Trim();
                }

                continue;
            }

            if (current.Length + sentence.Length > maxChars && current.Length > 0)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }

            current.Append(sentence);
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }

    /// <summary>Folds chunks shorter than the minimum into the next chunk (or the previous one at the end).</summary>
    private List<Piece> Merge(List<Piece> chunks)
    {
        var result = new List<Piece>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (chunk.Text.Length >= minChars)
            {
                result.Add(chunk);
                continue;
            }

            if (i + 1 < chunks.Count && chunk.Text.Length + chunks[i + 1].Text.Length + 2 <= maxChars)
            {
                var next = chunks[i + 1];
                chunks[i + 1] = new Piece(DeeperPath(chunk.HeadingPath, next.HeadingPath), chunk.Text + "\n\n" + next.Text);
                continue;
            }

            if (result.Count > 0 && chunk.Text.Length + result[^1].Text.Length + 2 <= maxChars)
            {
                var previous = result[^1];
                result[^1] = new Piece(previous.HeadingPath, previous.Text + "\n\n" + chunk.Text);
                continue;
            }

            result.Add(chunk);
        }

        return result;
    }

    private static IReadOnlyList<string> DeeperPath(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // When a heading-only chunk is merged into its first subsection, keep the shallower path so the
        // merged chunk still covers the parent heading.
        return a.Count <= b.Count && b.Take(a.Count).SequenceEqual(a) ? a : b;
    }

    /// <summary>
    /// The text of a heading without its inline Markdown, so a heading that is a link, or carries code or emphasis,
    /// enters the heading path the way a reader sees it rather than as source.
    /// </summary>
    public static string PlainText(string heading)
    {
        var text = ImageRegex().Replace(heading, "$1");
        text = LinkRegex().Replace(text, "$1");
        text = CodeRegex().Replace(text, "$1");
        text = EmphasisRegex().Replace(text, "$2");
        return text.Trim();
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\](?:\([^)]*\)|\[[^\]]*\])")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"`+([^`]+)`+")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"(?<!\w)(\*\*|__|\*|_|~~)(\S(?:.*?\S)?)\1(?!\w)")]
    private static partial Regex EmphasisRegex();

    [GeneratedRegex(@"^\s{0,3}(```|~~~)")]
    internal static partial Regex FenceRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9""'\(\[])")]
    private static partial Regex SentenceRegex();
}
