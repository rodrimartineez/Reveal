using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace Reveal.Core.Translation;

/// <summary>
/// One Opus-MT (Marian) translation direction running on ONNX Runtime: SentencePiece tokenization,
/// transformer encoder, and a beam-search decoder that uses the key/value cache of the merged decoder.
/// </summary>
public sealed class MarianModel : IDisposable
{
    private const int Layers = 6;
    private const int Heads = 8;
    private const int HeadDim = 64;
    private const int Hidden = 512;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly SentencePieceModel _source;
    private readonly SentencePieceModel _target;
    private readonly Dictionary<string, int> _vocab;
    private readonly string[] _idToPiece;
    private readonly int _unkId;
    private readonly int _eosId;
    private readonly int _padId;
    private readonly int _vocabSize;
    private readonly string[] _pastNames;
    private readonly string[] _presentNames;

    public MarianModel(string directory, int threads)
    {
        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "config.json"))).RootElement;
        _eosId = config.GetProperty("eos_token_id").GetInt32();
        _padId = config.GetProperty("pad_token_id").GetInt32();
        _vocabSize = config.GetProperty("vocab_size").GetInt32();

        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(directory, "vocab.json")))
                 ?? throw new InvalidDataException("vocab.json is empty");
        _unkId = _vocab.GetValueOrDefault("<unk>", 1);
        _idToPiece = new string[_vocabSize];
        foreach (var (piece, id) in _vocab)
        {
            if (id >= 0 && id < _vocabSize) _idToPiece[id] = piece;
        }

        _source = SentencePieceModel.Load(Path.Combine(directory, "source.spm"));
        _target = SentencePieceModel.Load(Path.Combine(directory, "target.spm"));

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, // DISABLE_ALL saves ~60 MB per model but is ~5x slower
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            EnableMemoryPattern = false,
        };

        _encoder = new InferenceSession(Path.Combine(directory, "encoder_model_quantized.onnx"), options);
        _decoder = new InferenceSession(Path.Combine(directory, "decoder_model_merged_quantized.onnx"), options);

        _pastNames = new string[Layers * 4];
        _presentNames = new string[Layers * 4];
        for (var l = 0; l < Layers; l++)
        {
            _pastNames[l * 4 + 0] = $"past_key_values.{l}.decoder.key";
            _pastNames[l * 4 + 1] = $"past_key_values.{l}.decoder.value";
            _pastNames[l * 4 + 2] = $"past_key_values.{l}.encoder.key";
            _pastNames[l * 4 + 3] = $"past_key_values.{l}.encoder.value";
            _presentNames[l * 4 + 0] = $"present.{l}.decoder.key";
            _presentNames[l * 4 + 1] = $"present.{l}.decoder.value";
            _presentNames[l * 4 + 2] = $"present.{l}.encoder.key";
            _presentNames[l * 4 + 3] = $"present.{l}.encoder.value";
        }
    }

    public int[] Tokenize(string text)
    {
        var pieces = _source.Encode(text);
        var ids = new int[pieces.Count + 1];
        for (var i = 0; i < pieces.Count; i++) ids[i] = _vocab.GetValueOrDefault(pieces[i], _unkId);
        ids[^1] = _eosId;
        return ids;
    }

    public string Detokenize(IReadOnlyList<int> ids)
    {
        var pieces = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            if (id == _eosId || id == _padId || id < 0 || id >= _vocabSize) continue;
            var piece = _idToPiece[id];
            if (piece is null or "<unk>" or "<pad>" or "</s>") continue;
            pieces.Add(piece);
        }

        return SentencePieceModel.Detokenize(pieces);
    }

    /// <summary>Beam search. Returns finished hypotheses, best first (text + length-normalised score).</summary>
    public List<(string Text, float Score)> Translate(string text, int beams, int returnCount, CancellationToken ct)
    {
        var input = Tokenize(text);
        if (input.Length > 400) input = [.. input.AsSpan(0, 399), _eosId];
        var srcLen = input.Length;

        // ---- encoder ----
        var inputIds = new long[srcLen];
        var mask = new long[srcLen];
        for (var i = 0; i < srcLen; i++)
        {
            inputIds[i] = input[i];
            mask[i] = 1;
        }

        float[] hiddenSingle;
        using (var idsValue = OrtValue.CreateTensorValueFromMemory(inputIds, [1, srcLen]))
        using (var maskValue = OrtValue.CreateTensorValueFromMemory(mask, [1, srcLen]))
        using (var outputs = _encoder.Run(new RunOptions(),
                   new Dictionary<string, OrtValue> { ["input_ids"] = idsValue, ["attention_mask"] = maskValue },
                   ["last_hidden_state"]))
        {
            hiddenSingle = outputs[0].GetTensorDataAsSpan<float>().ToArray();
        }

        var b = Math.Max(1, beams);
        var hidden = new float[b * srcLen * Hidden];
        for (var i = 0; i < b; i++) Array.Copy(hiddenSingle, 0, hidden, i * hiddenSingle.Length, hiddenSingle.Length);

        var encMask = new long[b * srcLen];
        Array.Fill(encMask, 1L);

        // ---- decoder state ----
        var past = new float[Layers * 4][];
        var pastLen = new int[2]; // [decoder length, encoder length]
        for (var i = 0; i < past.Length; i++) past[i] = [];

        var beamTokens = new List<int>[b];
        var beamScores = new float[b];
        for (var i = 0; i < b; i++)
        {
            beamTokens[i] = [_padId]; // decoder_start_token_id == pad_token_id for Marian
            beamScores[i] = i == 0 ? 0f : float.NegativeInfinity;
        }

        var finished = new List<(List<int> Tokens, float Score)>();
        var maxNew = Math.Min(400, srcLen * 2 + 10);
        var useCache = false;

        for (var step = 0; step < maxNew; step++)
        {
            ct.ThrowIfCancellationRequested();

            var lastTokens = new long[b];
            for (var i = 0; i < b; i++) lastTokens[i] = beamTokens[i][^1];

            var decLen = useCache ? pastLen[0] : 0;
            var encLen = useCache ? pastLen[1] : 0;

            var inputs = new Dictionary<string, OrtValue>(8 + past.Length);
            var toDispose = new List<OrtValue>(past.Length + 4);
            try
            {
                OrtValue Add(string name, OrtValue v)
                {
                    inputs[name] = v;
                    toDispose.Add(v);
                    return v;
                }

                Add("encoder_attention_mask", OrtValue.CreateTensorValueFromMemory(encMask, [b, srcLen]));
                Add("input_ids", OrtValue.CreateTensorValueFromMemory(lastTokens, [b, 1]));
                Add("encoder_hidden_states", OrtValue.CreateTensorValueFromMemory(hidden, [b, srcLen, Hidden]));
                for (var l = 0; l < Layers; l++)
                {
                    Add(_pastNames[l * 4 + 0], OrtValue.CreateTensorValueFromMemory(past[l * 4 + 0], [b, Heads, decLen, HeadDim]));
                    Add(_pastNames[l * 4 + 1], OrtValue.CreateTensorValueFromMemory(past[l * 4 + 1], [b, Heads, decLen, HeadDim]));
                    Add(_pastNames[l * 4 + 2], OrtValue.CreateTensorValueFromMemory(past[l * 4 + 2], [b, Heads, encLen, HeadDim]));
                    Add(_pastNames[l * 4 + 3], OrtValue.CreateTensorValueFromMemory(past[l * 4 + 3], [b, Heads, encLen, HeadDim]));
                }

                Add("use_cache_branch", OrtValue.CreateTensorValueFromMemory(new[] { useCache }, [1]));

                var outputNames = new List<string> { "logits" };
                outputNames.AddRange(_presentNames);

                using var outputs = _decoder.Run(new RunOptions(), inputs, outputNames);

                // ---- log-softmax of the last position for each beam ----
                var logits = outputs[0].GetTensorDataAsSpan<float>();
                var vocabLogits = logits.Length / b; // [b, 1, V]
                var logProbs = new float[b][];
                for (var i = 0; i < b; i++)
                {
                    logProbs[i] = LogSoftmax(logits.Slice(i * vocabLogits, vocabLogits), _padId);
                }

                // ---- pick the best 2*b continuations ----
                var candidates = SelectTop(logProbs, beamScores, 2 * b);
                var newTokens = new List<int>[b];
                var newScores = new float[b];
                var newSource = new int[b];
                var live = 0;

                for (var rank = 0; rank < candidates.Count && live < b; rank++)
                {
                    var (beamIdx, token, score) = candidates[rank];
                    if (token == _eosId)
                    {
                        if (rank >= b) continue; // HF rule: only top-b EOS candidates count
                        var tokens = new List<int>(beamTokens[beamIdx]);
                        var length = tokens.Count; // includes start token, excludes eos
                        finished.Add((tokens, score / length));
                    }
                    else
                    {
                        var tokens = new List<int>(beamTokens[beamIdx]) { token };
                        newTokens[live] = tokens;
                        newScores[live] = score;
                        newSource[live] = beamIdx;
                        live++;
                    }
                }

                if (live == 0) break;
                for (var i = live; i < b; i++)
                {
                    // Not enough live hypotheses (very short input): duplicate the best one with -inf score.
                    newTokens[i] = newTokens[0];
                    newScores[i] = float.NegativeInfinity;
                    newSource[i] = newSource[0];
                }

                // ---- update the key/value cache (reorder decoder part by selected beams) ----
                var newDecLen = decLen + 1;
                for (var l = 0; l < Layers; l++)
                {
                    for (var kv = 0; kv < 2; kv++)
                    {
                        var present = outputs[1 + l * 4 + kv].GetTensorDataAsSpan<float>();
                        var perBeam = present.Length / b;
                        var reordered = new float[present.Length];
                        for (var i = 0; i < b; i++)
                        {
                            present.Slice(newSource[i] * perBeam, perBeam).CopyTo(reordered.AsSpan(i * perBeam, perBeam));
                        }

                        past[l * 4 + kv] = reordered;
                    }

                    if (!useCache)
                    {
                        for (var kv = 2; kv < 4; kv++)
                        {
                            past[l * 4 + kv] = outputs[1 + l * 4 + kv].GetTensorDataAsSpan<float>().ToArray();
                        }
                    }
                }

                pastLen[0] = newDecLen;
                pastLen[1] = srcLen;
                useCache = true;

                beamTokens = newTokens;
                beamScores = newScores;

                // ---- stop criterion ----
                if (finished.Count >= b)
                {
                    var worst = finished.OrderByDescending(f => f.Score).Take(b).Last().Score;
                    var bestLive = float.NegativeInfinity;
                    for (var i = 0; i < b; i++) bestLive = Math.Max(bestLive, beamScores[i] / beamTokens[i].Count);
                    if (bestLive <= worst) break;
                }
            }
            finally
            {
                foreach (var v in toDispose) v.Dispose();
            }
        }

        if (finished.Count == 0)
        {
            for (var i = 0; i < b; i++)
            {
                if (!float.IsNegativeInfinity(beamScores[i])) finished.Add((beamTokens[i], beamScores[i] / beamTokens[i].Count));
            }
        }

        var results = new List<(string, float)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tokens, score) in finished.OrderByDescending(f => f.Score))
        {
            var textOut = Detokenize(tokens.Skip(1).ToList());
            if (textOut.Length == 0 || !seen.Add(textOut)) continue;
            results.Add((textOut, score));
            if (results.Count >= returnCount) break;
        }

        return results;
    }

    private static float[] LogSoftmax(ReadOnlySpan<float> logits, int forbiddenId)
    {
        var result = new float[logits.Length];
        var max = float.NegativeInfinity;
        for (var i = 0; i < logits.Length; i++)
        {
            if (i == forbiddenId) continue;
            if (logits[i] > max) max = logits[i];
        }

        double sum = 0;
        for (var i = 0; i < logits.Length; i++)
        {
            if (i == forbiddenId) continue;
            sum += Math.Exp(logits[i] - max);
        }

        var logSum = (float)Math.Log(sum) + max;
        for (var i = 0; i < logits.Length; i++)
        {
            result[i] = i == forbiddenId ? float.NegativeInfinity : logits[i] - logSum;
        }

        return result;
    }

    private static List<(int Beam, int Token, float Score)> SelectTop(float[][] logProbs, float[] beamScores, int count)
    {
        var heap = new PriorityQueue<(int Beam, int Token, float Score), float>();
        for (var b = 0; b < logProbs.Length; b++)
        {
            if (float.IsNegativeInfinity(beamScores[b])) continue;
            var lp = logProbs[b];
            // Cheap pre-filter: only tokens that can reach the top `count` of this beam.
            var threshold = KthLargest(lp, count);
            for (var t = 0; t < lp.Length; t++)
            {
                if (lp[t] < threshold) continue;
                var score = beamScores[b] + lp[t];
                if (heap.Count < count) heap.Enqueue((b, t, score), score);
                else if (score > heap.Peek().Score)
                {
                    heap.Dequeue();
                    heap.Enqueue((b, t, score), score);
                }
            }
        }

        var list = new List<(int, int, float)>(heap.Count);
        while (heap.Count > 0) list.Add(heap.Dequeue());
        list.Reverse();
        return list;
    }

    private static float KthLargest(float[] values, int k)
    {
        var heap = new PriorityQueue<float, float>();
        foreach (var v in values)
        {
            if (float.IsNegativeInfinity(v)) continue;
            if (heap.Count < k) heap.Enqueue(v, v);
            else if (v > heap.Peek())
            {
                heap.Dequeue();
                heap.Enqueue(v, v);
            }
        }

        return heap.Count == 0 ? float.NegativeInfinity : heap.Peek();
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
