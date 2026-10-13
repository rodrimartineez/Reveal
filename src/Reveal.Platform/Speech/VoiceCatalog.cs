using Windows.Media.SpeechSynthesis;

namespace Reveal.Platform.Speech;

public enum EnglishVariant
{
    American,
    British,
}

public sealed record VoiceInfo(string Id, string Name, string Language, string Gender)
{
    public bool IsAmerican => Language.Equals("en-US", StringComparison.OrdinalIgnoreCase);

    public bool IsBritish => Language.Equals("en-GB", StringComparison.OrdinalIgnoreCase);

    public bool IsEnglish => Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    public string Display => $"{Name} ({Language})";

    public override string ToString() => Display;
}

/// <summary>Lists the voices Windows has installed (the same ones used by Narrator and other apps).</summary>
public static class VoiceCatalog
{
    public static IReadOnlyList<VoiceInfo> GetEnglishVoices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices
                .Select(v => new VoiceInfo(v.Id, CleanName(v.DisplayName), v.Language, v.Gender.ToString()))
                .Where(v => v.IsEnglish)
                .OrderBy(v => v.Language)
                .ThenBy(v => v.Name)
                .ToList();
        }
        catch
        {
            return Array.Empty<VoiceInfo>();
        }
    }

    /// <summary>Voices that fit a variant: exact locale first, then other English voices as a fallback list.</summary>
    public static IReadOnlyList<VoiceInfo> ForVariant(IReadOnlyList<VoiceInfo> all, EnglishVariant variant)
    {
        var exact = all.Where(v => variant == EnglishVariant.American ? v.IsAmerican : v.IsBritish).ToList();
        return exact;
    }

    /// <summary>
    /// Picks the configured voice if it still exists, else the first voice of the right locale.
    /// Returns null when Windows has no voice for that variant (the UI then explains how to install one).
    /// </summary>
    public static VoiceInfo? Resolve(IReadOnlyList<VoiceInfo> all, EnglishVariant variant, string? preferredId)
    {
        if (!string.IsNullOrEmpty(preferredId))
        {
            var preferred = all.FirstOrDefault(v => v.Id == preferredId);
            if (preferred is not null) return preferred;
        }

        var candidates = ForVariant(all, variant);
        // Prefer the natural-sounding / female default voices Windows ships first (Zira, Hazel) but any works.
        return candidates.FirstOrDefault();
    }

    private static string CleanName(string display)
    {
        // "Microsoft Zira - English (United States)" -> "Zira"
        var name = display.Replace("Microsoft ", "", StringComparison.OrdinalIgnoreCase);
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? name[..dash] : name;
    }
}
