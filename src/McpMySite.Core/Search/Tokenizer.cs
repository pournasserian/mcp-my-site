namespace McpMySite.Core.Search;

/// <summary>
/// Language-neutral tokenizer: lower-cases, strips diacritics and splits on anything that is not a letter or digit.
/// Used by the in-memory BM25 index and for snippet highlighting. Kept deliberately simple; no stemming.
/// </summary>
public static class Tokenizer
{
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        var sb = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                sb.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else if (sb.Length > 0)
            {
                Flush(sb, tokens);
            }
        }

        if (sb.Length > 0)
        {
            Flush(sb, tokens);
        }

        return tokens;

        static void Flush(StringBuilder sb, List<string> tokens)
        {
            if (sb.Length > 1 || char.IsDigit(sb[0]))
            {
                tokens.Add(sb.ToString());
            }

            sb.Clear();
        }
    }
}
