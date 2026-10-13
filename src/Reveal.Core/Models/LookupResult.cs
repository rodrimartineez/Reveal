namespace Reveal.Core.Models;

public enum Lang
{
    Unknown,
    English,
    Spanish,
}

public enum LookupStatus
{
    /// <summary>Lexicon data is ready, translation may still be pending.</summary>
    Partial,
    Complete,
    /// <summary>Nothing useful could be produced (see <see cref="LookupResult.Message"/>).</summary>
    Failed,
}

/// <summary>
/// Everything Reveal shows about a selection. Immutable so it can be streamed to the UI
/// in progressive updates (lexicon first, translation as soon as the engine answers).
/// </summary>
public sealed record LookupResult
{
    /// <summary>Selection exactly as captured (cleaned of whitespace noise).</summary>
    public string SourceText { get; init; } = "";

    public Lang SourceLanguage { get; init; } = Lang.Unknown;

    /// <summary>English term that is the "main" word of the card (and what gets pronounced / saved).</summary>
    public string Headword { get; init; } = "";

    /// <summary>Spanish meanings, best first.</summary>
    public IReadOnlyList<string> Translations { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Synonyms { get; init; } = Array.Empty<string>();

    public string? Example { get; init; }

    public string? Ipa { get; init; }

    public string? PartOfSpeech { get; init; }

    public bool IsSentence { get; init; }

    public bool IsTranslationPending { get; init; }

    public LookupStatus Status { get; init; } = LookupStatus.Partial;

    /// <summary>User-facing explanation when something is missing or failed.</summary>
    public string? Message { get; init; }

    /// <summary>Which engine produced the translation (for diagnostics, never contains text).</summary>
    public string? TranslationEngine { get; init; }

    public string TranslationLine => string.Join(" / ", Translations);

    public bool HasContent => Headword.Length > 0;
}
