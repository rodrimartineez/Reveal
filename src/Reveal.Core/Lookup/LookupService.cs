using Reveal.Core.Lexicon;
using Reveal.Core.Models;
using Reveal.Core.Text;
using Reveal.Core.Translation;

namespace Reveal.Core.Lookup;

/// <summary>
/// Turns a selection into everything shown on the card: language detection, English headword, Spanish
/// meanings (offline lexicon + local MT), synonyms, example and phonetics. Results are reported
/// progressively: lexicon data first (instant), translation as soon as the engine answers.
/// </summary>
public sealed class LookupService
{
    public const int MaxSelectionChars = 300;
    private const int MaxTranslations = 3;
    private const int MaxSynonyms = 4;

    private readonly ILexicon _lexicon;
    private readonly LanguageDetector _detector;
    private readonly TranslationService _translator;
    private readonly Dictionary<string, LookupResult> _cache = new();
    private readonly Queue<string> _cacheOrder = new();
    private readonly object _cacheGate = new();

    public LookupService(ILexicon lexicon, LanguageDetector detector, TranslationService translator)
    {
        _lexicon = lexicon;
        _detector = detector;
        _translator = translator;
    }

    /// <summary>Forget everything (called when capture stops).</summary>
    public void ClearCache()
    {
        lock (_cacheGate)
        {
            _cache.Clear();
            _cacheOrder.Clear();
        }
    }

    public LanguageGuess Detect(string cleanedText) => _detector.Detect(cleanedText);

