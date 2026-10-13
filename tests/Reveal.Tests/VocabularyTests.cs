using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Reveal.Core.Settings;
using Reveal.Core.Text;
using Reveal.Core.Vocabulary;
using OxText = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace Reveal.Tests;

public sealed class VocabularyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reveal-tests-" + Guid.NewGuid().ToString("N"));

    public VocabularyTests()
    {
        Directory.CreateDirectory(_dir);
        AppPaths.OverrideRoot(_dir);
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot(null);
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private static VocabularyEntry Reluctant() => new(
        "reluctant",
        ["reacio", "reticente"],
        "He was reluctant to accept the offer.",
        ["unwilling", "hesitant"]);

    private string NewDoc(Action<Body>? seed = null, string name = "C1 Vocabulary.docx")
    {
        var path = Path.Combine(_dir, name);
        OpenXmlVocabularyStore.CreateEmpty(path);
        if (seed is not null)
        {
            using var doc = WordprocessingDocument.Open(path, true);
            seed(doc.MainDocumentPart!.Document!.Body!);
            doc.MainDocumentPart.Document.Save();
        }

        return path;
    }

    private static Paragraph P(string text, bool bold = false) =>
        new(new Run(bold ? new RunProperties(new Bold()) : new RunProperties(), new OxText(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static void AppendBeforeSect(Body body, params OpenXmlElement[] els)
    {
        var sect = body.Elements<SectionProperties>().LastOrDefault();
        foreach (var e in els)
        {
            if (sect is not null) body.InsertBefore(e, sect);
            else body.AppendChild(e);
        }
    }

    [Fact]
    public async Task Save_appends_entry_with_bold_headword_and_expected_lines()
    {
        var path = NewDoc();
        var service = new VocabularyService(() => path);

        var result = await service.SaveAsync(Reluctant());

        Assert.Equal(SaveOutcome.Added, result.Outcome);
        var paragraphs = OpenXmlVocabularyStore.ReadParagraphs(path).Where(p => p.Length > 0).ToList();
        Assert.Equal(
            ["RELUCTANT — reacio / reticente", "Example: He was reluctant to accept the offer.", "Synonyms: unwilling, hesitant"],
            paragraphs);

        using var doc = WordprocessingDocument.Open(path, false);
        var first = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First();
        var runs = first.Elements<Run>().ToList();
        Assert.True(runs[0].RunProperties?.Bold is not null, "headword must be bold");
        Assert.Equal("RELUCTANT", runs[0].InnerText);
        Assert.Null(runs[1].RunProperties?.Bold);
    }

    [Fact]
    public async Task Save_never_modifies_existing_content_and_keeps_section_properties_last()
    {
        var path = NewDoc(body => AppendBeforeSect(body,
            P("My C1 notes", bold: true),
            P("ubiquitous — omnipresente"),
            P("Example: Smartphones are ubiquitous."),
            new Table(new TableRow(new TableCell(P("cell text"))))));

        var before = OpenXmlVocabularyStore.ReadParagraphs(path).ToList();
        var service = new VocabularyService(() => path);

        Assert.Equal(SaveOutcome.Added, (await service.SaveAsync(Reluctant())).Outcome);

        var after = OpenXmlVocabularyStore.ReadParagraphs(path).ToList();
        Assert.Equal(before, after.Take(before.Count).ToList());
        Assert.Equal(before.Count + 3, after.Count);

        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        Assert.IsType<SectionProperties>(body.LastChild);
        Assert.Single(body.Elements<Table>());
    }

    [Fact]
    public async Task Save_twice_in_a_row_adds_once()
    {
        var path = NewDoc();
        var service = new VocabularyService(() => path);

        Assert.Equal(SaveOutcome.Added, (await service.SaveAsync(Reluctant())).Outcome);
        var second = await service.SaveAsync(Reluctant());

        Assert.Equal(SaveOutcome.Duplicate, second.Outcome);
        Assert.Equal(3, OpenXmlVocabularyStore.ReadParagraphs(path).Count(p => p.Length > 0));
    }

    [Theory]
    [InlineData("RELUCTANT")]
    [InlineData("  Reluctant. ")]
    [InlineData("re-luctant")]
    public async Task Duplicate_detection_ignores_case_spacing_and_punctuation(string variant)
    {
        var path = NewDoc(body => AppendBeforeSect(body, P("Reluctant — reacio")));
        var service = new VocabularyService(() => path);

        var result = await service.SaveAsync(Reluctant() with { Headword = variant });

        // "re-luctant" is a different word once punctuation is dropped ("re luctant"), the others are the same.
        if (variant == "re-luctant") Assert.Equal(SaveOutcome.Added, result.Outcome);
        else Assert.Equal(SaveOutcome.Duplicate, result.Outcome);
    }

    [Fact]
    public async Task Word_already_present_as_synonym_is_a_duplicate()
    {
        var path = NewDoc();
        var service = new VocabularyService(() => path);
        Assert.Equal(SaveOutcome.Added, (await service.SaveAsync(Reluctant())).Outcome);

        var result = await service.SaveAsync(new VocabularyEntry("Unwilling", ["reacio"], null, []));

        Assert.Equal(SaveOutcome.Duplicate, result.Outcome);
        Assert.Equal(DuplicateKind.Synonym, result.Duplicate!.Kind);
        Assert.Contains("RELUCTANT", result.Message);
    }

    [Fact]
    public async Task Duplicate_check_scans_the_whole_document_including_tables_and_hand_written_lists()
    {
        var path = NewDoc(body => AppendBeforeSect(body,
            P("1. meticulous - muy cuidadoso"),
            P("• give up (phrasal verb)"),
            new Table(new TableRow(new TableCell(P("ubiquitous — omnipresente"))))));
        var service = new VocabularyService(() => path);

        Assert.Equal(SaveOutcome.Duplicate, (await service.SaveAsync(new("Meticulous", [], null, []))).Outcome);
        Assert.Equal(SaveOutcome.Duplicate, (await service.SaveAsync(new("give up", [], null, []))).Outcome);
        Assert.Equal(SaveOutcome.Duplicate, (await service.SaveAsync(new("ubiquitous", [], null, []))).Outcome);
        Assert.Equal(SaveOutcome.Added, (await service.SaveAsync(new("ephemeral", ["efímero"], null, []))).Outcome);
    }

    [Fact]
    public async Task Example_sentences_do_not_count_as_entries()
    {
        var path = NewDoc(body => AppendBeforeSect(body, P("Example: He was reluctant to accept the offer.")));
        var service = new VocabularyService(() => path);

        Assert.Equal(SaveOutcome.Added, (await service.SaveAsync(Reluctant())).Outcome);
    }

    [Fact]
    public async Task Missing_or_unset_document_gives_clear_outcomes()
    {
        Assert.Equal(SaveOutcome.NoDocumentConfigured, (await new VocabularyService(() => null).SaveAsync(Reluctant())).Outcome);
        var missing = Path.Combine(_dir, "nope.docx");
        Assert.Equal(SaveOutcome.DocumentNotFound, (await new VocabularyService(() => missing).SaveAsync(Reluctant())).Outcome);

        var notDocx = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(notDocx, "hello");
        Assert.Equal(SaveOutcome.Error, (await new VocabularyService(() => notDocx).SaveAsync(Reluctant())).Outcome);

        var corrupt = Path.Combine(_dir, "corrupt.docx");
        File.WriteAllText(corrupt, "this is not a zip file");
        var result = await new VocabularyService(() => corrupt).SaveAsync(Reluctant());
        Assert.Equal(SaveOutcome.Error, result.Outcome);
        Assert.Equal("this is not a zip file", File.ReadAllText(corrupt)); // untouched
    }

    [Fact]
    public async Task Locked_file_without_word_reports_locked_and_leaves_file_intact()
    {
        var path = NewDoc();
        var bytesBefore = File.ReadAllBytes(path);
        var service = new VocabularyService(() => path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await service.SaveAsync(Reluctant());
            Assert.Equal(SaveOutcome.Locked, result.Outcome);
        }

        Assert.Equal(bytesBefore, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Backup_is_created_before_modifying()
    {
        var path = NewDoc(body => AppendBeforeSect(body, P("hello")));
        var service = new VocabularyService(() => path);
        await service.SaveAsync(Reluctant());

        Assert.Single(Directory.GetFiles(AppPaths.BackupsDirectory, "*.bak"));
    }

    [Fact]
    public async Task Many_sequential_saves_stay_consistent()
    {
        var path = NewDoc();
        var service = new VocabularyService(() => path);
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "alpha", "Bravo!" };
        var outcomes = new List<SaveOutcome>();
        foreach (var w in words) outcomes.Add((await service.SaveAsync(new(w, ["x"], "Ex " + w, []))).Outcome);

        Assert.Equal(
            [SaveOutcome.Added, SaveOutcome.Added, SaveOutcome.Added, SaveOutcome.Added, SaveOutcome.Added, SaveOutcome.Duplicate, SaveOutcome.Duplicate],
            outcomes);
    }

    [Fact]
    public void Index_handles_label_lines_and_slash_variants()
    {
        var index = VocabularyIndex.Build(
        [
            "COLOUR / COLOR — color",
            "Example: The colour red.",
            "Synonyms: hue; shade / tint",
        ]);

        Assert.NotNull(index.Find("color"));
        Assert.NotNull(index.Find("Colour"));
        Assert.Equal(DuplicateKind.Synonym, index.Find("tint")!.Kind);
        Assert.Null(index.Find("example"));
        Assert.Null(index.Find("red"));
    }

    [Theory]
    [InlineData("  Hello,  World! ", "hello world")]
    [InlineData("don't", "dont")]
    [InlineData("Café", "cafe")]
    [InlineData("well-known", "well known")]
    public void Normalizer_key(string input, string expected) => Assert.Equal(expected, TextNormalizer.Key(input));

    [Theory]
    [InlineData("  \"reluctant\"  ", "reluctant")]
    [InlineData("reluctant.", "reluctant")]
    [InlineData("give\r\nup", "give up")]
    [InlineData("recog-\nnize", "recognize")]
    [InlineData("How are you today?", "How are you today?")]
    [InlineData("(hello)", "hello")]
    [InlineData("​reluctant ", "reluctant")]
    public void Clean_selection(string input, string expected) => Assert.Equal(expected, TextNormalizer.CleanSelection(input));
}
