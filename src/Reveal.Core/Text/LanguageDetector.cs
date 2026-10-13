using System.Text.Json;
using Reveal.Core.Lexicon;
using Reveal.Core.Models;

namespace Reveal.Core.Text;

public sealed record LanguageGuess(Lang Language, bool Ambiguous);

/// <summary>
/// Decides whether a selection is English or Spanish, locally: word frequencies in both languages (subtitle
/// corpora), dictionary membership, accent/punctuation hints and a character n-gram model for unknown words.
/// </summary>
public sealed class LanguageDetector
{
    // Reveal is a tool to learn English: ties go to English, and cognates that are far more frequent in Spanish
    // subtitles than in English ones ("personal", "animal") still count as English unless Spanish dominates by 5x or more.
    private const double EnglishBiasWord = 0.7;
    private const double EnglishBiasText = 0.35;

    private readonly ILexicon _lexicon;
    private readonly Dictionary<string, double>? _en;
    private readonly Dictionary<string, double>? _es;
    private readonly double _enUnseen;
    private readonly double _esUnseen;

    public LanguageDetector(ILexicon lexicon, string? langIdPath = null)
    {
        _lexicon = lexicon;
        if (langIdPath is not null && File.Exists(langIdPath))
        {
            try
            {
                var model = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(langIdPath));
                if (model is not null && model.TryGetValue("en", out _en) && model.TryGetValue("es", out _es))
                {
                    _enUnseen = _en.GetValueOrDefault("_unseen", -12);
                    _esUnseen = _es.GetValueOrDefault("_unseen", -12);
                }
            }
            catch
            {
                _en = _es = null;
            }
        }
    }

    public LanguageGuess Detect(string text)
    {
        var words = TextNormalizer.SplitWords(text);
        if (words.Count == 0) return new LanguageGuess(Lang.Unknown, false);

        double score = 0; // > 0 means Spanish
        foreach (var w in words) score += TokenScore(w, words.Count == 1);

        // Character statistics only break ties for words the frequency lists don't settle (cognates like "personal"
        // exist in both languages: letters can't tell them apart).
        var knownInBoth = words.Count == 1 &&
                          _lexicon.Frequency(Lang.English, words[0]) > 0 && _lexicon.Frequency(Lang.Spanish, words[0]) > 0;
        score += NgramScore(words) * (words.Count > 1 ? 0.15 : knownInBoth ? 0.08 : 0.35);

        // Spanish-only punctuation and letters are decisive for sentences.
        if (text.Contains('¿') || text.Contains('¡')) score += 4;
        if (text.Contains('ñ') || text.Contains('Ñ')) score += 2;

        score -= words.Count == 1 ? EnglishBiasWord : EnglishBiasText;
        var ambiguous = Math.Abs(score) < 1.2 && words.Count == 1;
        return new LanguageGuess(score > 0 ? Lang.Spanish : Lang.English, ambiguous);
    }

    /// <summary>log10(freq_es / freq_en) style evidence for one token, in [-3, 3]. Positive = Spanish.</summary>
    private double TokenScore(string word, bool single)
    {
        var lower = word.ToLowerInvariant();
        var fEn = _lexicon.Frequency(Lang.English, lower);
        var fEs = _lexicon.Frequency(Lang.Spanish, lower);

        double evidence = 0;
        if (fEn > 0 || fEs > 0)
        {
            evidence = Math.Clamp(Math.Log10((fEs + 0.05) / (fEn + 0.05)), -3, 3);
        }

        var accents = lower.IndexOfAny(['á', 'é', 'í', 'ó', 'ú', 'ñ', 'ü']) >= 0;
        if (accents && fEn <= 0) evidence += 1.5;                 // accented and not an English word
        else if (accents && fEs > 0 && fEs > fEn) evidence += 0.5;

        if (fEn <= 0 && fEs <= 0)
        {
            // Unknown to both frequency lists: use the dictionaries.
            if (_lexicon.HasEnglishEntry(lower)) evidence -= 1.0;
            if (_lexicon.HasSpanishEntry(lower)) evidence += 1.0;
            evidence += SuffixHint(lower);
        }

        return single ? evidence : Math.Clamp(evidence, -2.0, 2.0);
    }

    private static double SuffixHint(string w)
    {
        string[] es = ["ción", "sión", "dad", "mente", "ando", "iendo", "ar", "er", "ir", "oso", "osa", "ito", "ita", "ía", "ado", "ido", "ales"];
        string[] en = ["tion", "ness", "ing", "ly", "ous", "ful", "able", "ment", "ity", "ed", "er", "ize", "ise"];
        foreach (var s in es) if (w.EndsWith(s, StringComparison.Ordinal) && w.Length > s.Length + 2) return 0.5;
        foreach (var s in en) if (w.EndsWith(s, StringComparison.Ordinal) && w.Length > s.Length + 2) return -0.5;
        return 0;
    }

    /// <summary>Average log-likelihood ratio of character 1-3 grams (positive = Spanish), scaled to roughly [-3, 3].</summary>
    private double NgramScore(IReadOnlyList<string> words)
    {
        if (_en is null || _es is null) return 0;

        double diff = 0;
        var count = 0;
        foreach (var raw in words)
        {
            var w = "_" + raw.ToLowerInvariant() + "_";
            for (var n = 1; n <= 3; n++)
            {
                for (var i = 0; i + n <= w.Length; i++)
                {
                    var g = w.Substring(i, n);
                    var le = _en.GetValueOrDefault(g, _enUnseen);
                    var ls = _es.GetValueOrDefault(g, _esUnseen);
                    diff += ls - le;
                    count++;
                }
            }
        }

        return count == 0 ? 0 : Math.Clamp(diff / count * 6, -3, 3);
    }
}
