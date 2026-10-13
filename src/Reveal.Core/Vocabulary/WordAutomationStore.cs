using System.Runtime.InteropServices;

namespace Reveal.Core.Vocabulary;

/// <summary>
/// When the vocabulary document is open in Word the file is locked, so it cannot be edited on disk.
/// In that case the entry is added through the running Word instance (late bound COM, no Office
/// assemblies needed) so the document on screen and on disk stay consistent.
/// </summary>
public static class WordAutomationStore
{
    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);

    /// <summary>Returns the running Word application, or null when Word isn't running.</summary>
    public static object? TryGetRunningWord()
    {
        try
        {
            if (CLSIDFromProgID("Word.Application", out var clsid) != 0) return null;
            return GetActiveObject(ref clsid, IntPtr.Zero, out var app) == 0 ? app : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Finds the open document with the given full path, or null.</summary>
    public static dynamic? FindOpenDocument(string path)
    {
        dynamic? app = TryGetRunningWord();
        if (app is null) return null;

        try
        {
            var full = Path.GetFullPath(path);
            foreach (dynamic doc in app.Documents)
            {
                try
                {
                    string name = doc.FullName;
                    if (string.Equals(Path.GetFullPath(name), full, StringComparison.OrdinalIgnoreCase)) return doc;
                }
                catch
                {
                    // A document in a weird state (protected view, closing...) is simply skipped.
                }
            }
        }
        catch
        {
            // Word busy (modal dialog, cell edit...) -> treated as "not available".
        }

        return null;
    }

    public static IReadOnlyList<string> ReadParagraphs(dynamic doc)
    {
        string text = doc.Content.Text;
        return text.Split('\r', '\a', '\v');
    }

    /// <summary>
    /// Appends the entry at the end of the open document. The document is saved afterwards only when it
    /// had no unsaved changes of its own, so the user's pending edits are never saved behind their back.
    /// Returns true when the document was saved.
    /// </summary>
    public static bool Append(dynamic doc, VocabularyEntry entry)
    {
        var wasSaved = (bool)doc.Saved;
        var lines = entry.Lines();
        var headLength = entry.DisplayHeadword.Length;

        // Make sure we start on a fresh, empty paragraph.
        dynamic content = doc.Content;
        string existing = content.Text;
        var lastParagraphEmpty = existing.Length == 0 || existing == "\r" || existing.EndsWith("\r\r", StringComparison.Ordinal);
        var hasContent = existing.Trim('\r', '\a', '\v', ' ').Length > 0;

        if (!lastParagraphEmpty) content.InsertParagraphAfter();

        for (var i = 0; i < lines.Count; i++)
        {
            int start = doc.Content.End - 1;
            dynamic range = doc.Range(start, start);
            range.InsertAfter(lines[i]);

            dynamic whole = doc.Range(start, start + lines[i].Length);
            whole.Style = -1; // wdStyleNormal: don't inherit headings/lists from the previous paragraph
            whole.Font.Bold = 0;
            whole.Font.Italic = 0;
            whole.ParagraphFormat.SpaceBefore = (i == 0 && hasContent) ? 10 : 0;
            whole.ParagraphFormat.SpaceAfter = 0;
            whole.ParagraphFormat.KeepWithNext = i < lines.Count - 1 ? -1 : 0;

            if (i == 0 && headLength > 0 && headLength <= lines[i].Length)
            {
                dynamic head = doc.Range(start, start + headLength);
                head.Font.Bold = 1;
            }

            if (i < lines.Count - 1) doc.Content.InsertParagraphAfter();
        }

        if (wasSaved)
        {
            doc.Save();
            return true;
        }

        return false;
    }
}
