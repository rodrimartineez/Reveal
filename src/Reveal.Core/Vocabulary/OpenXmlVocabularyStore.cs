using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OxText = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace Reveal.Core.Vocabulary;

/// <summary>
/// Reads and appends to a .docx by editing the package directly (no Word needed).
/// Strictly append-only: existing paragraphs are never touched.
/// </summary>
public static class OpenXmlVocabularyStore
{
    /// <summary>Plain text of every paragraph in the document (body, tables, text boxes...).</summary>
    public static IReadOnlyList<string> ReadParagraphs(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = WordprocessingDocument.Open(stream, false);
        return ReadParagraphs(doc);
    }

    public static IReadOnlyList<string> ReadParagraphs(WordprocessingDocument doc)
    {
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return Array.Empty<string>();

        return body.Descendants<Paragraph>().Select(ParagraphText).ToList();
    }

    private static string ParagraphText(Paragraph p)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var el in p.Descendants())
        {
            switch (el)
            {
                case OxText t: sb.Append(t.Text); break;
                case TabChar: sb.Append(' '); break;
                case Break: sb.Append(' '); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends the entry at the end of the body. <paramref name="stream"/> must be a read/write stream
    /// over the .docx file. Returns nothing: the caller already decided it is not a duplicate.
    /// </summary>
    public static void Append(Stream stream, VocabularyEntry entry)
    {
        using var doc = WordprocessingDocument.Open(stream, true);
        var main = doc.MainDocumentPart ?? doc.AddMainDocumentPart();
        main.Document ??= new Document(new Body());
        var body = main.Document.Body ??= new Body();

        var hasContent = body.Elements<Paragraph>().Any(p => !string.IsNullOrWhiteSpace(ParagraphText(p)));
        var runStyle = FindRunStyle(body);
        var lines = entry.Lines();
        var sectPr = body.Elements<SectionProperties>().LastOrDefault();

        for (var i = 0; i < lines.Count; i++)
        {
            var paragraph = BuildParagraph(
                lines[i],
                boldPrefixLength: i == 0 ? entry.DisplayHeadword.Length : 0,
                runStyle,
                spaceBefore: i == 0 && hasContent ? 200 : 0,
                spaceAfter: 0,
                keepNext: i < lines.Count - 1);

            if (sectPr is not null) body.InsertBefore(paragraph, sectPr);
            else body.AppendChild(paragraph);
        }

        main.Document.Save();
    }

    private static Paragraph BuildParagraph(
        string text, int boldPrefixLength, (RunFonts? Fonts, FontSize? Size) style,
        int spaceBefore, int spaceAfter, bool keepNext)
    {
        var props = new ParagraphProperties();
        if (keepNext) props.Append(new KeepNext());
        props.Append(new SpacingBetweenLines
        {
            Before = spaceBefore.ToString(),
            After = spaceAfter.ToString(),
        });

        var paragraph = new Paragraph(props);

        if (boldPrefixLength > 0 && boldPrefixLength <= text.Length)
        {
            paragraph.Append(BuildRun(text[..boldPrefixLength], bold: true, style));
            if (boldPrefixLength < text.Length) paragraph.Append(BuildRun(text[boldPrefixLength..], bold: false, style));
        }
        else
        {
            paragraph.Append(BuildRun(text, bold: false, style));
        }

        return paragraph;
    }

    private static Run BuildRun(string text, bool bold, (RunFonts? Fonts, FontSize? Size) style)
    {
        var rPr = new RunProperties();
        if (style.Fonts is not null) rPr.Append((RunFonts)style.Fonts.CloneNode(true));
        if (bold) rPr.Append(new Bold());
        if (style.Size is not null) rPr.Append((FontSize)style.Size.CloneNode(true));

        return new Run(rPr, new OxText(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    /// <summary>Use the font of the last text in the document so new entries blend in with the existing ones.</summary>
    private static (RunFonts? Fonts, FontSize? Size) FindRunStyle(Body body)
    {
        foreach (var p in body.Descendants<Paragraph>().Reverse())
        {
            var run = p.Elements<Run>().FirstOrDefault(r => r.Elements<OxText>().Any(t => !string.IsNullOrWhiteSpace(t.Text)));
            if (run?.RunProperties is { } props)
            {
                return (props.RunFonts, props.FontSize);
            }

            if (run is not null) return (null, null);
        }

        return (null, null);
    }

    /// <summary>Creates a brand new, empty vocabulary document.</summary>
    public static void CreateEmpty(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(new SectionProperties(
            new PageSize { Width = 11906, Height = 16838 },
            new PageMargin { Top = 1417, Right = 1701, Bottom = 1417, Left = 1701, Header = 708, Footer = 708, Gutter = 0 })));

        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Calibri", ComplexScript = "Calibri" },
                    new FontSize { Val = "24" },
                    new FontSizeComplexScript { Val = "24" },
                    new Languages { Val = "en-US", EastAsia = "en-US", Bidi = "ar-SA" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = "264", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle())
            {
                Type = StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true,
            });

        main.Document.Save();
    }
}
