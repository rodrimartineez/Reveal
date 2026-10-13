using System.Text;
using System.Text.Json;
using Reveal.Core.Models;

namespace Reveal.Core.Translation;

/// <summary>
/// OPTIONAL fallback that uses the public, keyless web endpoint of Google Translate. Free, but it sends the
/// selected text over the Internet, so it is disabled unless the user turns it on in Settings, and it is only used
/// when the offline engine is unavailable. Not an official API: it may change or rate limit without notice.
/// </summary>
public sealed class OnlineTranslator : ITranslator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly Func<bool> _isAllowed;

    public OnlineTranslator(Func<bool> isAllowed) => _isAllowed = isAllowed;

    public string Name => "Google Translate (online)";

    public bool IsAvailableFor(Lang from, Lang to) => _isAllowed() && from != to && from != Lang.Unknown && to != Lang.Unknown;

    public async Task<TranslationResult> TranslateAsync(string text, Lang from, Lang to, int alternatives, CancellationToken ct)
    {
        static string Code(Lang l) => l == Lang.Spanish ? "es" : "en";
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&dt=at"
                  + $"&sl={Code(from)}&tl={Code(to)}&q={Uri.EscapeDataString(text)}";

        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);

        var sb = new StringBuilder();
        foreach (var segment in doc.RootElement[0].EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 && segment[0].ValueKind == JsonValueKind.String)
            {
                sb.Append(segment[0].GetString());
            }
        }

        var alts = new List<string>();
        try
        {
            if (doc.RootElement.GetArrayLength() > 5 && doc.RootElement[5].ValueKind == JsonValueKind.Array)
            {
                foreach (var group in doc.RootElement[5].EnumerateArray())
                {
                    if (group.GetArrayLength() > 2 && group[2].ValueKind == JsonValueKind.Array)
                    {
                        foreach (var alt in group[2].EnumerateArray())
                        {
                            var word = alt[0].GetString();
                            if (!string.IsNullOrWhiteSpace(word)) alts.Add(word);
                        }
                    }
                }
            }
        }
        catch
        {
            // alternatives are optional
        }

        var best = sb.ToString().Trim();
        return new TranslationResult(best, alts.Where(a => !a.Equals(best, StringComparison.OrdinalIgnoreCase)).Take(alternatives).ToList(), Name);
    }
}
