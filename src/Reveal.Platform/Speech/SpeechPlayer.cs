using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Reveal.Platform.Speech;

/// <summary>
/// Plays text with a Windows voice (Windows.Media.SpeechSynthesis, on-device, free, offline).
/// Synthesised audio is cached in memory for the last few words so replaying is instant.
/// </summary>
public sealed class SpeechPlayer : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _cache = new();
    private readonly Queue<string> _cacheOrder = new();
    private MediaPlayer? _player;
    private CancellationTokenSource? _current;

    public double Rate { get; set; } = 1.0;

    public event Action? Started;

    public event Action? Finished;

    /// <summary>Synthesises (if needed) and plays <paramref name="text"/>. Stops whatever was playing before.</summary>
    public async Task SpeakAsync(string text, VoiceInfo voice, CancellationToken ct = default)
    {
        Stop();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _current = cts;

        try
        {
            var bytes = await GetAudioAsync(text, voice, cts.Token).ConfigureAwait(false);
            cts.Token.ThrowIfCancellationRequested();

            var tcs = new TaskCompletionSource();
            var player = new MediaPlayer { AutoPlay = false };
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
            player.MediaEnded += (_, _) => tcs.TrySetResult();
            player.MediaFailed += (_, e) => tcs.TrySetException(new InvalidOperationException("No se pudo reproducir el audio: " + e.ErrorMessage));
            lock (_gate)
            {
                _player?.Dispose();
                _player = player;
            }

            using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
            Started?.Invoke();
            player.Play();
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            Finished?.Invoke();
        }
    }

    /// <summary>Pre-synthesises audio in the background so the first click plays without delay.</summary>
    public Task PrepareAsync(string text, VoiceInfo voice) => Task.Run(async () =>
    {
        try { await GetAudioAsync(text, voice, CancellationToken.None).ConfigureAwait(false); }
        catch { /* best effort */ }
    });

    public void Stop()
    {
        lock (_gate)
        {
            _current?.Cancel();
            try { _player?.Pause(); } catch { /* ignore */ }
        }
    }

    private async Task<byte[]> GetAudioAsync(string text, VoiceInfo voice, CancellationToken ct)
    {
        var key = $"{voice.Id}|{Rate:0.00}|{text}";
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        var all = SpeechSynthesizer.AllVoices;
        var info = all.FirstOrDefault(v => v.Id == voice.Id)
                   ?? throw new InvalidOperationException($"La voz «{voice.Name}» ya no está instalada en Windows.");

        using var synth = new SpeechSynthesizer { Voice = info };
        synth.Options.SpeakingRate = Math.Clamp(Rate, 0.5, 2.0);
        using var synthesized = await synth.SynthesizeTextToStreamAsync(text);
        ct.ThrowIfCancellationRequested();

        var size = (uint)synthesized.Size;
        var buffer = new Windows.Storage.Streams.Buffer(size);
        await synthesized.ReadAsync(buffer, size, Windows.Storage.Streams.InputStreamOptions.None);
        var bytes = new byte[buffer.Length];
        Windows.Storage.Streams.DataReader.FromBuffer(buffer).ReadBytes(bytes);

        lock (_gate)
        {
            _cache[key] = bytes;
            _cacheOrder.Enqueue(key);
            while (_cacheOrder.Count > 12) _cache.Remove(_cacheOrder.Dequeue());
        }

        return bytes;
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _player?.Dispose();
            _player = null;
        }
    }
}
