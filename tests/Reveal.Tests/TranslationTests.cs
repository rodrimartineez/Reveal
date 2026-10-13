using Reveal.Core.Models;
using Reveal.Core.Translation;
using Xunit.Abstractions;

namespace Reveal.Tests;

public sealed class TranslationTests(ITestOutputHelper output)
{
    private static bool ModelsReady => ModelPack.All.All(p => p.IsInstalled);

    [Fact]
    public async Task Smoke_translate_samples()
    {
        if (!ModelsReady) return;
        using var t = new MarianTranslator();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (text, from, to) in new (string, Lang, Lang)[]
        {
            ("reluctant", Lang.English, Lang.Spanish),
            ("give up", Lang.English, Lang.Spanish),
            ("He was reluctant to accept the offer.", Lang.English, Lang.Spanish),
            ("reacio", Lang.Spanish, Lang.English),
            ("Tengo que irme ahora", Lang.Spanish, Lang.English),
            ("ubiquitous", Lang.English, Lang.Spanish),
            ("bank", Lang.English, Lang.Spanish),
            ("corriendo", Lang.Spanish, Lang.English),
        })
        {
            sw.Restart();
            var r = await t.TranslateAsync(text, from, to, 3, CancellationToken.None);
            output.WriteLine($"{text} -> {r.Text}  | alts: {string.Join(", ", r.Alternatives)}  ({sw.ElapsedMilliseconds} ms)");
        }
    }
}
