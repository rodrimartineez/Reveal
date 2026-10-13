using System.Text.RegularExpressions;
using Reveal.Core.Text;

namespace Reveal.Core.Vocabulary;

/// <summary>
/// What is already written in the vocabulary document, reduced to comparison keys.
/// Built from the plain text of every paragraph so it works with any previous content
/// (entries written by Reveal, hand written lists, notes...).
/// </summary>
public sealed class VocabularyIndex
{
    private static readonly Regex LabelLine = new(
        @"^\s*(?:example|examples|ejemplo|ejemplos|synonyms?|sin[oó]nimos?|syn|definition|def|translation|traducci[oó]n|note|notes|nota|notas)\s*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SynonymLine = new(
        @"^\s*(?:synonyms?|sin[oó]nimos?|syn)\s*[:：]\s*(?<list>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ListMarker = new(
        @"^\s*(?:\d+\s*[.)]|[-•*·▪●■◦–—])\s+",
        RegexOptions.Compiled);

    private static readonly Regex TrailingParenthetical = new(
        @"\s*[\(\[][^\)\]]*[\)\]]\s*$",
        RegexOptions.Compiled);

    private static readonly string[] Separators = [" — ", " – ", " - ", "—", "–", ":", "：", " -- "];

    private readonly Dictionary<string, string> _heads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _synonyms = new(StringComparer.Ordinal);

    public int HeadCount => _heads.Count;

    public int SynonymCount => _synonyms.Count;

    public static VocabularyIndex Build(IEnumerable<string> paragraphs)
    {
        var index = new VocabularyIndex();
        string? lastHead = null;

        foreach (var raw in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var synMatch = SynonymLine.Match(raw);
            if (synMatch.Success)
            {
                foreach (var syn in SplitList(synMatch.Groups["list"].Value))
                {
                    var key = TextNormalizer.Key(syn);
                    if (key.Length > 0) index._synonyms.TryAdd(key, lastHead ?? "");
                }

                continue;
            }

            if (LabelLine.IsMatch(raw)) continue;

            foreach (var head in ExtractHeads(raw))
            {
                var key = TextNormalizer.Key(head);
                if (key.Length == 0) continue;
                index._heads.TryAdd(key, head.Trim());
                lastHead = head.Trim();
            }
        }

        return index;
    }

    /// <summary>
    /// Looks <paramref name="englishWord"/> up. Returns null when it isn't in the document, otherwise a
    /// description of where it was found.
    /// </summary>
    public DuplicateMatch? Find(string englishWord)
    {
        var key = TextNormalizer.Key(englishWord);
        if (key.Length == 0) return null;

        if (_heads.TryGetValue(key, out var head)) return new DuplicateMatch(DuplicateKind.Entry, head);
        if (_synonyms.TryGetValue(key, out var owner)) return new DuplicateMatch(DuplicateKind.Synonym, owner);
        return null;
    }

    private static IEnumerable<string> ExtractHeads(string paragraph)
    {
        var text = ListMarker.Replace(paragraph.Trim(), "");
        if (text.Length == 0) yield break;

        string head = text;
        var cut = -1;
        foreach (var sep in Separators)
        {
            var i = text.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0 && (cut < 0 || i < cut)) cut = i;
        }

        if (cut > 0) head = text[..cut];

        head = TrailingParenthetical.Replace(head, "").Trim();
        if (head.Length == 0) yield break;

        // Only short fragments are plausible vocabulary headings.
        if (TextNormalizer.WordCount(head) > 8) yield break;

        foreach (var part in head.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }

    private static IEnumerable<string> SplitList(string list) =>
        list.Split([',', ';', '/', '|', '·', '•'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public enum DuplicateKind
{
    Entry,
    Synonym,
}

public sealed record DuplicateMatch(DuplicateKind Kind, string Owner);
