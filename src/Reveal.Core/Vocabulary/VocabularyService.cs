using System.Runtime.InteropServices;
using Reveal.Core.Settings;

namespace Reveal.Core.Vocabulary;

/// <summary>
/// Saves entries into the user's vocabulary .docx: duplicate check across the whole document,
/// append-only writes, automatic fallback to Word when the file is open there.
/// </summary>
public sealed class VocabularyService
{
    private const int MaxBackupsPerFile = 5;

    private readonly Func<string?> _pathProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VocabularyService(Func<string?> pathProvider) => _pathProvider = pathProvider;

    public string? DocumentPath => _pathProvider();

    /// <summary>Quick check used to show "already saved" on the card without writing anything.</summary>
    public async Task<DuplicateMatch?> FindAsync(string englishWord, CancellationToken ct = default)
    {
        var path = _pathProvider();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try { return LoadIndex(path).Find(englishWord); }
                catch { return null; }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SaveResult> SaveAsync(VocabularyEntry entry, CancellationToken ct = default)
    {
        var path = _pathProvider();
        if (string.IsNullOrWhiteSpace(path))
        {
            return new SaveResult(SaveOutcome.NoDocumentConfigured, "Elige primero un documento de vocabulario (.docx).");
        }

        if (string.IsNullOrWhiteSpace(entry.Headword))
        {
            return new SaveResult(SaveOutcome.Error, "No hay ninguna palabra que guardar.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SaveCore(path, entry), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SaveResult SaveCore(string path, VocabularyEntry entry)
    {
        if (!path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return new SaveResult(SaveOutcome.Error, "El documento de vocabulario debe ser un archivo .docx.");
        }

        if (!File.Exists(path))
        {
            return new SaveResult(SaveOutcome.DocumentNotFound, "No se encuentra el documento de vocabulario. Elígelo de nuevo en Reveal.");
        }

        try
        {
            // 1) Normal route: exclusive access to the file on disk.
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                return SaveThroughWord(path, entry);
            }
            catch (UnauthorizedAccessException)
            {
                return SaveThroughWord(path, entry, readOnlyHint: true);
            }

            using (stream)
            {
                var index = VocabularyIndex.Build(ReadParagraphsFrom(stream));
                var duplicate = index.Find(entry.Headword);
                if (duplicate is not null) return DuplicateResult(entry, duplicate);

                stream.Position = 0;
                Backup(path);
                OpenXmlVocabularyStore.Append(stream, entry);
            }

            return new SaveResult(SaveOutcome.Added, $"«{entry.Headword}» guardada en tu vocabulario.");
        }
        catch (InvalidDataException)
        {
            return new SaveResult(SaveOutcome.Error, "El archivo no es un documento .docx válido.");
        }
        catch (FileFormatException)
        {
            return new SaveResult(SaveOutcome.Error, "El archivo no es un documento .docx válido.");
        }
        catch (Exception ex)
        {
            return new SaveResult(SaveOutcome.Error, "No se pudo guardar: " + ex.Message);
        }
    }

    private static IReadOnlyList<string> ReadParagraphsFrom(Stream stream)
    {
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(stream, false);
        var list = OpenXmlVocabularyStore.ReadParagraphs(doc);
        stream.Position = 0;
        return list;
    }

    private static SaveResult DuplicateResult(VocabularyEntry entry, DuplicateMatch duplicate)
    {
        var message = duplicate.Kind == DuplicateKind.Entry
            ? $"«{entry.Headword}» ya está en tu vocabulario."
            : duplicate.Owner.Length > 0
                ? $"«{entry.Headword}» ya está como sinónimo de {duplicate.Owner}."
                : $"«{entry.Headword}» ya aparece como sinónimo en tu vocabulario.";
        return new SaveResult(SaveOutcome.Duplicate, message, duplicate);
    }

    private SaveResult SaveThroughWord(string path, VocabularyEntry entry, bool readOnlyHint = false)
    {
        dynamic? doc = null;
        try
        {
            doc = WordAutomationStore.FindOpenDocument(path);
            if (doc is null)
            {
                var why = readOnlyHint
                    ? "El documento es de solo lectura o está protegido."
                    : "El documento está abierto en otro programa. Ciérralo (o ábrelo en Word) e inténtalo de nuevo.";
                return new SaveResult(SaveOutcome.Locked, why);
            }

            var index = VocabularyIndex.Build((IReadOnlyList<string>)WordAutomationStore.ReadParagraphs(doc));
            var duplicate = index.Find(entry.Headword);
            if (duplicate is not null) return DuplicateResult(entry, duplicate) with { ViaWord = true };

            bool saved = WordAutomationStore.Append(doc, entry);
            var message = saved
                ? $"«{entry.Headword}» guardada en tu vocabulario."
                : $"«{entry.Headword}» añadida en Word (el documento tiene cambios sin guardar).";
            return new SaveResult(SaveOutcome.Added, message, ViaWord: true);
        }
        catch (COMException ex)
        {
            return new SaveResult(SaveOutcome.Locked,
                "Word no pudo aceptar el cambio ahora mismo (¿hay un cuadro de diálogo abierto?). " + ex.Message);
        }
        catch (Exception ex)
        {
            return new SaveResult(SaveOutcome.Error, "No se pudo guardar a través de Word: " + ex.Message);
        }
        finally
        {
            if (doc is not null) Marshal.ReleaseComObject(doc);
        }
    }

    private static LoadedIndex? _cache;

    private sealed record LoadedIndex(string Path, DateTime WriteTimeUtc, long Length, VocabularyIndex Index);

    private static VocabularyIndex LoadIndex(string path)
    {
        var info = new FileInfo(path);
        var cached = _cache;
        if (cached is not null && cached.Path == path && cached.WriteTimeUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
        {
            return cached.Index;
        }

        IReadOnlyList<string> paragraphs;
        try
        {
            paragraphs = OpenXmlVocabularyStore.ReadParagraphs(path);
        }
        catch (IOException)
        {
            // Open in Word: read what Word has on screen instead.
            var doc = WordAutomationStore.FindOpenDocument(path);
            if (doc is null) throw;
            try { paragraphs = (IReadOnlyList<string>)WordAutomationStore.ReadParagraphs(doc); }
            finally { Marshal.ReleaseComObject(doc); }
            return VocabularyIndex.Build(paragraphs);
        }

        var index = VocabularyIndex.Build(paragraphs);
        _cache = new LoadedIndex(path, info.LastWriteTimeUtc, info.Length, index);
        return index;
    }

    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
    }

    /// <summary>Keeps the last few versions of the document so a bad write can always be undone.</summary>
    private static void Backup(string path)
    {
        try
        {
            AppPaths.EnsureCreated(AppPaths.BackupsDirectory);
            var name = Path.GetFileNameWithoutExtension(path);
            var target = Path.Combine(AppPaths.BackupsDirectory, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak");
            File.Copy(path, target, overwrite: false);

            var old = new DirectoryInfo(AppPaths.BackupsDirectory)
                .GetFiles($"{name}.*.bak")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(MaxBackupsPerFile);
            foreach (var f in old)
            {
                try { f.Delete(); } catch { /* best effort */ }
            }
        }
        catch
        {
            // Backups are a safety net, never a reason to fail the save.
        }
    }

    /// <summary>Creates an empty document (used by "Create new vocabulary document").</summary>
    public static void CreateDocument(string path)
    {
        if (File.Exists(path)) throw new IOException("El archivo ya existe.");
        OpenXmlVocabularyStore.CreateEmpty(path);
    }
}
