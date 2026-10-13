using System.Buffers.Binary;
using System.Text;

namespace Reveal.Core.Translation;

/// <summary>
/// Minimal SentencePiece *unigram* implementation: reads the protobuf .spm file and segments text with
/// Viterbi. This is all the Marian (Opus-MT) models need, so no native tokenizer library is required.
/// </summary>
public sealed class SentencePieceModel
{
    private const char Space = '▁';

    private readonly Dictionary<string, (int Index, float Score)> _pieces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Index, float Score)>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly int _maxPieceLength;
    private readonly float _unknownScore;

    public IReadOnlyList<string> PieceList { get; }

    public int UnknownIndex { get; }

    private SentencePieceModel(List<(string Piece, float Score, int Type)> list)
    {
        PieceList = list.Select(p => p.Piece).ToList();
        var minScore = float.MaxValue;
        var unk = 0;

        for (var i = 0; i < list.Count; i++)
        {
            var (piece, score, type) = list[i];
            if (type == 2) unk = i;                  // UNKNOWN
            if (type is 1 or 4 or 0)                  // NORMAL / USER_DEFINED (0 = unspecified = NORMAL)
            {
                _pieces[piece] = (i, score);
                if (piece.Length > _maxPieceLength) _maxPieceLength = piece.Length;
                if (type != 4 && score < minScore) minScore = score;
            }
        }

        UnknownIndex = unk;
        _unknownScore = minScore - 10f;
        _lookup = _pieces.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public static SentencePieceModel Load(string path) => Parse(File.ReadAllBytes(path));

    public static SentencePieceModel Parse(ReadOnlySpan<byte> data)
    {
        var pieces = new List<(string, float, int)>();
        var pos = 0;

        while (pos < data.Length)
        {
            var tag = ReadVarint(data, ref pos);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);

            switch (wire)
            {
                case 0: ReadVarint(data, ref pos); break;
                case 1: pos += 8; break;
                case 5: pos += 4; break;
                case 2:
                    var len = (int)ReadVarint(data, ref pos);
                    if (field == 1) pieces.Add(ParsePiece(data.Slice(pos, len)));
                    pos += len;
                    break;
                default:
                    throw new InvalidDataException("Unsupported protobuf wire type in SentencePiece model.");
            }
        }

        if (pieces.Count == 0) throw new InvalidDataException("The SentencePiece model has no vocabulary.");
        return new SentencePieceModel(pieces);
    }

    private static (string Piece, float Score, int Type) ParsePiece(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var piece = "";
        var score = 0f;
        var type = 1;

        while (pos < data.Length)
        {
            var tag = ReadVarint(data, ref pos);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                    var v = (int)ReadVarint(data, ref pos);
                    if (field == 3) type = v;
                    break;
                case 1: pos += 8; break;
                case 5:
                    if (field == 2) score = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos, 4)));
                    pos += 4;
                    break;
                case 2:
                    var len = (int)ReadVarint(data, ref pos);
                    if (field == 1) piece = Encoding.UTF8.GetString(data.Slice(pos, len));
                    pos += len;
                    break;
                default:
                    throw new InvalidDataException("Unsupported protobuf wire type in SentencePiece piece.");
            }
        }

        return (piece, score, type);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    /// <summary>Segments <paramref name="text"/> into pieces (strings, with ▁ marking word starts).</summary>
    public List<string> Encode(string text)
    {
        var normalized = Normalize(text);
        var result = new List<string>();
        if (normalized.Length == 0) return result;

        var n = normalized.Length;
        var best = new float[n + 1];
        var from = new int[n + 1];
        var isUnk = new bool[n + 1];
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;

        var span = normalized.AsSpan();
        for (var i = 0; i < n; i++)
        {
            if (float.IsNegativeInfinity(best[i])) continue;

            var max = Math.Min(_maxPieceLength, n - i);
            var found = false;
            for (var len = 1; len <= max; len++)
            {
                if (_lookup.TryGetValue(span.Slice(i, len), out var entry))
                {
                    found = true;
                    var s = best[i] + entry.Score;
                    if (s > best[i + len])
                    {
                        best[i + len] = s;
                        from[i + len] = i;
                        isUnk[i + len] = false;
                    }
                }
            }

            if (!found || best[i + 1] == float.NegativeInfinity)
            {
                // Unknown character: consume one (surrogate pair aware) char as <unk>.
                var step = char.IsHighSurrogate(span[i]) && i + 2 <= n ? 2 : 1;
                var s = best[i] + _unknownScore;
                if (s > best[i + step])
                {
                    best[i + step] = s;
                    from[i + step] = i;
                    isUnk[i + step] = true;
                }
            }
        }

        var end = n;
        var stack = new List<string>();
        while (end > 0)
        {
            var start = from[end];
            stack.Add(isUnk[end] ? "<unk>" : normalized.Substring(start, end - start));
            end = start;
        }

        stack.Reverse();
        return stack;
    }

    private static string Normalize(string text)
    {
        var s = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length + 2);
        sb.Append(Space);
        var lastWasSpace = true;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (!lastWasSpace) sb.Append(Space);
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }

        while (sb.Length > 0 && sb[^1] == Space) sb.Length--;
        return sb.ToString();
    }

    public static string Detokenize(IEnumerable<string> pieces)
    {
        var sb = new StringBuilder();
        foreach (var p in pieces) sb.Append(p);
        return sb.ToString().Replace(Space, ' ').Trim();
    }
}