    public async Task<LookupResult> LookupAsync(
        string rawSelection, Lang? forcedLanguage, Action<LookupResult>? onPartial, CancellationToken ct)
    {
        var text = TextNormalizer.CleanSelection(rawSelection);
        if (text.Length == 0) return Failed("", "No hay texto seleccionado.");
        if (text.Length > MaxSelectionChars)
        {
            return Failed(text[..40] + "…", "La selección es demasiado larga. Selecciona una palabra o una frase corta.");
        }

        if (!text.Any(char.IsLetter)) return Failed(text, "La selección no contiene texto que traducir.");

        var language = forcedLanguage ?? _detector.Detect(text).Language;
        var cacheKey = language + "|" + text;
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(cacheKey, out var hit)) return hit;
        }

        var result = language == Lang.Spanish
            ? await LookupSpanishAsync(text, onPartial, ct).ConfigureAwait(false)
            : await LookupEnglishAsync(text, onPartial, ct).ConfigureAwait(false);

        if (result.Status == LookupStatus.Complete && !result.IsTranslationPending)
        {
            lock (_cacheGate)
            {
                if (_cache.TryAdd(cacheKey, result))
                {
                    _cacheOrder.Enqueue(cacheKey);
                    while (_cacheOrder.Count > 64) _cache.Remove(_cacheOrder.Dequeue());
                }
            }
        }

        return result;
    }

    // ------------------------------------------------------------------------------------ English

    private async Task<LookupResult> LookupEnglishAsync(string text, Action<LookupResult>? onPartial, CancellationToken ct)
    {
        var sentence = IsSentence(text);
        var words = TextNormalizer.WordCount(text);
        var match = !sentence && words <= 5 ? _lexicon.FindEnglish(text) : null;
        var entry = match?.Entry;
        var direct = match is { ViaLemma: false };

        var headword = ChooseHeadword(text, entry);
        var dictionaryMeanings = direct ? entry!.Translations.Take(MaxTranslations + 1).ToList() : [];

        var baseResult = new LookupResult
        {
            SourceText = text,
            SourceLanguage = Lang.English,
            Headword = headword,
            IsSentence = sentence,
            Synonyms = entry?.Synonyms.Take(MaxSynonyms).ToList() ?? [],
            Example = entry?.Example,
            Ipa = entry?.IpaUs ?? entry?.IpaUk,
            PartOfSpeech = entry?.PartOfSpeech,
        };

        var needTranslation = sentence || dictionaryMeanings.Count < 2;
        var partial = baseResult with
        {
            Translations = TopDistinct(dictionaryMeanings, MaxTranslations),
            IsTranslationPending = needTranslation,
            Status = LookupStatus.Partial,
        };
        if (needTranslation) onPartial?.Invoke(partial);

        if (!needTranslation)
        {
            return partial with { IsTranslationPending = false, Status = LookupStatus.Complete, TranslationEngine = "lexicon" };
        }

        try
        {
            // Sentences get one translation; phrases one plus a possible variant; single words up to three meanings.
            var alternatives = sentence ? 0 : words > 1 ? 1 : 2;
            var mt = await _translator.TranslateAsync(text, Lang.English, Lang.Spanish, alternatives, ct).ConfigureAwait(false);
            var meanings = new List<string>(dictionaryMeanings);
            AddMeaning(meanings, mt.Text, text);
            if (!sentence) foreach (var alt in mt.Alternatives) AddMeaning(meanings, alt, text);

            var top = TopDistinct(meanings, MaxTranslations);
            var message = top.Count == 0 ? "Sin traducción: parece un nombre propio o una palabra desconocida." : null;
            return baseResult with
            {
                Translations = top,
                IsTranslationPending = false,
                Status = top.Count > 0 || baseResult.Synonyms.Count > 0 ? LookupStatus.Complete : LookupStatus.Failed,
                Message = message,
                TranslationEngine = _translator.LastEngine,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TranslationUnavailableException ex)
        {
            return FinishWithoutMt(baseResult, dictionaryMeanings, ex.Message);
        }
        catch (Exception ex)
        {
            return FinishWithoutMt(baseResult, dictionaryMeanings, "No se pudo traducir: " + ex.Message);
        }
    }

    private static LookupResult FinishWithoutMt(LookupResult baseResult, List<string> dictionaryMeanings, string message) =>
        baseResult with
        {
            Translations = TopDistinct(dictionaryMeanings, MaxTranslations),
            IsTranslationPending = false,
            Status = dictionaryMeanings.Count > 0 || baseResult.Synonyms.Count > 0 || baseResult.Example is not null
                ? LookupStatus.Complete
                : LookupStatus.Failed,
            Message = message,
        };

    // ------------------------------------------------------------------------------------ Spanish

    private async Task<LookupResult> LookupSpanishAsync(string text, Action<LookupResult>? onPartial, CancellationToken ct)
    {
        var sentence = IsSentence(text);
        var reverse = sentence ? [] : _lexicon.FindEnglishForSpanish(text);

        onPartial?.Invoke(new LookupResult
        {
            SourceText = text,
            SourceLanguage = Lang.Spanish,
            Headword = "",
            IsSentence = sentence,
            IsTranslationPending = true,
            Status = LookupStatus.Partial,
        });

        string? mtBest = null;
        string? mtError = null;
        try
        {
            var mt = await _translator.TranslateAsync(text, Lang.Spanish, Lang.English, 2, ct).ConfigureAwait(false);
            mtBest = mt.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TranslationUnavailableException ex)
        {
            mtError = ex.Message;
        }
        catch (Exception ex)
        {
            mtError = "No se pudo traducir: " + ex.Message;
        }

        if (sentence)
        {
            if (mtBest is null) return Failed(text, mtError ?? "No se pudo traducir.");
            return new LookupResult
            {
                SourceText = text,
                SourceLanguage = Lang.Spanish,
                Headword = mtBest,
                IsSentence = true,
                Translations = [text],
                Status = LookupStatus.Complete,
                TranslationEngine = _translator.LastEngine,
            };
        }

        var head = ChooseEnglishHead(text, mtBest, reverse);
        if (head is null) return Failed(text, mtError ?? "No se encontró una traducción al inglés.");

        var match = _lexicon.FindEnglish(head);
        var entry = match?.Entry;
        var headword = ChooseHeadword(head, entry);

        var meanings = new List<string> { SpanishDisplay(text) };
        if (match is { ViaLemma: false })
        {
            foreach (var t in entry!.Translations) AddMeaning(meanings, t, head);
        }

        return new LookupResult
        {
            SourceText = text,
            SourceLanguage = Lang.Spanish,
            Headword = headword,
            Translations = TopDistinct(meanings, MaxTranslations),
            Synonyms = entry?.Synonyms.Take(MaxSynonyms).ToList() ?? [],
            Example = entry?.Example,
            Ipa = entry?.IpaUs ?? entry?.IpaUk,
            PartOfSpeech = entry?.PartOfSpeech,
            Status = LookupStatus.Complete,
            TranslationEngine = mtBest is null ? "lexicon" : _translator.LastEngine,
            Message = mtBest is null && mtError is not null ? "Traducción offline no instalada: resultado del diccionario." : null,
        };
    }

    /// <summary>
    /// Picks the English term for a Spanish word: the engine's answer when it agrees with the dictionary (or
    /// the dictionary has nothing), otherwise the most common dictionary equivalent.
    /// </summary>
    private static string? ChooseEnglishHead(string spanish, string? mt, IReadOnlyList<string> reverse)
    {
        var cleaned = mt is null ? null : CleanEnglishTerm(mt);
        var plausible = cleaned is not null && TextNormalizer.WordCount(cleaned) <= Math.Max(3, TextNormalizer.WordCount(spanish) + 2);

        if (plausible && (reverse.Count == 0 || reverse.Any(r => TextNormalizer.Key(r) == TextNormalizer.Key(cleaned))))
        {
            return cleaned;
        }

        if (reverse.Count > 0) return reverse[0];
        return plausible ? cleaned : null;
    }

    private static string CleanEnglishTerm(string mt)
    {
        var t = mt.Trim().TrimEnd('.', '!', ' ');
        // Opus-MT writes "To run" / "A table" for bare nouns and infinitives: drop the leading article.
        return t;
    }

    private static string SpanishDisplay(string text) =>
        TextNormalizer.WordCount(text) <= 4 && text.Length > 0 && char.IsUpper(text[0]) && text.Skip(1).Any(char.IsLower)
            ? char.ToLowerInvariant(text[0]) + text[1..]
            : text;

    // ------------------------------------------------------------------------------------ helpers

    internal static bool IsSentence(string text)
    {
        var words = TextNormalizer.WordCount(text);
        if (words >= 6) return true;
        return words >= 3 && text.Length > 0 && text[^1] is '.' or '!' or '?' or '…';
    }

    private static string ChooseHeadword(string selection, LexiconEntry? entry)
    {
        if (entry is not null)
        {
            // Use the dictionary's spelling/casing ("Madrid", "reluctant") when the selection is the same word.
            if (TextNormalizer.Key(entry.Word) == TextNormalizer.Key(selection)) return entry.Word;
        }

        var letters = selection.Where(char.IsLetter).ToList();
        if (letters.Count >= 4 && letters.All(char.IsUpper)) return selection.ToLowerInvariant();
        if (TextNormalizer.WordCount(selection) <= 5 && selection.Length > 0 && char.IsUpper(selection[0]) &&
            selection.Skip(1).Any(char.IsLower) && TextNormalizer.WordCount(selection) == 1 && entry is null)
        {
            return selection; // unknown capitalised word: probably a name
        }

        return selection;
    }

    private static void AddMeaning(List<string> list, string candidate, string source)
    {
        var c = candidate.Trim().TrimEnd('.', '!', ' ');
        if (c.Length == 0) return;
        var key = TextNormalizer.Key(c);
        if (key.Length == 0 || key == TextNormalizer.Key(source)) return;
        if (list.Any(x => TextNormalizer.Key(x) == key)) return;
        list.Add(c);
    }

    /// <summary>First <paramref name="count"/> meanings, dropping inflection variants of one already chosen (ubicuo/ubicua).</summary>
    private static List<string> TopDistinct(IEnumerable<string> meanings, int count)
    {
        var result = new List<string>();
        foreach (var m in meanings)
        {
            if (result.Any(r => SameStem(r, m))) continue;
            result.Add(m);
            if (result.Count == count) break;
        }

        return result;
    }

    private static bool SameStem(string a, string b)
    {
        var ka = TextNormalizer.Key(a);
        var kb = TextNormalizer.Key(b);
        if (ka == kb) return true;
        if (ka.Contains(' ') || kb.Contains(' ')) return false;
        var n = Math.Min(Math.Min(ka.Length, kb.Length), 6);
        return n >= 5 && string.CompareOrdinal(ka, 0, kb, 0, n) == 0 && Math.Abs(ka.Length - kb.Length) <= 2;
    }

    private static LookupResult Failed(string text, string message) => new()
    {
        SourceText = text,
        Status = LookupStatus.Failed,
        Message = message,
    };
}
