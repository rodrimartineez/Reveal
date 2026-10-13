using System.Globalization;
using System.Text;

namespace Reveal.Core.Text;

public static class TextNormalizer
{
    /// <summary>
    /// Comparison key used for duplicate detection and lexicon lookups: case, accents, spacing and
    /// punctuation are irrelevant ("Well-Known", "well known" and " well  known. " are the same).
    /// Apostrophes are dropped rather than turned into spaces ("don't" == "dont").
    /// </summary>
    public static string Key(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        var pendingSpace = false;

        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.Format) continue;
            if (ch is '\'' or '’' or '‘' or '`' or '´') continue;

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Cleans raw selected text: removes zero-width / control characters, collapses every kind of
    /// whitespace (including line breaks from wrapped text) and trims quotes or punctuation that
    /// surround the selection.
    /// </summary>
    public static string CleanSelection(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var sb = new StringBuilder(raw.Length);
        var pendingSpace = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];

            // Soft hyphen, zero width space/joiners, BOM, directional marks.
            if (ch is '­' or '​' or '‌' or '‍' or '﻿' or '‎' or '‏' or '⁠') continue;

            // Hyphenation at a line break ("recog-\nnize"): glue the pieces back together.
            if (ch == '-' && i + 1 < raw.Length && (raw[i + 1] == '\n' || raw[i + 1] == '\r')
                && sb.Length > 0 && char.IsLetter(sb[^1]))
            {
                var j = i + 1;
                while (j < raw.Length && char.IsWhiteSpace(raw[j])) j++;
                if (j < raw.Length && char.IsLower(raw[j]))
                {
                    i = j - 1;
                    continue;
                }
            }

            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == ' ')
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }

        var text = sb.ToString();
        return TrimEdges(text);
    }

    private static string TrimEdges(string text)
    {
        var start = 0;
        var end = text.Length;

        while (start < end && IsEdgeNoise(text[start])) start++;
        while (end > start && IsEdgeNoise(text[end - 1]))
        {
            // Keep sentence punctuation for real sentences ("How are you?").
            var c = text[end - 1];
            if (c is '?' or '!' or '.' or '…' && WordCount(text.AsSpan(start, end - start)) > 3) break;
            end--;
        }

        return text.Substring(start, end - start).Trim();
    }

    private static bool IsEdgeNoise(char c)
    {
        if (char.IsLetterOrDigit(c)) return false;
        // Inverted marks are meaningful at the start of Spanish sentences, but never at the edge of a single word.
        return true;
    }

    public static int WordCount(ReadOnlySpan<char> text)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            var isWordChar = char.IsLetterOrDigit(ch) || ch is '\'' or '’' or '-';
            if (isWordChar && !inWord) count++;
            inWord = isWordChar;
        }

        return count;
    }

    public static int WordCount(string text) => WordCount(text.AsSpan());

    public static IReadOnlyList<string> SplitWords(string text)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetter(ch) || ch is '\'' or '’' || (sb.Length > 0 && ch == '-'))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0)
            {
                words.Add(sb.ToString().Trim('\'', '’', '-'));
                sb.Clear();
            }
        }

        if (sb.Length > 0) words.Add(sb.ToString().Trim('\'', '’', '-'));
        words.RemoveAll(w => w.Length == 0);
        return words;
    }

    /// <summary>True when the text is a single token (no inner whitespace).</summary>
    public static bool IsSingleWord(string text) => text.Length > 0 && text.IndexOf(' ') < 0;

    public static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
