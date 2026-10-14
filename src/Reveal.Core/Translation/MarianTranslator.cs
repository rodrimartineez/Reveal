using Reveal.Core.Models;
using Reveal.Core.Text;

namespace Reveal.Core.Translation;

/// <summary>
/// Local, offline English&lt;-&gt;Spanish translation with Opus-MT models. Models are loaded on demand and released
/// after a period of inactivity so Reveal stays light while idle.
/// </summary>
public sealed class MarianTranslator : ITranslator, IDisposable
{
    private readonly TimeSpan _idleUnload;
    private readonly int _threads;
    private readonly Dictionary<string, MarianModel> _loaded = new();
    private readonly object _loadGate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly Timer _idleTimer;
    private DateTime _lastUse = DateTime.UtcNow;

    public MarianTranslator(TimeSpan? idleUnload = null, int? threads = null)
    {
        _idleUnload = idleUnload ?? TimeSpan.FromMinutes(15);
        _threads = threads ?? Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        _idleTimer = new Timer(_ => UnloadIfIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public string Name => "Opus-MT (local)";

    public bool IsAvailableFor(Lang from, Lang to) => PackFor(from, to)?.IsInstalled == true;

    private static ModelPack? PackFor(Lang from, Lang to) => (from, to) switch
    {
        (Lang.English, Lang.Spanish) => ModelPack.EnglishToSpanish,
        (Lang.Spanish, Lang.English) => ModelPack.SpanishToEnglish,
        _ => null,
    };

    /// <summary>Loads the model for a direction ahead of time (called when capture is switched on).</summary>
    public Task WarmupAsync(Lang from, Lang to) => Task.Run(() =>
    {
        try
        {
            var pack = PackFor(from, to);
            if (pack?.IsInstalled == true) GetModel(pack);
        }
        catch
        {
            // Warmup is best effort; the real translation will report errors.
        }
    });

    public async Task<TranslationResult> TranslateAsync(string text, Lang from, Lang to, int alternatives, CancellationToken ct)
    {
        var pack = PackFor(from, to) ?? throw new TranslationUnavailableException("Par de idiomas no soportado.");
        if (!pack.IsInstalled) throw new TranslationUnavailableException("El paquete de traducción offline no está instalado.");

        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => TranslateCore(pack, text, alternatives, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _lastUse = DateTime.UtcNow;
            _runGate.Release();
        }
    }

    private TranslationResult TranslateCore(ModelPack pack, string text, int alternatives, CancellationToken ct)
    {
        var model = GetModel(pack);
        var prepared = Prepare(text);
        var wantCount = Math.Max(1, alternatives + 1);
        var beams = Math.Max(4, wantCount);

        var hyps = model.Translate(prepared, beams, wantCount, ct);
        if (hyps.Count == 0) throw new TranslationUnavailableException("El motor no produjo ninguna traducción.");

        var cleaned = new List<string>();
        foreach (var (hyp, _) in hyps)
        {
            var c = PostProcess(text, hyp);
            if (c.Length > 0 && !cleaned.Contains(c, StringComparer.OrdinalIgnoreCase)) cleaned.Add(c);
        }

        var best = hyps[0].Score;
        // Keep only alternatives that are plausible (within ~1.5 nats of the best normalised score).
        var alts = new List<string>();
        for (var i = 1; i < hyps.Count; i++)
        {
            if (best - hyps[i].Score > 1.5f) break;
            var c = PostProcess(text, hyps[i].Text);
            if (c.Length > 0 && !c.Equals(cleaned[0], StringComparison.OrdinalIgnoreCase) &&
                !alts.Contains(c, StringComparer.OrdinalIgnoreCase)) alts.Add(c);
        }

        _lastUse = DateTime.UtcNow;
        return new TranslationResult(cleaned[0], alts.Take(alternatives).ToList(), Name);
    }

    /// <summary>ALL CAPS input confuses the models; translate it in lower case.</summary>
    internal static string Prepare(string text)
    {
        var letters = text.Count(char.IsLetter);
        if (letters >= 4 && text.Where(char.IsLetter).All(char.IsUpper)) return text.ToLowerInvariant();
        return text;
    }

    /// <summary>
    /// The models always produce full sentences ("Reacio."). When the source is a bare word or phrase make
    /// the output look the same: no final period, same initial capitalisation as the source.
    /// </summary>
    internal static string PostProcess(string source, string output)
    {
        var o = output.Trim();
        if (o.Length == 0) return o;

        var s = source.Trim();
        var sourceEndsWithPunct = s.Length > 0 && s[^1] is '.' or '!' or '?' or '…' or '。';
        var sentenceLike = TextNormalizer.WordCount(s) > 4 || sourceEndsWithPunct;

        if (!sentenceLike)
        {
            o = o.TrimEnd('.', ' ');
            if (o.Length > 0 && char.IsUpper(o[0]) && s.Length > 0 && char.IsLower(s[0]))
            {
                // Keep acronyms / all-caps words untouched ("USA"), lower the rest ("Reacio" -> "reacio").
                var firstWord = o.Split(' ', 2)[0];
                var allCaps = firstWord.Length > 1 && firstWord.All(c => !char.IsLetter(c) || char.IsUpper(c));
                if (!allCaps) o = char.ToLowerInvariant(o[0]) + o[1..];
            }
        }

        return o;
    }

    private MarianModel GetModel(ModelPack pack)
    {
        lock (_loadGate)
        {
            if (_loaded.TryGetValue(pack.Id, out var model)) return model;
            model = new MarianModel(pack.Directory, _threads);
            _loaded[pack.Id] = model;
            return model;
        }
    }

    private void UnloadIfIdle()
    {
        if (DateTime.UtcNow - _lastUse < _idleUnload) return;
        if (!_runGate.Wait(0)) return;
        try
        {
            lock (_loadGate)
            {
                if (_loaded.Count == 0) return;
                foreach (var m in _loaded.Values) m.Dispose();
                _loaded.Clear();
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>Releases the models right away (capture switched off).</summary>
    public void Unload()
    {
        _runGate.Wait();
        try
        {
            lock (_loadGate)
            {
                foreach (var m in _loaded.Values) m.Dispose();
                _loaded.Clear();
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        Unload();
    }
}
