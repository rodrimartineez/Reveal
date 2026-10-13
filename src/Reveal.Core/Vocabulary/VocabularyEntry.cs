namespace Reveal.Core.Vocabulary;

public sealed record VocabularyEntry(
    string Headword,
    IReadOnlyList<string> Translations,
    string? Example,
    IReadOnlyList<string> Synonyms)
{
    /// <summary>Entry heading as it is written in the document: "RELUCTANT".</summary>
    public string DisplayHeadword =>
        Text.TextNormalizer.WordCount(Headword) <= 4 ? Headword.Trim().ToUpperInvariant() : Headword.Trim();

    public string TranslationText => string.Join(" / ", Translations.Where(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>The lines of the entry, in order. The first one starts with the (bold) headword.</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>(3);
        var first = DisplayHeadword;
        if (TranslationText.Length > 0) first += " — " + TranslationText;
        lines.Add(first);
        if (!string.IsNullOrWhiteSpace(Example)) lines.Add("Example: " + Example!.Trim());
        var syn = Synonyms.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (syn.Count > 0) lines.Add("Synonyms: " + string.Join(", ", syn));
        return lines;
    }
}

public enum SaveOutcome
{
    Added,
    Duplicate,
    NoDocumentConfigured,
    DocumentNotFound,
    /// <summary>The file is locked by another program (typically Word) and could not be reached any other way.</summary>
    Locked,
    Error,
}

public sealed record SaveResult(SaveOutcome Outcome, string Message, DuplicateMatch? Duplicate = null, bool ViaWord = false)
{
    public bool IsSuccess => Outcome == SaveOutcome.Added;

    public bool IsAlreadySaved => Outcome == SaveOutcome.Duplicate;
}
