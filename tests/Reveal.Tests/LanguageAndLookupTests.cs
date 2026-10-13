using Reveal.Core.Lookup;
using Reveal.Core.Models;
using Reveal.Core.Translation;
using Xunit.Abstractions;

namespace Reveal.Tests;

public sealed class LanguageAndLookupTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("reluctant", Lang.English)]
    [InlineData("ubiquitous", Lang.English)]
    [InlineData("the", Lang.English)]
    [InlineData("give up", Lang.English)]
    [InlineData("look forward to", Lang.English)]
    [InlineData("He was reluctant to accept the offer.", Lang.English)]
    [InlineData("I have to go now", Lang.English)]
    [InlineData("running", Lang.English)]
    [InlineData("nevertheless", Lang.English)]
    [InlineData("cafe", Lang.English)]
    [InlineData("personal", Lang.English)]
    [InlineData("animal", Lang.English)]
    [InlineData("hospital", Lang.English)]
    [InlineData("sin", Lang.Spanish)]
    [InlineData("reacio", Lang.Spanish)]
    [InlineData("corriendo", Lang.Spanish)]
    [InlineData("casa", Lang.Spanish)]
    [InlineData("dificultad", Lang.Spanish)]
    [InlineData("niño", Lang.Spanish)]
    [InlineData("¿Cómo estás?", Lang.Spanish)]
    [InlineData("Tengo que irme ahora", Lang.Spanish)]
    [InlineData("llevar a cabo", Lang.Spanish)]
    [InlineData("estaba", Lang.Spanish)]
    [InlineData("aceptar la oferta", Lang.Spanish)]
    public void Detects_english_and_spanish(string text, Lang expected)
    {
        if (!TestEnv.HasLexicon) return;
        using var lexicon = TestEnv.OpenLexicon();
        var detector = TestEnv.Detector(lexicon);

        var guess = detector.Detect(text);
        Assert.Equal(expected, guess.Language);
    }

    [Fact]
    public void Lexicon_returns_synonyms_example_ipa_and_ranked_translations()
    {
        if (!TestEnv.HasLexicon) return;
        using var lexicon = TestEnv.OpenLexicon();

        var m = lexicon.FindEnglish("Reluctant");
        Assert.NotNull(m);
        Assert.False(m!.ViaLemma);
        Assert.Equal("reacio", m.Entry.Translations[0]);
        Assert.Contains("reticente", m.Entry.Translations);
        Assert.NotEmpty(m.Entry.Synonyms);
        Assert.False(string.IsNullOrWhiteSpace(m.Entry.Example));
        Assert.False(string.IsNullOrWhiteSpace(m.Entry.IpaUs));

        var inflected = lexicon.FindEnglish("gave up");
        Assert.NotNull(inflected);
        Assert.Equal("give up", inflected!.Entry.Word);

        Assert.Contains("reluctant", lexicon.FindEnglishForSpanish("reacio"));
        Assert.Contains("house", lexicon.FindEnglishForSpanish("casa"));
        Assert.Contains("difficult", lexicon.FindEnglishForSpanish("dificil")); // accent-insensitive
    }

    private static LookupService CreateLookup(Reveal.Core.Lexicon.SqliteLexicon lexicon, out MarianTranslator marian)
    {
        marian = new MarianTranslator();
        return new LookupService(lexicon, TestEnv.Detector(lexicon), new TranslationService(marian));
    }

    [Fact]
    public async Task English_word_gives_full_card()
    {
        if (!TestEnv.HasLexicon) return;
        using var lexicon = TestEnv.OpenLexicon();
        var lookup = CreateLookup(lexicon, out var marian);
        using var _ = marian;

        var r = await lookup.LookupAsync("  reluctant ", null, null, CancellationToken.None);

        output.WriteLine($"{r.Headword} | {r.TranslationLine} | syn: {string.Join(", ", r.Synonyms)} | ex: {r.Example} | {r.Ipa}");
        Assert.Equal(LookupStatus.Complete, r.Status);
        Assert.Equal(Lang.English, r.SourceLanguage);
        Assert.Equal("reluctant", r.Headword);
        Assert.Equal("reacio", r.Translations[0]);
        Assert.InRange(r.Translations.Count, 2, 3);
        Assert.NotEmpty(r.Synonyms);
        Assert.NotNull(r.Example);
    }

    [Fact]
    public async Task Spanish_word_uses_the_english_term_as_main_word_and_keeps_the_original_meaning_first()
    {
        if (!TestEnv.HasLexicon || !TestEnv.HasModels) return;
        using var lexicon = TestEnv.OpenLexicon();
        var lookup = CreateLookup(lexicon, out var marian);
        using var _ = marian;

        var r = await lookup.LookupAsync("reacio", null, null, CancellationToken.None);

        output.WriteLine($"{r.Headword} | {r.TranslationLine} | syn: {string.Join(", ", r.Synonyms)} | ex: {r.Example}");
        Assert.Equal(Lang.Spanish, r.SourceLanguage);
        Assert.Equal("reluctant", r.Headword);
        Assert.Equal("reacio", r.Translations[0]);
        Assert.NotEmpty(r.Synonyms);
    }

    [Theory]
    [InlineData("give up", "")]
    [InlineData("look forward to", "")]
    [InlineData("running", "")]
    [InlineData("ubiquitous", "ubicuo")]
    [InlineData("bank", "banco")]
    [InlineData("take off", "")]
    [InlineData("He was reluctant to accept the offer.", "reacio")]
    [InlineData("How are you doing today?", "")]
    public async Task English_inputs_translate(string text, string expectedFragment)
    {
        if (!TestEnv.HasLexicon || !TestEnv.HasModels) return;
        using var lexicon = TestEnv.OpenLexicon();
        var lookup = CreateLookup(lexicon, out var marian);
        using var _ = marian;

        var r = await lookup.LookupAsync(text, null, null, CancellationToken.None);

        output.WriteLine($"{text} => {r.Headword} | {r.TranslationLine} | syn: {string.Join(", ", r.Synonyms)} | engine={r.TranslationEngine}");
        Assert.Equal(Lang.English, r.SourceLanguage);
        Assert.NotEmpty(r.Translations);
        if (expectedFragment.Length > 0) Assert.Contains(expectedFragment, r.TranslationLine, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("corriendo", "running")]
    [InlineData("casa", "house")]
    [InlineData("rendirse", "surrender")]
    [InlineData("Tengo que irme ahora", "I have to go now")]
    [InlineData("difícil", "difficult")]
    public async Task Spanish_inputs_translate_to_english_headwords(string text, string expectedFragment)
    {
        if (!TestEnv.HasLexicon || !TestEnv.HasModels) return;
        using var lexicon = TestEnv.OpenLexicon();
        var lookup = CreateLookup(lexicon, out var marian);
        using var _ = marian;

        var r = await lookup.LookupAsync(text, null, null, CancellationToken.None);

        output.WriteLine($"{text} => {r.Headword} | {r.TranslationLine} | syn: {string.Join(", ", r.Synonyms)} | engine={r.TranslationEngine}");
        Assert.Equal(Lang.Spanish, r.SourceLanguage);
        Assert.Contains(expectedFragment, r.Headword, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(r.TranslationLine));
    }

    [Fact]
    public async Task Edge_cases_do_not_throw_and_explain_themselves()
    {
        if (!TestEnv.HasLexicon) return;
        using var lexicon = TestEnv.OpenLexicon();
        var lookup = CreateLookup(lexicon, out var marian);
        using var _ = marian;

        var empty = await lookup.LookupAsync("   ", null, null, CancellationToken.None);
        Assert.Equal(LookupStatus.Failed, empty.Status);

        var digits = await lookup.LookupAsync("12345", null, null, CancellationToken.None);
        Assert.Equal(LookupStatus.Failed, digits.Status);

        var tooLong = await lookup.LookupAsync(new string('a', 500), null, null, CancellationToken.None);
        Assert.Equal(LookupStatus.Failed, tooLong.Status);
        Assert.Contains("larga", tooLong.Message);
    }

    [Fact]
    public async Task Without_models_lexicon_data_is_still_shown_with_an_explanation()
    {
        if (!TestEnv.HasLexicon) return;
        using var lexicon = TestEnv.OpenLexicon();
        var lookup = new LookupService(lexicon, TestEnv.Detector(lexicon), new TranslationService(new UnavailableTranslator()));

        var known = await lookup.LookupAsync("reluctant", null, null, CancellationToken.None);
        Assert.Equal(LookupStatus.Complete, known.Status);
        Assert.NotEmpty(known.Translations); // from the offline dictionary

        var sentence = await lookup.LookupAsync("He was reluctant to accept the offer.", null, null, CancellationToken.None);
        Assert.Equal(LookupStatus.Failed, sentence.Status);
        Assert.False(string.IsNullOrWhiteSpace(sentence.Message));

        var spanishWord = await lookup.LookupAsync("reacio", null, null, CancellationToken.None);
        Assert.Equal("reluctant", spanishWord.Headword); // reverse dictionary, no MT needed
    }

    private sealed class UnavailableTranslator : ITranslator
    {
        public string Name => "none";

        public bool IsAvailableFor(Lang from, Lang to) => false;

        public Task<TranslationResult> TranslateAsync(string text, Lang from, Lang to, int alternatives, CancellationToken ct) =>
            throw new TranslationUnavailableException("n/a");
    }
}
