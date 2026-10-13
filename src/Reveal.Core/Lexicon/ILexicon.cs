using Reveal.Core.Models;

namespace Reveal.Core.Lexicon;

public sealed record LexiconEntry(
    string Word,
    string? PartOfSpeech,
    string? IpaUs,
    string? IpaUk,
    IReadOnlyList<string> Translations,
    IReadOnlyList<string> Synonyms,
    string? Example);

/// <summary>An English lexicon hit and how it was reached.</summary>
public sealed record EnglishMatch(LexiconEntry Entry, bool ViaLemma, string? Lemma);

/// <summary>Offline word data: English entries (synonyms, examples...), Spanish->English index, frequencies.</summary>
public interface ILexicon
{
    bool IsAvailable { get; }

    /// <summary>Finds an English word or phrase, trying the exact text, lower case and the lemma of inflected forms.</summary>
    EnglishMatch? FindEnglish(string text);

    /// <summary>English equivalents of a Spanish word, most common first (empty when unknown).</summary>
    IReadOnlyList<string> FindEnglishForSpanish(string spanishWord);

    /// <summary>Occurrences per million words in subtitles, or 0 when the word is not known.</summary>
    double Frequency(Lang language, string word);

    bool HasSpanishEntry(string word);

    bool HasEnglishEntry(string word);
}

/// <summary>Used when the lexicon file is missing: everything is empty and translation still works.</summary>
public sealed class EmptyLexicon : ILexicon
{
    public bool IsAvailable => false;

    public EnglishMatch? FindEnglish(string text) => null;

    public IReadOnlyList<string> FindEnglishForSpanish(string spanishWord) => Array.Empty<string>();

    public double Frequency(Lang language, string word) => 0;

    public bool HasSpanishEntry(string word) => false;

    public bool HasEnglishEntry(string word) => false;
}
