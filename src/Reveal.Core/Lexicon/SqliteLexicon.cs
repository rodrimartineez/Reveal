using Microsoft.Data.Sqlite;
using Reveal.Core.Models;
using Reveal.Core.Text;

namespace Reveal.Core.Lexicon;

/// <summary>Read-only SQLite lexicon built by tools/lexicon/build_lexicon.py.</summary>
public sealed class SqliteLexicon : ILexicon, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private readonly SqliteCommand _findEn;
    private readonly SqliteCommand _findLemma;
    private readonly SqliteCommand _findEs;
    private readonly SqliteCommand _findEsNd;
    private readonly SqliteCommand _freq;

    public SqliteLexicon(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();

        _findEn = Prepare("SELECT word,pos,ipa_us,ipa_uk,tr,syn,ex FROM en WHERE word=$w", "$w");
        _findLemma = Prepare("SELECT lemma FROM forms WHERE form=$w", "$w");
        _findEs = Prepare("SELECT en FROM es WHERE key=$w", "$w");
        _findEsNd = Prepare("SELECT en FROM es WHERE nd=$w LIMIT 1", "$w");
        _freq = Prepare("SELECT ppm FROM freq WHERE lang=$l AND word=$w", "$l", "$w");
    }

    public bool IsAvailable => true;

    private SqliteCommand Prepare(string sql, params string[] parameters)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters) cmd.Parameters.Add(p, SqliteType.Text);
        cmd.Prepare();
        return cmd;
    }

    public EnglishMatch? FindEnglish(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        var lower = trimmed.ToLowerInvariant();

        lock (_gate)
        {
            var direct = Get(trimmed) ?? (lower != trimmed ? Get(lower) : null);
            var hasData = direct is not null && (direct.Translations.Count > 0 || direct.Synonyms.Count > 0 || direct.Example is not null);

            if (hasData)
            {
                // An inflected form with a thin entry of its own ("running" -> adverb "consecutivo"): the lemma says more.
                if (direct!.Translations.Count < 2)
                {
                    var formLemma = Scalar(_findLemma, lower) ?? (lower != trimmed ? Scalar(_findLemma, trimmed) : null);
                    var lemmaEntry = formLemma is null ? null : Get(formLemma) ?? Get(formLemma.ToLowerInvariant());
                    if (lemmaEntry is not null)
                    {
                        return new EnglishMatch(lemmaEntry with { IpaUs = direct.IpaUs ?? lemmaEntry.IpaUs, IpaUk = direct.IpaUk ?? lemmaEntry.IpaUk }, true, formLemma);
                    }
                }

                return new EnglishMatch(direct, false, null);
            }

            // Inflected form (running, gave up, children): fall back to the lemma's entry.
            var lemma = Scalar(_findLemma, lower) ?? (lower != trimmed ? Scalar(_findLemma, trimmed) : null);
            if (lemma is not null)
            {
                var entry = Get(lemma) ?? Get(lemma.ToLowerInvariant());
                if (entry is not null) return new EnglishMatch(entry, true, lemma);
            }

            return direct is null ? null : new EnglishMatch(direct, false, null);
        }
    }

    public IReadOnlyList<string> FindEnglishForSpanish(string spanishWord)
    {
        var key = spanishWord.Trim().ToLowerInvariant();
        if (key.Length == 0) return Array.Empty<string>();

        lock (_gate)
        {
            var list = Scalar(_findEs, key) ?? Scalar(_findEsNd, TextNormalizer.RemoveDiacritics(key));
            return Split(list);
        }
    }

    public double Frequency(Lang language, string word)
    {
        if (language is not (Lang.English or Lang.Spanish) || word.Length == 0) return 0;
        lock (_gate)
        {
            _freq.Parameters["$l"].Value = language == Lang.English ? "en" : "es";
            _freq.Parameters["$w"].Value = word.ToLowerInvariant();
            return _freq.ExecuteScalar() is double d ? d : 0;
        }
    }

    public bool HasSpanishEntry(string word)
    {
        var key = word.Trim().ToLowerInvariant();
        if (key.Length == 0) return false;
        lock (_gate) return Scalar(_findEs, key) is not null || Scalar(_findEsNd, TextNormalizer.RemoveDiacritics(key)) is not null;
    }

    public bool HasEnglishEntry(string word)
    {
        lock (_gate) return Get(word.Trim()) is not null || Get(word.Trim().ToLowerInvariant()) is not null;
    }

    private LexiconEntry? Get(string word)
    {
        _findEn.Parameters["$w"].Value = word;
        using var reader = _findEn.ExecuteReader();
        if (!reader.Read()) return null;
        return new LexiconEntry(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            Split(reader.IsDBNull(4) ? null : reader.GetString(4)),
            Split(reader.IsDBNull(5) ? null : reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static string? Scalar(SqliteCommand command, string value)
    {
        command.Parameters["$w"].Value = value;
        return command.ExecuteScalar() as string;
    }

    private static IReadOnlyList<string> Split(string? joined) =>
        string.IsNullOrEmpty(joined) ? Array.Empty<string>() : joined.Split('|', StringSplitOptions.RemoveEmptyEntries);

    public void Dispose()
    {
        lock (_gate)
        {
            _findEn.Dispose();
            _findLemma.Dispose();
            _findEs.Dispose();
            _findEsNd.Dispose();
            _freq.Dispose();
            _connection.Dispose();
        }
    }
}
