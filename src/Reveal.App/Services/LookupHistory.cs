using Reveal.Core.Models;

namespace Reveal.App.Services;

/// <summary>One recent lookup, shown in the main window so it can be saved later.</summary>
public sealed class HistoryItem(LookupResult result)
{
    public LookupResult Result { get; } = result;

    public string Headword => Result.Headword;

    public string Translation => Result.TranslationLine;

    /// <summary>Already in the vocabulary document (saved now or found there).</summary>
    public bool Saved { get; set; }

    public bool Saving { get; set; }
}

/// <summary>
/// Most recent lookups of this run. Kept in memory only (never written to disk) and cleared when Reveal exits.
/// All members must be used from the UI thread.
/// </summary>
public sealed class LookupHistory
{
    public const int Capacity = 20;

    private readonly List<HistoryItem> _items = new();

    public event Action? Changed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<HistoryItem> Items => _items;

    public void Add(LookupResult result)
    {
        if (result.Status == LookupStatus.Failed || !result.HasContent) return;
        var existing = Find(result.Headword);
        var saved = existing?.Saved ?? false;
        if (existing is not null) _items.Remove(existing);
        _items.Insert(0, new HistoryItem(result) { Saved = saved });
        if (_items.Count > Capacity) _items.RemoveRange(Capacity, _items.Count - Capacity);
        Changed?.Invoke();
    }

    public void MarkSaved(string headword)
    {
        var item = Find(headword);
        if (item is null || item.Saved) return;
        item.Saved = true;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _items.Clear();
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    private HistoryItem? Find(string headword) =>
        _items.FirstOrDefault(i => string.Equals(i.Headword, headword, StringComparison.OrdinalIgnoreCase));
}
