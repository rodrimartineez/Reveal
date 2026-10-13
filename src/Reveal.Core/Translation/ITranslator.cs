using Reveal.Core.Models;

namespace Reveal.Core.Translation;

public sealed record TranslationResult(string Text, IReadOnlyList<string> Alternatives, string Engine);

/// <summary>
/// Pluggable translation backend. Reveal ships a local Opus-MT engine; anything else (another local
/// model, a different free service...) only has to implement this interface and be registered in
/// <see cref="TranslationService"/>.
/// </summary>
public interface ITranslator
{
    string Name { get; }

    /// <summary>True when the engine can translate right now (models installed, network allowed...).</summary>
    bool IsAvailableFor(Lang from, Lang to);

    /// <param name="alternatives">Maximum number of additional candidate translations to return.</param>
    Task<TranslationResult> TranslateAsync(string text, Lang from, Lang to, int alternatives, CancellationToken ct);
}

public sealed class TranslationUnavailableException : Exception
{
    public TranslationUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Tries each registered engine in order (local first) until one succeeds.</summary>
public sealed class TranslationService
{
    private readonly IReadOnlyList<ITranslator> _engines;

    public TranslationService(params ITranslator[] engines) => _engines = engines;

    public bool IsAvailableFor(Lang from, Lang to) => _engines.Any(e => e.IsAvailableFor(from, to));

    public string? LastEngine { get; private set; }

    public async Task<TranslationResult> TranslateAsync(string text, Lang from, Lang to, int alternatives, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var engine in _engines)
        {
            if (!engine.IsAvailableFor(from, to)) continue;
            try
            {
                var result = await engine.TranslateAsync(text, from, to, alternatives, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    LastEngine = engine.Name;
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new TranslationUnavailableException(
            "No hay ningún motor de traducción disponible. Descarga el paquete de traducción offline en Ajustes.", last);
    }
}
