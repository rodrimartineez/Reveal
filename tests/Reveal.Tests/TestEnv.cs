using Reveal.Core.Lexicon;
using Reveal.Core.Text;
using Reveal.Core.Translation;

namespace Reveal.Tests;

/// <summary>Locates the optional heavy fixtures (lexicon DB, language model, Opus-MT models). Tests skip silently if absent.</summary>
internal static class TestEnv
{
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Reveal.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
        }
    }

    public static string LexiconPath => Path.Combine(RepoRoot, "data", "build", "lexicon.db");

    public static string LangIdPath => Path.Combine(RepoRoot, "data", "build", "langid.json");

    public static bool HasLexicon => File.Exists(LexiconPath);

    public static bool HasModels => ModelPack.All.All(p => p.IsInstalled);

    public static SqliteLexicon OpenLexicon() => new(LexiconPath);

    public static LanguageDetector Detector(ILexicon lexicon) => new(lexicon, LangIdPath);
}
