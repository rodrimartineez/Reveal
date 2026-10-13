using Reveal.Core.Models;
using Reveal.Core.Settings;

namespace Reveal.Core.Translation;

public sealed record ModelPackFile(string RemotePath, string LocalName);

public sealed record ModelPack(string Id, Lang From, Lang To, string Repository, IReadOnlyList<ModelPackFile> Files)
{
    public string Directory => Path.Combine(AppPaths.ModelsDirectory, Id);

    public static readonly ModelPack EnglishToSpanish = Create("opus-mt-en-es", Lang.English, Lang.Spanish);

    public static readonly ModelPack SpanishToEnglish = Create("opus-mt-es-en", Lang.Spanish, Lang.English);

    public static IReadOnlyList<ModelPack> All { get; } = [EnglishToSpanish, SpanishToEnglish];

    private static ModelPack Create(string id, Lang from, Lang to) => new(id, from, to, "Xenova/" + id,
    [
        new("config.json", "config.json"),
        new("generation_config.json", "generation_config.json"),
        new("vocab.json", "vocab.json"),
        new("source.spm", "source.spm"),
        new("target.spm", "target.spm"),
        new("onnx/encoder_model_quantized.onnx", "encoder_model_quantized.onnx"),
        new("onnx/decoder_model_merged_quantized.onnx", "decoder_model_merged_quantized.onnx"),
    ]);

    public bool IsInstalled => Files.All(f => File.Exists(Path.Combine(Directory, f.LocalName)))
                               && File.Exists(Path.Combine(Directory, ".complete"));
}

public sealed record DownloadProgress(string Pack, string File, long BytesDone, long BytesTotal);

/// <summary>
/// Downloads the offline translation models (Opus-MT, converted to ONNX by the Xenova project) from Hugging Face.
/// This is the only moment Reveal needs Internet for translation; nothing about the user's text is ever sent.
/// </summary>
public sealed class ModelStore
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Reveal/1.0 (model download)");
        return client;
    }

    public bool AllInstalled => ModelPack.All.All(p => p.IsInstalled);

    public event Action? InstallStateChanged;

    public async Task DownloadAllAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        foreach (var pack in ModelPack.All)
        {
            if (!pack.IsInstalled) await DownloadAsync(pack, progress, ct).ConfigureAwait(false);
        }
    }

    public async Task DownloadAsync(ModelPack pack, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(pack.Directory);
        var marker = Path.Combine(pack.Directory, ".complete");
        if (File.Exists(marker)) File.Delete(marker);

        foreach (var file in pack.Files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(pack.Directory, file.LocalName);
            if (File.Exists(target) && new FileInfo(target).Length > 0) continue;

            var url = $"https://huggingface.co/{pack.Repository}/resolve/main/{file.RemotePath}";
            var temp = target + ".part";
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dest = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report(new DownloadProgress(pack.Id, file.LocalName, done, total));
                }
            }

            File.Move(temp, target, overwrite: true);
        }

        File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        InstallStateChanged?.Invoke();
    }

    public void DeleteAll()
    {
        foreach (var pack in ModelPack.All)
        {
            try
            {
                if (Directory.Exists(pack.Directory)) Directory.Delete(pack.Directory, true);
            }
            catch
            {
                // Files in use are cleaned up next time.
            }
        }

        InstallStateChanged?.Invoke();
    }
}
